using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    public class ToggleFavoriteRequest
    {
        public Guid customerId { get; set; }
        public Guid targetUserId { get; set; }
        public string? targetRole { get; set; }
    }

    public class SubmitReviewRequest
    {
        public Guid customerId { get; set; }
        public Guid targetUserId { get; set; }
        public string? targetRole { get; set; }
        public int rating { get; set; }
        public string? feedback { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class TalentInteractionController : ControllerBase
    {
        private readonly string _connectionString;

        public TalentInteractionController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        private sealed class TalentReviewEligibilityState
        {
            public bool HasAnyBooking { get; init; }
            public bool HasConfirmedPastBooking { get; init; }
            public bool HasCompletedBooking { get; init; }
            public bool HasCompletedPaidBooking { get; init; }
            public bool CanReview => HasCompletedPaidBooking;
            public string Message { get; init; } = "Sign in as a Customer and complete your booking before reviews become available.";
        }

        [HttpGet("summary/{targetRole}/{targetUserId}")]
        public async Task<IActionResult> GetSummary(string targetRole, Guid targetUserId, [FromQuery] Guid? customerId)
        {
            try
            {
                var normalizedRole = NormalizeRole(targetRole);
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTablesExist(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                decimal averageRating = 0;
                int reviewCount = 0;
                int favoriteCount = 0;
                bool isFavorited = false;
                var eligibility = new TalentReviewEligibilityState();
                var reviews = new List<object>();

                const string statsSql = @"
                    SELECT
                        COALESCE(AVG(r.rating), 0),
                        COUNT(r.id),
                        (
                            SELECT COUNT(DISTINCT f.customer_id)
                            FROM customer_favorites f
                            WHERE f.target_user_id = @targetUserId
                              AND LOWER(TRIM(COALESCE(f.target_role, ''))) = LOWER(TRIM(@targetRole))
                        ) AS favorite_count,
                        EXISTS (
                            SELECT 1
                            FROM customer_favorites f
                            WHERE f.target_user_id = @targetUserId
                              AND LOWER(TRIM(COALESCE(f.target_role, ''))) = LOWER(TRIM(@targetRole))
                              AND f.customer_id = @customerId
                        ) AS is_favorited
                    FROM talent_reviews r
                    WHERE r.target_user_id = @targetUserId
                      AND r.target_role = @targetRole;";

                using (var statsCmd = new NpgsqlCommand(statsSql, connection))
                {
                    statsCmd.Parameters.Add("@targetUserId", NpgsqlDbType.Uuid).Value = targetUserId;
                    statsCmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = normalizedRole;
                    statsCmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = (object?)customerId ?? DBNull.Value;

                    using var reader = await statsCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        averageRating = reader.IsDBNull(0) ? 0 : reader.GetDecimal(0);
                        reviewCount = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1));
                        favoriteCount = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2));
                        isFavorited = !reader.IsDBNull(3) && reader.GetBoolean(3);
                    }
                }

                const string reviewSql = @"
                    SELECT
                        COALESCE(u.firstname, ''),
                        COALESCE(u.lastname, ''),
                        r.rating,
                        COALESCE(r.feedback, ''),
                        r.created_at
                    FROM talent_reviews r
                    LEFT JOIN users u ON u.id = r.customer_id
                    WHERE r.target_user_id = @targetUserId
                      AND r.target_role = @targetRole
                    ORDER BY r.updated_at DESC
                    LIMIT 8;";

                using (var reviewCmd = new NpgsqlCommand(reviewSql, connection))
                {
                    reviewCmd.Parameters.Add("@targetUserId", NpgsqlDbType.Uuid).Value = targetUserId;
                    reviewCmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = normalizedRole;

                    using var reader = await reviewCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var first = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        var last = reader.IsDBNull(1) ? "" : reader.GetString(1);

                        reviews.Add(new
                        {
                            customerName = $"{first} {last}".Trim(),
                            rating = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                            feedback = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            createdAt = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4)
                        });
                    }
                }

                if (customerId.HasValue && customerId.Value != Guid.Empty)
                {
                    eligibility = await GetReviewEligibilityAsync(connection, customerId.Value, targetUserId, normalizedRole);
                }

                return Ok(new
                {
                    averageRating = Math.Round(averageRating, 1),
                    reviewCount,
                    favoriteCount,
                    isFavorited,
                    canReview = eligibility.CanReview,
                    hasAnyBooking = eligibility.HasAnyBooking,
                    hasConfirmedPastBooking = eligibility.HasConfirmedPastBooking,
                    hasCompletedBooking = eligibility.HasCompletedBooking,
                    hasCompletedPaidBooking = eligibility.HasCompletedPaidBooking,
                    reviewEligibilityMessage = eligibility.Message,
                    reviews
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load interaction summary: " + ex.Message });
            }
        }

        [HttpPost("favorite")]
        public async Task<IActionResult> ToggleFavorite([FromBody] ToggleFavoriteRequest req)
        {
            try
            {
                if (req.customerId == Guid.Empty || req.targetUserId == Guid.Empty)
                {
                    return BadRequest(new { message = "Missing favorite details." });
                }

                var normalizedRole = NormalizeRole(req.targetRole);

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTablesExist(connection);

                const string checkSql = @"
                    SELECT id
                    FROM customer_favorites
                    WHERE customer_id = @customerId
                      AND target_user_id = @targetUserId
                      AND LOWER(TRIM(COALESCE(target_role, ''))) = LOWER(TRIM(@targetRole))
                    LIMIT 1;";

                var hasFavorite = false;
                using (var checkCmd = new NpgsqlCommand(checkSql, connection))
                {
                    checkCmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                    checkCmd.Parameters.Add("@targetUserId", NpgsqlDbType.Uuid).Value = req.targetUserId;
                    checkCmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = normalizedRole;

                    var result = await checkCmd.ExecuteScalarAsync();
                    hasFavorite = result is Guid;
                }

                if (hasFavorite)
                {
                    const string deleteSql = @"
                        DELETE FROM customer_favorites
                        WHERE customer_id = @customerId
                          AND target_user_id = @targetUserId
                          AND LOWER(TRIM(COALESCE(target_role, ''))) = LOWER(TRIM(@targetRole));";
                    using var deleteCmd = new NpgsqlCommand(deleteSql, connection);
                    deleteCmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                    deleteCmd.Parameters.Add("@targetUserId", NpgsqlDbType.Uuid).Value = req.targetUserId;
                    deleteCmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = normalizedRole;
                    await deleteCmd.ExecuteNonQueryAsync();

                    return Ok(new { message = "Removed from favorites.", isFavorited = false });
                }

                const string insertSql = @"
                    INSERT INTO customer_favorites (id, customer_id, target_user_id, target_role, created_at)
                    VALUES (@id, @customerId, @targetUserId, @targetRole, NOW())
                    ON CONFLICT DO NOTHING;";

                using (var insertCmd = new NpgsqlCommand(insertSql, connection))
                {
                    insertCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                    insertCmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                    insertCmd.Parameters.Add("@targetUserId", NpgsqlDbType.Uuid).Value = req.targetUserId;
                    insertCmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = normalizedRole;
                    await insertCmd.ExecuteNonQueryAsync();
                }

                try
                {
                    var customerName = await GetCustomerDisplayNameAsync(connection, req.customerId);
                    await NotificationSupport.InsertNotificationIfNotExistsAsync(
                        connection,
                        req.targetUserId,
                        "favorite_added",
                        "New favorite",
                        $"{customerName} added your profile to favorites.",
                        req.customerId,
                        "user",
                        12);
                }
                catch
                {
                    // Keep favorite toggles working even if notification writing fails.
                }

                return Ok(new { message = "Added to favorites.", isFavorited = true });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update favorites: " + ex.Message });
            }
        }

        [HttpPost("review")]
        public async Task<IActionResult> SubmitReview([FromBody] SubmitReviewRequest req)
        {
            try
            {
                if (req.customerId == Guid.Empty || req.targetUserId == Guid.Empty)
                {
                    return BadRequest(new { message = "Missing review details." });
                }

                if (req.rating < 1 || req.rating > 5)
                {
                    return BadRequest(new { message = "Rating must be between 1 and 5." });
                }

                var normalizedRole = NormalizeRole(req.targetRole);
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTablesExist(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                var eligibility = await GetReviewEligibilityAsync(connection, req.customerId, req.targetUserId, normalizedRole);
                if (!eligibility.CanReview)
                {
                    return BadRequest(new { message = eligibility.Message });
                }

                const string upsertSql = @"
                    INSERT INTO talent_reviews (id, customer_id, target_user_id, target_role, rating, feedback, created_at, updated_at)
                    VALUES (@id, @customerId, @targetUserId, @targetRole, @rating, @feedback, NOW(), NOW())
                    ON CONFLICT (customer_id, target_user_id, target_role)
                    DO UPDATE SET
                        rating = EXCLUDED.rating,
                        feedback = EXCLUDED.feedback,
                        updated_at = NOW();";

                using var cmd = new NpgsqlCommand(upsertSql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                cmd.Parameters.Add("@targetUserId", NpgsqlDbType.Uuid).Value = req.targetUserId;
                cmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = normalizedRole;
                cmd.Parameters.Add("@rating", NpgsqlDbType.Integer).Value = req.rating;
                cmd.Parameters.Add("@feedback", NpgsqlDbType.Text).Value = (object?)req.feedback ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();

                var customerName = await GetCustomerDisplayNameAsync(connection, req.customerId);
                await NotificationSupport.InsertNotificationAsync(
                    connection,
                    req.targetUserId,
                    "review_submitted",
                    "New review",
                    $"{customerName} left a {req.rating}-star review on your profile.",
                    req.customerId,
                    "user");

                return Ok(new { message = "Feedback saved successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to save feedback: " + ex.Message });
            }
        }

        private static string NormalizeRole(string? value)
        {
            return string.Equals(value, "Sessionist", StringComparison.OrdinalIgnoreCase) ? "Sessionist" : "Artist";
        }

        private static async Task<TalentReviewEligibilityState> GetReviewEligibilityAsync(NpgsqlConnection connection, Guid customerId, Guid targetUserId, string normalizedRole)
        {
            const string sql = @"
                SELECT EXISTS (
                           SELECT 1
                           FROM bookings
                           WHERE customer_id = @customerId
                             AND target_user_id = @targetUserId
                             AND LOWER(COALESCE(target_role, '')) = LOWER(@targetRole)
                       ),
                       EXISTS (
                           SELECT 1
                           FROM bookings
                           WHERE customer_id = @customerId
                             AND target_user_id = @targetUserId
                             AND LOWER(COALESCE(target_role, '')) = LOWER(@targetRole)
                             AND LOWER(COALESCE(status, '')) = 'confirmed'
                             AND event_date IS NOT NULL
                             AND event_date <= NOW()
                       ),
                       EXISTS (
                           SELECT 1
                           FROM bookings
                           WHERE customer_id = @customerId
                             AND target_user_id = @targetUserId
                             AND LOWER(COALESCE(target_role, '')) = LOWER(@targetRole)
                             AND LOWER(COALESCE(status, '')) = 'completed'
                             AND event_date IS NOT NULL
                             AND event_date <= NOW()
                       ),
                       EXISTS (
                           SELECT 1
                           FROM bookings
                           WHERE customer_id = @customerId
                             AND target_user_id = @targetUserId
                             AND LOWER(COALESCE(target_role, '')) = LOWER(@targetRole)
                             AND LOWER(COALESCE(status, '')) = 'completed'
                             AND event_date IS NOT NULL
                             AND event_date <= NOW()
                             AND LOWER(COALESCE(service_fee_status, COALESCE(payment_status, 'unpaid'))) IN ('paid', 'notrequired')
                             AND (
                                 COALESCE(budget, 0) <= 0
                                 OR LOWER(COALESCE(talent_fee_status, 'unpaid')) = 'paid'
                             )
                       );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = customerId;
            cmd.Parameters.Add("@targetUserId", NpgsqlDbType.Uuid).Value = targetUserId;
            cmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = normalizedRole;

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new TalentReviewEligibilityState
                {
                    Message = "We could not verify your booking status for reviews right now."
                };
            }

            var hasAnyBooking = !reader.IsDBNull(0) && reader.GetBoolean(0);
            var hasConfirmedPastBooking = !reader.IsDBNull(1) && reader.GetBoolean(1);
            var hasCompletedBooking = !reader.IsDBNull(2) && reader.GetBoolean(2);
            var hasCompletedPaidBooking = !reader.IsDBNull(3) && reader.GetBoolean(3);

            var message = hasCompletedPaidBooking
                ? "Your booking with this talent is completed and settled, so you can leave a review now."
                : hasCompletedBooking
                    ? "Your booking is marked completed, but the transaction still needs to be fully settled before reviews unlock."
                    : hasConfirmedPastBooking
                        ? "Your booking date has passed, but reviews unlock only after the booking is marked completed and settled."
                        : hasAnyBooking
                            ? "Reviews unlock only after your booking with this talent is fully completed and settled."
                            : "No eligible completed booking with this talent was found on this customer account yet.";

            return new TalentReviewEligibilityState
            {
                HasAnyBooking = hasAnyBooking,
                HasConfirmedPastBooking = hasConfirmedPastBooking,
                HasCompletedBooking = hasCompletedBooking,
                HasCompletedPaidBooking = hasCompletedPaidBooking,
                Message = message
            };
        }

        private static async Task<string> GetCustomerDisplayNameAsync(NpgsqlConnection connection, Guid customerId)
        {
            const string sql = @"
                SELECT COALESCE(firstname, ''), COALESCE(lastname, '')
                FROM users
                WHERE id = @id;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = customerId;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var first = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var last = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var combined = $"{first} {last}".Trim();
                if (!string.IsNullOrWhiteSpace(combined))
                {
                    return combined;
                }
            }

            return "A customer";
        }

        private static async Task EnsureTablesExist(NpgsqlConnection connection)
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
    }
}
