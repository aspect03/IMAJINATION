using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    public class SubmitEventReviewRequest
    {
        public Guid customerId { get; set; }
        public Guid eventId { get; set; }
        public int rating { get; set; }
        public string? feedback { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class EventInteractionController : ControllerBase
    {
        private readonly string _connectionString;

        public EventInteractionController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        [HttpGet("summary/{eventId}")]
        public async Task<IActionResult> GetSummary(Guid eventId, [FromQuery] Guid? customerId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                decimal averageRating = 0;
                int reviewCount = 0;
                bool canReview = false;
                bool hasPurchasedTicket = false;
                bool hasScannedTicket = false;
                string reviewEligibilityMessage = "Sign in as a Customer and attend the event before reviews become available.";
                var reviews = new List<object>();

                const string statsSql = @"
                    SELECT COALESCE(AVG(rating), 0), COUNT(id)
                    FROM event_reviews
                    WHERE event_id = @eventId;";

                await using (var statsCmd = new NpgsqlCommand(statsSql, connection))
                {
                    statsCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                    await using var reader = await statsCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        averageRating = reader.IsDBNull(0) ? 0 : reader.GetDecimal(0);
                        reviewCount = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1));
                    }
                }

                if (customerId.HasValue && customerId.Value != Guid.Empty)
                {
                    const string eligibilitySql = @"
                        SELECT EXISTS (
                                   SELECT 1
                                   FROM tickets t
                                   WHERE t.event_id = @eventId
                                     AND t.customer_id = @customerId
                                     AND COALESCE(t.payment_method, '') <> 'AwaitingPayment'
                               ),
                               EXISTS (
                                   SELECT 1
                                   FROM tickets t
                                   WHERE t.event_id = @eventId
                                     AND t.customer_id = @customerId
                                     AND COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 1) ELSE 0 END) > 0
                                     AND COALESCE(t.payment_method, '') <> 'AwaitingPayment'
                               );";

                    await using var eligibilityCmd = new NpgsqlCommand(eligibilitySql, connection);
                    eligibilityCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                    eligibilityCmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = customerId.Value;
                    await using var eligibilityReader = await eligibilityCmd.ExecuteReaderAsync();
                    if (await eligibilityReader.ReadAsync())
                    {
                        hasPurchasedTicket = !eligibilityReader.IsDBNull(0) && eligibilityReader.GetBoolean(0);
                        hasScannedTicket = !eligibilityReader.IsDBNull(1) && eligibilityReader.GetBoolean(1);
                        canReview = hasScannedTicket;
                    }

                    if (hasScannedTicket)
                    {
                        reviewEligibilityMessage = "Your ticket was scanned for this event, so you can leave a review now.";
                    }
                    else if (hasPurchasedTicket)
                    {
                        reviewEligibilityMessage = "Your ticket purchase was found, but reviews unlock only after the organizer scans your ticket at the venue.";
                    }
                    else
                    {
                        reviewEligibilityMessage = "No eligible ticket for this event was found on this customer account yet.";
                    }
                }

                const string reviewSql = @"
                    SELECT COALESCE(u.firstname, ''),
                           COALESCE(u.lastname, ''),
                           rating,
                           COALESCE(feedback, ''),
                           created_at
                    FROM event_reviews er
                    LEFT JOIN users u ON u.id = er.customer_id
                    WHERE er.event_id = @eventId
                    ORDER BY er.updated_at DESC
                    LIMIT 8;";

                await using (var reviewCmd = new NpgsqlCommand(reviewSql, connection))
                {
                    reviewCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                    await using var reader = await reviewCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        reviews.Add(new
                        {
                            customerName = $"{(reader.IsDBNull(0) ? "" : reader.GetString(0))} {(reader.IsDBNull(1) ? "" : reader.GetString(1))}".Trim(),
                            rating = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                            feedback = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            createdAt = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4)
                        });
                    }
                }

                return Ok(new
                {
                    averageRating = Math.Round(averageRating, 1),
                    reviewCount,
                    canReview,
                    hasPurchasedTicket,
                    hasScannedTicket,
                    reviewEligibilityMessage,
                    reviews
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load event reviews: " + ex.Message });
            }
        }

        [HttpPost("review")]
        public async Task<IActionResult> SubmitReview([FromBody] SubmitEventReviewRequest req)
        {
            try
            {
                if (req.customerId == Guid.Empty || req.eventId == Guid.Empty)
                {
                    return BadRequest(new { message = "Missing event review details." });
                }

                if (req.rating < 1 || req.rating > 5)
                {
                    return BadRequest(new { message = "Rating must be between 1 and 5." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                Guid organizerId = Guid.Empty;
                string eventTitle = "Event";
                const string eligibilitySql = @"
                    SELECT EXISTS (
                               SELECT 1
                               FROM tickets t
                               WHERE t.event_id = @eventId
                                 AND t.customer_id = @customerId
                                 AND COALESCE(t.payment_method, '') <> 'AwaitingPayment'
                           ),
                           EXISTS (
                               SELECT 1
                               FROM tickets t
                               WHERE t.event_id = @eventId
                                 AND t.customer_id = @customerId
                                 AND COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 1) ELSE 0 END) > 0
                                 AND COALESCE(t.payment_method, '') <> 'AwaitingPayment'
                           ),
                           COALESCE(e.organizer_id, '00000000-0000-0000-0000-000000000000'::uuid),
                           COALESCE(e.title, 'Event')
                    FROM events e
                    WHERE e.id = @eventId;";

                await using (var eligibilityCmd = new NpgsqlCommand(eligibilitySql, connection))
                {
                    eligibilityCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = req.eventId;
                    eligibilityCmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                    await using var reader = await eligibilityCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Event not found." });
                    }

                    var hasPurchasedTicket = !reader.IsDBNull(0) && reader.GetBoolean(0);
                    var hasScannedTicket = !reader.IsDBNull(1) && reader.GetBoolean(1);
                    if (!hasScannedTicket)
                    {
                        var message = hasPurchasedTicket
                            ? "You can only review this event after your ticket has been scanned by the organizer at the venue."
                            : "No eligible ticket for this event was found on this customer account.";
                        return BadRequest(new { message });
                    }

                    organizerId = reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2);
                    eventTitle = reader.IsDBNull(3) ? "Event" : reader.GetString(3);
                }

                const string sql = @"
                    INSERT INTO event_reviews (id, event_id, customer_id, rating, feedback, created_at, updated_at)
                    VALUES (@id, @eventId, @customerId, @rating, @feedback, NOW(), NOW())
                    ON CONFLICT (event_id, customer_id)
                    DO UPDATE SET
                        rating = EXCLUDED.rating,
                        feedback = EXCLUDED.feedback,
                        updated_at = NOW();";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                cmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = req.eventId;
                cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                cmd.Parameters.Add("@rating", NpgsqlDbType.Integer).Value = req.rating;
                cmd.Parameters.Add("@feedback", NpgsqlDbType.Text).Value = (object?)req.feedback ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();

                if (organizerId != Guid.Empty)
                {
                    await NotificationSupport.InsertNotificationIfNotExistsAsync(
                        connection,
                        organizerId,
                        "event_review",
                        "New event rating",
                        $"A customer rated '{eventTitle}' after attending.",
                        req.eventId,
                        "event",
                        12);
                }

                return Ok(new { message = "Event rating saved successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to save event rating: " + ex.Message });
            }
        }
    }
}
