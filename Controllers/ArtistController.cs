using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using ImajinationAPI.Services;
using System.Threading;

namespace ImajinationAPI.Controllers
{
    public class UpdateArtistProfileDto
    {
        public string? bio { get; set; }
        public string? genres { get; set; }
        public string? spotifyLink { get; set; }
        public string? profilePicture { get; set; } // Base64 string
        public bool? isAvailable { get; set; }
        public decimal? basePrice { get; set; }
    }

    public class UpdateAvailabilityDto
    {
        public bool isAvailable { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class ArtistController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly UploadScanningService _uploadScanningService;
        private static readonly SemaphoreSlim ArtistSchemaLock = new(1, 1);
        private static volatile bool _artistSchemaEnsured;

        public ArtistController(IConfiguration configuration, UploadScanningService uploadScanningService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _uploadScanningService = uploadScanningService;
        }

        private async Task EnsureEventLineupColumns(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE events ADD COLUMN IF NOT EXISTS artist_lineup text;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sessionist_lineup text;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureAvailabilityColumn(NpgsqlConnection connection)
        {
            const string sql = @"ALTER TABLE users ADD COLUMN IF NOT EXISTS is_available boolean NOT NULL DEFAULT TRUE;";
            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureTalentRegistrationColumns(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users ADD COLUMN IF NOT EXISTS talent_category VARCHAR(60);
                ALTER TABLE users ADD COLUMN IF NOT EXISTS member_names TEXT;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureTalentInteractionTables(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS customer_favorites (
                    id uuid PRIMARY KEY,
                    customer_id uuid NOT NULL,
                    target_user_id uuid NOT NULL,
                    target_role varchar(50) NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    CONSTRAINT uq_customer_favorite UNIQUE (customer_id, target_user_id, target_role)
                );

                CREATE TABLE IF NOT EXISTS talent_reviews (
                    id uuid PRIMARY KEY,
                    customer_id uuid NOT NULL,
                    target_user_id uuid NOT NULL,
                    target_role varchar(50) NOT NULL,
                    rating int NOT NULL,
                    feedback text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW(),
                    CONSTRAINT uq_customer_review UNIQUE (customer_id, target_user_id, target_role)
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureArtistSchemaOnce(NpgsqlConnection connection)
        {
            if (_artistSchemaEnsured) return;

            await ArtistSchemaLock.WaitAsync();
            try
            {
                if (_artistSchemaEnsured) return;

                await EnsureAvailabilityColumn(connection);
                await EnsureTalentRegistrationColumns(connection);
                await EnsureVerifiedGigsTableExists(connection);
                await EnsureTalentInteractionTables(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                _artistSchemaEnsured = true;
            }
            finally
            {
                ArtistSchemaLock.Release();
            }
        }

        // 1. GET ALL ARTISTS (For the Explore page & Organizer Create Event Dropdown)
        [HttpGet("all")]
        public async Task<IActionResult> GetAllArtists()
        {
            try
            {
                var artists = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureArtistSchemaOnce(connection);

                // Select only users who signed up with the 'Artist' role
                string sql = @"
                    SELECT
                        u.id,
                        u.stagename,
                        u.firstname,
                        u.lastname,
                        u.profile_picture,
                        u.genres,
                        COALESCE(u.is_available, TRUE),
                        COALESCE(u.is_verified, FALSE),
                        u.bio,
                        u.spotify_link,
                        u.talent_category,
                        u.member_names,
                        COALESCE(u.base_price, 0),
                        COALESCE(review_stats.average_rating, 0),
                        COALESCE(review_stats.review_count, 0)
                    FROM users u
                    LEFT JOIN (
                        SELECT
                            target_user_id,
                            ROUND(AVG(rating)::numeric, 1) AS average_rating,
                            COUNT(*) AS review_count
                        FROM talent_reviews
                        WHERE LOWER(COALESCE(target_role, '')) = 'artist'
                        GROUP BY target_user_id
                    ) review_stats ON review_stats.target_user_id = u.id
                    WHERE u.role = 'Artist'";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string stage = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string first = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string last = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    
                    artists.Add(new
                    {
                        id = reader.GetGuid(0),
                        // Use Stage Name if available, otherwise use First + Last name
                        displayName = !string.IsNullOrEmpty(stage) ? stage : $"{first} {last}".Trim(),
                        profilePicture = reader.IsDBNull(4) ? "https://images.unsplash.com/photo-1549834125-82d3c48159a3?auto=format&fit=crop&q=80&w=300" : reader.GetString(4),
                        genres = reader.IsDBNull(5) ? "Music" : reader.GetString(5),
                        isAvailable = reader.IsDBNull(6) || reader.GetBoolean(6),
                        isVerified = !reader.IsDBNull(7) && reader.GetBoolean(7),
                        talentCategory = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        memberNames = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        basePrice = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12),
                        averageRating = reader.IsDBNull(13) ? 0 : reader.GetDecimal(13),
                        reviewCount = reader.IsDBNull(14) ? 0 : Convert.ToInt32(reader.GetInt64(14)),
                        profileCompletionPercent = CommunitySupport.CalculateProfileCompletion(
                            "Artist",
                            first,
                            last,
                            reader.IsDBNull(8) ? "" : reader.GetString(8),
                            reader.IsDBNull(4) ? "" : reader.GetString(4),
                            reader.IsDBNull(5) ? "" : reader.GetString(5),
                            reader.IsDBNull(9) ? "" : reader.GetString(9),
                            "",
                            "",
                            "",
                            stage
                        ).Percent
                    });
                }
                return Ok(artists);
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // 2. GET SINGLE ARTIST DETAILS
        [HttpGet("{id}")]
        public async Task<IActionResult> GetArtistById(Guid id)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureArtistSchemaOnce(connection);

                string sql = "SELECT stagename, firstname, lastname, profile_picture, bio, genres, spotify_link, COALESCE(is_available, TRUE), COALESCE(is_verified, FALSE), talent_category, member_names, COALESCE(base_price, 0) FROM users WHERE id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    string stage = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    string first = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string last = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string displayName = !string.IsNullOrEmpty(stage) ? stage : $"{first} {last}".Trim();
                    string profilePicture = reader.IsDBNull(3) ? "https://images.unsplash.com/photo-1549834125-82d3c48159a3?auto=format&fit=crop&q=80&w=300" : reader.GetString(3);
                    string bio = reader.IsDBNull(4) ? "This artist hasn't written a bio yet." : reader.GetString(4);
                    string genres = reader.IsDBNull(5) ? "Music" : reader.GetString(5);
                    string spotifyLink = reader.IsDBNull(6) ? "" : reader.GetString(6);
                    bool isAvailable = reader.IsDBNull(7) || reader.GetBoolean(7);
                    bool isVerified = !reader.IsDBNull(8) && reader.GetBoolean(8);
                    string talentCategory = reader.IsDBNull(9) ? "" : reader.GetString(9);
                    string memberNames = reader.IsDBNull(10) ? "" : reader.GetString(10);
                    decimal basePrice = reader.IsDBNull(11) ? 0 : reader.GetDecimal(11);
                    var profileSummary = CommunitySupport.CalculateProfileCompletion("Artist", first, last, bio, profilePicture, genres, spotifyLink, "", "", "", stage);
                    await reader.CloseAsync();
                    await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Artist", first, last, bio, profilePicture, genres, spotifyLink, "", "", "", stage);

                    var relatedEvents = new List<object>();
                    const string relatedEventsSql = @"
                        SELECT id, title, event_time, city, location, COALESCE(status, 'Upcoming'), poster_url
                        FROM events
                        WHERE artist_lineup LIKE @needle
                        ORDER BY event_time DESC
                        LIMIT 8";

                    using var eventsCmd = new NpgsqlCommand(relatedEventsSql, connection);
                    eventsCmd.Parameters.AddWithValue("@needle", $"%{id}%");
                    eventsCmd.CommandTimeout = 5;

                    using var eventReader = await eventsCmd.ExecuteReaderAsync();
                    while (await eventReader.ReadAsync())
                    {
                        relatedEvents.Add(new
                        {
                            id = eventReader.GetGuid(0),
                            title = eventReader.IsDBNull(1) ? "Untitled Event" : eventReader.GetString(1),
                            time = eventReader.IsDBNull(2) ? DateTime.MinValue : eventReader.GetDateTime(2),
                            city = eventReader.IsDBNull(3) ? "" : eventReader.GetString(3),
                            location = eventReader.IsDBNull(4) ? "" : eventReader.GetString(4),
                            status = eventReader.IsDBNull(5) ? "Upcoming" : eventReader.GetString(5),
                            posterUrl = eventReader.IsDBNull(6) ? "" : eventReader.GetString(6)
                        });
                    }
                    await eventReader.CloseAsync();

                    var verifiedGigs = new List<object>();
                    const string verifiedGigsSql = @"
                        SELECT vg.id,
                               vg.verified_at,
                               COALESCE(vg.role_at_event, 'Artist'),
                               COALESCE(e.title, 'Untitled Event'),
                               e.event_time,
                               COALESCE(e.city, ''),
                               COALESCE(e.location, '')
                        FROM verified_gigs vg
                        LEFT JOIN events e ON e.id = vg.event_id
                        WHERE vg.user_id = @id
                        ORDER BY vg.verified_at DESC
                        LIMIT 10;";

                    using var verifiedCmd = new NpgsqlCommand(verifiedGigsSql, connection);
                    verifiedCmd.Parameters.AddWithValue("@id", id);
                    verifiedCmd.CommandTimeout = 5;

                    using var verifiedReader = await verifiedCmd.ExecuteReaderAsync();
                    while (await verifiedReader.ReadAsync())
                    {
                        verifiedGigs.Add(new
                        {
                            id = verifiedReader.GetGuid(0),
                            verifiedAt = verifiedReader.IsDBNull(1) ? DateTime.UtcNow : verifiedReader.GetDateTime(1),
                            roleAtEvent = verifiedReader.IsDBNull(2) ? "Artist" : verifiedReader.GetString(2),
                            title = verifiedReader.IsDBNull(3) ? "Untitled Event" : verifiedReader.GetString(3),
                            time = verifiedReader.IsDBNull(4) ? DateTime.MinValue : verifiedReader.GetDateTime(4),
                            city = verifiedReader.IsDBNull(5) ? "" : verifiedReader.GetString(5),
                            location = verifiedReader.IsDBNull(6) ? "" : verifiedReader.GetString(6)
                        });
                    }

                    return Ok(new
                    {
                        displayName,
                        profilePicture,
                        bio,
                        genres,
                        spotifyLink,
                        talentCategory,
                        memberNames,
                        basePrice,
                        isAvailable,
                        isVerified = profileSummary.IsVerified,
                        profileCompletionPercent = profileSummary.Percent,
                        profileCompletionLabel = profileSummary.Label,
                        relatedEvents,
                        verifiedGigCount = verifiedGigs.Count,
                        verifiedGigs
                    });
                }
                return NotFound(new { message = "Artist not found." });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // 3. UPDATE ARTIST PROFILE (Picture, Bio, etc.)
        [Authorize(Roles = "Artist")]
        [HttpPut("{id}/profile")]
        public async Task<IActionResult> UpdateProfile(Guid id, [FromBody] UpdateArtistProfileDto req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureArtistSchemaOnce(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var sanitizedBio = SecuritySupport.SanitizePlainText(req.bio, 2500, true);
                var sanitizedGenres = SecuritySupport.SanitizePlainText(req.genres, 400, false);
                var sanitizedSpotifyLink = SecuritySupport.SanitizeUrl(req.spotifyLink);
                var normalizedPicture = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.profilePicture, 2_500_000, out var imageError);
                if (imageError is not null)
                {
                    return BadRequest(new { message = imageError });
                }
                var pictureScan = await _uploadScanningService.ScanDataUrlAsync(normalizedPicture, "artist profile image");
                if (!pictureScan.IsClean)
                {
                    return BadRequest(new { message = pictureScan.Message });
                }

                string sql = @"
                    UPDATE users SET 
                        bio = @bio, 
                        genres = @genres, 
                        spotify_link = @spotify, 
                        profile_picture = COALESCE(@pic, profile_picture),
                        is_available = COALESCE(@isAvailable, is_available),
                        base_price = COALESCE(@basePrice, base_price)
                    WHERE id = @id AND role = 'Artist'";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@bio", string.IsNullOrWhiteSpace(sanitizedBio) ? DBNull.Value : sanitizedBio);
                cmd.Parameters.AddWithValue("@genres", string.IsNullOrWhiteSpace(sanitizedGenres) ? DBNull.Value : sanitizedGenres);
                cmd.Parameters.AddWithValue("@spotify", string.IsNullOrWhiteSpace(sanitizedSpotifyLink) ? DBNull.Value : sanitizedSpotifyLink);
                cmd.Parameters.AddWithValue("@pic", (object?)normalizedPicture ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@isAvailable", (object?)req.isAvailable ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@basePrice", (object?)req.basePrice ?? DBNull.Value);

                int rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return BadRequest(new { message = "Update failed. Make sure you are an Artist." });
                
                const string profileSql = @"
                    SELECT COALESCE(stagename, ''),
                           COALESCE(firstname, ''),
                           COALESCE(lastname, ''),
                           COALESCE(profile_picture, ''),
                           COALESCE(bio, ''),
                           COALESCE(genres, ''),
                           COALESCE(spotify_link, ''),
                           COALESCE(is_available, TRUE),
                           COALESCE(is_verified, FALSE),
                           COALESCE(base_price, 0)
                    FROM users
                    WHERE id = @id AND role = 'Artist';";

                string stage = "";
                string first = "";
                string last = "";
                string profilePicture = "";
                string bio = "";
                string genres = "";
                string spotifyLink = "";
                bool isAvailable = true;
                decimal basePrice = 0;

                using (var profileCmd = new NpgsqlCommand(profileSql, connection))
                {
                    profileCmd.Parameters.AddWithValue("@id", id);
                    profileCmd.CommandTimeout = 5;
                    using var reader = await profileCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        stage = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        first = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        last = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        profilePicture = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        bio = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        genres = reader.IsDBNull(5) ? "" : reader.GetString(5);
                        spotifyLink = reader.IsDBNull(6) ? "" : reader.GetString(6);
                        isAvailable = reader.IsDBNull(7) || reader.GetBoolean(7);
                        basePrice = reader.IsDBNull(9) ? 0 : reader.GetDecimal(9);
                    }
                }

                var profileSummary = CommunitySupport.CalculateProfileCompletion("Artist", first, last, bio, profilePicture, genres, spotifyLink, "", "", "", stage);
                await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Artist", first, last, bio, profilePicture, genres, spotifyLink, "", "", "", stage);
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    id,
                    "Artist",
                    "profile_updated",
                    "user",
                    id,
                    HttpContext,
                    "Artist profile updated.");

                return Ok(new
                {
                    message = "Profile updated successfully!",
                    isAvailable,
                    profilePicture,
                    basePrice,
                    isVerified = profileSummary.IsVerified,
                    profileCompletionPercent = profileSummary.Percent,
                    profileCompletionLabel = profileSummary.Label
                });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        [Authorize(Roles = "Artist")]
        [HttpPatch("{id}/availability")]
        public async Task<IActionResult> UpdateAvailability(Guid id, [FromBody] UpdateAvailabilityDto req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureArtistSchemaOnce(connection);

                const string sql = "UPDATE users SET is_available = @isAvailable WHERE id = @id AND role = 'Artist'";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@isAvailable", req.isAvailable);
                cmd.CommandTimeout = 5;

                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0)
                {
                    return NotFound(new { message = "Artist not found." });
                }

                return Ok(new { message = "Availability updated successfully.", isAvailable = req.isAvailable });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // GET 4 SPOTLIGHT ARTISTS FOR THE LANDING PAGE
        [HttpGet("spotlight")]
        public async Task<IActionResult> GetSpotlightArtists()
        {
            try
            {
                var artists = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureArtistSchemaOnce(connection);

                // LIMIT 4 to keep the landing page fast
                string sql = "SELECT id, stagename, firstname, lastname, profile_picture, genres, COALESCE(is_available, TRUE), COALESCE(is_verified, FALSE) FROM users WHERE role = 'Artist' LIMIT 4";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string stage = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string first = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string last = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    
                    artists.Add(new
                    {
                        id = reader.GetGuid(0),
                        displayName = !string.IsNullOrEmpty(stage) ? stage : $"{first} {last}".Trim(),
                        profilePicture = reader.IsDBNull(4) ? "https://images.unsplash.com/photo-1549834125-82d3c48159a3?auto=format&fit=crop&q=80&w=300" : reader.GetString(4),
                        genres = reader.IsDBNull(5) ? "Music" : reader.GetString(5),
                        isAvailable = reader.IsDBNull(6) || reader.GetBoolean(6),
                        isVerified = !reader.IsDBNull(7) && reader.GetBoolean(7)
                    });
                }
                return Ok(artists);
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        private static async Task EnsureVerifiedGigsTableExists(NpgsqlConnection connection)
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
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
