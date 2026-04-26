using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using ImajinationAPI.Models;
using ImajinationAPI.Services;
using Npgsql;
using NpgsqlTypes;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace ImajinationAPI.Controllers
{
    public class CreateBookingRequest
    {
        public Guid customerId { get; set; }
        public Guid targetUserId { get; set; }
        public Guid? eventId { get; set; }
        public string? requesterRole { get; set; }
        public string? targetRole { get; set; }
        public string? serviceType { get; set; }
        public string? eventTitle { get; set; }
        public DateTime? eventDate { get; set; }
        public DateTime? eventEndTime { get; set; }
        public string? location { get; set; }
        public decimal? budget { get; set; }
        public string? notes { get; set; }
        public string? message { get; set; }
        public List<BookingSlotRequest>? bookingSlots { get; set; }
    }

    public class BookingSlotRequest
    {
        public DateTime? eventDate { get; set; }
        public DateTime? eventEndTime { get; set; }
    }

    public class UpdateBookingStatusRequest
    {
        public string? status { get; set; }
    }

    public class BookingCheckoutRequest
    {
        public string? successUrl { get; set; }
        public string? cancelUrl { get; set; }
        public string? paymentType { get; set; }
    }

    public class UpdateBookingContractRequest
    {
        public string? title { get; set; }
        public string? terms { get; set; }
        public decimal? agreedFee { get; set; }
        public string? contractStatus { get; set; }
    }

    public class CreateBookingRefundRequest
    {
        public string? reasonCode { get; set; }
        public string? notes { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class BookingController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly string _paymongoSecretKey;
        private readonly MessageProtectionService _messageProtection;
        private readonly IConfiguration _configuration;
        private const decimal BookingServiceFee = 20m;
        private const decimal PayMongoMinimumAmount = 20m;
        private const decimal TalentPlatformFeeRate = 0.08m;
        private const decimal TalentPlatformFeeMinimum = 20m;

        public BookingController(IConfiguration configuration, MessageProtectionService messageProtection)
        {
            _configuration = configuration;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _paymongoSecretKey = configuration["PayMongo:SecretKey"] ?? string.Empty;
            _messageProtection = messageProtection;
        }

        private static string NormalizePaymentStatus(string? rawStatus)
        {
            if (string.IsNullOrWhiteSpace(rawStatus))
            {
                return "Unpaid";
            }

            return rawStatus.Trim();
        }

        private static string NormalizeServiceFeeStatus(string? rawServiceFeeStatus, string? rawPaymentStatus)
        {
            var serviceStatus = NormalizePaymentStatus(rawServiceFeeStatus);
            var paymentStatus = NormalizePaymentStatus(rawPaymentStatus);

            if (serviceStatus.Equals("ServiceFeePaid", StringComparison.OrdinalIgnoreCase))
            {
                return "Paid";
            }

            if (serviceStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) ||
                serviceStatus.Equals("AwaitingPayment", StringComparison.OrdinalIgnoreCase) ||
                serviceStatus.Equals("NotRequired", StringComparison.OrdinalIgnoreCase) ||
                serviceStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase) ||
                serviceStatus.Equals("Unpaid", StringComparison.OrdinalIgnoreCase))
            {
                return serviceStatus;
            }

            if (paymentStatus.Equals("ServiceFeePaid", StringComparison.OrdinalIgnoreCase))
            {
                return "Paid";
            }

            if (paymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) ||
                paymentStatus.Equals("AwaitingPayment", StringComparison.OrdinalIgnoreCase) ||
                paymentStatus.Equals("NotRequired", StringComparison.OrdinalIgnoreCase) ||
                paymentStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase))
            {
                return paymentStatus;
            }

            return "Unpaid";
        }

        private static string NormalizeTalentFeeStatus(string? rawTalentFeeStatus)
        {
            var talentStatus = NormalizePaymentStatus(rawTalentFeeStatus);

            if (talentStatus.Equals("AwaitingTalentFeePayment", StringComparison.OrdinalIgnoreCase))
            {
                return "AwaitingPayment";
            }

            return talentStatus;
        }

        private static Guid? GetActorUserId(ClaimsPrincipal user)
        {
            return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
                ? parsedUserId
                : null;
        }

        private static string GetActorRole(ClaimsPrincipal user)
        {
            var role = user.FindFirstValue(ClaimTypes.Role);
            return string.IsNullOrWhiteSpace(role) ? "User" : role.Trim();
        }

        private static async Task<int> CountActiveBookingsForCustomerAsync(NpgsqlConnection connection, Guid customerId, string targetRole)
        {
            const string sql = @"
                SELECT COUNT(*)
                FROM bookings
                WHERE customer_id = @customerId
                  AND LOWER(COALESCE(target_role, '')) = LOWER(@targetRole)
                  AND COALESCE(status, 'Pending') NOT ILIKE 'Cancelled%'
                  AND LOWER(COALESCE(status, 'Pending')) <> 'completed';";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = customerId;
            cmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = targetRole;
            var result = await cmd.ExecuteScalarAsync();
            return result is null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        private static DateTime? NormalizeBookingEndToUtc(DateTime? eventDate, DateTime? eventEndTime)
        {
            var normalizedStart = PlatformFeatureSupport.NormalizeToUtc(eventDate);
            if (!normalizedStart.HasValue)
            {
                return null;
            }

            var normalizedEnd = PlatformFeatureSupport.NormalizeToUtc(eventEndTime);
            if (!normalizedEnd.HasValue || normalizedEnd <= normalizedStart)
            {
                return normalizedStart.Value.AddHours(4);
            }

            return normalizedEnd;
        }

        private static decimal CalculateTalentPlatformFee(string targetRole, decimal? budget)
        {
            if (!string.Equals(targetRole, "Artist", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetRole, "Sessionist", StringComparison.OrdinalIgnoreCase))
            {
                return 0m;
            }

            var normalizedBudget = budget.GetValueOrDefault();
            if (normalizedBudget <= 0)
            {
                return 0m;
            }

            return decimal.Round(decimal.Max(TalentPlatformFeeMinimum, normalizedBudget * TalentPlatformFeeRate), 2);
        }

        [HttpPost]
        public async Task<IActionResult> CreateBooking([FromBody] CreateBookingRequest req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);

                if (req.customerId == Guid.Empty || req.targetUserId == Guid.Empty)
                {
                    return BadRequest(new { message = "Missing booking participants." });
                }

                var normalizedRole = NormalizeTargetRole(req.targetRole);
                var normalizedRequesterRole = string.Equals(req.requesterRole, "Organizer", StringComparison.OrdinalIgnoreCase) ? "Organizer" : "Customer";
                if ((string.Equals(normalizedRole, "Artist", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalizedRole, "Sessionist", StringComparison.OrdinalIgnoreCase)) &&
                    !await CommunitySupport.IsIdentityApprovedAsync(connection, req.targetUserId, normalizedRole))
                {
                    return Conflict(new { message = $"{normalizedRole} cannot receive bookings until admin verification is approved." });
                }

                var notes = string.IsNullOrWhiteSpace(req.notes) ? req.message : req.notes;
                var protectedNotes = string.IsNullOrWhiteSpace(notes) ? null : _messageProtection.Protect(notes);
                var bookingFee = normalizedRequesterRole == "Organizer" ? 0m : BookingServiceFee;
                var talentPlatformFee = CalculateTalentPlatformFee(normalizedRole, req.budget);
                var initialStatus = normalizedRequesterRole == "Organizer"
                    ? $"Pending {normalizedRole} Approval"
                    : $"Awaiting {normalizedRole} Fee Payment";
                var paymentStatus = normalizedRequesterRole == "Organizer" ? "NotRequired" : "Unpaid";
                var serviceFeeStatus = normalizedRequesterRole == "Organizer" ? "NotRequired" : "Unpaid";
                var talentPlatformFeeStatus = normalizedRequesterRole == "Organizer" && talentPlatformFee <= 0 ? "NotRequired" : (talentPlatformFee > 0 ? "Unpaid" : "NotRequired");
                await EnsureNotificationsTableExists(connection);

                var bookingWindows = new List<(DateTime Start, DateTime End)>();
                if (req.bookingSlots is { Count: > 0 })
                {
                    foreach (var slot in req.bookingSlots.Where(item => item?.eventDate != null))
                    {
                        var normalizedStart = NormalizeToUtc(slot.eventDate);
                        var normalizedEnd = NormalizeBookingEndToUtc(slot.eventDate, slot.eventEndTime);
                        if (!normalizedStart.HasValue || !normalizedEnd.HasValue)
                        {
                            continue;
                        }

                        bookingWindows.Add((normalizedStart.Value, normalizedEnd.Value));
                    }
                }

                if (bookingWindows.Count == 0)
                {
                    var normalizedStart = NormalizeToUtc(req.eventDate);
                    var normalizedEnd = NormalizeBookingEndToUtc(req.eventDate, req.eventEndTime);
                    if (normalizedStart.HasValue && normalizedEnd.HasValue)
                    {
                        bookingWindows.Add((normalizedStart.Value, normalizedEnd.Value));
                    }
                }

                if (bookingWindows.Count == 0)
                {
                    return BadRequest(new { message = "Add at least one booking date and end time." });
                }

                if (normalizedRequesterRole == "Customer")
                {
                    var activeBookingsForRole = await CountActiveBookingsForCustomerAsync(connection, req.customerId, normalizedRole);
                    if (activeBookingsForRole + bookingWindows.Count > 20)
                    {
                        return Conflict(new
                        {
                            message = $"You can only keep 20 active {normalizedRole.ToLowerInvariant()} bookings at the same time. Finish or cancel an existing request first."
                        });
                    }
                }

                foreach (var window in bookingWindows)
                {
                    if (await PlatformFeatureSupport.UserHasCalendarBlockAsync(connection, req.targetUserId, normalizedRole, window.Start))
                    {
                        return Conflict(new { message = $"{normalizedRole} blocked {window.Start:MMMM d, yyyy} from bookings." });
                    }

                    if (await PlatformFeatureSupport.UserHasBookingConflictAsync(connection, req.targetUserId, window.Start, window.End))
                    {
                        return Conflict(new { message = $"{normalizedRole} is already booked during one of the selected time slots." });
                    }
                }

                const string sql = @"
                    INSERT INTO bookings
                        (id, customer_id, target_user_id, event_id, target_role, service_type, event_title, event_date, event_end_time, location, budget, booking_fee, notes, message, status, payment_status, service_fee_status, talent_platform_fee, talent_platform_fee_status, booking_group_id, booking_sequence, created_at)
                    VALUES
                        (@id, @customerId, @targetUserId, @eventId, @targetRole, @serviceType, @eventTitle, @eventDate, @eventEndTime, @location, @budget, @bookingFee, @notes, @message, @status, @paymentStatus, @serviceFeeStatus, @talentPlatformFee, @talentPlatformFeeStatus, @bookingGroupId, @bookingSequence, NOW());";

                var bookingGroupId = bookingWindows.Count > 1 ? Guid.NewGuid() : (Guid?)null;
                var bookingIds = new List<Guid>();

                for (var index = 0; index < bookingWindows.Count; index++)
                {
                    var bookingId = Guid.NewGuid();
                    var window = bookingWindows[index];
                    using var cmd = new NpgsqlCommand(sql, connection);
                    cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                    cmd.Parameters.Add("@targetUserId", NpgsqlDbType.Uuid).Value = req.targetUserId;
                    cmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = (object?)req.eventId ?? DBNull.Value;
                    cmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = normalizedRole;
                    cmd.Parameters.Add("@serviceType", NpgsqlDbType.Text).Value = (object?)req.serviceType ?? DBNull.Value;
                    cmd.Parameters.Add("@eventTitle", NpgsqlDbType.Text).Value = (object?)req.eventTitle ?? DBNull.Value;
                    cmd.Parameters.Add("@eventDate", NpgsqlDbType.TimestampTz).Value = window.Start;
                    cmd.Parameters.Add("@eventEndTime", NpgsqlDbType.TimestampTz).Value = window.End;
                    cmd.Parameters.Add("@location", NpgsqlDbType.Text).Value = (object?)req.location ?? DBNull.Value;
                    cmd.Parameters.Add("@budget", NpgsqlDbType.Numeric).Value = (object?)req.budget ?? DBNull.Value;
                    cmd.Parameters.Add("@bookingFee", NpgsqlDbType.Numeric).Value = bookingFee;
                    cmd.Parameters.Add("@notes", NpgsqlDbType.Text).Value = (object?)protectedNotes ?? DBNull.Value;
                    cmd.Parameters.Add("@message", NpgsqlDbType.Text).Value = (object?)protectedNotes ?? DBNull.Value;
                    cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = initialStatus;
                    cmd.Parameters.Add("@paymentStatus", NpgsqlDbType.Text).Value = paymentStatus;
                    cmd.Parameters.Add("@serviceFeeStatus", NpgsqlDbType.Text).Value = serviceFeeStatus;
                    cmd.Parameters.Add("@talentPlatformFee", NpgsqlDbType.Numeric).Value = talentPlatformFee;
                    cmd.Parameters.Add("@talentPlatformFeeStatus", NpgsqlDbType.Text).Value = talentPlatformFeeStatus;
                    cmd.Parameters.Add("@bookingGroupId", NpgsqlDbType.Uuid).Value = (object?)bookingGroupId ?? DBNull.Value;
                    cmd.Parameters.Add("@bookingSequence", NpgsqlDbType.Integer).Value = index + 1;
                    await cmd.ExecuteNonQueryAsync();
                    bookingIds.Add(bookingId);

                    await InsertNotification(
                        connection,
                        req.targetUserId,
                        "booking_request",
                        "New booking request",
                        $"{(string.IsNullOrWhiteSpace(req.eventTitle) ? "A new booking request" : req.eventTitle)} is waiting for your review.",
                        bookingId,
                        "booking");
                }

                return Ok(new
                {
                    message = normalizedRequesterRole == "Organizer"
                        ? (bookingIds.Count > 1 ? $"{bookingIds.Count} booking requests created and sent to the talent." : "Booking request created and sent to the talent.")
                        : bookingIds.Count > 1
                            ? $"{bookingIds.Count} booking requests were created. Continue with the first service-fee payment to start the first chat thread."
                            : $"Booking created. Pay the {FormatPeso(BookingServiceFee)} service fee to open the chat.",
                    bookingId = bookingIds.FirstOrDefault(),
                    bookingIds,
                    bookingCount = bookingIds.Count,
                    eventId = req.eventId,
                    bookingFee = bookingFee,
                    talentPlatformFee,
                    status = initialStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create booking request: " + ex.Message });
            }
        }

        [HttpGet("target/{userId}")]
        public async Task<IActionResult> GetBookingsForTarget(Guid userId)
        {
            try
            {
                var bookings = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureBookingMessagesTableExists(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.customer_id,
                        b.target_user_id,
                        b.event_id,
                        COALESCE(u.firstname, '') AS customer_firstname,
                        COALESCE(u.lastname, '') AS customer_lastname,
                        COALESCE(u.email, '') AS customer_email,
                        COALESCE(b.service_type, ''),
                        COALESCE(b.event_title, ''),
                        b.event_date,
                        COALESCE(b.location, ''),
                        COALESCE(b.budget, 0),
                        COALESCE(b.booking_fee, 15),
                        COALESCE(b.notes, COALESCE(b.message, '')),
                        COALESCE(b.message, ''),
                        COALESCE(b.status, 'Pending'),
                        b.created_at,
                        COALESCE(b.payment_status, 'Unpaid'),
                        COALESCE(b.payment_method, ''),
                        COALESCE(b.service_fee_status, COALESCE(b.payment_status, 'Unpaid')),
                        COALESCE(b.talent_fee_status, 'Unpaid'),
                        COALESCE(b.service_fee_payment_method, COALESCE(b.payment_method, '')),
                        COALESCE(b.talent_fee_payment_method, ''),
                        EXISTS (
                            SELECT 1
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                        ) AS has_messages
                    FROM bookings b
                    LEFT JOIN users u ON u.id = b.customer_id
                    WHERE b.target_user_id = @userId
                    ORDER BY b.created_at DESC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var firstName = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    var lastName = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    var decryptedNotes = _messageProtection.Unprotect(reader.IsDBNull(13) ? "" : reader.GetString(13));
                    var decryptedMessage = _messageProtection.Unprotect(reader.IsDBNull(14) ? "" : reader.GetString(14));

                    bookings.Add(new
                    {
                        id = reader.GetGuid(0),
                        customerId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
                        targetUserId = reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2),
                        eventId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3),
                        customerName = $"{firstName} {lastName}".Trim(),
                        customerEmail = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        serviceType = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        eventTitle = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        eventDate = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9),
                        location = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        budget = reader.IsDBNull(11) ? 0 : reader.GetDecimal(11),
                        bookingFee = reader.IsDBNull(12) ? BookingServiceFee : reader.GetDecimal(12),
                        notes = decryptedNotes,
                        message = decryptedMessage,
                        status = reader.IsDBNull(15) ? "Pending" : reader.GetString(15),
                        createdAt = reader.IsDBNull(16) ? DateTime.UtcNow : reader.GetDateTime(16),
                        paymentStatus = NormalizePaymentStatus(reader.IsDBNull(17) ? "Unpaid" : reader.GetString(17)),
                        paymentMethod = reader.IsDBNull(18) ? "" : reader.GetString(18),
                        serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(19) ? "Unpaid" : reader.GetString(19), reader.IsDBNull(17) ? "Unpaid" : reader.GetString(17)),
                        talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(20) ? "Unpaid" : reader.GetString(20)),
                        serviceFeePaymentMethod = reader.IsDBNull(21) ? "" : reader.GetString(21),
                        talentFeePaymentMethod = reader.IsDBNull(22) ? "" : reader.GetString(22),
                        hasMessages = !reader.IsDBNull(23) && reader.GetBoolean(23)
                    });
                }

                return Ok(bookings);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to fetch bookings: " + ex.Message });
            }
        }

        [HttpGet("customer/{userId}")]
        public async Task<IActionResult> GetBookingsForCustomer(Guid userId)
        {
            try
            {
                var bookings = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureBookingMessagesTableExists(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.customer_id,
                        b.target_user_id,
                        b.event_id,
                        COALESCE(t.firstname, '') AS target_firstname,
                        COALESCE(t.lastname, '') AS target_lastname,
                        COALESCE(t.stagename, '') AS target_stage_name,
                        COALESCE(b.target_role, ''),
                        COALESCE(b.service_type, ''),
                        COALESCE(b.event_title, ''),
                        b.event_date,
                        COALESCE(b.location, ''),
                        COALESCE(b.budget, 0),
                        COALESCE(b.booking_fee, 15),
                        COALESCE(b.notes, COALESCE(b.message, '')),
                        COALESCE(b.message, ''),
                        COALESCE(b.status, 'Pending'),
                        b.created_at,
                        COALESCE(b.payment_status, 'Unpaid'),
                        COALESCE(b.payment_method, ''),
                        COALESCE(b.service_fee_status, COALESCE(b.payment_status, 'Unpaid')),
                        COALESCE(b.talent_fee_status, 'Unpaid'),
                        COALESCE(b.service_fee_payment_method, COALESCE(b.payment_method, '')),
                        COALESCE(b.talent_fee_payment_method, ''),
                        EXISTS (
                            SELECT 1
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                        ) AS has_messages
                    FROM bookings b
                    LEFT JOIN users t ON t.id = b.target_user_id
                    WHERE b.customer_id = @userId
                    ORDER BY b.created_at DESC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var targetFirst = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    var targetLast = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    var targetStage = reader.IsDBNull(6) ? "" : reader.GetString(6);
                    var decryptedNotes = _messageProtection.Unprotect(reader.IsDBNull(14) ? "" : reader.GetString(14));
                    var decryptedMessage = _messageProtection.Unprotect(reader.IsDBNull(15) ? "" : reader.GetString(15));

                    bookings.Add(new
                    {
                        id = reader.GetGuid(0),
                        customerId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
                        targetUserId = reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2),
                        eventId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3),
                        targetName = !string.IsNullOrWhiteSpace(targetStage) ? targetStage : $"{targetFirst} {targetLast}".Trim(),
                        targetRole = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        serviceType = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        eventTitle = reader.IsDBNull(9) ? "" : reader.GetString(9),
                        eventDate = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10),
                        location = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        budget = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12),
                        bookingFee = reader.IsDBNull(13) ? BookingServiceFee : reader.GetDecimal(13),
                        notes = decryptedNotes,
                        message = decryptedMessage,
                        status = reader.IsDBNull(16) ? "Pending" : reader.GetString(16),
                        createdAt = reader.IsDBNull(17) ? DateTime.UtcNow : reader.GetDateTime(17),
                        paymentStatus = NormalizePaymentStatus(reader.IsDBNull(18) ? "Unpaid" : reader.GetString(18)),
                        paymentMethod = reader.IsDBNull(19) ? "" : reader.GetString(19),
                        serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(20) ? "Unpaid" : reader.GetString(20), reader.IsDBNull(18) ? "Unpaid" : reader.GetString(18)),
                        talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(21) ? "Unpaid" : reader.GetString(21)),
                        serviceFeePaymentMethod = reader.IsDBNull(22) ? "" : reader.GetString(22),
                        talentFeePaymentMethod = reader.IsDBNull(23) ? "" : reader.GetString(23),
                        hasMessages = !reader.IsDBNull(24) && reader.GetBoolean(24)
                    });
                }

                return Ok(bookings);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to fetch customer bookings: " + ex.Message });
            }
        }

        [HttpGet("organizer/{userId}")]
        public async Task<IActionResult> GetBookingsForOrganizer(Guid userId)
        {
            try
            {
                var bookings = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureBookingMessagesTableExists(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.event_id,
                        COALESCE(b.event_title, COALESCE(e.title, 'Booking Request')),
                        b.event_date,
                        COALESCE(b.location, COALESCE(e.location, '')),
                        COALESCE(b.service_type, ''),
                        COALESCE(b.status, 'Pending'),
                        COALESCE(b.payment_status, 'Unpaid'),
                        COALESCE(b.service_fee_status, COALESCE(b.payment_status, 'Unpaid')),
                        COALESCE(b.talent_fee_status, 'Unpaid'),
                        COALESCE(b.booking_fee, 0),
                        COALESCE(b.budget, 0),
                        b.created_at,
                        COALESCE(b.target_role, ''),
                        COALESCE(t.stagename, ''),
                        COALESCE(t.firstname, ''),
                        COALESCE(t.lastname, ''),
                        COALESCE(c.firstname, ''),
                        COALESCE(c.lastname, ''),
                        EXISTS (
                            SELECT 1
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                        ) AS has_messages
                    FROM bookings b
                    LEFT JOIN events e ON e.id = b.event_id
                    LEFT JOIN users t ON t.id = b.target_user_id
                    LEFT JOIN users c ON c.id = b.customer_id
                    WHERE e.organizer_id = @userId
                       OR (b.customer_id = @userId AND COALESCE(b.target_role, '') IN ('Artist', 'Sessionist'))
                    ORDER BY b.created_at DESC;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var targetStageName = reader.IsDBNull(14) ? string.Empty : reader.GetString(14);
                    var targetFullName = $"{(reader.IsDBNull(15) ? string.Empty : reader.GetString(15))} {(reader.IsDBNull(16) ? string.Empty : reader.GetString(16))}".Trim();
                    var requesterName = $"{(reader.IsDBNull(17) ? string.Empty : reader.GetString(17))} {(reader.IsDBNull(18) ? string.Empty : reader.GetString(18))}".Trim();

                    bookings.Add(new
                    {
                        id = reader.GetGuid(0),
                        eventId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1),
                        eventTitle = reader.IsDBNull(2) ? "Booking Request" : reader.GetString(2),
                        eventDate = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                        location = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        serviceType = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                        status = reader.IsDBNull(6) ? "Pending" : reader.GetString(6),
                        paymentStatus = NormalizePaymentStatus(reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7)),
                        serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(8) ? "Unpaid" : reader.GetString(8), reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7)),
                        talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(9) ? "Unpaid" : reader.GetString(9)),
                        bookingFee = reader.IsDBNull(10) ? 0m : reader.GetDecimal(10),
                        budget = reader.IsDBNull(11) ? 0m : reader.GetDecimal(11),
                        createdAt = reader.IsDBNull(12) ? DateTime.UtcNow : reader.GetDateTime(12),
                        targetRole = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                        targetName = !string.IsNullOrWhiteSpace(targetStageName) ? targetStageName : (string.IsNullOrWhiteSpace(targetFullName) ? "Unknown Talent" : targetFullName),
                        requesterName = string.IsNullOrWhiteSpace(requesterName) ? "Unknown Requester" : requesterName,
                        hasMessages = !reader.IsDBNull(19) && reader.GetBoolean(19)
                    });
                }

                return Ok(bookings);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to fetch organizer bookings: " + ex.Message });
            }
        }

        [HttpPatch("{bookingId}/status")]
        public async Task<IActionResult> UpdateBookingStatus(Guid bookingId, [FromBody] UpdateBookingStatusRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.status))
                {
                    return BadRequest(new { message = "Booking status is required." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureNotificationsTableExists(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await PaymentRefundService.EnsureSchemaAsync(connection);
                await EnsureEventBookingSupportExists(connection);

                string targetRole;
                string paymentStatus;
                string serviceFeeStatus;
                string currentStatus;
                string talentFeeStatus;
                string talentPlatformFeeStatus;
                decimal talentPlatformFee;
                decimal budget;
                Guid customerId;
                Guid targetUserId;
                Guid? eventId;
                string eventTitle;
                DateTime? eventDate;
                DateTime? eventEndTime;
                string location;

                const string loadSql = @"
                    SELECT COALESCE(target_role, 'Artist'),
                           COALESCE(payment_status, 'Unpaid'),
                           COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                           COALESCE(status, 'Pending'),
                           COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee, 0),
                           COALESCE(budget, 0),
                           customer_id,
                           target_user_id,
                           event_id,
                           COALESCE(event_title, 'Booking Request'),
                           event_date,
                           event_end_time,
                           COALESCE(location, '')
                    FROM bookings
                    WHERE id = @id";
                using (var loadCmd = new NpgsqlCommand(loadSql, connection))
                {
                    loadCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    using var reader = await loadCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking request not found." });
                    }

                    targetRole = reader.IsDBNull(0) ? "Artist" : NormalizeTargetRole(reader.GetString(0));
                    paymentStatus = NormalizePaymentStatus(reader.IsDBNull(1) ? "Unpaid" : reader.GetString(1));
                    serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(2) ? paymentStatus : reader.GetString(2), paymentStatus);
                    currentStatus = reader.IsDBNull(3) ? "Pending" : reader.GetString(3);
                    talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(4) ? "Unpaid" : reader.GetString(4));
                    talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(5) ? "Unpaid" : reader.GetString(5));
                    talentPlatformFee = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6);
                    budget = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7);
                    customerId = reader.IsDBNull(8) ? Guid.Empty : reader.GetGuid(8);
                    targetUserId = reader.IsDBNull(9) ? Guid.Empty : reader.GetGuid(9);
                    eventId = reader.IsDBNull(10) ? (Guid?)null : reader.GetGuid(10);
                    eventTitle = reader.IsDBNull(11) ? "Booking Request" : reader.GetString(11);
                    eventDate = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12);
                    eventEndTime = reader.IsDBNull(13) ? (DateTime?)null : reader.GetDateTime(13);
                    location = reader.IsDBNull(14) ? "" : reader.GetString(14);
                }

                var normalizedStatus = req.status.Trim();
                if (normalizedStatus.Equals("Accepted", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = "Confirmed";
                }
                else if (normalizedStatus.Equals("Declined", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = $"Cancelled by {targetRole}";
                }
                else if (normalizedStatus.Equals("CancelledByCustomer", StringComparison.OrdinalIgnoreCase) ||
                         normalizedStatus.Equals("Cancelled by Customer", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = "Cancelled by Customer";
                }

                if (currentStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "Completed bookings can no longer be cancelled." });
                }

                if (currentStatus.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "This booking has already been cancelled." });
                }

                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) &&
                    serviceFeeStatus != "Paid" &&
                    serviceFeeStatus != "NotRequired")
                {
                    return BadRequest(new { message = "The booking fee must be paid before this request can be confirmed." });
                }

                if (normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    if (!currentStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) &&
                        !currentStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        return BadRequest(new { message = "Only confirmed bookings can be marked as completed." });
                    }

                    if (!eventDate.HasValue)
                    {
                        return BadRequest(new { message = "This booking needs an event date before it can be completed." });
                    }

                    var completionGate = (eventEndTime ?? eventDate.Value).ToUniversalTime();
                    if (completionGate > DateTime.UtcNow)
                    {
                        return BadRequest(new { message = "This booking can only be completed after the booked time range has passed." });
                    }
                }

                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    if (await PlatformFeatureSupport.UserHasCalendarBlockAsync(connection, targetUserId, targetRole, eventDate))
                    {
                        return Conflict(new { message = $"{targetRole} blocked this date from bookings." });
                    }

                    if (await PlatformFeatureSupport.UserHasBookingConflictAsync(connection, targetUserId, eventDate, eventEndTime, bookingId))
                    {
                        return Conflict(new { message = $"{targetRole} is already booked in this time slot." });
                    }
                }

                var nextPaymentStatus = paymentStatus;
                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    nextPaymentStatus = serviceFeeStatus == "NotRequired"
                        ? "NotRequired"
                        : budget > 0 ? "AwaitingTalentFeePayment" : "Paid";
                }
                else if (normalizedStatus.StartsWith("Cancelled by ", StringComparison.OrdinalIgnoreCase) && serviceFeeStatus == "Paid")
                {
                    nextPaymentStatus = "Refund Pending";
                }
                else if (normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    nextPaymentStatus = budget > 0 && !talentFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase)
                        ? paymentStatus
                        : serviceFeeStatus == "NotRequired" ? "NotRequired" : "Paid";
                }

                const string sql = "UPDATE bookings SET status = @status, payment_status = @paymentStatus, talent_platform_fee_status = CASE WHEN @confirmed = TRUE AND talent_platform_fee > 0 AND COALESCE(talent_platform_fee_status, 'Unpaid') = 'Unpaid' THEN 'AwaitingPayment' ELSE talent_platform_fee_status END WHERE id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = normalizedStatus;
                cmd.Parameters.Add("@paymentStatus", NpgsqlDbType.Text).Value = nextPaymentStatus;
                cmd.Parameters.Add("@confirmed", NpgsqlDbType.Boolean).Value = normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;

                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0)
                {
                    return NotFound(new { message = "Booking request not found." });
                }

                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) && eventId.HasValue && targetUserId != Guid.Empty)
                {
                    await SyncBookingIntoEventLineup(connection, bookingId, eventId.Value, targetUserId, targetRole);
                }

                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    await PlatformFeatureSupport.EnsureBookingContractAsync(connection, bookingId, eventTitle, budget, eventDate, location, targetRole);
                }

                if (normalizedStatus.Equals($"Cancelled by {targetRole}", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessAutomaticBookingRefundsAsync(
                        connection,
                        bookingId,
                        reasonCode: "TalentCancelled",
                        notes: $"{targetRole} cancelled the booking before fulfillment.",
                        requesterUserId: targetUserId,
                        requesterRole: targetRole,
                        refundServiceFee: true,
                        refundTalentFee: true,
                        refundPlatformFee: false);
                }
                else if (normalizedStatus.Equals("Cancelled by Customer", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessAutomaticBookingRefundsAsync(
                        connection,
                        bookingId,
                        reasonCode: "CustomerCancelled",
                        notes: "Customer cancelled the booking.",
                        requesterUserId: customerId,
                        requesterRole: "Customer",
                        refundServiceFee: false,
                        refundTalentFee: false,
                        refundPlatformFee: true);
                }

                if (customerId != Guid.Empty)
                {
                    var customerMessage = normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase)
                        ? $"{targetRole} confirmed your booking for {eventTitle}."
                        : normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                            ? $"Your booking for {eventTitle} has been marked as completed."
                        : normalizedStatus.Equals("Cancelled by Customer", StringComparison.OrdinalIgnoreCase)
                            ? $"You cancelled your booking for {eventTitle}."
                        : normalizedStatus.StartsWith("Cancelled by ", StringComparison.OrdinalIgnoreCase)
                            ? $"{targetRole} cancelled your booking for {eventTitle}."
                            : $"Your booking for {eventTitle} is now {normalizedStatus}.";

                    await InsertNotification(connection, customerId, "booking_status", "Booking updated", customerMessage, bookingId, "booking");
                }

                if (targetUserId != Guid.Empty && targetUserId != customerId && normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    await InsertNotification(connection, targetUserId, "booking_status", "Booking completed", $"The booking for {eventTitle} has been marked as completed.", bookingId, "booking");
                }
                else if (targetUserId != Guid.Empty && targetUserId != customerId && normalizedStatus.Equals("Cancelled by Customer", StringComparison.OrdinalIgnoreCase))
                {
                    await InsertNotification(connection, targetUserId, "booking_status", "Booking cancelled", $"The customer cancelled the booking for {eventTitle}.", bookingId, "booking");
                }

                return Ok(new { message = "Booking request updated.", status = normalizedStatus, paymentStatus = nextPaymentStatus });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update booking request: " + ex.Message });
            }
        }

        [HttpGet("{bookingId}")]
        public async Task<IActionResult> GetBookingById(Guid bookingId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.customer_id,
                        b.target_user_id,
                        b.event_id,
                        COALESCE(c.firstname, '') AS customer_firstname,
                        COALESCE(c.lastname, '') AS customer_lastname,
                        COALESCE(t.firstname, '') AS target_firstname,
                        COALESCE(t.lastname, '') AS target_lastname,
                        COALESCE(t.stagename, '') AS target_stage_name,
                        COALESCE(b.target_role, ''),
                        COALESCE(b.service_type, ''),
                        COALESCE(b.event_title, ''),
                        b.event_date,
                        b.event_end_time,
                        COALESCE(b.location, ''),
                        COALESCE(b.budget, 0),
                        COALESCE(b.booking_fee, 15),
                        COALESCE(b.talent_platform_fee, 0),
                        COALESCE(b.notes, COALESCE(b.message, '')),
                        COALESCE(b.message, ''),
                        COALESCE(b.status, 'Pending'),
                        COALESCE(b.payment_status, 'Unpaid'),
                        b.paid_at,
                        COALESCE(b.payment_method, ''),
                        COALESCE(b.service_fee_status, COALESCE(b.payment_status, 'Unpaid')),
                        COALESCE(b.talent_fee_status, 'Unpaid'),
                        COALESCE(b.talent_platform_fee_status, 'Unpaid'),
                        b.service_fee_paid_at,
                        b.talent_fee_paid_at,
                        b.talent_platform_fee_paid_at,
                        COALESCE(b.service_fee_payment_method, COALESCE(b.payment_method, '')),
                        COALESCE(b.talent_fee_payment_method, ''),
                        COALESCE(b.talent_platform_fee_payment_method, ''),
                        COALESCE(b.talent_fee_payment_reference, ''),
                        COALESCE(b.talent_platform_fee_payment_reference, ''),
                        COALESCE(b.talent_fee_checkout_reference, ''),
                        COALESCE(b.talent_platform_fee_checkout_reference, ''),
                        COALESCE(b.paymongo_payment_reference, ''),
                        COALESCE(b.paymongo_checkout_reference, ''),
                        COALESCE(b.paymongo_payment_id, ''),
                        COALESCE(b.talent_fee_payment_id, ''),
                        COALESCE(b.talent_platform_fee_payment_id, ''),
                        b.booking_group_id,
                        COALESCE(b.booking_sequence, 1)
                    FROM bookings b
                    LEFT JOIN users c ON c.id = b.customer_id
                    LEFT JOIN users t ON t.id = b.target_user_id
                    WHERE b.id = @bookingId;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;

                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "Booking request not found." });
                }

                var customerFirst = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var customerLast = reader.IsDBNull(5) ? "" : reader.GetString(5);
                var targetFirst = reader.IsDBNull(6) ? "" : reader.GetString(6);
                var targetLast = reader.IsDBNull(7) ? "" : reader.GetString(7);
                var targetStage = reader.IsDBNull(8) ? "" : reader.GetString(8);

                return Ok(new
                {
                    id = reader.GetGuid(0),
                    customerId = reader.GetGuid(1),
                    targetUserId = reader.GetGuid(2),
                    eventId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3),
                    customerName = $"{customerFirst} {customerLast}".Trim(),
                    targetName = !string.IsNullOrWhiteSpace(targetStage) ? targetStage : $"{targetFirst} {targetLast}".Trim(),
                    targetRole = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    serviceType = reader.IsDBNull(10) ? "" : reader.GetString(10),
                    eventTitle = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    eventDate = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12),
                    eventEndTime = reader.IsDBNull(13) ? (DateTime?)null : reader.GetDateTime(13),
                    location = reader.IsDBNull(14) ? "" : reader.GetString(14),
                    budget = reader.IsDBNull(15) ? 0 : reader.GetDecimal(15),
                    bookingFee = reader.IsDBNull(16) ? BookingServiceFee : reader.GetDecimal(16),
                    talentPlatformFee = reader.IsDBNull(17) ? 0 : reader.GetDecimal(17),
                    notes = _messageProtection.Unprotect(reader.IsDBNull(18) ? "" : reader.GetString(18)),
                    message = _messageProtection.Unprotect(reader.IsDBNull(19) ? "" : reader.GetString(19)),
                    status = reader.IsDBNull(20) ? "Pending" : reader.GetString(20),
                    paymentStatus = NormalizePaymentStatus(reader.IsDBNull(21) ? "Unpaid" : reader.GetString(21)),
                    paidAt = reader.IsDBNull(22) ? (DateTime?)null : reader.GetDateTime(22),
                    paymentMethod = reader.IsDBNull(23) ? "" : reader.GetString(23),
                    serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(24) ? "Unpaid" : reader.GetString(24), reader.IsDBNull(21) ? "Unpaid" : reader.GetString(21)),
                    talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(25) ? "Unpaid" : reader.GetString(25)),
                    talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(26) ? "Unpaid" : reader.GetString(26)),
                    serviceFeePaidAt = reader.IsDBNull(27) ? (DateTime?)null : reader.GetDateTime(27),
                    talentFeePaidAt = reader.IsDBNull(28) ? (DateTime?)null : reader.GetDateTime(28),
                    talentPlatformFeePaidAt = reader.IsDBNull(29) ? (DateTime?)null : reader.GetDateTime(29),
                    serviceFeePaymentMethod = reader.IsDBNull(30) ? "" : reader.GetString(30),
                    talentFeePaymentMethod = reader.IsDBNull(31) ? "" : reader.GetString(31),
                    talentPlatformFeePaymentMethod = reader.IsDBNull(32) ? "" : reader.GetString(32),
                    talentFeePaymentReference = reader.IsDBNull(33) ? "" : reader.GetString(33),
                    talentPlatformFeePaymentReference = reader.IsDBNull(34) ? "" : reader.GetString(34),
                    talentFeeCheckoutReference = reader.IsDBNull(35) ? "" : reader.GetString(35),
                    talentPlatformFeeCheckoutReference = reader.IsDBNull(36) ? "" : reader.GetString(36),
                    paymentReference = reader.IsDBNull(37) ? "" : reader.GetString(37),
                    checkoutReference = reader.IsDBNull(38) ? "" : reader.GetString(38),
                    paymentId = reader.IsDBNull(39) ? "" : reader.GetString(39),
                    talentFeePaymentId = reader.IsDBNull(40) ? "" : reader.GetString(40),
                    talentPlatformFeePaymentId = reader.IsDBNull(41) ? "" : reader.GetString(41),
                    bookingGroupId = reader.IsDBNull(42) ? (Guid?)null : reader.GetGuid(42),
                    bookingSequence = reader.IsDBNull(43) ? 1 : reader.GetInt32(43)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load booking details: " + ex.Message });
            }
        }

        [HttpGet("{bookingId}/contract")]
        public async Task<IActionResult> GetBookingContract(Guid bookingId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                const string sql = @"
                    SELECT id,
                           booking_id,
                           contract_status,
                           title,
                           terms,
                           COALESCE(agreed_fee, 0),
                           event_date,
                           COALESCE(location, ''),
                           created_at,
                           updated_at
                    FROM booking_contracts
                    WHERE booking_id = @bookingId;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "No contract draft yet for this booking." });
                }

                return Ok(new
                {
                    id = reader.GetGuid(0),
                    bookingId = reader.GetGuid(1),
                    contractStatus = reader.IsDBNull(2) ? "Draft" : reader.GetString(2),
                    title = reader.IsDBNull(3) ? "Performance Contract" : reader.GetString(3),
                    terms = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    agreedFee = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                    eventDate = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                    location = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    createdAt = reader.IsDBNull(8) ? DateTime.UtcNow : reader.GetDateTime(8),
                    updatedAt = reader.IsDBNull(9) ? DateTime.UtcNow : reader.GetDateTime(9)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load contract draft: " + ex.Message });
            }
        }

        [HttpPost("{bookingId}/contract")]
        public async Task<IActionResult> CreateBookingContract(Guid bookingId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                const string bookingSql = @"
                    SELECT
                        COALESCE(event_title, 'Booking Request'),
                        COALESCE(budget, 0),
                        event_date,
                        COALESCE(location, ''),
                        COALESCE(target_role, 'Artist')
                    FROM bookings
                    WHERE id = @id;";

                string eventTitle;
                decimal budget;
                DateTime? eventDate;
                string location;
                string targetRole;

                await using (var bookingCmd = new NpgsqlCommand(bookingSql, connection))
                {
                    bookingCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    await using var reader = await bookingCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking not found." });
                    }

                    eventTitle = reader.IsDBNull(0) ? "Booking Request" : reader.GetString(0);
                    budget = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
                    eventDate = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
                    location = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    targetRole = reader.IsDBNull(4) ? "Artist" : reader.GetString(4);
                }

                await PlatformFeatureSupport.EnsureBookingContractAsync(connection, bookingId, eventTitle, budget, eventDate, location, targetRole);

                return await GetBookingContract(bookingId);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create contract draft: " + ex.Message });
            }
        }

        [HttpPatch("{bookingId}/contract")]
        public async Task<IActionResult> UpdateBookingContract(Guid bookingId, [FromBody] UpdateBookingContractRequest req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                const string sql = @"
                    UPDATE booking_contracts
                    SET title = COALESCE(@title, title),
                        terms = COALESCE(@terms, terms),
                        agreed_fee = COALESCE(@agreedFee, agreed_fee),
                        contract_status = COALESCE(@contractStatus, contract_status),
                        updated_at = NOW()
                    WHERE booking_id = @bookingId;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = (object?)req.title ?? DBNull.Value;
                cmd.Parameters.Add("@terms", NpgsqlDbType.Text).Value = (object?)req.terms ?? DBNull.Value;
                cmd.Parameters.Add("@agreedFee", NpgsqlDbType.Numeric).Value = (object?)req.agreedFee ?? DBNull.Value;
                cmd.Parameters.Add("@contractStatus", NpgsqlDbType.Text).Value = (object?)req.contractStatus ?? DBNull.Value;
                var updated = await cmd.ExecuteNonQueryAsync();

                if (updated == 0)
                {
                    return NotFound(new { message = "No contract draft yet for this booking." });
                }

                return Ok(new { message = "Contract draft updated." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update contract draft: " + ex.Message });
            }
        }

        [HttpPost("{bookingId}/checkout")]
        [HttpPost("checkout/{bookingId}")]
        public async Task<IActionResult> CreateBookingCheckout(Guid bookingId, [FromBody] BookingCheckoutRequest req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);

                const string bookingSql = @"
                    SELECT COALESCE(event_title, 'Booking Request'),
                           COALESCE(booking_fee, 15),
                           COALESCE(budget, 0),
                           COALESCE(talent_platform_fee, 0),
                           COALESCE(status, 'Pending'),
                           COALESCE(payment_status, 'Unpaid'),
                           COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                           COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee_status, 'Unpaid'),
                           customer_id,
                           target_user_id,
                           event_id
                    FROM bookings
                    WHERE id = @id;";

                string eventTitle;
                decimal bookingFee;
                decimal budget;
                decimal talentPlatformFee;
                string status;
                string paymentStatus;
                string serviceFeeStatus;
                string talentFeeStatus;
                string talentPlatformFeeStatus;
                Guid customerId;
                Guid targetUserId;
                Guid? eventId;

                using (var bookingCmd = new NpgsqlCommand(bookingSql, connection))
                {
                    bookingCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    using var reader = await bookingCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking request not found." });
                    }

                    eventTitle = reader.IsDBNull(0) ? "Booking Request" : reader.GetString(0);
                    bookingFee = reader.IsDBNull(1) ? BookingServiceFee : reader.GetDecimal(1);
                    budget = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
                    talentPlatformFee = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3);
                    status = reader.IsDBNull(4) ? "Pending" : reader.GetString(4);
                    paymentStatus = NormalizePaymentStatus(reader.IsDBNull(5) ? "Unpaid" : reader.GetString(5));
                    serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(6) ? paymentStatus : reader.GetString(6), paymentStatus);
                    talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7));
                    talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(8) ? "Unpaid" : reader.GetString(8));
                    customerId = reader.IsDBNull(9) ? Guid.Empty : reader.GetGuid(9);
                    targetUserId = reader.IsDBNull(10) ? Guid.Empty : reader.GetGuid(10);
                    eventId = reader.IsDBNull(11) ? null : reader.GetGuid(11);
                }

                var paymentType = string.Equals(req.paymentType, "talent", StringComparison.OrdinalIgnoreCase)
                    ? "talent"
                    : string.Equals(req.paymentType, "platform", StringComparison.OrdinalIgnoreCase)
                        ? "platform"
                        : "service";
                var amountToCharge = paymentType == "talent" ? budget : paymentType == "platform" ? talentPlatformFee : bookingFee;

                if (paymentType == "service" && amountToCharge < PayMongoMinimumAmount)
                {
                    amountToCharge = PayMongoMinimumAmount;
                }

                if (status.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "This booking is no longer eligible for payment." });
                }

                if (string.IsNullOrWhiteSpace(_paymongoSecretKey))
                {
                    return StatusCode(500, new { message = "PayMongo is not configured yet. Add PayMongo:SecretKey before starting checkout." });
                }

                if (paymentType == "service" && serviceFeeStatus == "Paid")
                {
                    return Conflict(new { message = "The booking service fee has already been paid." });
                }

                if (paymentType == "talent" && !string.Equals(status, "Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "The promised talent fee can only be paid after the booking is confirmed." });
                }

                if (paymentType == "talent" && talentFeeStatus == "Paid")
                {
                    return Conflict(new { message = "The promised talent fee has already been paid." });
                }

                if (paymentType == "platform" && talentPlatformFeeStatus == "Paid")
                {
                    return Conflict(new { message = "The talent platform fee has already been paid." });
                }

                if (paymentType == "platform" && !string.Equals(status, "Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "The platform fee becomes payable after the booking is confirmed." });
                }

                if (amountToCharge < PayMongoMinimumAmount)
                {
                    return BadRequest(new { message = $"PayMongo requires a minimum amount of {FormatPeso(PayMongoMinimumAmount)}." });
                }

                if (paymentType == "talent" && amountToCharge <= 0)
                {
                    return BadRequest(new { message = "PayMongo requires a minimum amount of ₱20.00." });
                }

                if (string.IsNullOrWhiteSpace(req.successUrl) || string.IsNullOrWhiteSpace(req.cancelUrl))
                {
                    return BadRequest(new { message = "Success and cancel URLs are required." });
                }

                var amountInCentavos = (int)(amountToCharge * 100);
                var paymentLabel = paymentType == "talent" ? "Talent Fee" : paymentType == "platform" ? "Talent Platform Fee" : "Booking Service Fee";
                var paymongoPayload = new
                {
                    data = new
                    {
                        attributes = new
                        {
                            send_email_receipt = false,
                            show_description = true,
                            show_line_items = true,
                            payment_method_types = new[] { "gcash", "card", "paymaya" },
                            line_items = new[]
                            {
                                new
                                {
                                    currency = "PHP",
                                    amount = amountInCentavos,
                                    name = $"{paymentLabel} - {eventTitle}",
                                    quantity = 1
                                }
                            },
                            success_url = req.successUrl,
                            cancel_url = req.cancelUrl,
                            metadata = new
                            {
                                booking_id = bookingId.ToString(),
                                payment_type = paymentType
                            }
                        }
                    }
                };

                using var client = new HttpClient();
                var plainTextBytes = Encoding.UTF8.GetBytes(_paymongoSecretKey);
                var base64Auth = Convert.ToBase64String(plainTextBytes);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);

                var content = new StringContent(JsonSerializer.Serialize(paymongoPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://api.paymongo.com/v1/checkout_sessions", content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var payMongoMessage = GetPayMongoHttpErrorMessage(
                        (int)response.StatusCode,
                        responseString,
                        "PayMongo could not create the checkout session. Please verify the amount and payment settings."
                    );
                    return StatusCode((int)response.StatusCode, new { message = payMongoMessage, details = responseString });
                }

                using var doc = JsonDocument.Parse(responseString);
                var data = doc.RootElement.GetProperty("data");
                var checkoutId = data.GetProperty("id").GetString();
                var checkoutUrl = data.GetProperty("attributes").GetProperty("checkout_url").GetString();
                var checkoutReference = data.GetProperty("attributes").TryGetProperty("reference_number", out var referenceProp)
                    ? referenceProp.GetString()
                    : null;

                var updateSql = paymentType == "talent"
                    ? @"
                        UPDATE bookings
                        SET payment_status = 'AwaitingTalentFeePayment',
                            talent_fee_status = 'AwaitingPayment',
                            talent_fee_checkout_id = @checkoutId,
                            talent_fee_checkout_url = @checkoutUrl,
                            talent_fee_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;"
                    : paymentType == "platform"
                        ? @"
                        UPDATE bookings
                        SET talent_platform_fee_status = 'AwaitingPayment',
                            talent_platform_fee = @amountToCharge,
                            talent_platform_fee_checkout_id = @checkoutId,
                            talent_platform_fee_checkout_url = @checkoutUrl,
                            talent_platform_fee_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;"
                    : @"
                        UPDATE bookings
                        SET payment_status = 'AwaitingPayment',
                            service_fee_status = 'AwaitingPayment',
                            booking_fee = @amountToCharge,
                            paymongo_checkout_id = @checkoutId,
                            paymongo_checkout_url = @checkoutUrl,
                            paymongo_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;";

                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.Add("@checkoutId", NpgsqlDbType.Text).Value = (object?)checkoutId ?? DBNull.Value;
                updateCmd.Parameters.Add("@checkoutUrl", NpgsqlDbType.Text).Value = (object?)checkoutUrl ?? DBNull.Value;
                updateCmd.Parameters.Add("@checkoutReference", NpgsqlDbType.Text).Value = (object?)checkoutReference ?? DBNull.Value;
                updateCmd.Parameters.Add("@amountToCharge", NpgsqlDbType.Numeric).Value = amountToCharge;
                updateCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await updateCmd.ExecuteNonQueryAsync();

                await PaymentLedgerService.UpsertPendingAsync(
                    connection,
                    paymentScope: paymentType == "talent" ? "booking_talent_fee" : paymentType == "platform" ? "booking_talent_platform_fee" : "booking_service_fee",
                    userId: paymentType == "platform"
                        ? (targetUserId == Guid.Empty ? null : targetUserId)
                        : (customerId == Guid.Empty ? null : customerId),
                    organizerId: targetUserId == Guid.Empty ? null : targetUserId,
                    eventId: eventId,
                    ticketId: null,
                    bookingId: bookingId,
                    amount: amountToCharge,
                    description: $"{paymentLabel} for {eventTitle}",
                    checkoutId: checkoutId ?? string.Empty,
                    checkoutReference: checkoutReference ?? string.Empty,
                    featureUnlockState: paymentType == "talent" ? "AwaitingTalentSettlement" : paymentType == "platform" ? "TalentPlatformFeePending" : "MessagesLocked",
                    metadata: new
                    {
                        paymentType,
                        eventTitle,
                        bookingFee,
                        budget
                    });

                return Ok(new
                {
                    message = "Booking checkout session created.",
                    checkoutUrl,
                    paymentStatus = paymentType == "talent" ? "AwaitingTalentFeePayment" : paymentType == "platform" ? "AwaitingTalentPlatformFeePayment" : "AwaitingPayment",
                    paymentType,
                    amount = amountToCharge
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Failed to create booking checkout.",
                        ex)
                });
            }
        }

        [HttpPost("{bookingId}/payment/confirm")]
        public async Task<IActionResult> ConfirmBookingPayment(Guid bookingId, [FromQuery] string? paymentType)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureNotificationsTableExists(connection);

                const string sql = @"
                    SELECT COALESCE(paymongo_checkout_id, ''),
                           COALESCE(payment_status, 'Unpaid'),
                           COALESCE(paymongo_checkout_reference, ''),
                           COALESCE(status, 'Pending'),
                           COALESCE(target_role, 'Artist'),
                           COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                           COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee_status, 'Unpaid'),
                           COALESCE(talent_fee_checkout_id, ''),
                           COALESCE(talent_fee_checkout_reference, ''),
                           COALESCE(talent_platform_fee_checkout_id, ''),
                           COALESCE(talent_platform_fee_checkout_reference, ''),
                           customer_id,
                           target_user_id,
                           COALESCE(event_title, 'Booking Request')
                    FROM bookings
                    WHERE id = @id;";

                string checkoutId;
                string paymentStatus;
                string checkoutReference;
                string status;
                string targetRole;
                string serviceFeeStatus;
                string talentFeeStatus;
                string talentPlatformFeeStatus;
                string talentCheckoutId;
                string talentCheckoutReference;
                string platformCheckoutId;
                string platformCheckoutReference;
                Guid customerId;
                Guid targetUserId;
                string eventTitle;

                using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking request not found." });
                    }

                    checkoutId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    paymentStatus = NormalizePaymentStatus(reader.IsDBNull(1) ? "Unpaid" : reader.GetString(1));
                    checkoutReference = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    status = reader.IsDBNull(3) ? "Pending" : reader.GetString(3);
                    targetRole = reader.IsDBNull(4) ? "Artist" : NormalizeTargetRole(reader.GetString(4));
                    serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(5) ? paymentStatus : reader.GetString(5), paymentStatus);
                    talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(6) ? "Unpaid" : reader.GetString(6));
                    talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7));
                    talentCheckoutId = reader.IsDBNull(8) ? "" : reader.GetString(8);
                    talentCheckoutReference = reader.IsDBNull(9) ? "" : reader.GetString(9);
                    platformCheckoutId = reader.IsDBNull(10) ? "" : reader.GetString(10);
                    platformCheckoutReference = reader.IsDBNull(11) ? "" : reader.GetString(11);
                    customerId = reader.IsDBNull(12) ? Guid.Empty : reader.GetGuid(12);
                    targetUserId = reader.IsDBNull(13) ? Guid.Empty : reader.GetGuid(13);
                    eventTitle = reader.IsDBNull(14) ? "Booking Request" : reader.GetString(14);
                }

                var normalizedPaymentType = string.Equals(paymentType, "talent", StringComparison.OrdinalIgnoreCase)
                    ? "talent"
                    : string.Equals(paymentType, "platform", StringComparison.OrdinalIgnoreCase)
                        ? "platform"
                        : "service";
                if (normalizedPaymentType == "talent")
                {
                    checkoutId = talentCheckoutId;
                    checkoutReference = talentCheckoutReference;
                }
                else if (normalizedPaymentType == "platform")
                {
                    checkoutId = platformCheckoutId;
                    checkoutReference = platformCheckoutReference;
                }

                if (normalizedPaymentType == "service" && serviceFeeStatus == "Paid")
                {
                    return Ok(new { message = "Booking payment already confirmed.", paymentStatus = serviceFeeStatus });
                }

                if (normalizedPaymentType == "talent" && talentFeeStatus == "Paid")
                {
                    return Ok(new { message = "Talent fee already confirmed.", paymentStatus = talentFeeStatus });
                }

                if (normalizedPaymentType == "platform" && talentPlatformFeeStatus == "Paid")
                {
                    return Ok(new { message = "Talent platform fee already confirmed.", paymentStatus = talentPlatformFeeStatus });
                }

                if (string.IsNullOrWhiteSpace(checkoutId))
                {
                    return BadRequest(new { message = "No checkout session is attached to this booking yet." });
                }

                using var client = new HttpClient();
                var plainTextBytes = Encoding.UTF8.GetBytes(_paymongoSecretKey);
                var base64Auth = Convert.ToBase64String(plainTextBytes);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);

                var response = await client.GetAsync($"https://api.paymongo.com/v1/checkout_sessions/{checkoutId}");
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new { message = "Failed to verify PayMongo checkout.", details = responseString });
                }

                using var doc = JsonDocument.Parse(responseString);
                var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
                var payments = attributes.TryGetProperty("payments", out var paymentArray) && paymentArray.ValueKind == JsonValueKind.Array
                    ? paymentArray
                    : default;
                if (string.IsNullOrWhiteSpace(checkoutReference) && attributes.TryGetProperty("reference_number", out var checkoutReferenceProp))
                {
                    checkoutReference = checkoutReferenceProp.GetString() ?? "";
                }

                if (payments.ValueKind != JsonValueKind.Array || payments.GetArrayLength() == 0)
                {
                    return Ok(new { message = "Payment is still pending.", paymentStatus = "AwaitingPayment" });
                }

                string paymentReference = "";
                string paymentMethod = "";
                string paymentId = "";
                DateTime? paidAt = null;

                var firstPayment = payments[0];
                if (firstPayment.TryGetProperty("id", out var paymentIdProp))
                {
                    paymentId = paymentIdProp.GetString() ?? "";
                }

                if (firstPayment.TryGetProperty("attributes", out var paymentAttributes))
                {
                    if (paymentAttributes.TryGetProperty("reference_number", out var referenceProp))
                    {
                        paymentReference = referenceProp.GetString() ?? "";
                    }

                    if (paymentAttributes.TryGetProperty("paid_at", out var paidAtProp) && paidAtProp.ValueKind == JsonValueKind.Number)
                    {
                        paidAt = DateTimeOffset.FromUnixTimeSeconds(paidAtProp.GetInt64()).UtcDateTime;
                    }

                    if (paymentAttributes.TryGetProperty("source", out var sourceProp) &&
                        sourceProp.ValueKind == JsonValueKind.Object &&
                        sourceProp.TryGetProperty("type", out var sourceTypeProp))
                    {
                        paymentMethod = sourceTypeProp.GetString() ?? "";
                    }
                }

                var nextStatus = normalizedPaymentType == "service" && status.StartsWith("Awaiting", StringComparison.OrdinalIgnoreCase)
                    ? $"Pending {targetRole} Approval"
                    : status;

                var updateSql = normalizedPaymentType == "talent"
                    ? @"
                        UPDATE bookings
                        SET payment_status = 'Paid',
                            talent_fee_status = 'Paid',
                            talent_fee_paid_at = COALESCE(@paidAt, NOW()),
                            talent_fee_payment_id = @paymentId,
                            talent_fee_payment_reference = @paymentReference,
                            talent_fee_payment_method = @paymentMethod,
                            talent_fee_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;"
                    : normalizedPaymentType == "platform"
                        ? @"
                        UPDATE bookings
                        SET talent_platform_fee_status = 'Paid',
                            talent_platform_fee_paid_at = COALESCE(@paidAt, NOW()),
                            talent_platform_fee_payment_id = @paymentId,
                            talent_platform_fee_payment_reference = @paymentReference,
                            talent_platform_fee_payment_method = @paymentMethod,
                            talent_platform_fee_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;"
                    : @"
                        UPDATE bookings
                        SET payment_status = CASE
                                WHEN COALESCE(budget, 0) > 0 THEN 'ServiceFeePaid'
                                ELSE 'Paid'
                            END,
                            service_fee_status = 'Paid',
                            status = @status,
                            paid_at = COALESCE(@paidAt, NOW()),
                            service_fee_paid_at = COALESCE(@paidAt, NOW()),
                            paymongo_payment_id = @paymentId,
                            paymongo_payment_reference = @paymentReference,
                            payment_method = @paymentMethod,
                            service_fee_payment_method = @paymentMethod,
                            paymongo_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;";

                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = nextStatus;
                updateCmd.Parameters.Add("@paidAt", NpgsqlDbType.TimestampTz).Value = (object?)paidAt ?? DBNull.Value;
                updateCmd.Parameters.Add("@paymentId", NpgsqlDbType.Text).Value = (object?)paymentId ?? DBNull.Value;
                updateCmd.Parameters.Add("@paymentReference", NpgsqlDbType.Text).Value = (object?)paymentReference ?? DBNull.Value;
                updateCmd.Parameters.Add("@paymentMethod", NpgsqlDbType.Text).Value = (object?)paymentMethod ?? DBNull.Value;
                updateCmd.Parameters.Add("@checkoutReference", NpgsqlDbType.Text).Value = (object?)checkoutReference ?? DBNull.Value;
                updateCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await updateCmd.ExecuteNonQueryAsync();

                await PaymentLedgerService.MarkPaidAsync(
                    connection,
                    paymentScope: normalizedPaymentType == "talent" ? "booking_talent_fee" : normalizedPaymentType == "platform" ? "booking_talent_platform_fee" : "booking_service_fee",
                    ticketId: null,
                    bookingId: bookingId,
                    paymentMethod: paymentMethod,
                    paymentReference: paymentReference,
                    checkoutReference: checkoutReference,
                    featureUnlockState: normalizedPaymentType == "talent" ? "TalentFeeSettled" : normalizedPaymentType == "platform" ? "TalentPlatformFeeSettled" : "MessagesUnlocked",
                    paidAt: paidAt);

                if (normalizedPaymentType == "service" && targetUserId != Guid.Empty)
                {
                    await InsertNotification(connection, targetUserId, "booking_payment", "Service fee paid", $"The booking service fee for {eventTitle} has been paid. You can now review the request.", bookingId, "booking");
                }

                if (normalizedPaymentType == "talent" && targetUserId != Guid.Empty)
                {
                    await InsertNotification(connection, targetUserId, "talent_fee_paid", "Talent fee paid", $"The promised talent fee for {eventTitle} has been completed.", bookingId, "booking");
                }

                if (normalizedPaymentType == "platform" && targetUserId != Guid.Empty)
                {
                    await InsertNotification(connection, targetUserId, "platform_fee_paid", "Platform fee paid", $"Your platform fee for {eventTitle} is confirmed.", bookingId, "booking");
                }

                if (normalizedPaymentType == "talent" && customerId != Guid.Empty)
                {
                    await InsertNotification(connection, customerId, "payment_receipt", "Talent fee confirmed", $"Your talent fee payment for {eventTitle} is confirmed.", bookingId, "booking");
                }

                return Ok(new
                {
                    message = normalizedPaymentType == "talent" ? "Talent fee confirmed." : normalizedPaymentType == "platform" ? "Talent platform fee confirmed." : "Booking payment confirmed.",
                    paymentStatus = normalizedPaymentType == "talent" || normalizedPaymentType == "platform" ? "Paid" : (status.StartsWith("Awaiting", StringComparison.OrdinalIgnoreCase) ? "ServiceFeePaid" : "Paid"),
                    paymentMethod,
                    status = nextStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Failed to confirm booking payment.",
                        ex)
                });
            }
        }

        [HttpPost("{bookingId}/refunds/request")]
        public async Task<IActionResult> RequestBookingRefund(Guid bookingId, [FromBody] CreateBookingRefundRequest? req)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before requesting a refund." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureNotificationsTableExists(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await PaymentRefundService.EnsureSchemaAsync(connection);

                const string sql = @"
                    SELECT customer_id,
                           target_user_id,
                           event_id,
                           COALESCE(target_role, 'Artist'),
                           COALESCE(status, 'Pending'),
                           event_date,
                           event_end_time,
                           COALESCE(event_title, 'Booking Request'),
                           COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                           COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee_status, 'Unpaid')
                    FROM bookings
                    WHERE id = @id;";

                Guid customerId;
                Guid targetUserId;
                Guid? eventId;
                string targetRole;
                string status;
                DateTime? eventDate;
                DateTime? eventEndTime;
                string eventTitle;
                string serviceFeeStatus;
                string talentFeeStatus;
                string platformFeeStatus;

                await using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking request not found." });
                    }

                    customerId = reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0);
                    targetUserId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);
                    eventId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
                    targetRole = reader.IsDBNull(3) ? "Artist" : reader.GetString(3);
                    status = reader.IsDBNull(4) ? "Pending" : reader.GetString(4);
                    eventDate = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
                    eventEndTime = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6);
                    eventTitle = reader.IsDBNull(7) ? "Booking Request" : reader.GetString(7);
                    serviceFeeStatus = reader.IsDBNull(8) ? "Unpaid" : reader.GetString(8);
                    talentFeeStatus = reader.IsDBNull(9) ? "Unpaid" : reader.GetString(9);
                    platformFeeStatus = reader.IsDBNull(10) ? "Unpaid" : reader.GetString(10);
                }

                var isCustomerSide = actorUserId.Value == customerId || actorRole.Equals("Organizer", StringComparison.OrdinalIgnoreCase);
                var isTargetSide = actorUserId.Value == targetUserId || actorRole.Equals(targetRole, StringComparison.OrdinalIgnoreCase);
                if (!isCustomerSide && !isTargetSide && !actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                var reasonCode = string.IsNullOrWhiteSpace(req?.reasonCode) ? "Other" : req!.reasonCode!.Trim();
                var notes = req?.notes?.Trim();
                var bookingEnded = (eventEndTime ?? eventDate)?.ToUniversalTime() <= DateTime.UtcNow;

                if (reasonCode.Equals("TalentNoShow", StringComparison.OrdinalIgnoreCase) && !isCustomerSide)
                {
                    return BadRequest(new { message = "Only the customer or organizer can report a talent no-show." });
                }

                if (reasonCode.Equals("CustomerNoShow", StringComparison.OrdinalIgnoreCase) && !isTargetSide)
                {
                    return BadRequest(new { message = "Only the assigned artist or sessionist can report a customer no-show." });
                }

                if ((reasonCode.Equals("TalentNoShow", StringComparison.OrdinalIgnoreCase) ||
                     reasonCode.Equals("CustomerNoShow", StringComparison.OrdinalIgnoreCase)) &&
                    bookingEnded != true)
                {
                    return BadRequest(new { message = "No-show refunds can only be requested after the booked schedule has passed." });
                }

                var result = await ProcessAutomaticBookingRefundsAsync(
                    connection,
                    bookingId,
                    reasonCode,
                    notes,
                    actorUserId,
                    actorRole,
                    refundServiceFee: isCustomerSide && (reasonCode.Equals("TalentNoShow", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("TalentCancelled", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("Other", StringComparison.OrdinalIgnoreCase)),
                    refundTalentFee: isCustomerSide && (reasonCode.Equals("TalentNoShow", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("TalentCancelled", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("Other", StringComparison.OrdinalIgnoreCase)),
                    refundPlatformFee: isTargetSide && (reasonCode.Equals("CustomerNoShow", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("CustomerCancelled", StringComparison.OrdinalIgnoreCase)));

                if (!result.AnyProcessed)
                {
                    return BadRequest(new
                    {
                        message = isCustomerSide
                            ? $"No refundable customer payment was found for {eventTitle}. Current statuses: service fee {serviceFeeStatus}, talent fee {talentFeeStatus}."
                            : $"No refundable platform fee was found for {eventTitle}. Current platform fee status: {platformFeeStatus}."
                    });
                }

                if (customerId != Guid.Empty && isTargetSide)
                {
                    await InsertNotification(connection, customerId, "booking_refund", "Booking refund requested", $"{targetRole} requested a refund review for {eventTitle}.", bookingId, "booking");
                }

                if (targetUserId != Guid.Empty && isCustomerSide)
                {
                    await InsertNotification(connection, targetUserId, "booking_refund", "Booking refund requested", $"A refund review was requested for {eventTitle}.", bookingId, "booking");
                }

                return Ok(new
                {
                    message = result.HasManualReview
                        ? "Refund request recorded. Some items need manual review because PayMongo could not complete them automatically."
                        : "Refund request processed.",
                    refundStatus = result.OverallStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Failed to process booking refund.",
                        ex)
                });
            }
        }

        private sealed record BookingRefundBatchResult(bool AnyProcessed, bool HasManualReview, string OverallStatus);

        private async Task<BookingRefundBatchResult> ProcessAutomaticBookingRefundsAsync(
            NpgsqlConnection connection,
            Guid bookingId,
            string reasonCode,
            string? notes,
            Guid? requesterUserId,
            string? requesterRole,
            bool refundServiceFee,
            bool refundTalentFee,
            bool refundPlatformFee)
        {
            const string sql = @"
                SELECT customer_id,
                       target_user_id,
                       COALESCE(event_title, 'Booking Request'),
                       COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                       COALESCE(talent_fee_status, 'Unpaid'),
                       COALESCE(talent_platform_fee_status, 'Unpaid'),
                       COALESCE(booking_fee, 0),
                       COALESCE(budget, 0),
                       COALESCE(talent_platform_fee, 0),
                       COALESCE(paymongo_payment_id, ''),
                       COALESCE(talent_fee_payment_id, ''),
                       COALESCE(talent_platform_fee_payment_id, ''),
                       COALESCE(paymongo_checkout_id, ''),
                       COALESCE(talent_fee_checkout_id, ''),
                       COALESCE(talent_platform_fee_checkout_id, '')
                FROM bookings
                WHERE id = @id;";

            Guid customerId;
            Guid targetUserId;
            string eventTitle;
            string serviceFeeStatus;
            string talentFeeStatus;
            string platformFeeStatus;
            decimal bookingFee;
            decimal budget;
            decimal platformFee;
            string servicePaymentId;
            string talentPaymentId;
            string platformPaymentId;
            string serviceCheckoutId;
            string talentCheckoutId;
            string platformCheckoutId;

            await using (var cmd = new NpgsqlCommand(sql, connection))
            {
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return new BookingRefundBatchResult(false, false, "Unpaid");
                }

                customerId = reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0);
                targetUserId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);
                eventTitle = reader.IsDBNull(2) ? "Booking Request" : reader.GetString(2);
                serviceFeeStatus = reader.IsDBNull(3) ? "Unpaid" : reader.GetString(3);
                talentFeeStatus = reader.IsDBNull(4) ? "Unpaid" : reader.GetString(4);
                platformFeeStatus = reader.IsDBNull(5) ? "Unpaid" : reader.GetString(5);
                bookingFee = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6);
                budget = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7);
                platformFee = reader.IsDBNull(8) ? 0 : reader.GetDecimal(8);
                servicePaymentId = reader.IsDBNull(9) ? "" : reader.GetString(9);
                talentPaymentId = reader.IsDBNull(10) ? "" : reader.GetString(10);
                platformPaymentId = reader.IsDBNull(11) ? "" : reader.GetString(11);
                serviceCheckoutId = reader.IsDBNull(12) ? "" : reader.GetString(12);
                talentCheckoutId = reader.IsDBNull(13) ? "" : reader.GetString(13);
                platformCheckoutId = reader.IsDBNull(14) ? "" : reader.GetString(14);
            }

            var anyProcessed = false;
            var hasManualReview = false;

            if (refundServiceFee && serviceFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) && bookingFee > 0)
            {
                var result = await RefundBookingComponentAsync(
                    connection,
                    bookingId,
                    "booking_service_fee",
                    "service",
                    bookingFee,
                    string.IsNullOrWhiteSpace(servicePaymentId)
                        ? await PaymentRefundService.ResolveCheckoutPaymentIdAsync(_paymongoSecretKey, serviceCheckoutId)
                        : servicePaymentId,
                    requesterUserId,
                    customerId == Guid.Empty ? requesterUserId : customerId,
                    requesterRole,
                    reasonCode,
                    notes);

                anyProcessed |= result is not null;
                hasManualReview |= string.Equals(result, "Refund Pending", StringComparison.OrdinalIgnoreCase) || string.Equals(result, "Refund Failed", StringComparison.OrdinalIgnoreCase);
            }

            if (refundTalentFee && talentFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) && budget > 0)
            {
                var result = await RefundBookingComponentAsync(
                    connection,
                    bookingId,
                    "booking_talent_fee",
                    "talent",
                    budget,
                    string.IsNullOrWhiteSpace(talentPaymentId)
                        ? await PaymentRefundService.ResolveCheckoutPaymentIdAsync(_paymongoSecretKey, talentCheckoutId)
                        : talentPaymentId,
                    requesterUserId,
                    customerId == Guid.Empty ? requesterUserId : customerId,
                    requesterRole,
                    reasonCode,
                    notes);

                anyProcessed |= result is not null;
                hasManualReview |= string.Equals(result, "Refund Pending", StringComparison.OrdinalIgnoreCase) || string.Equals(result, "Refund Failed", StringComparison.OrdinalIgnoreCase);
            }

            if (refundPlatformFee && platformFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) && platformFee > 0)
            {
                var result = await RefundBookingComponentAsync(
                    connection,
                    bookingId,
                    "booking_talent_platform_fee",
                    "platform",
                    platformFee,
                    string.IsNullOrWhiteSpace(platformPaymentId)
                        ? await PaymentRefundService.ResolveCheckoutPaymentIdAsync(_paymongoSecretKey, platformCheckoutId)
                        : platformPaymentId,
                    requesterUserId,
                    targetUserId == Guid.Empty ? requesterUserId : targetUserId,
                    requesterRole,
                    reasonCode,
                    notes);

                anyProcessed |= result is not null;
                hasManualReview |= string.Equals(result, "Refund Pending", StringComparison.OrdinalIgnoreCase) || string.Equals(result, "Refund Failed", StringComparison.OrdinalIgnoreCase);
            }

            var overallStatus = await RecomputeBookingPaymentStatusAsync(connection, bookingId);

            if (anyProcessed)
            {
                if (customerId != Guid.Empty && (refundServiceFee || refundTalentFee))
                {
                    await InsertNotification(connection, customerId, "booking_refund", "Booking refund updated", $"Your refund status for {eventTitle} is now {overallStatus}.", bookingId, "booking");
                }

                if (targetUserId != Guid.Empty && refundPlatformFee)
                {
                    await InsertNotification(connection, targetUserId, "booking_refund", "Platform fee refund updated", $"Your platform-fee refund status for {eventTitle} is now {overallStatus}.", bookingId, "booking");
                }
            }

            return new BookingRefundBatchResult(anyProcessed, hasManualReview, overallStatus);
        }

        private async Task<string?> RefundBookingComponentAsync(
            NpgsqlConnection connection,
            Guid bookingId,
            string paymentScope,
            string componentType,
            decimal amount,
            string? paymentId,
            Guid? requesterUserId,
            Guid? beneficiaryUserId,
            string? requesterRole,
            string reasonCode,
            string? notes)
        {
            if (amount <= 0)
            {
                return null;
            }

            var pendingStatusSql = componentType == "talent"
                ? "UPDATE bookings SET talent_fee_status = 'Refund Pending', payment_status = 'Refund Pending' WHERE id = @id;"
                : componentType == "platform"
                    ? "UPDATE bookings SET talent_platform_fee_status = 'Refund Pending', payment_status = 'Refund Pending' WHERE id = @id;"
                    : "UPDATE bookings SET service_fee_status = 'Refund Pending', payment_status = 'Refund Pending' WHERE id = @id;";

            await using (var pendingCmd = new NpgsqlCommand(pendingStatusSql, connection))
            {
                pendingCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                await pendingCmd.ExecuteNonQueryAsync();
            }

            var refundRequestId = await PaymentRefundService.CreateRefundRequestAsync(
                connection,
                refundScope: "booking",
                paymentScope: paymentScope,
                bookingId: bookingId,
                ticketId: null,
                requesterUserId: requesterUserId,
                beneficiaryUserId: beneficiaryUserId,
                requesterRole: requesterRole,
                reasonCode: reasonCode,
                reasonDetails: notes,
                amount: amount,
                providerPaymentId: paymentId,
                metadata: new { componentType });

            var refundResult = await PaymentRefundService.CreatePayMongoRefundAsync(
                _paymongoSecretKey,
                paymentId ?? string.Empty,
                amount,
                reasonCode,
                notes);

            var targetStatus = refundResult.Status == "Refunded"
                ? "Refunded"
                : refundResult.Status == "Refund Pending"
                    ? "Refund Pending"
                    : "Refund Failed";
            var bookingUpdateSql = componentType == "talent"
                ? @"
                    UPDATE bookings
                    SET talent_fee_status = @componentStatus
                    WHERE id = @id;"
                : componentType == "platform"
                    ? @"
                    UPDATE bookings
                    SET talent_platform_fee_status = @componentStatus
                    WHERE id = @id;"
                    : @"
                    UPDATE bookings
                    SET service_fee_status = @componentStatus
                    WHERE id = @id;";

            await using (var updateCmd = new NpgsqlCommand(bookingUpdateSql, connection))
            {
                updateCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                updateCmd.Parameters.Add("@componentStatus", NpgsqlDbType.Text).Value = targetStatus;
                await updateCmd.ExecuteNonQueryAsync();
            }

            await PaymentRefundService.UpdateRefundRequestAsync(
                connection,
                refundRequestId,
                status: targetStatus == "Refunded" ? "Refunded" : targetStatus == "Refund Pending" ? "ManualReview" : "Failed",
                providerRefundId: refundResult.RefundId,
                providerStatus: refundResult.ProviderStatus,
                errorCode: refundResult.ErrorCode,
                errorMessage: refundResult.ErrorMessage);

            if (targetStatus == "Refunded")
            {
                await PaymentLedgerService.MarkRefundedAsync(connection, paymentScope, null, bookingId, "Refunded");
                return "Refunded";
            }

            return targetStatus == "Failed" ? "Refund Failed" : "Refund Pending";
        }

        private static async Task<string> RecomputeBookingPaymentStatusAsync(NpgsqlConnection connection, Guid bookingId)
        {
            const string sql = @"
                SELECT COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                       COALESCE(talent_fee_status, 'Unpaid'),
                       COALESCE(talent_platform_fee_status, 'Unpaid')
                FROM bookings
                WHERE id = @id;";

            string serviceFeeStatus;
            string talentFeeStatus;
            string platformFeeStatus;

            await using (var cmd = new NpgsqlCommand(sql, connection))
            {
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return "Unpaid";
                }

                serviceFeeStatus = reader.IsDBNull(0) ? "Unpaid" : reader.GetString(0);
                talentFeeStatus = reader.IsDBNull(1) ? "Unpaid" : reader.GetString(1);
                platformFeeStatus = reader.IsDBNull(2) ? "Unpaid" : reader.GetString(2);
            }

            string nextStatus;
            if (serviceFeeStatus.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
                talentFeeStatus.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
                platformFeeStatus.Contains("Refund", StringComparison.OrdinalIgnoreCase))
            {
                if (serviceFeeStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase) ||
                    talentFeeStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase) ||
                    platformFeeStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase))
                {
                    nextStatus = "Refund Pending";
                }
                else if (serviceFeeStatus.Equals("Refund Failed", StringComparison.OrdinalIgnoreCase) ||
                         talentFeeStatus.Equals("Refund Failed", StringComparison.OrdinalIgnoreCase) ||
                         platformFeeStatus.Equals("Refund Failed", StringComparison.OrdinalIgnoreCase))
                {
                    nextStatus = "Refund Failed";
                }
                else
                {
                    nextStatus = "Refunded";
                }
            }
            else if (serviceFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) &&
                     (talentFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) || talentFeeStatus.Equals("Unpaid", StringComparison.OrdinalIgnoreCase) || talentFeeStatus.Equals("NotRequired", StringComparison.OrdinalIgnoreCase)))
            {
                nextStatus = "Paid";
            }
            else
            {
                nextStatus = "Unpaid";
            }

            await using var updateCmd = new NpgsqlCommand("UPDATE bookings SET payment_status = @paymentStatus WHERE id = @id;", connection);
            updateCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
            updateCmd.Parameters.Add("@paymentStatus", NpgsqlDbType.Text).Value = nextStatus;
            await updateCmd.ExecuteNonQueryAsync();
            return nextStatus;
        }

        private static async Task EnsureBookingsTableExists(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS bookings (
                    id uuid PRIMARY KEY,
                    customer_id uuid NOT NULL,
                    target_user_id uuid NOT NULL,
                    event_id uuid NULL,
                    target_role varchar(50),
                    service_type text NULL,
                    event_title text,
                    event_date timestamptz NULL,
                    event_end_time timestamptz NULL,
                    location text,
                    budget numeric NULL,
                    booking_fee numeric NOT NULL DEFAULT 15,
                    talent_platform_fee numeric NOT NULL DEFAULT 0,
                    notes text NULL,
                    message text,
                    status varchar(60) NOT NULL DEFAULT 'Pending',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    payment_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    service_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    talent_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    talent_platform_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    payment_method text NULL,
                    service_fee_payment_method text NULL,
                    talent_fee_payment_method text NULL,
                    talent_platform_fee_payment_method text NULL,
                    paymongo_checkout_id text NULL,
                    paymongo_checkout_url text NULL,
                    paymongo_checkout_reference text NULL,
                    paymongo_payment_id text NULL,
                    paymongo_payment_reference text NULL,
                    talent_fee_checkout_id text NULL,
                    talent_fee_checkout_url text NULL,
                    talent_fee_checkout_reference text NULL,
                    talent_fee_payment_id text NULL,
                    talent_fee_payment_reference text NULL,
                    talent_platform_fee_checkout_id text NULL,
                    talent_platform_fee_checkout_url text NULL,
                    talent_platform_fee_checkout_reference text NULL,
                    talent_platform_fee_payment_id text NULL,
                    talent_platform_fee_payment_reference text NULL,
                    paid_at timestamptz NULL,
                    service_fee_paid_at timestamptz NULL,
                    talent_fee_paid_at timestamptz NULL,
                    talent_platform_fee_paid_at timestamptz NULL,
                    booking_group_id uuid NULL,
                    booking_sequence integer NOT NULL DEFAULT 1
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();

            const string alterSql = @"
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_type text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS event_id uuid NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS event_end_time timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS booking_fee numeric NOT NULL DEFAULT 15;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee numeric NOT NULL DEFAULT 0;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS notes text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS payment_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS payment_method text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_fee_payment_method text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_payment_method text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_payment_method text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_checkout_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_checkout_url text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_checkout_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_payment_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_payment_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_checkout_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_checkout_url text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_checkout_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_payment_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_payment_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_checkout_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_checkout_url text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_checkout_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_payment_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_payment_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paid_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_fee_paid_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_paid_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_paid_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS booking_group_id uuid NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS booking_sequence integer NOT NULL DEFAULT 1;
                ALTER TABLE bookings ALTER COLUMN status TYPE varchar(60);";

            using var alterCmd = new NpgsqlCommand(alterSql, connection);
            await alterCmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureNotificationsTableExists(NpgsqlConnection connection)
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

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureEventBookingSupportExists(NpgsqlConnection connection)
        {
            const string sql = @"
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

        private static async Task InsertNotification(
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

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@type", NpgsqlDbType.Text).Value = type;
            cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = title;
            cmd.Parameters.Add("@message", NpgsqlDbType.Text).Value = message;
            cmd.Parameters.Add("@relatedId", NpgsqlDbType.Uuid).Value = (object?)relatedId ?? DBNull.Value;
            cmd.Parameters.Add("@relatedType", NpgsqlDbType.Text).Value = (object?)relatedType ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task SyncBookingIntoEventLineup(
            NpgsqlConnection connection,
            Guid bookingId,
            Guid eventId,
            Guid targetUserId,
            string targetRole)
        {
            await EnsureEventBookingSupportExists(connection);

            const string userSql = @"
                SELECT COALESCE(stagename, ''),
                       COALESCE(firstname, ''),
                       COALESCE(lastname, ''),
                       COALESCE(profile_picture, '')
                FROM users
                WHERE id = @id;";

            string displayName = "";
            string profilePicture = "";
            using (var userCmd = new NpgsqlCommand(userSql, connection))
            {
                userCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = targetUserId;
                using var reader = await userCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var stageName = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var firstName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var lastName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    profilePicture = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    displayName = !string.IsNullOrWhiteSpace(stageName) ? stageName : $"{firstName} {lastName}".Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = targetRole;
            }

            if (string.Equals(targetRole, "Sessionist", StringComparison.OrdinalIgnoreCase))
            {
                const string linkSql = @"
                    INSERT INTO event_sessionists (id, event_id, sessionist_user_id, booking_id, status, created_at)
                    VALUES (@id, @eventId, @userId, @bookingId, 'Confirmed', NOW())
                    ON CONFLICT (event_id, sessionist_user_id)
                    DO UPDATE SET booking_id = EXCLUDED.booking_id, status = 'Confirmed';";

                using var linkCmd = new NpgsqlCommand(linkSql, connection);
                linkCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                linkCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                linkCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = targetUserId;
                linkCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await linkCmd.ExecuteNonQueryAsync();
            }
            else
            {
                const string linkSql = @"
                    INSERT INTO event_artists (id, event_id, artist_user_id, booking_id, status, created_at)
                    VALUES (@id, @eventId, @userId, @bookingId, 'Confirmed', NOW())
                    ON CONFLICT (event_id, artist_user_id)
                    DO UPDATE SET booking_id = EXCLUDED.booking_id, status = 'Confirmed';";

                using var linkCmd = new NpgsqlCommand(linkSql, connection);
                linkCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                linkCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                linkCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = targetUserId;
                linkCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await linkCmd.ExecuteNonQueryAsync();
            }

            var isSessionist = string.Equals(targetRole, "Sessionist", StringComparison.OrdinalIgnoreCase);
            var roleName = isSessionist ? "Sessionist" : "Artist";

            string existingJson = "[]";
            string eventTitle = "This event";
            const string sessionistLineupSql = "SELECT COALESCE(sessionist_lineup, '[]'), COALESCE(title, 'This event') FROM events WHERE id = @id;";
            const string artistLineupSql = "SELECT COALESCE(artist_lineup, '[]'), COALESCE(title, 'This event') FROM events WHERE id = @id;";
            using (var lineupCmd = new NpgsqlCommand(isSessionist ? sessionistLineupSql : artistLineupSql, connection))
            {
                lineupCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = eventId;
                using var reader = await lineupCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    existingJson = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
                    eventTitle = reader.IsDBNull(1) ? "This event" : reader.GetString(1);
                }
            }

            List<TalentLineupItemDto> lineup;
            try
            {
                lineup = JsonSerializer.Deserialize<List<TalentLineupItemDto>>(existingJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<TalentLineupItemDto>();
            }
            catch
            {
                lineup = new List<TalentLineupItemDto>();
            }

            if (lineup.Any(item => item.id == targetUserId))
            {
                return;
            }

            lineup.Add(new TalentLineupItemDto
            {
                id = targetUserId,
                displayName = displayName,
                role = roleName,
                profilePicture = string.IsNullOrWhiteSpace(profilePicture) ? null : profilePicture
            });

            const string updateSessionistLineupSql = "UPDATE events SET sessionist_lineup = @json WHERE id = @eventId;";
            const string updateArtistLineupSql = "UPDATE events SET artist_lineup = @json WHERE id = @eventId;";
            using var updateCmd = new NpgsqlCommand(isSessionist ? updateSessionistLineupSql : updateArtistLineupSql, connection);
            updateCmd.Parameters.Add("@json", NpgsqlDbType.Text).Value = JsonSerializer.Serialize(lineup);
            updateCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
            await updateCmd.ExecuteNonQueryAsync();

            await InsertNotification(
                connection,
                targetUserId,
                "lineup_added",
                "Added to event lineup",
                $"You were added to the lineup for '{eventTitle}'.",
                eventId,
                "event");
        }

        private static async Task EnsureBookingMessagesTableExists(NpgsqlConnection connection)
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

        private static DateTime? NormalizeToUtc(DateTime? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            return value.Value.Kind switch
            {
                DateTimeKind.Utc => value.Value,
                DateTimeKind.Local => value.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Local).ToUniversalTime()
            };
        }

        private static string NormalizeTargetRole(string? role)
        {
            var normalized = (role ?? "").Trim().ToLowerInvariant();
            return normalized == "sessionist" ? "Sessionist" : "Artist";
        }

        private static string FormatPeso(decimal amount)
        {
            return $"PHP {amount:0.00}";
        }

        private static string GetPayMongoErrorMessage(string responseString, string fallbackMessage)
        {
            if (string.IsNullOrWhiteSpace(responseString))
            {
                return fallbackMessage;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                if (root.TryGetProperty("errors", out var errors) &&
                    errors.ValueKind == JsonValueKind.Array &&
                    errors.GetArrayLength() > 0)
                {
                    var firstError = errors[0];

                    if (firstError.TryGetProperty("detail", out var detail) &&
                        detail.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(detail.GetString()))
                    {
                        return detail.GetString()!;
                    }

                    if (firstError.TryGetProperty("code", out var code) &&
                        code.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(code.GetString()))
                    {
                        return $"PayMongo error: {code.GetString()}";
                    }
                }
            }
            catch
            {
            }

            return fallbackMessage;
        }

        private static string GetPayMongoHttpErrorMessage(int statusCode, string responseString, string fallbackMessage)
        {
            if (statusCode == StatusCodes.Status401Unauthorized)
            {
                return "PayMongo rejected the configured secret key. Use the exact test secret key for local checkout.";
            }

            return GetPayMongoErrorMessage(responseString, fallbackMessage);
        }
    }
}
