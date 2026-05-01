using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
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

    public class ReviewVerificationRequest
    {
        public string? action { get; set; }
        public string? notes { get; set; }
    }

    public class ReviewRefundRequest
    {
        public string? action { get; set; }
        public string? notes { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = "Admin")]
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
                ApplySensitiveResponseHeaders();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureUserModerationColumnsAsync(connection);
                await EnsureBookingMonitoringColumnsAsync(connection);
                await EnsureTicketMonitoringColumnsAsync(connection);
                await PaymentRefundService.EnsureSchemaAsync(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    TryReadActorUserId(),
                    TryReadActorRole() ?? "Admin",
                    "admin_dashboard_viewed",
                    "admin_dashboard",
                    null,
                    HttpContext,
                    "Admin opened the control room dashboard.");

                var now = DateTime.Now;
                decimal grossTicketSales = 0;
                int totalUsers = 0;
                int activeGigs = 0;
                int completedContracts = 0;
                int pendingVerificationRequests = 0;
                var eventModeration = new List<object>();
                var accountModeration = new List<object>();
                var verificationQueue = new List<object>();
                var bookingStatusCounts = new List<object>();
                var topEvents = new List<object>();
                var topTalent = new List<object>();
                var bookingLogs = new List<object>();
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

                const string verificationCountSql = @"
                    SELECT COUNT(*)
                    FROM talent_verification_requests
                    WHERE COALESCE(status, 'Pending') = 'Pending';";
                using (var cmd = new NpgsqlCommand(verificationCountSql, connection))
                {
                    pendingVerificationRequests = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
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

                const string bookingLogsSql = @"
                    SELECT
                        b.target_user_id,
                        CASE
                            WHEN COALESCE(u.stagename, '') <> '' THEN u.stagename
                            ELSE TRIM(CONCAT(COALESCE(u.firstname, ''), ' ', COALESCE(u.lastname, '')))
                        END AS display_name,
                        COALESCE(u.role, b.target_role) AS role,
                        COUNT(*) AS total_bookings,
                        COUNT(*) FILTER (WHERE b.status ILIKE 'Cancelled%') AS cancellations,
                        COUNT(*) FILTER (WHERE COALESCE(b.status, '') IN ('Pending', 'AwaitingApproval', 'AwaitingPayment', 'AwaitingTalentFeePayment')) AS pending_count,
                        COUNT(*) FILTER (WHERE COALESCE(b.status, '') IN ('Confirmed', 'Active')) AS active_count,
                        COALESCE(SUM(CASE WHEN COALESCE(b.talent_platform_fee_status, 'Unpaid') = 'Paid' THEN COALESCE(b.talent_platform_fee, 0) ELSE 0 END), 0) AS platform_fee_earned
                    FROM bookings b
                    INNER JOIN users u ON u.id = b.target_user_id
                    WHERE COALESCE(u.role, b.target_role) IN ('Artist', 'Sessionist')
                    GROUP BY b.target_user_id, u.stagename, u.firstname, u.lastname, u.role, b.target_role
                    ORDER BY total_bookings DESC, display_name ASC
                    LIMIT 20;";

                using (var cmd = new NpgsqlCommand(bookingLogsSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var displayName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        bookingLogs.Add(new
                        {
                            userId = reader.GetGuid(0),
                            displayName = string.IsNullOrWhiteSpace(displayName) ? "Unnamed" : displayName,
                            role = reader.IsDBNull(2) ? "Artist" : reader.GetString(2),
                            totalBookings = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3)),
                            cancellations = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetInt64(4)),
                            pendingCount = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetInt64(5)),
                            activeCount = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetInt64(6)),
                            platformFeeEarned = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7)
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

                const string verificationQueueSql = @"
                    SELECT
                        tvr.id,
                        tvr.user_id,
                        COALESCE(tvr.role, ''),
                        COALESCE(tvr.verification_path, ''),
                        COALESCE(tvr.status, 'Pending'),
                        COALESCE(tvr.evidence_summary, ''),
                        tvr.created_at,
                        COALESCE(tvr.id_type, ''),
                        COALESCE(tvr.id_number_last4, ''),
                        COALESCE(tvr.id_image_front, ''),
                        COALESCE(tvr.id_image_back, ''),
                        COALESCE(tvr.selfie_image, ''),
                        COALESCE(tvr.id_review_status, 'Pending'),
                        COALESCE(tvr.facial_review_status, 'Pending'),
                        COALESCE(tvr.automated_status, ''),
                        COALESCE(tvr.automated_recommendation, ''),
                        COALESCE(tvr.automated_score, 0),
                        COALESCE(tvr.automated_notes, ''),
                        COALESCE(u.stagename, ''),
                        COALESCE(u.productionname, ''),
                        COALESCE(u.firstname, ''),
                        COALESCE(u.lastname, ''),
                        COALESCE(u.email, '')
                    FROM talent_verification_requests tvr
                    LEFT JOIN users u ON u.id = tvr.user_id
                    WHERE COALESCE(tvr.status, 'Pending') = 'Pending'
                    ORDER BY tvr.created_at DESC
                    LIMIT 6;";

                using (var cmd = new NpgsqlCommand(verificationQueueSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var stageName = reader.IsDBNull(18) ? "" : reader.GetString(18);
                        var productionName = reader.IsDBNull(19) ? "" : reader.GetString(19);
                        var firstName = reader.IsDBNull(20) ? "" : reader.GetString(20);
                        var lastName = reader.IsDBNull(21) ? "" : reader.GetString(21);
                        var email = reader.IsDBNull(22) ? "" : reader.GetString(22);
                        var displayName = !string.IsNullOrWhiteSpace(stageName)
                            ? stageName
                            : !string.IsNullOrWhiteSpace(productionName)
                                ? productionName
                                : $"{firstName} {lastName}".Trim();

                        verificationQueue.Add(new
                        {
                            requestId = reader.GetGuid(0),
                            userId = reader.GetGuid(1),
                            role = reader.IsDBNull(2) ? "Talent" : reader.GetString(2),
                            verificationPath = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            status = reader.IsDBNull(4) ? "Pending" : reader.GetString(4),
                            evidenceSummary = reader.IsDBNull(5) ? "" : reader.GetString(5),
                            createdAt = reader.IsDBNull(6) ? now : reader.GetDateTime(6),
                            idType = reader.IsDBNull(7) ? "" : reader.GetString(7),
                            idNumberLast4 = reader.IsDBNull(8) ? "" : reader.GetString(8),
                            hasIdImageFront = !reader.IsDBNull(9) && !string.IsNullOrWhiteSpace(reader.GetString(9)),
                            hasIdImageBack = !reader.IsDBNull(10) && !string.IsNullOrWhiteSpace(reader.GetString(10)),
                            hasSelfieImage = !reader.IsDBNull(11) && !string.IsNullOrWhiteSpace(reader.GetString(11)),
                            idReviewStatus = reader.IsDBNull(12) ? "Pending" : reader.GetString(12),
                            facialReviewStatus = reader.IsDBNull(13) ? "Pending" : reader.GetString(13),
                            automatedStatus = reader.IsDBNull(14) ? "" : reader.GetString(14),
                            automatedRecommendation = reader.IsDBNull(15) ? "" : reader.GetString(15),
                            automatedScore = reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                            automatedNotes = reader.IsDBNull(17) ? "" : reader.GetString(17),
                            displayName = string.IsNullOrWhiteSpace(displayName) ? "Unnamed Talent" : displayName,
                            email = email
                        });
                    }
                }

                const string paymentSummarySql = @"
                    SELECT
                        COUNT(*) FILTER (WHERE COALESCE(service_fee_status, 'Unpaid') = 'Paid') AS service_fees_paid,
                        COUNT(*) FILTER (WHERE COALESCE(talent_fee_status, 'Unpaid') = 'Paid') AS talent_fees_paid,
                        COUNT(*) FILTER (WHERE COALESCE(talent_platform_fee_status, 'Unpaid') = 'Paid') AS platform_fees_paid,
                        COUNT(*) FILTER (
                            WHERE COALESCE(payment_status, 'Unpaid') IN ('AwaitingPayment', 'AwaitingTalentFeePayment')
                               OR COALESCE(service_fee_status, 'Unpaid') = 'AwaitingPayment'
                               OR COALESCE(talent_fee_status, 'Unpaid') = 'AwaitingPayment'
                               OR COALESCE(talent_platform_fee_status, 'Unpaid') = 'AwaitingPayment'
                        ) AS payments_waiting,
                        COUNT(*) FILTER (WHERE COALESCE(payment_status, 'Unpaid') = 'Refund Pending') AS refunds_pending,
                        COALESCE(SUM(CASE WHEN COALESCE(service_fee_status, 'Unpaid') = 'Paid' THEN COALESCE(booking_fee, 0) ELSE 0 END), 0) AS service_fee_revenue,
                        COALESCE(SUM(CASE WHEN COALESCE(talent_fee_status, 'Unpaid') = 'Paid' THEN COALESCE(budget, 0) ELSE 0 END), 0) AS talent_fee_collected,
                        COALESCE(SUM(CASE WHEN COALESCE(talent_platform_fee_status, 'Unpaid') = 'Paid' THEN COALESCE(talent_platform_fee, 0) ELSE 0 END), 0) AS platform_fee_revenue
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
                            platformFeesPaid = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2)),
                            paymentsWaiting = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3)),
                            refundsPending = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetInt64(4)),
                            serviceFeeRevenue = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                            talentFeeCollected = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                            platformFeeRevenue = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7)
                        };
                    }
                }

                const string ticketRefundSummarySql = @"
                    SELECT COUNT(*)
                    FROM refund_requests
                    WHERE refund_scope = 'ticket'
                      AND status IN ('Requested', 'ManualReview');";
                using (var cmd = new NpgsqlCommand(ticketRefundSummarySql, connection))
                {
                    var pendingTicketRefunds = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
                    paymentSummary = new
                    {
                        serviceFeesPaid = GetAnonymousInt(paymentSummary, "serviceFeesPaid"),
                        talentFeesPaid = GetAnonymousInt(paymentSummary, "talentFeesPaid"),
                        platformFeesPaid = GetAnonymousInt(paymentSummary, "platformFeesPaid"),
                        paymentsWaiting = GetAnonymousInt(paymentSummary, "paymentsWaiting"),
                        refundsPending = GetAnonymousInt(paymentSummary, "refundsPending") + pendingTicketRefunds,
                        ticketRefundsPending = pendingTicketRefunds,
                        serviceFeeRevenue = GetAnonymousDecimal(paymentSummary, "serviceFeeRevenue"),
                        talentFeeCollected = GetAnonymousDecimal(paymentSummary, "talentFeeCollected"),
                        platformFeeRevenue = GetAnonymousDecimal(paymentSummary, "platformFeeRevenue")
                    };
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
                        COALESCE(b.talent_platform_fee_status, 'Unpaid'),
                        COALESCE(b.booking_fee, 0),
                        COALESCE(b.budget, 0),
                        COALESCE(b.talent_platform_fee, 0),
                        b.created_at
                    FROM bookings b
                    LEFT JOIN users c ON c.id = b.customer_id
                    LEFT JOIN users t ON t.id = b.target_user_id
                    WHERE COALESCE(b.payment_status, 'Unpaid') = 'Refund Pending'
                       OR COALESCE(b.payment_status, 'Unpaid') IN ('AwaitingPayment', 'AwaitingTalentFeePayment')
                       OR COALESCE(b.service_fee_status, 'Unpaid') = 'AwaitingPayment'
                       OR COALESCE(b.talent_fee_status, 'Unpaid') = 'AwaitingPayment'
                       OR COALESCE(b.talent_platform_fee_status, 'Unpaid') = 'AwaitingPayment'
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
                            talentPlatformFeeStatus = reader.IsDBNull(10) ? "Unpaid" : reader.GetString(10),
                            bookingFee = reader.IsDBNull(11) ? 0 : reader.GetDecimal(11),
                            budget = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12),
                            talentPlatformFee = reader.IsDBNull(13) ? 0 : reader.GetDecimal(13),
                            createdAt = reader.IsDBNull(14) ? now : reader.GetDateTime(14)
                        });
                    }
                }

                const string ticketRefundWatchlistSql = @"
                    SELECT
                        rr.id,
                        rr.ticket_id,
                        COALESCE(rr.status, 'Requested'),
                        COALESCE(rr.reason_code, 'Other'),
                        COALESCE(rr.reason_details, ''),
                        COALESCE(rr.amount, 0),
                        COALESCE(rr.error_message, ''),
                        rr.created_at,
                        COALESCE(e.title, 'Event Ticket'),
                        COALESCE(c.firstname, ''),
                        COALESCE(c.lastname, ''),
                        COALESCE(t.refund_status, 'Refund Pending')
                    FROM refund_requests rr
                    LEFT JOIN tickets t ON t.id = rr.ticket_id
                    LEFT JOIN events e ON e.id = t.event_id
                    LEFT JOIN users c ON c.id = t.customer_id
                    WHERE rr.refund_scope = 'ticket'
                      AND rr.status IN ('Requested', 'ManualReview')
                    ORDER BY rr.created_at DESC
                    LIMIT 8;";

                using (var cmd = new NpgsqlCommand(ticketRefundWatchlistSql, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var customerName = $"{(reader.IsDBNull(9) ? "" : reader.GetString(9))} {(reader.IsDBNull(10) ? "" : reader.GetString(10))}".Trim();
                        paymentWatchlist.Add(new
                        {
                            itemType = "ticket_refund",
                            requestId = reader.GetGuid(0),
                            ticketId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1),
                            eventTitle = reader.IsDBNull(8) ? "Event Ticket" : reader.GetString(8),
                            customerName = string.IsNullOrWhiteSpace(customerName) ? "Unknown Customer" : customerName,
                            talentName = "Ticket Refund",
                            paymentStatus = reader.IsDBNull(11) ? "Refund Pending" : reader.GetString(11),
                            serviceFeeStatus = reader.IsDBNull(2) ? "Requested" : reader.GetString(2),
                            talentFeeStatus = reader.IsDBNull(3) ? "Other" : reader.GetString(3),
                            talentPlatformFeeStatus = reader.IsDBNull(6) ? "" : reader.GetString(6),
                            bookingFee = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                            budget = 0,
                            talentPlatformFee = 0,
                            createdAt = reader.IsDBNull(7) ? now : reader.GetDateTime(7),
                            reasonDetails = reader.IsDBNull(4) ? "" : reader.GetString(4),
                            canResolve = true
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
                    pendingVerificationRequests,
                    systemHealth,
                    eventModeration,
                    accountModeration,
                    verificationQueue,
                    bookingStatusCounts,
                    topEvents,
                    topTalent,
                    bookingLogs,
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

        [HttpGet("verification/{requestId}/asset/{assetType}")]
        public async Task<IActionResult> GetVerificationAsset(Guid requestId, string assetType)
        {
            try
            {
                ApplySensitiveResponseHeaders();
                var normalizedAssetType = (assetType ?? string.Empty).Trim().ToLowerInvariant();
                var allowedAssets = new Dictionary<string, (string Query, string Label)>(StringComparer.OrdinalIgnoreCase)
                {
                    ["front"] = ("""
                        SELECT COALESCE(id_image_front, '')
                        FROM talent_verification_requests
                        WHERE id = @id
                        LIMIT 1;
                        """, "ID Front"),
                    ["back"] = ("""
                        SELECT COALESCE(id_image_back, '')
                        FROM talent_verification_requests
                        WHERE id = @id
                        LIMIT 1;
                        """, "ID Back"),
                    ["selfie"] = ("""
                        SELECT COALESCE(selfie_image, '')
                        FROM talent_verification_requests
                        WHERE id = @id
                        LIMIT 1;
                        """, "Selfie Verification")
                };

                if (!allowedAssets.TryGetValue(normalizedAssetType, out var assetMeta))
                {
                    return BadRequest(new { message = "Invalid verification asset type." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                await using var cmd = new NpgsqlCommand(assetMeta.Query, connection);
                cmd.Parameters.AddWithValue("@id", requestId);
                var rawValue = Convert.ToString(await cmd.ExecuteScalarAsync() ?? string.Empty) ?? string.Empty;
                var imageDataUrl = SecuritySupport.RevealSensitiveData(rawValue, _connectionString);
                if (string.IsNullOrWhiteSpace(imageDataUrl))
                {
                    return NotFound(new { message = "Verification asset not found." });
                }

                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    TryReadActorUserId(),
                    TryReadActorRole() ?? "Admin",
                    "identity_verification_asset_viewed",
                    "verification_request",
                    requestId,
                    HttpContext,
                    $"Admin opened {assetMeta.Label} for verification request {requestId}.");

                return Ok(new
                {
                    assetType = normalizedAssetType,
                    label = assetMeta.Label,
                    imageDataUrl
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load verification asset: " + ex.Message });
            }
        }

        [HttpPost("verification/{requestId}/review")]
        public async Task<IActionResult> ReviewVerification(Guid requestId, [FromBody] ReviewVerificationRequest req)
        {
            try
            {
                var action = (req.action ?? string.Empty).Trim().ToLowerInvariant();
                if (action != "approve" && action != "reject")
                {
                    return BadRequest(new { message = "Invalid verification review action." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                Guid userId = Guid.Empty;
                string role = string.Empty;
                string displayName = "there";
                string idType = string.Empty;
                string idLast4 = string.Empty;

                const string lookupSql = @"
                    SELECT
                        tvr.user_id,
                        COALESCE(tvr.role, ''),
                        COALESCE(tvr.id_type, ''),
                        COALESCE(tvr.id_number_last4, ''),
                        COALESCE(u.stagename, ''),
                        COALESCE(u.productionname, ''),
                        COALESCE(u.firstname, ''),
                        COALESCE(u.lastname, '')
                    FROM talent_verification_requests tvr
                    LEFT JOIN users u ON u.id = tvr.user_id
                    WHERE tvr.id = @id
                    LIMIT 1;";

                await using (var lookupCmd = new NpgsqlCommand(lookupSql, connection))
                {
                    lookupCmd.Parameters.AddWithValue("@id", requestId);
                    await using var reader = await lookupCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Verification request not found." });
                    }

                    userId = reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0);
                    role = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    idType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    idLast4 = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    var stageName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    var productionName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                    var firstName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                    var lastName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
                    displayName = !string.IsNullOrWhiteSpace(productionName)
                        ? productionName
                        : !string.IsNullOrWhiteSpace(stageName)
                            ? stageName
                            : $"{firstName} {lastName}".Trim();
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = "there";
                    }
                }

                var adminNotes = SecuritySupport.SanitizePlainText(req.notes, 1500, true);
                var requestStatus = action == "approve" ? "Approved" : "Rejected";
                var reviewStatus = action == "approve" ? "Verified" : "Rejected";
                var userVerificationStatus = action == "approve" ? "Approved" : "Rejected";
                var userVerificationLevel = action == "approve" ? "Identity Verified" : "Verification Rejected";
                var userVerificationMethod = "Philippine ID + Facial Review";
                var userVerificationNotes = string.IsNullOrWhiteSpace(adminNotes)
                    ? (action == "approve"
                        ? $"Admin approved {idType} verification ending in {idLast4}."
                        : "Admin rejected the submitted Philippine ID and facial verification evidence.")
                    : adminNotes;

                const string updateRequestSql = @"
                    UPDATE talent_verification_requests
                    SET status = @status,
                        admin_notes = @adminNotes,
                        reviewed_at = NOW(),
                        id_review_status = @idReviewStatus,
                        facial_review_status = @facialReviewStatus
                    WHERE id = @id;";
                await using (var requestCmd = new NpgsqlCommand(updateRequestSql, connection))
                {
                    requestCmd.Parameters.AddWithValue("@id", requestId);
                    requestCmd.Parameters.AddWithValue("@status", requestStatus);
                    requestCmd.Parameters.AddWithValue("@adminNotes", (object?)adminNotes ?? DBNull.Value);
                    requestCmd.Parameters.AddWithValue("@idReviewStatus", reviewStatus);
                    requestCmd.Parameters.AddWithValue("@facialReviewStatus", reviewStatus);
                    var affected = await requestCmd.ExecuteNonQueryAsync();
                    if (affected == 0)
                    {
                        return NotFound(new { message = "Verification request not found." });
                    }
                }

                const string updateUserSql = @"
                    UPDATE users
                    SET verification_status = @verificationStatus,
                        verification_level = @verificationLevel,
                        verification_method = @verificationMethod,
                        verification_notes = @verificationNotes,
                        verification_reviewed_at = NOW()
                    WHERE id = @userId;";
                await using (var userCmd = new NpgsqlCommand(updateUserSql, connection))
                {
                    userCmd.Parameters.AddWithValue("@userId", userId);
                    userCmd.Parameters.AddWithValue("@verificationStatus", userVerificationStatus);
                    userCmd.Parameters.AddWithValue("@verificationLevel", userVerificationLevel);
                    userCmd.Parameters.AddWithValue("@verificationMethod", userVerificationMethod);
                    userCmd.Parameters.AddWithValue("@verificationNotes", userVerificationNotes);
                    await userCmd.ExecuteNonQueryAsync();
                }

                await CommunitySupport.SyncProfileVerificationFromDatabaseAsync(connection, userId, role);

                await NotificationSupport.InsertNotificationAsync(
                    connection,
                    userId,
                    "verification_review",
                    action == "approve" ? "Identity verification approved" : "Identity verification needs attention",
                    action == "approve"
                        ? "Your Philippine ID and facial verification were approved by the admin team."
                        : $"Your Philippine ID and facial verification were rejected. {userVerificationNotes}",
                    requestId,
                    "verification_request");

                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    TryReadActorUserId(),
                    TryReadActorRole() ?? "Admin",
                    action == "approve" ? "identity_verification_approved" : "identity_verification_rejected",
                    "verification_request",
                    requestId,
                    HttpContext,
                    $"Admin {action}d {role} identity verification for {displayName}.");

                return Ok(new
                {
                    message = action == "approve"
                        ? "Identity verification approved."
                        : "Identity verification rejected.",
                    status = requestStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to review verification request: " + ex.Message });
            }
        }

        [HttpPost("refunds/{requestId}/resolve")]
        public async Task<IActionResult> ResolveRefund(Guid requestId, [FromBody] ReviewRefundRequest req)
        {
            try
            {
                var action = (req.action ?? string.Empty).Trim().ToLowerInvariant();
                if (action != "refunded" && action != "failed")
                {
                    return BadRequest(new { message = "Invalid refund resolution action." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketMonitoringColumnsAsync(connection);
                await PaymentRefundService.EnsureSchemaAsync(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                Guid? ticketId = null;
                Guid? beneficiaryUserId = null;
                string refundScope = string.Empty;
                decimal amount = 0m;

                const string lookupSql = @"
                    SELECT refund_scope,
                           ticket_id,
                           beneficiary_user_id,
                           COALESCE(amount, 0),
                           COALESCE(status, 'Requested')
                    FROM refund_requests
                    WHERE id = @id
                    LIMIT 1;";

                await using (var cmd = new NpgsqlCommand(lookupSql, connection))
                {
                    cmd.Parameters.AddWithValue("@id", requestId);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Refund request not found." });
                    }

                    refundScope = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    ticketId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
                    beneficiaryUserId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
                    amount = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3);
                    var currentStatus = reader.IsDBNull(4) ? "Requested" : reader.GetString(4);
                    if (currentStatus is "Refunded" or "Failed")
                    {
                        return BadRequest(new { message = "This refund request has already been resolved." });
                    }
                }

                if (!string.Equals(refundScope, "ticket", StringComparison.OrdinalIgnoreCase) || !ticketId.HasValue)
                {
                    return BadRequest(new { message = "Only ticket refund requests can be resolved here." });
                }

                var targetTicketStatus = action == "refunded" ? "Refunded" : "Refund Failed";
                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE tickets
                    SET refund_status = @status,
                        refund_amount = @amount,
                        refund_reason = COALESCE(NULLIF(@notes, ''), refund_reason),
                        refund_processed_at = CASE WHEN @status = 'Refunded' THEN NOW() ELSE refund_processed_at END
                    WHERE id = @ticketId;", connection))
                {
                    cmd.Parameters.AddWithValue("@status", targetTicketStatus);
                    cmd.Parameters.AddWithValue("@amount", amount);
                    cmd.Parameters.AddWithValue("@notes", (object?)(req.notes ?? string.Empty) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ticketId", ticketId.Value);
                    await cmd.ExecuteNonQueryAsync();
                }

                await PaymentRefundService.UpdateRefundRequestAsync(
                    connection,
                    requestId,
                    status: action == "refunded" ? "Refunded" : "Failed",
                    providerRefundId: null,
                    providerStatus: "AdminResolved",
                    errorCode: action == "failed" ? "admin_review" : null,
                    errorMessage: string.IsNullOrWhiteSpace(req.notes)
                        ? (action == "refunded" ? "Resolved by admin review." : "Refund failed after admin review.")
                        : req.notes);

                if (action == "refunded")
                {
                    await PaymentLedgerService.MarkRefundedAsync(connection, "ticket_purchase", ticketId.Value, null, "Refunded");
                }

                if (beneficiaryUserId.HasValue && beneficiaryUserId.Value != Guid.Empty)
                {
                    await NotificationSupport.InsertNotificationAsync(
                        connection,
                        beneficiaryUserId.Value,
                        "ticket_refund",
                        action == "refunded" ? "Ticket refund completed" : "Ticket refund update",
                        action == "refunded"
                            ? "Your ticket refund was approved and marked as refunded."
                            : $"Your ticket refund request could not be completed. {(string.IsNullOrWhiteSpace(req.notes) ? "Please contact support for more details." : req.notes)}",
                        ticketId,
                        "ticket");
                }

                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    TryReadActorUserId(),
                    TryReadActorRole() ?? "Admin",
                    "ticket_refund_resolved",
                    "refund_request",
                    requestId,
                    HttpContext,
                    $"Admin marked ticket refund request {requestId} as {targetTicketStatus}.");

                return Ok(new
                {
                    message = action == "refunded"
                        ? "Ticket refund marked as refunded."
                        : "Ticket refund marked as failed.",
                    status = targetTicketStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to resolve refund request: " + ex.Message });
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

        private void ApplySensitiveResponseHeaders()
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["X-Frame-Options"] = "DENY";
            Response.Headers["Referrer-Policy"] = "no-referrer";
        }

        private static async Task EnsureBookingMonitoringColumnsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS bookings (
                    id uuid PRIMARY KEY,
                    service_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    talent_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    talent_platform_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    payment_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    booking_fee numeric NULL,
                    budget numeric NULL,
                    talent_platform_fee numeric NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );

                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS payment_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS booking_fee numeric NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS budget numeric NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee numeric NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT NOW();";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureTicketMonitoringColumnsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS refund_status varchar(30) NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS refund_amount numeric(12,2) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS refund_reason text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS refund_processed_at timestamptz NULL;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static int GetAnonymousInt(object source, string propertyName)
        {
            var prop = source.GetType().GetProperty(propertyName);
            if (prop?.GetValue(source) is null) return 0;
            return Convert.ToInt32(prop.GetValue(source));
        }

        private static decimal GetAnonymousDecimal(object source, string propertyName)
        {
            var prop = source.GetType().GetProperty(propertyName);
            if (prop?.GetValue(source) is null) return 0m;
            return Convert.ToDecimal(prop.GetValue(source));
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
