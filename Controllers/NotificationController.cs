using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NotificationController : ControllerBase
    {
        private readonly string _connectionString;

        public NotificationController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetUserNotifications(Guid userId)
        {
            try
            {
                var notifications = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await NotificationSupport.GenerateEventReminderNotificationsAsync(connection, userId);

                const string sql = @"
                    SELECT id, type, title, message, related_id, related_type, is_read, created_at
                    FROM notifications
                    WHERE user_id = @userId
                    ORDER BY created_at DESC
                    LIMIT 50;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    notifications.Add(new
                    {
                        id = reader.GetGuid(0),
                        type = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        title = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        message = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        relatedId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4),
                        relatedType = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        isRead = !reader.IsDBNull(6) && reader.GetBoolean(6),
                        createdAt = reader.IsDBNull(7) ? DateTime.UtcNow : reader.GetDateTime(7)
                    });
                }

                return Ok(notifications);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load notifications: " + ex.Message });
            }
        }

        [HttpPost("{notificationId}/read")]
        public async Task<IActionResult> MarkAsRead(Guid notificationId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                const string sql = "UPDATE notifications SET is_read = TRUE WHERE id = @id;";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = notificationId;

                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0)
                {
                    return NotFound(new { message = "Notification not found." });
                }

                return Ok(new { message = "Notification marked as read." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update notification: " + ex.Message });
            }
        }
    }
}
