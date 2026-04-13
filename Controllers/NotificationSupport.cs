using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    internal static class NotificationSupport
    {
        public static async Task EnsureNotificationsTableExistsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS notifications (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    type varchar(60) NOT NULL,
                    title varchar(150) NOT NULL,
                    message text NOT NULL,
                    related_id uuid NULL,
                    related_type varchar(50) NULL,
                    is_read boolean NOT NULL DEFAULT FALSE,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task InsertNotificationAsync(
            NpgsqlConnection connection,
            Guid userId,
            string type,
            string title,
            string message,
            Guid? relatedId = null,
            string? relatedType = null)
        {
            const string sql = @"
                INSERT INTO notifications (id, user_id, type, title, message, related_id, related_type, is_read, created_at)
                VALUES (@id, @userId, @type, @title, @message, @relatedId, @relatedType, FALSE, NOW());";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@type", NpgsqlDbType.Text).Value = type;
            cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = title;
            cmd.Parameters.Add("@message", NpgsqlDbType.Text).Value = message;
            cmd.Parameters.Add("@relatedId", NpgsqlDbType.Uuid).Value = (object?)relatedId ?? DBNull.Value;
            cmd.Parameters.Add("@relatedType", NpgsqlDbType.Text).Value = (object?)relatedType ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task InsertNotificationIfNotExistsAsync(
            NpgsqlConnection connection,
            Guid userId,
            string type,
            string title,
            string message,
            Guid? relatedId = null,
            string? relatedType = null,
            int dedupeHours = 24)
        {
            const string sql = @"
                INSERT INTO notifications (id, user_id, type, title, message, related_id, related_type, is_read, created_at)
                SELECT @id, @userId, @type, @title, @message, @relatedId, @relatedType, FALSE, NOW()
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM notifications
                    WHERE user_id = @userId
                      AND type = @type
                      AND COALESCE(related_id, '00000000-0000-0000-0000-000000000000'::uuid) = COALESCE(@relatedId, '00000000-0000-0000-0000-000000000000'::uuid)
                      AND COALESCE(related_type, '') = COALESCE(@relatedType, '')
                      AND created_at >= NOW() - make_interval(hours => @dedupeHours)
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@type", NpgsqlDbType.Text).Value = type;
            cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = title;
            cmd.Parameters.Add("@message", NpgsqlDbType.Text).Value = message;
            cmd.Parameters.Add("@relatedId", NpgsqlDbType.Uuid).Value = (object?)relatedId ?? DBNull.Value;
            cmd.Parameters.Add("@relatedType", NpgsqlDbType.Text).Value = (object?)relatedType ?? DBNull.Value;
            cmd.Parameters.Add("@dedupeHours", NpgsqlDbType.Integer).Value = dedupeHours;
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task GenerateEventReminderNotificationsAsync(NpgsqlConnection connection, Guid userId)
        {
            await EnsureNotificationsTableExistsAsync(connection);

            var tomorrowStart = DateTime.UtcNow.Date.AddDays(1);
            var tomorrowEnd = tomorrowStart.AddDays(1);

            await GenerateOrganizerEventRemindersAsync(connection, userId, tomorrowStart, tomorrowEnd);
            await GenerateCustomerTicketRemindersAsync(connection, userId, tomorrowStart, tomorrowEnd);
            await GenerateTalentBookingRemindersAsync(connection, userId, tomorrowStart, tomorrowEnd);
        }

        private static async Task GenerateOrganizerEventRemindersAsync(NpgsqlConnection connection, Guid userId, DateTime start, DateTime end)
        {
            const string sql = @"
                SELECT id, COALESCE(title, 'Your event')
                FROM events
                WHERE organizer_id = @userId
                  AND event_time >= @start
                  AND event_time < @end
                  AND COALESCE(status, 'Upcoming') NOT IN ('Cancelled', 'Suspended', 'Finished');";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@start", NpgsqlDbType.TimestampTz).Value = start;
            cmd.Parameters.Add("@end", NpgsqlDbType.TimestampTz).Value = end;

            await using var reader = await cmd.ExecuteReaderAsync();
            var reminders = new List<(Guid Id, string Title)>();
            while (await reader.ReadAsync())
            {
                reminders.Add((reader.GetGuid(0), reader.IsDBNull(1) ? "Your event" : reader.GetString(1)));
            }

            await reader.CloseAsync();
            foreach (var reminder in reminders)
            {
                await InsertNotificationIfNotExistsAsync(
                    connection,
                    userId,
                    "event_reminder",
                    "Event reminder",
                    $"'{reminder.Title}' is happening tomorrow. Review your lineup, tickets, and event setup.",
                    reminder.Id,
                    "event",
                    36);
            }
        }

        private static async Task GenerateCustomerTicketRemindersAsync(NpgsqlConnection connection, Guid userId, DateTime start, DateTime end)
        {
            const string sql = @"
                SELECT DISTINCT e.id, COALESCE(e.title, 'Your event')
                FROM tickets t
                INNER JOIN events e ON e.id = t.event_id
                WHERE t.customer_id = @userId
                  AND e.event_time >= @start
                  AND e.event_time < @end
                  AND COALESCE(t.payment_method, '') <> 'AwaitingPayment'
                  AND COALESCE(e.status, 'Upcoming') NOT IN ('Cancelled', 'Suspended', 'Finished');";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@start", NpgsqlDbType.TimestampTz).Value = start;
            cmd.Parameters.Add("@end", NpgsqlDbType.TimestampTz).Value = end;

            await using var reader = await cmd.ExecuteReaderAsync();
            var reminders = new List<(Guid Id, string Title)>();
            while (await reader.ReadAsync())
            {
                reminders.Add((reader.GetGuid(0), reader.IsDBNull(1) ? "Your event" : reader.GetString(1)));
            }

            await reader.CloseAsync();
            foreach (var reminder in reminders)
            {
                await InsertNotificationIfNotExistsAsync(
                    connection,
                    userId,
                    "ticket_reminder",
                    "Your event is tomorrow",
                    $"Your ticket for '{reminder.Title}' is ready. Make sure you have it ready for entry tomorrow.",
                    reminder.Id,
                    "event",
                    36);
            }
        }

        private static async Task GenerateTalentBookingRemindersAsync(NpgsqlConnection connection, Guid userId, DateTime start, DateTime end)
        {
            const string sql = @"
                SELECT DISTINCT
                    COALESCE(b.event_id, e.id),
                    COALESCE(NULLIF(b.event_title, ''), e.title, 'Your schedule')
                FROM bookings b
                LEFT JOIN events e ON e.id = b.event_id
                WHERE b.target_user_id = @userId
                  AND COALESCE(b.status, '') IN ('Confirmed', 'Completed')
                  AND COALESCE(e.event_time, b.event_date) >= @start
                  AND COALESCE(e.event_time, b.event_date) < @end;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@start", NpgsqlDbType.TimestampTz).Value = start;
            cmd.Parameters.Add("@end", NpgsqlDbType.TimestampTz).Value = end;

            await using var reader = await cmd.ExecuteReaderAsync();
            var reminders = new List<(Guid? Id, string Title)>();
            while (await reader.ReadAsync())
            {
                reminders.Add((reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.IsDBNull(1) ? "Your schedule" : reader.GetString(1)));
            }

            await reader.CloseAsync();
            foreach (var reminder in reminders)
            {
                await InsertNotificationIfNotExistsAsync(
                    connection,
                    userId,
                    "schedule_reminder",
                    "Schedule reminder",
                    $"'{reminder.Title}' is scheduled for tomorrow. Review the chat and event details before you go.",
                    reminder.Id,
                    reminder.Id.HasValue ? "event" : "booking",
                    36);
            }
        }
    }
}
