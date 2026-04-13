using Microsoft.AspNetCore.Mvc;
using ImajinationAPI.Services;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    public class CreateMessageRequest
    {
        public Guid senderId { get; set; }
        public string? message { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class MessageController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly MessageProtectionService _messageProtection;

        public MessageController(IConfiguration configuration, MessageProtectionService messageProtection)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _messageProtection = messageProtection;
        }

        [HttpGet("conversations/{userId}")]
        public async Task<IActionResult> GetConversations(Guid userId)
        {
            try
            {
                var conversations = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMessagesTableExists(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.customer_id,
                        b.target_user_id,
                        COALESCE(b.target_role, ''),
                        COALESCE(b.event_title, ''),
                        COALESCE(c.firstname, '') AS customer_firstname,
                        COALESCE(c.lastname, '') AS customer_lastname,
                        COALESCE(t.firstname, '') AS target_firstname,
                        COALESCE(t.lastname, '') AS target_lastname,
                        COALESCE(t.stagename, '') AS target_stage_name,
                        COALESCE((
                            SELECT m.message_text
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                            ORDER BY m.created_at DESC
                            LIMIT 1
                        ), b.message, '') AS last_message,
                        COALESCE((
                            SELECT m.created_at
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                            ORDER BY m.created_at DESC
                            LIMIT 1
                        ), b.created_at) AS last_activity,
                        EXISTS (
                            SELECT 1
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                        ) AS has_messages
                    FROM bookings b
                    LEFT JOIN users c ON c.id = b.customer_id
                    LEFT JOIN users t ON t.id = b.target_user_id
                    WHERE b.customer_id = @userId OR b.target_user_id = @userId
                    ORDER BY last_activity DESC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var customerId = reader.GetGuid(1);
                    var targetUserId = reader.GetGuid(2);
                    var isCustomer = customerId == userId;
                    var customerName = $"{(reader.IsDBNull(5) ? "" : reader.GetString(5))} {(reader.IsDBNull(6) ? "" : reader.GetString(6))}".Trim();
                    var targetStage = reader.IsDBNull(9) ? "" : reader.GetString(9);
                    var targetName = !string.IsNullOrWhiteSpace(targetStage)
                        ? targetStage
                        : $"{(reader.IsDBNull(7) ? "" : reader.GetString(7))} {(reader.IsDBNull(8) ? "" : reader.GetString(8))}".Trim();

                    var decryptedLastMessage = _messageProtection.Unprotect(reader.IsDBNull(10) ? "" : reader.GetString(10));

                    conversations.Add(new
                    {
                        bookingId = reader.GetGuid(0),
                        counterpartName = isCustomer ? targetName : customerName,
                        counterpartId = isCustomer ? targetUserId : customerId,
                        counterpartRole = isCustomer ? (reader.IsDBNull(3) ? "" : reader.GetString(3)) : "Customer",
                        eventTitle = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        lastMessage = decryptedLastMessage,
                        lastActivity = reader.IsDBNull(11) ? DateTime.UtcNow : reader.GetDateTime(11),
                        hasMessages = !reader.IsDBNull(12) && reader.GetBoolean(12)
                    });
                }

                return Ok(conversations);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load conversations: " + ex.Message });
            }
        }

        [HttpGet("booking/{bookingId}")]
        public async Task<IActionResult> GetMessagesForBooking(Guid bookingId)
        {
            try
            {
                var messages = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMessagesTableExists(connection);

                const string sql = @"
                    SELECT id, sender_id, receiver_id, message_text, created_at
                    FROM booking_messages
                    WHERE booking_id = @bookingId
                    ORDER BY created_at ASC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    messages.Add(new
                    {
                        id = reader.GetGuid(0),
                        senderId = reader.GetGuid(1),
                        receiverId = reader.GetGuid(2),
                        message = _messageProtection.Unprotect(reader.IsDBNull(3) ? "" : reader.GetString(3)),
                        createdAt = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4)
                    });
                }

                return Ok(messages);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load messages: " + ex.Message });
            }
        }

        [HttpPost("booking/{bookingId}")]
        public async Task<IActionResult> SendMessage(Guid bookingId, [FromBody] CreateMessageRequest req)
        {
            try
            {
                if (req.senderId == Guid.Empty || string.IsNullOrWhiteSpace(req.message))
                {
                    return BadRequest(new { message = "Sender and message are required." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMessagesTableExists(connection);

                Guid customerId;
                Guid targetUserId;

                const string bookingSql = "SELECT customer_id, target_user_id FROM bookings WHERE id = @bookingId";
                using (var bookingCmd = new NpgsqlCommand(bookingSql, connection))
                {
                    bookingCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                    using var reader = await bookingCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking request not found." });
                    }

                    customerId = reader.GetGuid(0);
                    targetUserId = reader.GetGuid(1);
                }

                var receiverId = req.senderId == customerId ? targetUserId : customerId;

                const string sql = @"
                    INSERT INTO booking_messages (id, booking_id, sender_id, receiver_id, message_text, created_at)
                    VALUES (@id, @bookingId, @senderId, @receiverId, @messageText, NOW());";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                cmd.Parameters.Add("@senderId", NpgsqlDbType.Uuid).Value = req.senderId;
                cmd.Parameters.Add("@receiverId", NpgsqlDbType.Uuid).Value = receiverId;
                cmd.Parameters.Add("@messageText", NpgsqlDbType.Text).Value = _messageProtection.Protect(req.message!);
                await cmd.ExecuteNonQueryAsync();

                return Ok(new { message = "Message sent successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to send message: " + ex.Message });
            }
        }

        private static async Task EnsureMessagesTableExists(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS booking_messages (
                    id uuid PRIMARY KEY,
                    booking_id uuid NOT NULL,
                    sender_id uuid NOT NULL,
                    receiver_id uuid NOT NULL,
                    message_text text NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
