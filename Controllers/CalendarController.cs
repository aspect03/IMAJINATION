using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    public class CreateCalendarBlockRequest
    {
        public Guid userId { get; set; }
        public string? role { get; set; }
        public DateTime? blockDate { get; set; }
        public DateTime? blockedDate { get; set; }
        public string? reason { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class CalendarController : ControllerBase
    {
        private readonly string _connectionString;

        public CalendarController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        [HttpGet("{role}/{userId}")]
        public async Task<IActionResult> GetCalendar(string role, Guid userId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                var blocks = new List<object>();
                const string blockSql = @"
                    SELECT id, blocked_date, COALESCE(reason, ''), created_at
                    FROM user_calendar_blocks
                    WHERE user_id = @userId
                      AND LOWER(role) = LOWER(@role)
                    ORDER BY blocked_date ASC;";

                await using (var blockCmd = new NpgsqlCommand(blockSql, connection))
                {
                    blockCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                    blockCmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = role;
                    await using var reader = await blockCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        blocks.Add(new
                        {
                            id = reader.GetGuid(0),
                            blockDate = reader.GetFieldValue<DateOnly>(1).ToDateTime(TimeOnly.MinValue),
                            reason = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            createdAt = reader.IsDBNull(3) ? DateTime.UtcNow : reader.GetDateTime(3)
                        });
                    }
                }

                var bookedDates = new List<object>();
                const string bookedSql = @"
                    SELECT id,
                           event_date,
                           COALESCE(event_title, 'Booking Request'),
                           COALESCE(status, 'Pending')
                    FROM bookings
                    WHERE target_user_id = @userId
                      AND event_date IS NOT NULL
                      AND status NOT ILIKE 'Cancelled%'
                    ORDER BY event_date ASC;";

                await using (var bookedCmd = new NpgsqlCommand(bookedSql, connection))
                {
                    bookedCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                    await using var reader = await bookedCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        bookedDates.Add(new
                        {
                            bookingId = reader.GetGuid(0),
                            eventDate = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
                            eventTitle = reader.IsDBNull(2) ? "Booking Request" : reader.GetString(2),
                            status = reader.IsDBNull(3) ? "Pending" : reader.GetString(3)
                        });
                    }
                }

                return Ok(new { blocks, bookedDates });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load calendar: " + ex.Message });
            }
        }

        [HttpPost("block")]
        public async Task<IActionResult> CreateBlock([FromBody] CreateCalendarBlockRequest req)
        {
            try
            {
                if (req.userId == Guid.Empty || string.IsNullOrWhiteSpace(req.role))
                {
                    return BadRequest(new { message = "Missing calendar block details." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                var requestedDate = req.blockDate ?? req.blockedDate;
                var normalizedRequestedDate = PlatformFeatureSupport.NormalizeToUtc(requestedDate);
                if (!normalizedRequestedDate.HasValue)
                {
                    return BadRequest(new { message = "Missing calendar block date." });
                }

                var blockDate = DateOnly.FromDateTime(normalizedRequestedDate.Value);

                const string sql = @"
                    INSERT INTO user_calendar_blocks (id, user_id, role, blocked_date, reason, created_at)
                    VALUES (@id, @userId, @role, @blockDate, @reason, NOW())
                    ON CONFLICT (user_id, role, blocked_date)
                    DO UPDATE SET reason = EXCLUDED.reason;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = req.userId;
                cmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = req.role;
                cmd.Parameters.Add("@blockDate", NpgsqlDbType.Date).Value = blockDate;
                cmd.Parameters.Add("@reason", NpgsqlDbType.Text).Value = (object?)req.reason ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();

                return Ok(new { message = "Date blocked successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to save calendar block: " + ex.Message });
            }
        }

        [HttpDelete("block/{blockId}")]
        public async Task<IActionResult> DeleteBlock(Guid blockId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                await using var cmd = new NpgsqlCommand("DELETE FROM user_calendar_blocks WHERE id = @id;", connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = blockId;
                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return NotFound(new { message = "Calendar block not found." });

                return Ok(new { message = "Blocked date removed." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to delete calendar block: " + ex.Message });
            }
        }
    }
}
