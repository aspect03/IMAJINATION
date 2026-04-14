using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Net;
using System.Net.Mail;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class UpdateEventStatusRequest
    {
        public string? status { get; set; }
    }

    public class UpdateUserModerationRequest
    {
        public string? action { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class AdminController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly IConfiguration _configuration;

        public AdminController(IConfiguration configuration)
        {
            _configuration = configuration;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        [HttpGet("dashboard")]
        public async Task<IActionResult> GetDashboard()
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureUserModerationColumnsAsync(connection);
                await EnsureBookingMonitoringColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var now = DateTime.Now;
                decimal grossTicketSales = 0;
                int totalUsers = 0;
                int activeGigs = 0;
                int completedContracts = 0;
                var eventModeration = new List<object>();
                var accountModeration = new List<object>();
                var bookingStatusCounts = new List<object>();
                var topEvents = new List<object>();
                var topTalent = new List<object>();
                var recentSignups = new List<object>();
                object paymentSummary = new { };
                var paymentWatchlist = new List<object>();
                object securitySummary = new { };
                var securityActivity = new List<object>();

                const string summarySql = @"
                    SELECT
                        (SELECT COALESCE(SUM(total_price), 0) FROM tickets),
                        (SELECT COUNT(*) FROM users),
                        (
                            SELECT COUNT(*)
                            FROM events
                            WHERE COALESCE(status, 'Upcoming') NOT IN ('Finished', 'Cancelled', 'Suspended')
                              AND event_time >= @now
                        ),
                        (
                            SELECT COUNT(*)
                            FROM bookings
                            WHERE COALESCE(status, '') IN ('Confirmed', 'Completed')
                        );";

                using (var cmd = new NpgsqlCommand(summarySql, connection))
                {
                    cmd.Parameters.AddWithValue("@now", now);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        grossTicketSales = reader.IsDBNull(0) ? 0 : reader.GetDecimal(0);
                        totalUsers = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1));
                        activeGigs = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2));
                        completedContracts = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3));
                    }
                }

                const string eventsSql = @"
                    SELECT
                        e.id,
                        COALESCE(e.title, 'Untitled Event'),
                        TRIM(CONCAT(COALESCE(u.firstname, ''), ' ', COALESCE(u.lastname, ''))),
                        COALESCE(u.productionname, ''),
                        COALESCE(e.status, 'Upcoming'),
                        COALESCE(e.event_time, NOW()),
                        COALESCE(SUM(t.quantity), 0) AS tickets_sold,
                        COALESCE(SUM(t.total_price), 0) AS gross_revenue
                    FROM events e
                    LEFT JOIN users u ON u.id = e.organizer_id
                    LEFT JOIN tickets t ON t.event_id = e.id
                    GROUP BY e.id, e.title, u.firstname, u.lastname, u.productionname, e.status, e.event_time
                    ORDER BY e.event_time DESC
                    LIMIT 8;";

                using (var cmd = new NpgsqlCommand(eventsSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var organizerName = reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3))
                            ? (reader.IsDBNull(2) ? "Unknown Organizer" : reader.GetString(2))
                            : reader.GetString(3);

                        eventModeration.Add(new
                        {
                            eventId = reader.GetGuid(0),
                            title = reader.IsDBNull(1) ? "Untitled Event" : reader.GetString(1),
                            organizer = string.IsNullOrWhiteSpace(organizerName) ? "Unknown Organizer" : organizerName,
                            status = reader.IsDBNull(4) ? "Upcoming" : reader.GetString(4),
                            eventTime = reader.IsDBNull(5) ? now : reader.GetDateTime(5),
                            ticketsSold = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetInt64(6)),
                            grossRevenue = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7)
                        });
                    }
                }

                const string usersSql = @"
                    SELECT
                        id,
                        COALESCE(firstname, ''),
                        COALESCE(lastname, ''),
                        COALESCE(stagename, ''),
                        COALESCE(productionname, ''),
                        COALESCE(role, 'User'),
                        COALESCE(email, ''),
                        COALESCE(profile_picture, ''),
                        COALESCE(is_available, TRUE),
                        createdat,
                        COALESCE(is_banned, FALSE),
                        COALESCE(account_status, 'Active')
                    FROM users
                    ORDER BY createdat DESC NULLS LAST
                    LIMIT 8;";

                using (var cmd = new NpgsqlCommand(usersSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var firstName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var lastName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        var stageName = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        var productionName = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        var role = reader.IsDBNull(5) ? "User" : reader.GetString(5);

                        var displayName = !string.IsNullOrWhiteSpace(stageName)
                            ? stageName
                            : !string.IsNullOrWhiteSpace(productionName)
                                ? productionName
                                : $"{firstName} {lastName}".Trim();

                        accountModeration.Add(new
                        {
                            userId = reader.GetGuid(0),
                            displayName = string.IsNullOrWhiteSpace(displayName) ? "Unnamed User" : displayName,
                            role,
                            email = reader.IsDBNull(6) ? "" : reader.GetString(6),
                            profilePicture = reader.IsDBNull(7) ? "" : reader.GetString(7),
                            isAvailable = reader.IsDBNull(8) || reader.GetBoolean(8),
                            createdAt = reader.IsDBNull(9) ? now : reader.GetDateTime(9),
                            isBanned = !reader.IsDBNull(10) && reader.GetBoolean(10),
                            accountStatus = reader.IsDBNull(11) ? "Active" : reader.GetString(11)
                        });
                    }
                }

                const string bookingStatusSql = @"
                    SELECT COALESCE(status, 'Unknown') AS booking_status, COUNT(*)
                    FROM bookings
                    GROUP BY COALESCE(status, 'Unknown')
                    ORDER BY COUNT(*) DESC, booking_status ASC;";

                using (var cmd = new NpgsqlCommand(bookingStatusSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        bookingStatusCounts.Add(new
                        {
                            status = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0),
                            count = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1))
                        });
                    }
                }

                const string topEventsSql = @"
                    SELECT
                        COALESCE(e.title, 'Untitled Event'),
                        COALESCE(SUM(t.quantity), 0) AS tickets_sold,
                        COALESCE(SUM(t.total_price), 0) AS gross_revenue,
                        COALESCE(e.status, 'Upcoming')
                    FROM events e
                    LEFT JOIN tickets t ON t.event_id = e.id
                    GROUP BY e.id, e.title, e.status
                    ORDER BY gross_revenue DESC, tickets_sold DESC, e.title ASC
                    LIMIT 5;";

                using (var cmd = new NpgsqlCommand(topEventsSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        topEvents.Add(new
                        {
                            title = reader.IsDBNull(0) ? "Untitled Event" : reader.GetString(0),
                            ticketsSold = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
                            grossRevenue = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2),
                            status = reader.IsDBNull(3) ? "Upcoming" : reader.GetString(3)
                        });
                    }
                }

                const string topTalentSql = @"
                    SELECT
                        b.target_user_id,
                        COALESCE(u.stagename, ''),
                        TRIM(CONCAT(COALESCE(u.firstname, ''), ' ', COALESCE(u.lastname, ''))),
                        COALESCE(u.role, b.target_role),
                        COUNT(*) AS booking_count
                    FROM bookings b
                    INNER JOIN users u ON u.id = b.target_user_id
                    WHERE COALESCE(b.status, '') IN ('Confirmed', 'Completed')
                    GROUP BY b.target_user_id, u.stagename, u.firstname, u.lastname, u.role, b.target_role
                    ORDER BY booking_count DESC, u.role ASC
                    LIMIT 5;";

                using (var cmd = new NpgsqlCommand(topTalentSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var stageName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var fullName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        topTalent.Add(new
                        {
                            userId = reader.GetGuid(0),
                            displayName = string.IsNullOrWhiteSpace(stageName) ? (string.IsNullOrWhiteSpace(fullName) ? "Unnamed Talent" : fullName) : stageName,
                            role = reader.IsDBNull(3) ? "Talent" : reader.GetString(3),
                            bookingCount = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetInt64(4))
                        });
                    }
                }

                const string recentSignupsSql = @"
                    SELECT
                        id,
                        COALESCE(firstname, ''),
                        COALESCE(lastname, ''),
                        COALESCE(stagename, ''),
                        COALESCE(productionname, ''),
                        COALESCE(role, 'User'),
                        COALESCE(email, ''),
                        createdat
                    FROM users
                    ORDER BY createdat DESC NULLS LAST
                    LIMIT 5;";

                using (var cmd = new NpgsqlCommand(recentSignupsSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var firstName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var lastName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        var stageName = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        var productionName = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        var displayName = !string.IsNullOrWhiteSpace(stageName)
                            ? stageName
                            : !string.IsNullOrWhiteSpace(productionName)
                                ? productionName
                                : $"{firstName} {lastName}".Trim();

                        recentSignups.Add(new
                        {
                            userId = reader.GetGuid(0),
                            displayName = string.IsNullOrWhiteSpace(displayName) ? "Unnamed User" : displayName,
                            role = reader.IsDBNull(5) ? "User" : reader.GetString(5),
                            email = reader.IsDBNull(6) ? "" : reader.GetString(6),
                            createdAt = reader.IsDBNull(7) ? now : reader.GetDateTime(7)
                        });
                    }
                }

                const string paymentSummarySql = @"
                    SELECT
                        COUNT(*) FILTER (WHERE COALESCE(service_fee_status, 'Unpaid') = 'Paid') AS service_fees_paid,
                        COUNT(*) FILTER (WHERE COALESCE(talent_fee_status, 'Unpaid') = 'Paid') AS talent_fees_paid,
                        COUNT(*) FILTER (
                            WHERE COALESCE(payment_status, 'Unpaid') IN ('AwaitingPayment', 'AwaitingTalentFeePayment')
                               OR COALESCE(service_fee_status, 'Unpaid') = 'AwaitingPayment'
                               OR COALESCE(talent_fee_status, 'Unpaid') = 'AwaitingPayment'
                        ) AS payments_waiting,
                        COUNT(*) FILTER (WHERE COALESCE(payment_status, 'Unpaid') = 'Refund Pending') AS refunds_pending,
                        COALESCE(SUM(CASE WHEN COALESCE(service_fee_status, 'Unpaid') = 'Paid' THEN COALESCE(booking_fee, 0) ELSE 0 END), 0) AS service_fee_revenue,
                        COALESCE(SUM(CASE WHEN COALESCE(talent_fee_status, 'Unpaid') = 'Paid' THEN COALESCE(budget, 0) ELSE 0 END), 0) AS talent_fee_collected
                    FROM bookings;";

                using (var cmd = new NpgsqlCommand(paymentSummarySql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        paymentSummary = new
                        {
                            serviceFeesPaid = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0)),
                            talentFeesPaid = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
                            paymentsWaiting = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2)),
                            refundsPending = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3)),
                            serviceFeeRevenue = reader.IsDBNull(4) ? 0 : reader.GetDecimal(4),
                            talentFeeCollected = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5)
                        };
                    }
                }

                const string paymentWatchlistSql = @"
                    SELECT
                        b.id,
                        COALESCE(b.event_title, ''),
                        COALESCE(c.firstname, ''),
                        COALESCE(c.lastname, ''),
                        COALESCE(t.stagename, ''),
                        COALESCE(t.firstname, ''),
                        COALESCE(t.lastname, ''),
                        COALESCE(b.payment_status, 'Unpaid'),
                        COALESCE(b.service_fee_status, 'Unpaid'),
                        COALESCE(b.talent_fee_status, 'Unpaid'),
                        COALESCE(b.booking_fee, 0),
                        COALESCE(b.budget, 0),
                        b.created_at
                    FROM bookings b
                    LEFT JOIN users c ON c.id = b.customer_id
                    LEFT JOIN users t ON t.id = b.target_user_id
                    WHERE COALESCE(b.payment_status, 'Unpaid') = 'Refund Pending'
                       OR COALESCE(b.payment_status, 'Unpaid') IN ('AwaitingPayment', 'AwaitingTalentFeePayment')
                       OR COALESCE(b.service_fee_status, 'Unpaid') = 'AwaitingPayment'
                       OR COALESCE(b.talent_fee_status, 'Unpaid') = 'AwaitingPayment'
                    ORDER BY b.created_at DESC
                    LIMIT 8;";

                using (var cmd = new NpgsqlCommand(paymentWatchlistSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var customerName = $"{(reader.IsDBNull(2) ? "" : reader.GetString(2))} {(reader.IsDBNull(3) ? "" : reader.GetString(3))}".Trim();
                        var talentStage = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        var talentName = string.IsNullOrWhiteSpace(talentStage)
                            ? $"{(reader.IsDBNull(5) ? "" : reader.GetString(5))} {(reader.IsDBNull(6) ? "" : reader.GetString(6))}".Trim()
                            : talentStage;

                        paymentWatchlist.Add(new
                        {
                            bookingId = reader.GetGuid(0),
                            eventTitle = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            customerName = string.IsNullOrWhiteSpace(customerName) ? "Unknown Customer" : customerName,
                            talentName = string.IsNullOrWhiteSpace(talentName) ? "Unknown Talent" : talentName,
                            paymentStatus = reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7),
                            serviceFeeStatus = reader.IsDBNull(8) ? "Unpaid" : reader.GetString(8),
                            talentFeeStatus = reader.IsDBNull(9) ? "Unpaid" : reader.GetString(9),
                            bookingFee = reader.IsDBNull(10) ? 0 : reader.GetDecimal(10),
                            budget = reader.IsDBNull(11) ? 0 : reader.GetDecimal(11),
                            createdAt = reader.IsDBNull(12) ? now : reader.GetDateTime(12)
                        });
                    }
                }

                const string securitySummarySql = @"
                    SELECT
                        COUNT(*) FILTER (WHERE action_type = 'login_failed' AND created_at >= NOW() - INTERVAL '24 hours'),
                        COUNT(*) FILTER (WHERE action_type = 'login_lockout_triggered' AND created_at >= NOW() - INTERVAL '24 hours'),
                        COUNT(*) FILTER (WHERE action_type = 'otp_request_throttled' AND created_at >= NOW() - INTERVAL '24 hours'),
                        COUNT(*) FILTER (WHERE action_type = 'report_submitted' AND created_at >= NOW() - INTERVAL '24 hours')
                    FROM security_audit_logs;";

                using (var cmd = new NpgsqlCommand(securitySummarySql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        securitySummary = new
                        {
                            failedLogins24h = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0)),
                            lockouts24h = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
                            otpBlocks24h = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2)),
                            reports24h = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3))
                        };
                    }
                }

                const string securityActivitySql = @"
                    SELECT action_type,
                           COALESCE(actor_role, ''),
                           COALESCE(target_type, ''),
                           COALESCE(details, ''),
                           created_at
                    FROM security_audit_logs
                    ORDER BY created_at DESC
                    LIMIT 12;";

                using (var cmd = new NpgsqlCommand(securityActivitySql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        securityActivity.Add(new
                        {
                            actionType = reader.IsDBNull(0) ? "" : reader.GetString(0),
                            actorRole = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            targetType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            details = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            createdAt = reader.IsDBNull(4) ? now : reader.GetDateTime(4)
                        });
                    }
                }

                var systemHealth = activeGigs > 0 || totalUsers > 0
                    ? "System Running Smoothly"
                    : "Platform Waiting For Activity";

                return Ok(new
                {
                    grossTicketSales,
                    totalUsers,
                    activeGigs,
                    completedContracts,
                    systemHealth,
                    eventModeration,
                    accountModeration,
                    bookingStatusCounts,
                    topEvents,
                    topTalent,
                    recentSignups,
                    paymentSummary,
                    paymentWatchlist,
                    securitySummary,
                    securityActivity
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load admin dashboard: " + ex.Message });
            }
        }

        [HttpPost("events/{eventId}/status")]
        public async Task<IActionResult> UpdateEventStatus(Guid eventId, [FromBody] UpdateEventStatusRequest req)
        {
            try
            {
                var nextStatus = (req.status ?? string.Empty).Trim();
                var allowedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Upcoming",
                    "Active",
                    "Flagged",
                    "Suspended",
                    "Finished",
                    "Cancelled"
                };

                if (!allowedStatuses.Contains(nextStatus))
                {
                    return BadRequest(new { message = "Invalid event status." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                Guid organizerId = Guid.Empty;
                string eventTitle = "The event";

                const string eventLookupSql = "SELECT COALESCE(organizer_id, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(title, 'The event') FROM events WHERE id = @id;";
                using (var lookupCmd = new NpgsqlCommand(eventLookupSql, connection))
                {
                    lookupCmd.Parameters.AddWithValue("@id", eventId);
                    using var reader = await lookupCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        organizerId = reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0);
                        eventTitle = reader.IsDBNull(1) ? "The event" : reader.GetString(1);
                    }
                    else
                    {
                        return NotFound(new { message = "Event not found." });
                    }
                }

                const string sql = "UPDATE events SET status = @status WHERE id = @id;";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@status", nextStatus);
                cmd.Parameters.AddWithValue("@id", eventId);

                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0)
                {
                    return NotFound(new { message = "Event not found." });
                }

                if (organizerId != Guid.Empty)
                {
                    await NotificationSupport.InsertNotificationAsync(
                        connection,
                        organizerId,
                        "admin_event_action",
                        "Admin updated your event",
                        $"'{eventTitle}' was marked as {nextStatus} by an administrator.",
                        eventId,
                        "event");
                }

                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    TryReadActorUserId(),
                    TryReadActorRole() ?? "Admin",
                    "admin_event_status_updated",
                    "event",
                    eventId,
                    HttpContext,
                    $"Admin changed '{eventTitle}' status to {nextStatus}.");

                return Ok(new { message = "Event status updated.", status = nextStatus });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update event status: " + ex.Message });
            }
        }

        [HttpPost("users/{userId}/moderation")]
        public async Task<IActionResult> UpdateUserModeration(Guid userId, [FromBody] UpdateUserModerationRequest req)
        {
            try
            {
                var action = (req.action ?? string.Empty).Trim().ToLowerInvariant();
                if (action != "ban" && action != "unban" && action != "approve" && action != "deny")
                {
                    return BadRequest(new { message = "Invalid moderation action." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureUserModerationColumnsAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                Guid targetUserId = Guid.Empty;
                string targetEmail = string.Empty;
                string targetRole = string.Empty;
                string targetDisplayName = "there";

                const string userLookupSql = @"
                    SELECT
                        id,
                        COALESCE(email, ''),
                        COALESCE(role, ''),
                        COALESCE(stagename, ''),
                        COALESCE(productionname, ''),
                        COALESCE(firstname, ''),
                        COALESCE(lastname, '')
                    FROM users
                    WHERE id = @id
                    LIMIT 1;";

                using (var lookupCmd = new NpgsqlCommand(userLookupSql, connection))
                {
                    lookupCmd.Parameters.AddWithValue("@id", userId);
                    using var reader = await lookupCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "User not found." });
                    }

                    targetUserId = reader.GetGuid(0);
                    targetEmail = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    targetRole = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var stageName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    var productionName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    var firstName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                    var lastName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                    targetDisplayName = !string.IsNullOrWhiteSpace(productionName)
                        ? productionName
                        : !string.IsNullOrWhiteSpace(stageName)
                            ? stageName
                            : $"{firstName} {lastName}".Trim();

                    if (string.IsNullOrWhiteSpace(targetDisplayName))
                    {
                        targetDisplayName = "there";
                    }
                }

                var nextIsBanned = action == "ban";
                var nextAccountStatus = action switch
                {
                    "ban" => "Banned",
                    "unban" => "Active",
                    "approve" => "Active",
                    "deny" => "Denied",
                    _ => "Active"
                };

                const string sql = @"
                    UPDATE users
                    SET is_banned = @isBanned,
                        account_status = @accountStatus
                    WHERE id = @id;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@isBanned", nextIsBanned);
                cmd.Parameters.AddWithValue("@accountStatus", nextAccountStatus);
                cmd.Parameters.AddWithValue("@id", userId);

                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0)
                {
                    return NotFound(new { message = "User not found." });
                }

                await NotificationSupport.InsertNotificationAsync(
                    connection,
                    targetUserId,
                    "admin_account_action",
                    action switch
                    {
                        "approve" => "Organizer account approved",
                        "deny" => "Organizer account denied",
                        "ban" => "Account restricted",
                        _ => "Account restored"
                    },
                    action switch
                    {
                        "approve" => "An administrator approved your organizer account. You can now sign in and start setting up events.",
                        "deny" => "An administrator denied your organizer account request. Please contact support if you think this was a mistake.",
                        "ban" => "An administrator restricted your account. Please contact support if you think this was a mistake.",
                        _ => "An administrator restored your account access."
                    },
                    targetUserId,
                    "user");

                if (string.Equals(targetRole, "Organizer", StringComparison.OrdinalIgnoreCase)
                    && (action == "approve" || action == "deny")
                    && !string.IsNullOrWhiteSpace(targetEmail))
                {
                    await TrySendOrganizerDecisionEmailAsync(targetEmail, targetDisplayName, action == "approve");
                }

                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    TryReadActorUserId(),
                    TryReadActorRole() ?? "Admin",
                    "admin_user_moderation",
                    "user",
                    targetUserId,
                    HttpContext,
                    $"Admin action '{action}' applied to {targetRole} account {targetDisplayName}.");

                return Ok(new
                {
                    message = action switch
                    {
                        "approve" => "Organizer approved successfully.",
                        "deny" => "Organizer denied successfully.",
                        "ban" => "User banned successfully.",
                        _ => "User restored successfully."
                    },
                    accountStatus = nextAccountStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update user moderation: " + ex.Message });
            }
        }

        private static async Task EnsureUserModerationColumnsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS is_banned BOOLEAN NOT NULL DEFAULT FALSE;

                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS account_status VARCHAR(40) NOT NULL DEFAULT 'Active';";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureBookingMonitoringColumnsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS bookings (
                    id uuid PRIMARY KEY,
                    service_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    talent_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    payment_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    booking_fee numeric NULL,
                    budget numeric NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );

                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS payment_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS booking_fee numeric NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS budget numeric NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT NOW();";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private Guid? TryReadActorUserId()
        {
            var raw = Request.Headers["X-Actor-UserId"].ToString();
            return Guid.TryParse(raw, out var parsed) ? parsed : null;
        }

        private string? TryReadActorRole()
        {
            var raw = Request.Headers["X-Actor-Role"].ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }

        private async Task TrySendOrganizerDecisionEmailAsync(string recipientEmail, string displayName, bool approved)
        {
            try
            {
                var senderEmail = ConfigurationFallbacks.GetRequiredSetting(_configuration, "EmailSettings:SenderEmail", "EmailSettings__SenderEmail", "Brevo sender email");
                var senderName = ConfigurationFallbacks.GetRequiredSetting(_configuration, "EmailSettings:SenderName", "EmailSettings__SenderName", "Brevo sender name");
                var smtpServer = ConfigurationFallbacks.GetRequiredSetting(_configuration, "EmailSettings:SmtpServer", "EmailSettings__SmtpServer", "Brevo SMTP server");
                var smtpPortRaw = ConfigurationFallbacks.GetRequiredSetting(_configuration, "EmailSettings:Port", "EmailSettings__Port", "Brevo SMTP port");
                var smtpUsername = ConfigurationFallbacks.GetRequiredSetting(_configuration, "EmailSettings:Username", "EmailSettings__Username", "Brevo SMTP username");
                var smtpPassword = ConfigurationFallbacks.GetRequiredSetting(_configuration, "EmailSettings:Password", "EmailSettings__Password", "Brevo SMTP password");

                if (!int.TryParse(smtpPortRaw, out var smtpPort))
                {
                    return;
                }

                using var mail = new MailMessage();
                mail.From = new MailAddress(senderEmail, senderName);
                mail.To.Add(recipientEmail);
                mail.Subject = approved
                    ? "Your Imajination Organizer Account Has Been Approved"
                    : "Your Imajination Organizer Account Was Not Approved";
                mail.IsBodyHtml = true;
                mail.Body = approved
                    ? $@"
                        <div style='font-family:Montserrat,Arial,sans-serif;background:#09090b;padding:40px 20px;color:#fff;'>
                          <div style='max-width:640px;margin:auto;background:#171717;border:1px solid #2a2a2a;border-radius:28px;padding:36px;box-shadow:0 24px 80px rgba(0,0,0,0.45);'>
                            <p style='font-size:11px;letter-spacing:3px;text-transform:uppercase;color:#f87171;margin:0 0 16px;font-weight:700;'>Organizer Approval</p>
                            <h2 style='margin:0 0 14px;font-size:36px;line-height:1.12;font-weight:800;color:#ffffff;'>Hello {WebUtility.HtmlEncode(displayName)}, your organizer account is now approved.</h2>
                            <p style='color:#d4d4d8;line-height:1.8;margin:0 0 22px;font-size:15px;'>An administrator reviewed your organizer registration and approved it. You can now sign in, complete your profile, and begin creating events inside Imajination.</p>
                            <div style='padding:18px 20px;background:#052e16;border:1px solid #166534;border-radius:18px;color:#bbf7d0;font-size:15px;line-height:1.6;'>You can now access the organizer dashboard using the email you registered with.</div>
                          </div>
                        </div>"
                    : $@"
                        <div style='font-family:Montserrat,Arial,sans-serif;background:#09090b;padding:40px 20px;color:#fff;'>
                          <div style='max-width:640px;margin:auto;background:#171717;border:1px solid #2a2a2a;border-radius:28px;padding:36px;box-shadow:0 24px 80px rgba(0,0,0,0.45);'>
                            <p style='font-size:11px;letter-spacing:3px;text-transform:uppercase;color:#f87171;margin:0 0 16px;font-weight:700;'>Organizer Approval</p>
                            <h2 style='margin:0 0 14px;font-size:36px;line-height:1.12;font-weight:800;color:#ffffff;'>Hello {WebUtility.HtmlEncode(displayName)}, your organizer account was not approved.</h2>
                            <p style='color:#d4d4d8;line-height:1.8;margin:0 0 22px;font-size:15px;'>An administrator reviewed your organizer registration and did not approve it at this time. If you believe this was a mistake, please contact support or submit updated organizer details before trying again.</p>
                            <div style='padding:18px 20px;background:#3f0d0d;border:1px solid #7f1d1d;border-radius:18px;color:#fecaca;font-size:15px;line-height:1.6;'>Your organizer sign-in will stay blocked until an administrator approves the account.</div>
                          </div>
                        </div>";

                using var smtp = new SmtpClient(smtpServer, smtpPort);
                smtp.Credentials = new NetworkCredential(smtpUsername, smtpPassword);
                smtp.EnableSsl = true;
                await smtp.SendMailAsync(mail);
            }
            catch
            {
                // Moderation should still succeed even if the email provider rejects or delays the email.
            }
        }
    }
}
