using Microsoft.AspNetCore.Mvc;
using Npgsql;
using ImajinationAPI.Models;
using System.Text.Json;
using System.Threading;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventController : ControllerBase
    {
        private readonly string _connectionString;
        private static readonly SemaphoreSlim EventLineupSchemaLock = new(1, 1);
        private static volatile bool _eventLineupColumnsEnsured;

        public EventController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        private static readonly JsonSerializerOptions LineupJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private async Task EnsureVerifiedGigsTableExists(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS verified_gigs (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    event_id uuid NOT NULL,
                    role_at_event varchar(30) NOT NULL,
                    verification_status varchar(30) NOT NULL DEFAULT 'Verified',
                    notes text NULL,
                    verified_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (user_id, event_id, role_at_event)
                );

                CREATE TABLE IF NOT EXISTS event_artists (
                    id uuid PRIMARY KEY,
                    event_id uuid NOT NULL,
                    artist_user_id uuid NOT NULL,
                    booking_id uuid NULL,
                    status varchar(40) NOT NULL DEFAULT 'Confirmed',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (event_id, artist_user_id)
                );

                CREATE TABLE IF NOT EXISTS event_sessionists (
                    id uuid PRIMARY KEY,
                    event_id uuid NOT NULL,
                    sessionist_user_id uuid NOT NULL,
                    booking_id uuid NULL,
                    status varchar(40) NOT NULL DEFAULT 'Confirmed',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (event_id, sessionist_user_id)
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task CreateVerifiedGigRecords(NpgsqlConnection connection, Guid eventId)
        {
            await EnsureVerifiedGigsTableExists(connection);
            await EnsureEventLineupColumnsOnce(connection);

            const string eventSql = @"
                SELECT COALESCE(artist_lineup, '[]'), COALESCE(sessionist_lineup, '[]')
                FROM events
                WHERE id = @id;";

            string artistLineupJson = "[]";
            string sessionistLineupJson = "[]";
            using (var eventCmd = new NpgsqlCommand(eventSql, connection))
            {
                eventCmd.Parameters.AddWithValue("@id", eventId);
                using var reader = await eventCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    artistLineupJson = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
                    sessionistLineupJson = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                }
            }

            var artistIds = new HashSet<Guid>();
            var sessionistIds = new HashSet<Guid>();

            const string eventArtistsSql = "SELECT artist_user_id FROM event_artists WHERE event_id = @eventId;";
            using (var artistCmd = new NpgsqlCommand(eventArtistsSql, connection))
            {
                artistCmd.Parameters.AddWithValue("@eventId", eventId);
                using var reader = await artistCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0)) artistIds.Add(reader.GetGuid(0));
                }
            }

            const string eventSessionistsSql = "SELECT sessionist_user_id FROM event_sessionists WHERE event_id = @eventId;";
            using (var sessionistCmd = new NpgsqlCommand(eventSessionistsSql, connection))
            {
                sessionistCmd.Parameters.AddWithValue("@eventId", eventId);
                using var reader = await sessionistCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0)) sessionistIds.Add(reader.GetGuid(0));
                }
            }

            foreach (var item in DeserializeLineup(artistLineupJson))
            {
                if (item.id != Guid.Empty) artistIds.Add(item.id);
            }

            foreach (var item in DeserializeLineup(sessionistLineupJson))
            {
                if (item.id != Guid.Empty) sessionistIds.Add(item.id);
            }

            const string insertSql = @"
                INSERT INTO verified_gigs (id, user_id, event_id, role_at_event, verification_status, notes, verified_at)
                VALUES (@id, @userId, @eventId, @role, 'Verified', @notes, NOW())
                ON CONFLICT (user_id, event_id, role_at_event)
                DO NOTHING;";

            foreach (var userId in artistIds)
            {
                using var cmd = new NpgsqlCommand(insertSql, connection);
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@eventId", eventId);
                cmd.Parameters.AddWithValue("@role", "Artist");
                cmd.Parameters.AddWithValue("@notes", "Verified from finished event lineup.");
                await cmd.ExecuteNonQueryAsync();
            }

            foreach (var userId in sessionistIds)
            {
                using var cmd = new NpgsqlCommand(insertSql, connection);
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@eventId", eventId);
                cmd.Parameters.AddWithValue("@role", "Sessionist");
                cmd.Parameters.AddWithValue("@notes", "Verified from finished event lineup.");
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private async Task EnsureEventLineupColumns(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE events ADD COLUMN IF NOT EXISTS artist_lineup text;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sessionist_lineup text;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureEventLineupColumnsOnce(NpgsqlConnection connection)
        {
            if (_eventLineupColumnsEnsured) return;

            await EventLineupSchemaLock.WaitAsync();
            try
            {
                if (_eventLineupColumnsEnsured) return;

                await EnsureEventLineupColumns(connection);
                _eventLineupColumnsEnsured = true;
            }
            finally
            {
                EventLineupSchemaLock.Release();
            }
        }

        private static List<TalentLineupItemDto> NormalizeLineup(IEnumerable<TalentLineupItemDto>? lineup, string role)
        {
            if (lineup == null) return new List<TalentLineupItemDto>();

            return lineup
                .Where(item => item != null && item.id != Guid.Empty && !string.IsNullOrWhiteSpace(item.displayName))
                .GroupBy(item => item.id)
                .Select(group =>
                {
                    var item = group.First();
                    return new TalentLineupItemDto
                    {
                        id = item.id,
                        displayName = item.displayName.Trim(),
                        role = role,
                        profilePicture = string.IsNullOrWhiteSpace(item.profilePicture) ? null : item.profilePicture
                    };
                })
                .ToList();
        }

        private static string SerializeLineup(IEnumerable<TalentLineupItemDto> lineup) =>
            JsonSerializer.Serialize(lineup, LineupJsonOptions);

        private static List<TalentLineupItemDto> DeserializeLineup(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<TalentLineupItemDto>();

            try
            {
                return JsonSerializer.Deserialize<List<TalentLineupItemDto>>(raw, LineupJsonOptions) ?? new List<TalentLineupItemDto>();
            }
            catch
            {
                return new List<TalentLineupItemDto>();
            }
        }

        private static string BuildLineupDisplay(CreateEventDto req)
        {
            var linkedNames = NormalizeLineup(req.artistLineup, "Artist")
                .Concat(NormalizeLineup(req.sessionistLineup, "Sessionist"))
                .Select(item => item.displayName)
                .ToList();

            if (linkedNames.Count > 0)
            {
                return string.Join(", ", linkedNames);
            }

            return req.artists ?? "";
        }

        private static List<TalentLineupItemDto> MergeLineupMembers(IEnumerable<TalentLineupItemDto> artists, IEnumerable<TalentLineupItemDto> sessionists)
        {
            return artists.Concat(sessionists)
                .Where(item => item != null && item.id != Guid.Empty)
                .GroupBy(item => item.id)
                .Select(group => group.First())
                .ToList();
        }

        private static async Task NotifyLineupAddedAsync(NpgsqlConnection connection, Guid eventId, string eventTitle, IEnumerable<TalentLineupItemDto> lineup)
        {
            await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
            foreach (var item in lineup.Where(item => item.id != Guid.Empty))
            {
                await NotificationSupport.InsertNotificationIfNotExistsAsync(
                    connection,
                    item.id,
                    "lineup_added",
                    "Added to event lineup",
                    $"You were added to the lineup for '{eventTitle}'.",
                    eventId,
                    "event",
                    24);
            }
        }

        private static async Task NotifyLineupRemovedAsync(NpgsqlConnection connection, Guid userId, Guid eventId, string eventTitle)
        {
            await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
            await NotificationSupport.InsertNotificationAsync(
                connection,
                userId,
                "lineup_removed",
                "Removed from event lineup",
                $"You were removed from the lineup for '{eventTitle}'.",
                eventId,
                "event");
        }

        private async Task AutoFinishExpiredEvents(NpgsqlConnection connection, Guid? organizerId = null)
        {
            // Once the calendar day after the event starts, automatically treat it as finished.
            var sql = @"
                UPDATE events
                SET status = 'Finished'
                WHERE COALESCE(status, 'Upcoming') <> 'Finished'
                  AND event_time < @today";

            if (organizerId.HasValue)
            {
                sql += " AND organizer_id = @organizerId";
            }

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@today", DateTime.Today);
            if (organizerId.HasValue)
            {
                cmd.Parameters.AddWithValue("@organizerId", organizerId.Value);
            }
            await cmd.ExecuteNonQueryAsync();
        }

        [HttpDelete("{eventId}/lineup/{role}/{userId}")]
        public async Task<IActionResult> RemoveLineupMember(Guid eventId, string role, Guid userId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureVerifiedGigsTableExists(connection);
                await EnsureEventLineupColumnsOnce(connection);

                const string readSql = @"
                    SELECT COALESCE(artist_lineup, '[]'), COALESCE(sessionist_lineup, '[]'), COALESCE(title, 'This event')
                    FROM events
                    WHERE id = @id;";

                string artistLineupRaw = "[]";
                string sessionistLineupRaw = "[]";
                string eventTitle = "This event";
                using (var readCmd = new NpgsqlCommand(readSql, connection))
                {
                    readCmd.Parameters.AddWithValue("@id", eventId);
                    using var reader = await readCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Event not found." });
                    }

                    artistLineupRaw = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
                    sessionistLineupRaw = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                    eventTitle = reader.IsDBNull(2) ? "This event" : reader.GetString(2);
                }

                var normalizedRole = (role ?? string.Empty).Trim().ToLowerInvariant();
                var artistLineup = DeserializeLineup(artistLineupRaw);
                var sessionistLineup = DeserializeLineup(sessionistLineupRaw);
                int removedCount = 0;

                if (normalizedRole == "artist")
                {
                    removedCount = artistLineup.RemoveAll(item => item.id == userId);

                    const string deleteSql = "DELETE FROM event_artists WHERE event_id = @eventId AND artist_user_id = @userId;";
                    using var deleteCmd = new NpgsqlCommand(deleteSql, connection);
                    deleteCmd.Parameters.AddWithValue("@eventId", eventId);
                    deleteCmd.Parameters.AddWithValue("@userId", userId);
                    await deleteCmd.ExecuteNonQueryAsync();
                }
                else if (normalizedRole == "sessionist")
                {
                    removedCount = sessionistLineup.RemoveAll(item => item.id == userId);

                    const string deleteSql = "DELETE FROM event_sessionists WHERE event_id = @eventId AND sessionist_user_id = @userId;";
                    using var deleteCmd = new NpgsqlCommand(deleteSql, connection);
                    deleteCmd.Parameters.AddWithValue("@eventId", eventId);
                    deleteCmd.Parameters.AddWithValue("@userId", userId);
                    await deleteCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    return BadRequest(new { message = "Invalid lineup role." });
                }

                const string updateSql = @"
                    UPDATE events
                    SET artist_lineup = @artistLineup,
                        sessionist_lineup = @sessionistLineup,
                        artists = @artists
                    WHERE id = @id;";

                var mergedDisplay = artistLineup
                    .Concat(sessionistLineup)
                    .Select(item => item.displayName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();

                using (var updateCmd = new NpgsqlCommand(updateSql, connection))
                {
                    updateCmd.Parameters.AddWithValue("@artistLineup", SerializeLineup(artistLineup));
                    updateCmd.Parameters.AddWithValue("@sessionistLineup", SerializeLineup(sessionistLineup));
                    updateCmd.Parameters.AddWithValue("@artists", string.Join(", ", mergedDisplay));
                    updateCmd.Parameters.AddWithValue("@id", eventId);
                    await updateCmd.ExecuteNonQueryAsync();
                }

                if (removedCount > 0)
                {
                    await NotifyLineupRemovedAsync(connection, userId, eventId, eventTitle);
                }

                return Ok(new
                {
                    message = removedCount > 0 ? "Lineup member removed." : "Lineup was already up to date.",
                    artistLineup,
                    sessionistLineup
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to remove lineup member: " + ex.Message });
            }
        }

        // 1. CREATE EVENT
        [HttpPost("create")]
        [RequestSizeLimit(100_000_000)]
        public async Task<IActionResult> CreateEvent([FromBody] CreateEventDto req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var sanitizedTitle = SecuritySupport.SanitizePlainText(req.title, 180, false);
                var sanitizedArtists = SecuritySupport.SanitizePlainText(BuildLineupDisplay(req), 600, true);
                var sanitizedDescription = SecuritySupport.SanitizePlainText(req.description, 4000, true);
                var sanitizedCity = SecuritySupport.SanitizePlainText(req.city, 120, false);
                var sanitizedLocation = SecuritySupport.SanitizePlainText(req.location, 200, true);
                var sanitizedGenres = SecuritySupport.SanitizePlainText(req.genres, 300, false);
                var sanitizedEventType = SecuritySupport.SanitizePlainText(req.eventType, 80, false);
                var sanitizedTierName = SecuritySupport.SanitizePlainText(req.tierName, 120, false);
                var sanitizedBundles = SecuritySupport.SanitizePlainText(req.bundles, 1000, true);
                var sanitizedDiscounts = SecuritySupport.SanitizePlainText(req.discounts, 1000, true);
                var sanitizedSponsors = SecuritySupport.SanitizePlainText(req.sponsors, 1000, true);
                var sanitizedSaleName = SecuritySupport.SanitizePlainText(req.saleName, 120, false);
                var sanitizedSaleType = SecuritySupport.SanitizePlainText(req.saleType, 40, false);
                var sanitizedStatus = SecuritySupport.SanitizePlainText(req.status, 40, false);
                var normalizedStatus = string.Equals(sanitizedStatus, "Draft", StringComparison.OrdinalIgnoreCase) ? "Draft" : "Upcoming";
                var normalizedPoster = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.posterUrl, 3_500_000, out var posterError);
                if (posterError is not null)
                {
                    return BadRequest(new { message = posterError });
                }
                var normalizedArtistLineup = NormalizeLineup(req.artistLineup, "Artist");
                var normalizedSessionistLineup = NormalizeLineup(req.sessionistLineup, "Sessionist");
                var lineupDisplay = sanitizedArtists;
                var mergedLineup = MergeLineupMembers(normalizedArtistLineup, normalizedSessionistLineup);
                var eventId = Guid.NewGuid();

                string sql = @"
                    INSERT INTO events 
                    (id, organizer_id, title, artists, description, event_time, city, location, poster_url, base_price, total_slots, event_type, genres, tier_name, tier_price, tier_slots, bundles, discounts, sponsors, sale_name, sale_type, sale_value, sale_starts_at, sale_ends_at, status, artist_lineup, sessionist_lineup) 
                    VALUES 
                    (@id, @orgId, @title, @artists, @desc, @time, @city, @loc, @poster, @price, @slots, @eType, @genres, @tName, @tPrice, @tSlots, @bund, @disc, @spons, @saleName, @saleType, @saleValue, @saleStartsAt, @saleEndsAt, @status, @artistLineup, @sessionistLineup)";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", eventId);
                cmd.Parameters.AddWithValue("@orgId", req.organizerId);
                cmd.Parameters.AddWithValue("@title", sanitizedTitle);
                cmd.Parameters.AddWithValue("@artists", lineupDisplay);
                cmd.Parameters.AddWithValue("@desc", sanitizedDescription);
                cmd.Parameters.AddWithValue("@time", req.time);
                cmd.Parameters.AddWithValue("@city", string.IsNullOrWhiteSpace(sanitizedCity) ? DBNull.Value : sanitizedCity);
                cmd.Parameters.AddWithValue("@loc", sanitizedLocation);
                cmd.Parameters.AddWithValue("@poster", (object?)normalizedPoster ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@price", req.price);
                cmd.Parameters.AddWithValue("@slots", req.slots);
                
                cmd.Parameters.AddWithValue("@eType", string.IsNullOrWhiteSpace(sanitizedEventType) ? "Live Gig" : sanitizedEventType);
                cmd.Parameters.AddWithValue("@genres", sanitizedGenres);

                cmd.Parameters.AddWithValue("@tName", string.IsNullOrWhiteSpace(sanitizedTierName) ? DBNull.Value : sanitizedTierName);
                cmd.Parameters.AddWithValue("@tPrice", (object?)req.tierPrice ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tSlots", (object?)req.tierSlots ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bund", string.IsNullOrWhiteSpace(sanitizedBundles) ? DBNull.Value : sanitizedBundles);
                cmd.Parameters.AddWithValue("@disc", string.IsNullOrWhiteSpace(sanitizedDiscounts) ? DBNull.Value : sanitizedDiscounts);
                cmd.Parameters.AddWithValue("@spons", string.IsNullOrWhiteSpace(sanitizedSponsors) ? DBNull.Value : sanitizedSponsors);
                cmd.Parameters.AddWithValue("@saleName", string.IsNullOrWhiteSpace(sanitizedSaleName) ? DBNull.Value : sanitizedSaleName);
                cmd.Parameters.AddWithValue("@saleType", string.IsNullOrWhiteSpace(sanitizedSaleType) ? DBNull.Value : sanitizedSaleType);
                cmd.Parameters.AddWithValue("@saleValue", (object?)req.saleValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleStartsAt", (object?)PlatformFeatureSupport.NormalizeToUtc(req.saleStartsAt) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleEndsAt", (object?)PlatformFeatureSupport.NormalizeToUtc(req.saleEndsAt) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@status", normalizedStatus);
                cmd.Parameters.AddWithValue("@artistLineup", SerializeLineup(normalizedArtistLineup));
                cmd.Parameters.AddWithValue("@sessionistLineup", SerializeLineup(normalizedSessionistLineup));

                await cmd.ExecuteNonQueryAsync();
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    req.organizerId,
                    "Organizer",
                    "event_created",
                    "event",
                    eventId,
                    HttpContext,
                    $"Organizer created event '{sanitizedTitle}'.");
                await NotifyLineupAddedAsync(connection, eventId, req.title, mergedLineup);
                return Ok(new { message = normalizedStatus == "Draft" ? "Draft saved successfully!" : "Event successfully created!" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create event: " + ex.Message });
            }
        }

        // 2. GET ALL EVENTS FOR ORGANIZER
        [HttpGet("organizer/{orgId}")]
        public async Task<IActionResult> GetOrganizerEvents(Guid orgId)
        {
            try
            {
                var events = new List<EventDto>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await AutoFinishExpiredEvents(connection, orgId);

                const string sql = @"
                    SELECT
                        e.id,
                        e.title,
                        e.event_time,
                        e.city,
                        e.location,
                        e.base_price,
                        e.total_slots,
                        e.tickets_sold,
                        e.status,
                        e.event_type,
                        e.genres,
                        e.sale_name,
                        e.sale_type,
                        e.sale_value,
                        e.sale_starts_at,
                        e.sale_ends_at,
                        e.artist_lineup,
                        e.sessionist_lineup,
                        COALESCE((
                            SELECT SUM(COALESCE(t.quantity, 0))
                            FROM tickets t
                            WHERE t.event_id = e.id
                              AND COALESCE(t.is_used, FALSE) = TRUE
                        ), 0) AS attended_tickets
                    FROM events e
                    WHERE e.organizer_id = @orgId
                    ORDER BY e.event_time ASC";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@orgId", orgId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    events.Add(new EventDto
                    {
                        id = reader.GetGuid(0),
                        title = reader.GetString(1),
                        time = reader.GetDateTime(2),
                        city = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        location = reader.GetString(4),
                        price = reader.GetDecimal(5),
                        slots = reader.GetInt32(6),
                        ticketsSold = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                        attendedTickets = reader.IsDBNull(18) ? 0 : Convert.ToInt32(reader.GetInt64(18)),
                        status = reader.IsDBNull(8) ? "Upcoming" : reader.GetString(8),
                        eventType = reader.IsDBNull(9) ? "Live Gig" : reader.GetString(9),
                        genres = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        saleName = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        saleType = reader.IsDBNull(12) ? "" : reader.GetString(12),
                        saleValue = reader.IsDBNull(13) ? null : (decimal?)reader.GetDecimal(13),
                        saleStartsAt = reader.IsDBNull(14) ? null : (DateTime?)reader.GetDateTime(14),
                        saleEndsAt = reader.IsDBNull(15) ? null : (DateTime?)reader.GetDateTime(15),
                        artistLineup = DeserializeLineup(reader.IsDBNull(16) ? null : reader.GetString(16)),
                        sessionistLineup = DeserializeLineup(reader.IsDBNull(17) ? null : reader.GetString(17))
                    });
                }
                return Ok(events);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching events: " + ex.Message });
            }
        }

        [HttpGet("organizer/{orgId}/dashboard")]
        public async Task<IActionResult> GetOrganizerDashboard(Guid orgId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await AutoFinishExpiredEvents(connection, orgId);

                var now = DateTime.Now;
                var next30Days = now.AddDays(30);
                var startOfYear = new DateTime(now.Year, 1, 1);
                var startOfNextYear = startOfYear.AddYears(1);

                int upcomingEvents;
                int ticketsSold;
                int finishedEvents;
                decimal revenueYtd;
                var recentSales = new List<object>();

                const string statsSql = @"
                    SELECT
                        COUNT(*) FILTER (
                            WHERE status = 'Upcoming'
                              AND event_time >= @now
                              AND event_time < @next30Days
                        ) AS upcoming_events,
                        COALESCE(SUM(COALESCE(tickets_sold, 0)), 0) AS tickets_sold,
                        COUNT(*) FILTER (WHERE status = 'Finished') AS finished_events
                    FROM events
                    WHERE organizer_id = @orgId;";

                using (var statsCmd = new NpgsqlCommand(statsSql, connection))
                {
                    statsCmd.Parameters.AddWithValue("@orgId", orgId);
                    statsCmd.Parameters.AddWithValue("@now", now);
                    statsCmd.Parameters.AddWithValue("@next30Days", next30Days);

                    using var reader = await statsCmd.ExecuteReaderAsync();
                    await reader.ReadAsync();
                    upcomingEvents = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    ticketsSold = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1));
                    finishedEvents = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                }

                const string revenueSql = @"
                    SELECT COALESCE(SUM(t.total_price), 0)
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    WHERE e.organizer_id = @orgId
                      AND t.purchase_date >= @startOfYear
                      AND t.purchase_date < @startOfNextYear;";

                using (var revenueCmd = new NpgsqlCommand(revenueSql, connection))
                {
                    revenueCmd.Parameters.AddWithValue("@orgId", orgId);
                    revenueCmd.Parameters.AddWithValue("@startOfYear", startOfYear);
                    revenueCmd.Parameters.AddWithValue("@startOfNextYear", startOfNextYear);

                    var result = await revenueCmd.ExecuteScalarAsync();
                    revenueYtd = result == null || result == DBNull.Value ? 0 : Convert.ToDecimal(result);
                }

                const string recentSalesSql = @"
                    SELECT e.title, t.purchase_date, t.payment_method, t.total_price
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    WHERE e.organizer_id = @orgId
                    ORDER BY t.purchase_date DESC
                    LIMIT 5;";

                using (var recentSalesCmd = new NpgsqlCommand(recentSalesSql, connection))
                {
                    recentSalesCmd.Parameters.AddWithValue("@orgId", orgId);

                    using var reader = await recentSalesCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        recentSales.Add(new
                        {
                            eventTitle = reader.IsDBNull(0) ? "Untitled Event" : reader.GetString(0),
                            purchaseDate = reader.IsDBNull(1) ? now : reader.GetDateTime(1),
                            paymentMethod = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
                            totalPrice = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3)
                        });
                    }
                }

                return Ok(new
                {
                    upcomingEvents,
                    ticketsSold,
                    revenueYtd,
                    finishedEvents,
                    recentSales
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching organizer dashboard: " + ex.Message });
            }
        }

        [HttpGet("organizer/{orgId}/attendees")]
        public async Task<IActionResult> GetOrganizerAttendees(Guid orgId)
        {
            try
            {
                var attendees = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await AutoFinishExpiredEvents(connection, orgId);

                const string sql = @"
                    SELECT
                        t.id,
                        e.title,
                        COALESCE(u.firstname, ''),
                        COALESCE(u.lastname, ''),
                        COALESCE(u.email, ''),
                        COALESCE(t.tier_name, 'General Admission'),
                        COALESCE(t.quantity, 0),
                        COALESCE(t.total_price, 0),
                        t.purchase_date,
                        COALESCE(t.payment_method, 'Unknown'),
                        COALESCE(t.is_used, FALSE)
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    LEFT JOIN users u ON u.id = t.customer_id
                    WHERE e.organizer_id = @orgId
                    ORDER BY t.purchase_date DESC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@orgId", orgId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var firstName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var lastName = reader.IsDBNull(3) ? "" : reader.GetString(3);

                    attendees.Add(new
                    {
                        ticketId = reader.GetGuid(0),
                        eventTitle = reader.IsDBNull(1) ? "Untitled Event" : reader.GetString(1),
                        attendeeName = $"{firstName} {lastName}".Trim(),
                        email = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        tierName = reader.IsDBNull(5) ? "General Admission" : reader.GetString(5),
                        quantity = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                        totalPrice = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7),
                        purchaseDate = reader.IsDBNull(8) ? DateTime.Now : reader.GetDateTime(8),
                        paymentMethod = reader.IsDBNull(9) ? "Unknown" : reader.GetString(9),
                        isUsed = !reader.IsDBNull(10) && reader.GetBoolean(10)
                    });
                }

                return Ok(attendees);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching attendees: " + ex.Message });
            }
        }

        [HttpGet("organizer/{orgId}/payouts")]
        public async Task<IActionResult> GetOrganizerPayouts(Guid orgId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await AutoFinishExpiredEvents(connection, orgId);

                decimal totalRevenue = 0;
                int totalTickets = 0;
                int paidTransactions = 0;
                var eventBreakdown = new List<object>();

                const string totalsSql = @"
                    SELECT
                        COALESCE(SUM(t.total_price), 0) AS total_revenue,
                        COALESCE(SUM(t.quantity), 0) AS total_tickets,
                        COUNT(*) AS transactions
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    WHERE e.organizer_id = @orgId;";

                using (var totalsCmd = new NpgsqlCommand(totalsSql, connection))
                {
                    totalsCmd.Parameters.AddWithValue("@orgId", orgId);

                    using var reader = await totalsCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        totalRevenue = reader.IsDBNull(0) ? 0 : reader.GetDecimal(0);
                        totalTickets = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1));
                        paidTransactions = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2));
                    }
                }

                const string eventsSql = @"
                    SELECT
                        e.id,
                        e.title,
                        COALESCE(e.status, 'Upcoming'),
                        COALESCE(SUM(t.total_price), 0) AS gross_revenue,
                        COALESCE(SUM(t.quantity), 0) AS tickets_sold,
                        COUNT(t.id) AS transactions
                    FROM events e
                    LEFT JOIN tickets t ON t.event_id = e.id
                    WHERE e.organizer_id = @orgId
                    GROUP BY e.id, e.title, e.status, e.event_time
                    ORDER BY gross_revenue DESC, e.event_time ASC;";

                using (var eventsCmd = new NpgsqlCommand(eventsSql, connection))
                {
                    eventsCmd.Parameters.AddWithValue("@orgId", orgId);

                    using var reader = await eventsCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        eventBreakdown.Add(new
                        {
                            eventId = reader.GetGuid(0),
                            eventTitle = reader.IsDBNull(1) ? "Untitled Event" : reader.GetString(1),
                            status = reader.IsDBNull(2) ? "Upcoming" : reader.GetString(2),
                            grossRevenue = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3),
                            ticketsSold = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetInt64(4)),
                            transactions = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetInt64(5))
                        });
                    }
                }

                return Ok(new
                {
                    totalRevenue,
                    totalTickets,
                    paidTransactions,
                    eventBreakdown
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching payout data: " + ex.Message });
            }
        }

        [HttpGet("organizer/{orgId}/analytics/{eventId}")]
        public async Task<IActionResult> GetOrganizerEventAnalytics(Guid orgId, Guid eventId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await AutoFinishExpiredEvents(connection, orgId);

                object? summary = null;
                var salesTimeline = new List<object>();
                var tierBreakdown = new List<object>();
                var recentTransactions = new List<object>();

                const string summarySql = @"
                    SELECT
                        e.id,
                        COALESCE(e.title, 'Untitled Event'),
                        COALESCE(e.status, 'Upcoming'),
                        e.event_time,
                        COALESCE(e.city, ''),
                        COALESCE(e.location, ''),
                        COALESCE(e.event_type, ''),
                        COALESCE(e.base_price, 0),
                        COALESCE(e.total_slots, 0),
                        COALESCE(e.tickets_sold, 0),
                        COALESCE(e.artist_lineup, '[]'),
                        COALESCE(e.sessionist_lineup, '[]'),
                        COALESCE(SUM(t.total_price), 0) AS gross_revenue,
                        COALESCE(SUM(t.quantity), 0) AS purchased_tickets,
                        COALESCE(SUM(CASE WHEN COALESCE(t.is_used, FALSE) THEN t.quantity ELSE 0 END), 0) AS used_tickets,
                        COUNT(t.id) AS transactions
                    FROM events e
                    LEFT JOIN tickets t ON t.event_id = e.id
                    WHERE e.id = @eventId
                      AND e.organizer_id = @orgId
                    GROUP BY
                        e.id,
                        e.title,
                        e.status,
                        e.event_time,
                        e.city,
                        e.location,
                        e.event_type,
                        e.base_price,
                        e.total_slots,
                        e.tickets_sold,
                        e.artist_lineup,
                        e.sessionist_lineup;";

                using (var summaryCmd = new NpgsqlCommand(summarySql, connection))
                {
                    summaryCmd.Parameters.AddWithValue("@eventId", eventId);
                    summaryCmd.Parameters.AddWithValue("@orgId", orgId);

                    using var reader = await summaryCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Event not found or does not belong to this organizer." });
                    }

                    var artistLineup = DeserializeLineup(reader.IsDBNull(10) ? "[]" : reader.GetString(10));
                    var sessionistLineup = DeserializeLineup(reader.IsDBNull(11) ? "[]" : reader.GetString(11));
                    var totalSlots = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
                    var soldTickets = reader.IsDBNull(13) ? 0 : Convert.ToInt32(reader.GetInt64(13));
                    var usedTickets = reader.IsDBNull(14) ? 0 : Convert.ToInt32(reader.GetInt64(14));
                    var grossRevenue = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12);
                    var transactions = reader.IsDBNull(15) ? 0 : Convert.ToInt32(reader.GetInt64(15));
                    var remainingTickets = Math.Max(0, totalSlots - soldTickets);
                    var attendanceRate = soldTickets <= 0 ? 0 : Math.Round((decimal)usedTickets / soldTickets * 100m, 1);
                    var sellThroughRate = totalSlots <= 0 ? 0 : Math.Round((decimal)soldTickets / totalSlots * 100m, 1);
                    var averageOrderValue = transactions <= 0 ? 0 : Math.Round(grossRevenue / transactions, 2);

                    summary = new
                    {
                        eventId = reader.GetGuid(0),
                        title = reader.IsDBNull(1) ? "Untitled Event" : reader.GetString(1),
                        status = reader.IsDBNull(2) ? "Upcoming" : reader.GetString(2),
                        eventTime = reader.IsDBNull(3) ? DateTime.Now : reader.GetDateTime(3),
                        city = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        location = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        eventType = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        ticketPrice = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7),
                        totalSlots,
                        ticketsSold = soldTickets,
                        remainingTickets,
                        usedTickets,
                        grossRevenue,
                        transactions,
                        attendanceRate,
                        sellThroughRate,
                        averageOrderValue,
                        artistCount = artistLineup.Count,
                        sessionistCount = sessionistLineup.Count
                    };
                }

                const string timelineSql = @"
                    SELECT
                        DATE_TRUNC('day', purchase_date) AS sale_day,
                        COALESCE(SUM(quantity), 0) AS tickets_sold,
                        COALESCE(SUM(total_price), 0) AS revenue
                    FROM tickets
                    WHERE event_id = @eventId
                    GROUP BY sale_day
                    ORDER BY sale_day ASC;";

                using (var timelineCmd = new NpgsqlCommand(timelineSql, connection))
                {
                    timelineCmd.Parameters.AddWithValue("@eventId", eventId);

                    using var reader = await timelineCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        salesTimeline.Add(new
                        {
                            day = reader.IsDBNull(0) ? DateTime.Now.Date : reader.GetDateTime(0),
                            ticketsSold = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
                            revenue = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2)
                        });
                    }
                }

                const string tiersSql = @"
                    SELECT
                        COALESCE(tier_name, 'General Admission') AS tier_name,
                        COALESCE(SUM(quantity), 0) AS tickets_sold,
                        COALESCE(SUM(total_price), 0) AS revenue,
                        COALESCE(SUM(CASE WHEN COALESCE(is_used, FALSE) THEN quantity ELSE 0 END), 0) AS used_tickets
                    FROM tickets
                    WHERE event_id = @eventId
                    GROUP BY COALESCE(tier_name, 'General Admission')
                    ORDER BY revenue DESC, tickets_sold DESC, tier_name ASC;";

                using (var tiersCmd = new NpgsqlCommand(tiersSql, connection))
                {
                    tiersCmd.Parameters.AddWithValue("@eventId", eventId);

                    using var reader = await tiersCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        tierBreakdown.Add(new
                        {
                            tierName = reader.IsDBNull(0) ? "General Admission" : reader.GetString(0),
                            ticketsSold = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
                            revenue = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2),
                            usedTickets = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3))
                        });
                    }
                }

                const string recentTransactionsSql = @"
                    SELECT
                        COALESCE(u.firstname, ''),
                        COALESCE(u.lastname, ''),
                        COALESCE(u.email, ''),
                        COALESCE(t.tier_name, 'General Admission'),
                        COALESCE(t.quantity, 0),
                        COALESCE(t.total_price, 0),
                        t.purchase_date,
                        COALESCE(t.payment_method, 'Unknown'),
                        COALESCE(t.is_used, FALSE)
                    FROM tickets t
                    LEFT JOIN users u ON u.id = t.customer_id
                    WHERE t.event_id = @eventId
                    ORDER BY t.purchase_date DESC
                    LIMIT 8;";

                using (var transactionsCmd = new NpgsqlCommand(recentTransactionsSql, connection))
                {
                    transactionsCmd.Parameters.AddWithValue("@eventId", eventId);

                    using var reader = await transactionsCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var firstName = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        var lastName = reader.IsDBNull(1) ? "" : reader.GetString(1);

                        recentTransactions.Add(new
                        {
                            attendeeName = $"{firstName} {lastName}".Trim(),
                            email = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            tierName = reader.IsDBNull(3) ? "General Admission" : reader.GetString(3),
                            quantity = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                            totalPrice = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                            purchaseDate = reader.IsDBNull(6) ? DateTime.Now : reader.GetDateTime(6),
                            paymentMethod = reader.IsDBNull(7) ? "Unknown" : reader.GetString(7),
                            isUsed = !reader.IsDBNull(8) && reader.GetBoolean(8)
                        });
                    }
                }

                return Ok(new
                {
                    summary,
                    salesTimeline,
                    tierBreakdown,
                    recentTransactions
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching organizer event analytics: " + ex.Message });
            }
        }

        // 3. DELETE EVENT
        [HttpDelete("{eventId}")]
        public async Task<IActionResult> DeleteEvent(Guid eventId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                string sql = "DELETE FROM events WHERE id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", eventId);

                int rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return NotFound(new { message = "Event not found." });

                return Ok(new { message = "Event deleted." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error deleting event: " + ex.Message });
            }
        }

        // 4. FINISH EVENT
        [HttpPost("{eventId}/finish")]
        public async Task<IActionResult> FinishEvent(Guid eventId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureVerifiedGigsTableExists(connection);

                string updateSql = "UPDATE events SET status = 'Finished' WHERE id = @id RETURNING title, base_price, tickets_sold, total_slots";
                using var cmd = new NpgsqlCommand(updateSql, connection);
                cmd.Parameters.AddWithValue("@id", eventId);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    string title = reader.GetString(0);
                    decimal price = reader.GetDecimal(1);
                    int sold = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    int capacity = reader.GetInt32(3);
                    decimal totalRevenue = price * sold;
                    await reader.CloseAsync();
                    await CreateVerifiedGigRecords(connection, eventId);

                    return Ok(new { 
                        message = "Event finished.",
                        report = new { eventName = title, ticketPrice = price, ticketsSold = sold, totalCapacity = capacity, grossRevenue = totalRevenue }
                    });
                }
                return NotFound(new { message = "Event not found." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error finishing event: " + ex.Message });
            }
        }

        // 5. GET ALL PUBLIC EVENTS (For Events.html)
        [HttpGet("all")]
        public async Task<IActionResult> GetAllEvents()
        {
            try
            {
                var events = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                string sql = @"
                    SELECT e.id,
                           e.title,
                           e.event_time,
                           e.city,
                           e.location,
                           e.base_price,
                           e.poster_url,
                           e.event_type,
                           e.genres,
                           e.organizer_id,
                           COALESCE(NULLIF(u.productionname, ''), NULLIF(TRIM(COALESCE(u.firstname, '') || ' ' || COALESCE(u.lastname, '')), ''), 'Unknown Organizer'),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.is_verified, FALSE)
                    FROM events e
                    LEFT JOIN users u ON u.id = e.organizer_id
                    WHERE COALESCE(e.status, 'Upcoming') = 'Upcoming'
                      AND e.event_time >= CURRENT_DATE
                    ORDER BY e.event_time ASC
                    LIMIT 12";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    events.Add(new
                    {
                        id = reader.GetGuid(0),
                        title = reader.GetString(1),
                        time = reader.GetDateTime(2),
                        city = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        location = reader.GetString(4),
                        price = reader.GetDecimal(5),
                        posterUrl = reader.IsDBNull(6) ? "https://images.unsplash.com/photo-1514525253161-7a46d19cd819?auto=format&fit=crop&q=80&w=1200" : reader.GetString(6),
                        eventType = reader.IsDBNull(7) ? "Live Gig" : reader.GetString(7),
                        genres = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        organizerId = reader.IsDBNull(9) ? Guid.Empty : reader.GetGuid(9),
                        organizerName = reader.IsDBNull(10) ? "Unknown Organizer" : reader.GetString(10),
                        organizerProfilePicture = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        organizerVerified = !reader.IsDBNull(12) && reader.GetBoolean(12)
                    });
                }
                return Ok(events);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching events: " + ex.Message });
            }
        }

        // 5.b GET 6 EVENTS FOR LANDING PAGE CAROUSEL (NEW FIX!)
        [HttpGet("landing")]
        public async Task<IActionResult> GetLandingEvents()
        {
            try
            {
                var events = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                string sql = @"
                    SELECT e.id,
                           e.title,
                           e.event_time,
                           e.city,
                           e.location,
                           e.base_price,
                           e.poster_url,
                           e.event_type,
                           e.genres,
                           e.organizer_id,
                           COALESCE(NULLIF(u.productionname, ''), NULLIF(TRIM(COALESCE(u.firstname, '') || ' ' || COALESCE(u.lastname, '')), ''), 'Unknown Organizer'),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.is_verified, FALSE)
                    FROM events e
                    LEFT JOIN users u ON u.id = e.organizer_id
                    WHERE COALESCE(e.status, 'Upcoming') = 'Upcoming'
                      AND e.event_time >= CURRENT_DATE
                    ORDER BY e.event_time ASC
                    LIMIT 6";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    events.Add(new
                    {
                        id = reader.GetGuid(0),
                        title = reader.GetString(1),
                        time = reader.GetDateTime(2),
                        city = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        location = reader.GetString(4),
                        price = reader.GetDecimal(5),
                        posterUrl = reader.IsDBNull(6) ? "https://images.unsplash.com/photo-1514525253161-7a46d19cd819?auto=format&fit=crop&q=80&w=1200" : reader.GetString(6),
                        eventType = reader.IsDBNull(7) ? "Live Gig" : reader.GetString(7),
                        genres = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        organizerId = reader.IsDBNull(9) ? Guid.Empty : reader.GetGuid(9),
                        organizerName = reader.IsDBNull(10) ? "Unknown Organizer" : reader.GetString(10),
                        organizerProfilePicture = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        organizerVerified = !reader.IsDBNull(12) && reader.GetBoolean(12)
                    });
                }
                return Ok(events);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching events: " + ex.Message });
            }
        }

        // 6. GET SINGLE EVENT DETAILS
        [HttpGet("{id}")]
        public async Task<IActionResult> GetEventById(Guid id)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);

                string sql = @"
                    SELECT e.id,
                           e.title,
                           e.artists,
                           e.description,
                           e.event_time,
                           e.city,
                           e.location,
                           e.poster_url,
                           e.base_price,
                           e.total_slots,
                           e.tickets_sold,
                           e.status,
                           e.event_type,
                           e.genres,
                           e.tier_name,
                           e.tier_price,
                           e.tier_slots,
                           e.bundles,
                           e.sale_name,
                           e.sale_type,
                           e.sale_value,
                           e.sale_starts_at,
                           e.sale_ends_at,
                           e.artist_lineup,
                           e.sessionist_lineup,
                           e.organizer_id,
                           COALESCE(NULLIF(u.productionname, ''), NULLIF(TRIM(COALESCE(u.firstname, '') || ' ' || COALESCE(u.lastname, '')), ''), 'Unknown Organizer'),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.bio, ''),
                           COALESCE(u.is_verified, FALSE)
                    FROM events e
                    LEFT JOIN users u ON u.id = e.organizer_id
                    WHERE e.id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
                if (await reader.ReadAsync())
                {
                    return Ok(new
                    {
                        id = reader.GetGuid(0),
                        title = reader.IsDBNull(1) ? "Untitled Event" : reader.GetString(1),
                        artists = reader.IsDBNull(2) ? "TBA" : reader.GetString(2),
                        description = reader.IsDBNull(3) ? "No description available." : reader.GetString(3),
                        time = reader.GetDateTime(4),
                        city = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        location = reader.IsDBNull(6) ? "TBA" : reader.GetString(6),
                        posterUrl = reader.IsDBNull(7) ? "https://images.unsplash.com/photo-1492684223066-81342ee5ff30?auto=format&fit=crop&q=80&w=1600" : reader.GetString(7),
                        price = reader.IsDBNull(8) ? 0 : reader.GetDecimal(8),
                        totalSlots = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                        ticketsSold = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                        status = reader.IsDBNull(11) ? "Upcoming" : reader.GetString(11),
                        eventType = reader.IsDBNull(12) ? "Live Gig" : reader.GetString(12),
                        genres = reader.IsDBNull(13) ? "" : reader.GetString(13),
                        
                        tierName = reader.IsDBNull(14) ? null : reader.GetString(14),
                        tierPrice = reader.IsDBNull(15) ? null : (decimal?)reader.GetDecimal(15),
                        tierSlots = reader.IsDBNull(16) ? null : (int?)reader.GetInt32(16),
                        bundles = reader.IsDBNull(17) ? null : reader.GetString(17),
                        saleName = reader.IsDBNull(18) ? null : reader.GetString(18),
                        saleType = reader.IsDBNull(19) ? null : reader.GetString(19),
                        saleValue = reader.IsDBNull(20) ? null : (decimal?)reader.GetDecimal(20),
                        saleStartsAt = reader.IsDBNull(21) ? null : (DateTime?)reader.GetDateTime(21),
                        saleEndsAt = reader.IsDBNull(22) ? null : (DateTime?)reader.GetDateTime(22),
                        artistLineup = DeserializeLineup(reader.IsDBNull(23) ? null : reader.GetString(23)),
                        sessionistLineup = DeserializeLineup(reader.IsDBNull(24) ? null : reader.GetString(24)),
                        organizerId = reader.IsDBNull(25) ? Guid.Empty : reader.GetGuid(25),
                        organizerName = reader.IsDBNull(26) ? "Unknown Organizer" : reader.GetString(26),
                        organizerProfilePicture = reader.IsDBNull(27) ? "" : reader.GetString(27),
                        organizerBio = reader.IsDBNull(28) ? "" : reader.GetString(28),
                        organizerVerified = !reader.IsDBNull(29) && reader.GetBoolean(29)
                    });
                }
                return NotFound(new { message = "Event not found." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching event details: " + ex.Message });
            }
        }

        // 7. UPDATE EVENT
        [HttpPut("{id}")]
        [RequestSizeLimit(100_000_000)]
        public async Task<IActionResult> UpdateEvent(Guid id, [FromBody] CreateEventDto req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var sanitizedTitle = SecuritySupport.SanitizePlainText(req.title, 180, false);
                var sanitizedArtists = SecuritySupport.SanitizePlainText(BuildLineupDisplay(req), 600, true);
                var sanitizedDescription = SecuritySupport.SanitizePlainText(req.description, 4000, true);
                var sanitizedCity = SecuritySupport.SanitizePlainText(req.city, 120, false);
                var sanitizedLocation = SecuritySupport.SanitizePlainText(req.location, 200, true);
                var sanitizedGenres = SecuritySupport.SanitizePlainText(req.genres, 300, false);
                var sanitizedEventType = SecuritySupport.SanitizePlainText(req.eventType, 80, false);
                var sanitizedTierName = SecuritySupport.SanitizePlainText(req.tierName, 120, false);
                var sanitizedBundles = SecuritySupport.SanitizePlainText(req.bundles, 1000, true);
                var sanitizedDiscounts = SecuritySupport.SanitizePlainText(req.discounts, 1000, true);
                var sanitizedSponsors = SecuritySupport.SanitizePlainText(req.sponsors, 1000, true);
                var sanitizedSaleName = SecuritySupport.SanitizePlainText(req.saleName, 120, false);
                var sanitizedSaleType = SecuritySupport.SanitizePlainText(req.saleType, 40, false);
                var sanitizedStatus = SecuritySupport.SanitizePlainText(req.status, 40, false);
                var normalizedStatus = string.Equals(sanitizedStatus, "Draft", StringComparison.OrdinalIgnoreCase) ? "Draft" : "Upcoming";
                var normalizedPoster = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.posterUrl, 3_500_000, out var posterError);
                if (posterError is not null)
                {
                    return BadRequest(new { message = posterError });
                }

                var previousArtistLineup = new List<TalentLineupItemDto>();
                var previousSessionistLineup = new List<TalentLineupItemDto>();
                const string existingSql = @"
                    SELECT COALESCE(artist_lineup, '[]'),
                           COALESCE(sessionist_lineup, '[]'),
                           COALESCE(title, 'This event')
                    FROM events
                    WHERE id = @id AND organizer_id = @orgId;";
                string previousTitle = "This event";
                using (var existingCmd = new NpgsqlCommand(existingSql, connection))
                {
                    existingCmd.Parameters.AddWithValue("@id", id);
                    existingCmd.Parameters.AddWithValue("@orgId", req.organizerId);
                    using var reader = await existingCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Event not found or you don't have permission to edit it." });
                    }

                    previousArtistLineup = DeserializeLineup(reader.IsDBNull(0) ? null : reader.GetString(0));
                    previousSessionistLineup = DeserializeLineup(reader.IsDBNull(1) ? null : reader.GetString(1));
                    previousTitle = reader.IsDBNull(2) ? "This event" : reader.GetString(2);
                }

                var normalizedArtistLineup = NormalizeLineup(req.artistLineup, "Artist");
                var normalizedSessionistLineup = NormalizeLineup(req.sessionistLineup, "Sessionist");
                var lineupDisplay = sanitizedArtists;

                string sql = @"
                    UPDATE events SET 
                        title = @title, 
                        artists = @artists, 
                        description = @desc, 
                        event_time = @time, 
                        city = @city,
                        location = @loc, 
                        poster_url = COALESCE(@poster, poster_url), 
                        base_price = @price, 
                        total_slots = @slots, 
                        event_type = @eType, 
                        genres = @genres,
                        tier_name = @tName,
                        tier_price = @tPrice,
                        tier_slots = @tSlots,
                        bundles = @bund,
                        discounts = COALESCE(@disc, discounts),
                        sponsors = COALESCE(@spons, sponsors),
                        sale_name = COALESCE(@saleName, sale_name),
                        sale_type = COALESCE(@saleType, sale_type),
                        sale_value = COALESCE(@saleValue, sale_value),
                        sale_starts_at = COALESCE(@saleStartsAt, sale_starts_at),
                        sale_ends_at = COALESCE(@saleEndsAt, sale_ends_at),
                        status = @status,
                        artist_lineup = @artistLineup,
                        sessionist_lineup = @sessionistLineup
                    WHERE id = @id AND organizer_id = @orgId";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@orgId", req.organizerId); 
                
                cmd.Parameters.AddWithValue("@title", sanitizedTitle);
                cmd.Parameters.AddWithValue("@artists", lineupDisplay);
                cmd.Parameters.AddWithValue("@desc", sanitizedDescription);
                cmd.Parameters.AddWithValue("@time", req.time);
                cmd.Parameters.AddWithValue("@city", string.IsNullOrWhiteSpace(sanitizedCity) ? DBNull.Value : sanitizedCity);
                cmd.Parameters.AddWithValue("@loc", sanitizedLocation);
                cmd.Parameters.AddWithValue("@poster", (object?)normalizedPoster ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@price", req.price);
                cmd.Parameters.AddWithValue("@slots", req.slots);
                cmd.Parameters.AddWithValue("@eType", string.IsNullOrWhiteSpace(sanitizedEventType) ? "Live Gig" : sanitizedEventType);
                cmd.Parameters.AddWithValue("@genres", sanitizedGenres);
                
                cmd.Parameters.AddWithValue("@tName", string.IsNullOrWhiteSpace(sanitizedTierName) ? DBNull.Value : sanitizedTierName);
                cmd.Parameters.AddWithValue("@tPrice", (object?)req.tierPrice ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tSlots", (object?)req.tierSlots ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bund", string.IsNullOrWhiteSpace(sanitizedBundles) ? DBNull.Value : sanitizedBundles);
                
                cmd.Parameters.AddWithValue("@disc", string.IsNullOrWhiteSpace(sanitizedDiscounts) ? DBNull.Value : sanitizedDiscounts);
                cmd.Parameters.AddWithValue("@spons", string.IsNullOrWhiteSpace(sanitizedSponsors) ? DBNull.Value : sanitizedSponsors);
                cmd.Parameters.AddWithValue("@saleName", string.IsNullOrWhiteSpace(sanitizedSaleName) ? DBNull.Value : sanitizedSaleName);
                cmd.Parameters.AddWithValue("@saleType", string.IsNullOrWhiteSpace(sanitizedSaleType) ? DBNull.Value : sanitizedSaleType);
                cmd.Parameters.AddWithValue("@saleValue", (object?)req.saleValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleStartsAt", (object?)PlatformFeatureSupport.NormalizeToUtc(req.saleStartsAt) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleEndsAt", (object?)PlatformFeatureSupport.NormalizeToUtc(req.saleEndsAt) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@status", normalizedStatus);
                cmd.Parameters.AddWithValue("@artistLineup", SerializeLineup(normalizedArtistLineup));
                cmd.Parameters.AddWithValue("@sessionistLineup", SerializeLineup(normalizedSessionistLineup));

                int rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return NotFound(new { message = "Event not found or you don't have permission to edit it." });
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    req.organizerId,
                    "Organizer",
                    "event_updated",
                    "event",
                    id,
                    HttpContext,
                    $"Organizer updated event '{sanitizedTitle}'.");

                var previousIds = MergeLineupMembers(previousArtistLineup, previousSessionistLineup).Select(item => item.id).ToHashSet();
                var currentMembers = MergeLineupMembers(normalizedArtistLineup, normalizedSessionistLineup);
                var currentIds = currentMembers.Select(item => item.id).ToHashSet();

                var addedMembers = currentMembers.Where(item => !previousIds.Contains(item.id)).ToList();
                var removedIds = previousIds.Where(idValue => !currentIds.Contains(idValue)).ToList();

                if (addedMembers.Count > 0)
                {
                    await NotifyLineupAddedAsync(connection, id, req.title, addedMembers);
                }

                foreach (var removedId in removedIds)
                {
                    await NotifyLineupRemovedAsync(connection, removedId, id, string.IsNullOrWhiteSpace(req.title) ? previousTitle : req.title);
                }

                return Ok(new { message = normalizedStatus == "Draft" ? "Draft updated successfully!" : "Event successfully updated!" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update event: " + ex.Message });
            }
        }
    }
}
