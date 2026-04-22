using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class CheckoutRequest
    {
        // FIX: Changed to strings so ASP.NET stops auto-blocking them!
        public string eventId { get; set; }
        public string customerId { get; set; }
        
        public string tierName { get; set; }
        public int quantity { get; set; }
        public decimal totalPrice { get; set; }
        public string successUrl { get; set; } 
        public string cancelUrl { get; set; }  
    }

    public class ScanTicketRequest
    {
        public string? ticketValue { get; set; }
    }

    internal sealed record ParsedTicketScan(Guid TicketId, int? UnitNumber);

    internal sealed record TicketPricingPhase(
        string Name,
        decimal Multiplier,
        int PercentageMarkup,
        string BadgeTone,
        string Description);

    [Route("api/[controller]")]
    [ApiController]
    public class TicketController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly string _paymongoSecretKey;
        private readonly IConfiguration _configuration;
        private const decimal PayMongoMinimumAmount = 20m;
        private const decimal TicketServiceFeeRate = 0.05m;

        public TicketController(IConfiguration configuration)
        {
            _configuration = configuration;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _paymongoSecretKey = configuration["PayMongo:SecretKey"] ?? string.Empty;
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

        [HttpGet("event-checkout/{id}")]
        public async Task<IActionResult> GetEventForCheckout(Guid id)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                string sql = @"
                    SELECT title,
                           poster_url,
                           organizer_id,
                           base_price,
                           tier_name,
                           tier_price,
                           total_slots,
                           tickets_sold,
                           bundles,
                           event_time,
                           sale_name,
                           sale_type,
                           sale_value,
                           sale_starts_at,
                           sale_ends_at,
                           COALESCE(max_tickets_per_customer, 5)
                    FROM events
                    WHERE id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var organizerId = reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2);
                    var basePrice = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3);
                    var tierPrice = reader.IsDBNull(5) ? null : (decimal?)reader.GetDecimal(5);
                    var eventTime = reader.IsDBNull(9) ? DateTime.UtcNow : reader.GetDateTime(9);
                    var saleName = reader.IsDBNull(10) ? null : reader.GetString(10);
                    var saleType = reader.IsDBNull(11) ? null : reader.GetString(11);
                    var saleValue = reader.IsDBNull(12) ? null : (decimal?)reader.GetDecimal(12);
                    var saleStartsAt = reader.IsDBNull(13) ? null : (DateTime?)reader.GetDateTime(13);
                    var saleEndsAt = reader.IsDBNull(14) ? null : (DateTime?)reader.GetDateTime(14);
                    var maxTicketsPerCustomer = reader.IsDBNull(15) ? 5 : Math.Clamp(reader.GetInt32(15), 3, 10);
                    var saleActive = IsSaleActive(saleStartsAt, saleEndsAt);
                    var pricingPhase = GetTicketPricingPhase(eventTime);
                    var adjustedBasePrice = ApplyPhaseMarkup(ApplySale(basePrice, saleType, saleValue, saleActive), pricingPhase);
                    decimal? adjustedTierPrice = tierPrice.HasValue
                        ? ApplyPhaseMarkup(ApplySale(tierPrice.Value, saleType, saleValue, saleActive), pricingPhase)
                        : null;
                    var bundleTiers = BuildBundleTierPayload(
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        saleType,
                        saleValue,
                        saleActive,
                        pricingPhase);

                    return Ok(new
                    {
                        title = reader.GetString(0),
                        posterUrl = reader.IsDBNull(1) ? "https://images.unsplash.com/photo-1492684223066-81342ee5ff30" : reader.GetString(1),
                        organizerId,
                        basePrice,
                        displayBasePrice = adjustedBasePrice,
                        tierName = reader.IsDBNull(4) ? null : reader.GetString(4),
                        tierPrice,
                        displayTierPrice = adjustedTierPrice,
                        slots = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                        ticketsSold = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                        bundles = reader.IsDBNull(8) ? null : reader.GetString(8),
                        time = eventTime,
                        saleName,
                        saleType,
                        saleValue,
                        saleStartsAt,
                        saleEndsAt,
                        saleActive,
                        pricingPhase = pricingPhase.Name,
                        pricingPhasePercentage = pricingPhase.PercentageMarkup,
                        pricingPhaseTone = pricingPhase.BadgeTone,
                        pricingPhaseDescription = pricingPhase.Description,
                        pricingMultiplier = pricingPhase.Multiplier,
                        maxTicketsPerCustomer,
                        ticketServiceFeeRate = TicketServiceFeeRate,
                        ticketServiceFeeLabel = $"Ticket Service Fee ({TicketServiceFeeRate * 100:0}%)",
                        bundleTiers
                    });
                }
                return NotFound(new { message = "Event not found." });
            }
            catch (Exception ex) { return StatusCode(500, new { message = "DB Fetch Error: " + ex.Message }); }
        }

        [HttpPost("checkout")]
        public async Task<IActionResult> Checkout([FromBody] CheckoutRequest req)
        {
            try
            {
                if (req.totalPrice < 20)
                {
                    return BadRequest(new { message = "PayMongo requires a minimum amount of ₱20.00." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketPaymentColumnsExist(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                // Safely parse the string IDs back to UUIDs for the database
                Guid parsedEventId = Guid.Parse(req.eventId);
                Guid parsedCustomerId = Guid.Parse(req.customerId);

                string eventTitle = "Imajination Ticket";
                Guid organizerId = Guid.Empty;
                decimal basePrice = 0m;
                string? primaryTierName = null;
                decimal? primaryTierPrice = null;
                string? bundles = null;
                DateTime eventTime = DateTime.UtcNow;
                string? saleType = null;
                decimal? saleValue = null;
                DateTime? saleStartsAt = null;
                DateTime? saleEndsAt = null;
                int maxTicketsPerCustomer = 5;
                string getTitleSql = @"
                    SELECT title, organizer_id, base_price, tier_name, tier_price, bundles, event_time, sale_type, sale_value, sale_starts_at, sale_ends_at, COALESCE(max_tickets_per_customer, 5)
                    FROM events
                    WHERE id = @id";
                using (var titleCmd = new NpgsqlCommand(getTitleSql, connection))
                {
                    titleCmd.Parameters.AddWithValue("@id", parsedEventId);
                    using var reader = await titleCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0)) eventTitle = reader.GetString(0);
                        if (!reader.IsDBNull(1)) organizerId = reader.GetGuid(1);
                        basePrice = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
                        primaryTierName = reader.IsDBNull(3) ? null : reader.GetString(3);
                        primaryTierPrice = reader.IsDBNull(4) ? null : reader.GetDecimal(4);
                        bundles = reader.IsDBNull(5) ? null : reader.GetString(5);
                        eventTime = reader.IsDBNull(6) ? DateTime.UtcNow : reader.GetDateTime(6);
                        saleType = reader.IsDBNull(7) ? null : reader.GetString(7);
                        saleValue = reader.IsDBNull(8) ? null : reader.GetDecimal(8);
                        saleStartsAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9);
                        saleEndsAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10);
                        maxTicketsPerCustomer = reader.IsDBNull(11) ? 5 : Math.Clamp(reader.GetInt32(11), 3, 10);
                    }
                    else
                    {
                        return NotFound(new { message = "Event not found." });
                    }
                }

                if (organizerId != Guid.Empty && organizerId == parsedCustomerId)
                {
                    return BadRequest(new { message = "Event organizers cannot buy tickets for their own event." });
                }

                if (req.quantity < 1 || req.quantity > maxTicketsPerCustomer)
                {
                    return BadRequest(new { message = $"This event allows up to {maxTicketsPerCustomer} tickets per customer." });
                }

                const string personalLimitSql = @"
                    SELECT COALESCE(SUM(quantity), 0)
                    FROM tickets
                    WHERE event_id = @eventId
                      AND customer_id = @customerId;";
                await using (var personalLimitCmd = new NpgsqlCommand(personalLimitSql, connection))
                {
                    personalLimitCmd.Parameters.AddWithValue("@eventId", parsedEventId);
                    personalLimitCmd.Parameters.AddWithValue("@customerId", parsedCustomerId);
                    var existingCount = Convert.ToInt32(await personalLimitCmd.ExecuteScalarAsync() ?? 0);
                    if (existingCount + req.quantity > maxTicketsPerCustomer)
                    {
                        return BadRequest(new
                        {
                            message = $"You already reserved {existingCount} ticket(s) for this event. The organizer limit is {maxTicketsPerCustomer} per customer."
                        });
                    }
                }

                var normalizedTierName = string.IsNullOrWhiteSpace(req.tierName) ? "General Admission" : req.tierName.Trim();
                var saleActive = IsSaleActive(saleStartsAt, saleEndsAt);
                var pricingPhase = GetTicketPricingPhase(eventTime);
                var unitPrice = ResolveTierPrice(normalizedTierName, basePrice, primaryTierName, primaryTierPrice, bundles, saleType, saleValue, saleActive, pricingPhase);
                var subtotal = decimal.Round(unitPrice * req.quantity, 2);
                var serviceFee = decimal.Round(subtotal * TicketServiceFeeRate, 2);
                var finalTotal = subtotal + serviceFee;

                string insertSql = @"
                    INSERT INTO tickets (
                        event_id, customer_id, tier_name, quantity, total_price, payment_method,
                        ticket_unit_price, ticket_subtotal, ticket_service_fee, ticket_service_fee_rate,
                        pricing_phase_name, pricing_phase_percentage
                    ) 
                    VALUES (
                        @eId, @cId, @tier, @qty, @total, 'AwaitingPayment',
                        @unitPrice, @subtotal, @serviceFee, @serviceFeeRate,
                        @phaseName, @phasePercentage
                    ) RETURNING id";
                
                using var insertCmd = new NpgsqlCommand(insertSql, connection);
                insertCmd.Parameters.AddWithValue("@eId", parsedEventId);
                insertCmd.Parameters.AddWithValue("@cId", parsedCustomerId);
                insertCmd.Parameters.AddWithValue("@tier", normalizedTierName);
                insertCmd.Parameters.AddWithValue("@qty", req.quantity);
                insertCmd.Parameters.AddWithValue("@total", finalTotal);
                insertCmd.Parameters.AddWithValue("@unitPrice", unitPrice);
                insertCmd.Parameters.AddWithValue("@subtotal", subtotal);
                insertCmd.Parameters.AddWithValue("@serviceFee", serviceFee);
                insertCmd.Parameters.AddWithValue("@serviceFeeRate", TicketServiceFeeRate);
                insertCmd.Parameters.AddWithValue("@phaseName", pricingPhase.Name);
                insertCmd.Parameters.AddWithValue("@phasePercentage", pricingPhase.PercentageMarkup);
                var newTicketId = await insertCmd.ExecuteScalarAsync();

                string updateSql = "UPDATE events SET tickets_sold = COALESCE(tickets_sold, 0) + @qty WHERE id = @eId";
                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("@qty", req.quantity);
                updateCmd.Parameters.AddWithValue("@eId", parsedEventId);
                await updateCmd.ExecuteNonQueryAsync();

                var successUrl = AppendQuery(req.successUrl, $"ticketPaid=1&ticketId={newTicketId}");
                var cancelUrl = AppendQuery(req.cancelUrl, $"ticketPending=1&ticketId={newTicketId}");

                if (string.IsNullOrWhiteSpace(_paymongoSecretKey))
                {
                    return StatusCode(500, new { message = "PayMongo is not configured yet. Add PayMongo:SecretKey before starting checkout." });
                }

                if (finalTotal < PayMongoMinimumAmount)
                {
                    return BadRequest(new { message = $"PayMongo requires a minimum amount of ₱{PayMongoMinimumAmount:0.00}." });
                }

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
                                new { currency = "PHP", amount = (int)(unitPrice * 100), name = $"{normalizedTierName} Ticket - {eventTitle}", quantity = req.quantity },
                                new { currency = "PHP", amount = (int)(serviceFee * 100), name = $"Ticket Service Fee ({TicketServiceFeeRate * 100:0}%)", quantity = 1 }
                            },
                            success_url = successUrl,
                            cancel_url = cancelUrl
                        }
                    }
                };

                using var client = new HttpClient();
                var plainTextBytes = Encoding.UTF8.GetBytes(_paymongoSecretKey);
                string base64Auth = Convert.ToBase64String(plainTextBytes);
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

                using JsonDocument doc = JsonDocument.Parse(responseString);
                var data = doc.RootElement.GetProperty("data");
                string checkoutUrl = data.GetProperty("attributes").GetProperty("checkout_url").GetString();
                string checkoutId = data.GetProperty("id").GetString();
                string checkoutReference = data.GetProperty("attributes").TryGetProperty("reference_number", out var referenceProp)
                    ? referenceProp.GetString()
                    : string.Empty;

                string ticketUpdateSql = @"
                    UPDATE tickets
                    SET paymongo_checkout_id = @checkoutId,
                        paymongo_checkout_reference = @checkoutReference,
                        payment_method = 'AwaitingPayment'
                    WHERE id = @ticketId";

                using var ticketUpdateCmd = new NpgsqlCommand(ticketUpdateSql, connection);
                ticketUpdateCmd.Parameters.AddWithValue("@checkoutId", (object?)checkoutId ?? DBNull.Value);
                ticketUpdateCmd.Parameters.AddWithValue("@checkoutReference", (object?)checkoutReference ?? DBNull.Value);
                ticketUpdateCmd.Parameters.AddWithValue("@ticketId", (Guid)newTicketId);
                await ticketUpdateCmd.ExecuteNonQueryAsync();

                await PaymentLedgerService.UpsertPendingAsync(
                    connection,
                    paymentScope: "ticket_purchase",
                    userId: parsedCustomerId,
                    organizerId: organizerId == Guid.Empty ? null : organizerId,
                    eventId: parsedEventId,
                    ticketId: (Guid)newTicketId,
                    bookingId: null,
                    amount: finalTotal,
                    description: $"Ticket purchase for {eventTitle}",
                    checkoutId: checkoutId ?? string.Empty,
                    checkoutReference: checkoutReference ?? string.Empty,
                    featureUnlockState: "TicketPending",
                    metadata: new
                    {
                        tierName = normalizedTierName,
                        quantity = req.quantity,
                        subtotal,
                        serviceFee,
                        pricingPhase = pricingPhase.Name,
                        pricingPhasePercentage = pricingPhase.PercentageMarkup
                    });

                return Ok(new { message = "Checkout session created!", checkoutUrl = checkoutUrl });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Checkout could not be started right now.",
                        ex)
                });
            }
        }

        private static bool IsSaleActive(DateTime? startsAt, DateTime? endsAt)
        {
            var now = DateTime.UtcNow;
            var hasStarted = !startsAt.HasValue || startsAt.Value <= now;
            var hasNotEnded = !endsAt.HasValue || endsAt.Value >= now;
            return hasStarted && hasNotEnded;
        }

        private static TicketPricingPhase GetTicketPricingPhase(DateTime eventTime)
        {
            var normalizedEventTime = eventTime.Kind == DateTimeKind.Utc ? eventTime : eventTime.ToUniversalTime();
            var hoursUntilEvent = (normalizedEventTime - DateTime.UtcNow).TotalHours;

            if (hoursUntilEvent <= 0)
            {
                return new TicketPricingPhase(
                    "Walk-In",
                    1.20m,
                    20,
                    "red",
                    "Walk-in pricing adds 20% once the event has started.");
            }

            if (hoursUntilEvent <= 2)
            {
                return new TicketPricingPhase(
                    "Early Bird",
                    1.10m,
                    10,
                    "amber",
                    "Early-bird pricing adds 10% close to show time.");
            }

            return new TicketPricingPhase(
                "Pre-Sale",
                1.00m,
                0,
                "blue",
                "Pre-sale pricing uses the standard ticket rate.");
        }

        private static decimal ApplyPhaseMarkup(decimal basePrice, TicketPricingPhase pricingPhase)
            => decimal.Round(basePrice * pricingPhase.Multiplier, 2);

        private static decimal ApplySale(decimal originalPrice, string? saleType, decimal? saleValue, bool saleActive)
        {
            if (!saleActive || !saleValue.HasValue || saleValue.Value <= 0) return originalPrice;

            var normalizedType = (saleType ?? string.Empty).Trim().ToLowerInvariant();
            decimal discounted = normalizedType switch
            {
                "percent" => originalPrice - (originalPrice * (saleValue.Value / 100m)),
                "amount" => originalPrice - saleValue.Value,
                _ => originalPrice
            };

            return discounted < 0 ? 0 : decimal.Round(discounted, 2);
        }

        private static decimal ResolveTierPrice(
            string requestedTier,
            decimal basePrice,
            string? specialTierName,
            decimal? specialTierPrice,
            string? bundles,
            string? saleType,
            decimal? saleValue,
            bool saleActive,
            TicketPricingPhase pricingPhase)
        {
            decimal resolvedPrice = basePrice;

            if (!string.IsNullOrWhiteSpace(requestedTier))
            {
                if (!string.IsNullOrWhiteSpace(specialTierName) &&
                    requestedTier.Equals(specialTierName.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    specialTierPrice.HasValue)
                {
                    resolvedPrice = specialTierPrice.Value;
                }
                else if (!string.IsNullOrWhiteSpace(bundles))
                {
                    foreach (var bundle in ParseBundleTierRecords(bundles))
                    {
                        if (requestedTier.Equals(bundle.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            resolvedPrice = bundle.Price;
                            break;
                        }
                    }
                }
            }

            var saleAdjustedPrice = ApplySale(resolvedPrice, saleType, saleValue, saleActive);
            return ApplyPhaseMarkup(saleAdjustedPrice, pricingPhase);
        }

        private static List<object> BuildBundleTierPayload(
            string? bundles,
            string? saleType,
            decimal? saleValue,
            bool saleActive,
            TicketPricingPhase pricingPhase)
        {
            if (string.IsNullOrWhiteSpace(bundles))
            {
                return new List<object>();
            }

            return ParseBundleTierRecords(bundles)
                .Select(bundle => new
                {
                    tierName = bundle.Name,
                    basePrice = bundle.Price,
                    displayPrice = ApplyPhaseMarkup(ApplySale(bundle.Price, saleType, saleValue, saleActive), pricingPhase),
                    slots = bundle.Slots
                })
                .Cast<object>()
                .ToList();
        }

        private static IEnumerable<(string Name, decimal Price, int Slots)> ParseBundleTierRecords(string bundles)
        {
            var parts = bundles.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    part,
                    @"\[Tier\]\s*(.*?):\s*P([0-9.]+)\s*\((\d+)\s*slots\)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!match.Success) continue;
                if (!decimal.TryParse(match.Groups[2].Value, out var price)) continue;

                yield return (
                    match.Groups[1].Value.Trim(),
                    price,
                    int.TryParse(match.Groups[3].Value, out var slots) ? slots : 0
                );
            }
        }

        [HttpGet("customer/{customerId}")]
        public async Task<IActionResult> GetCustomerTickets(Guid customerId)
        {
            try
            {
                var tickets = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketPaymentColumnsExist(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);

                // FIX: Added 'e.status' so the frontend knows if the event is Finished!
                string sql = @"
                    SELECT t.id, t.event_id, t.tier_name, t.quantity, t.total_price, t.payment_method, t.purchase_date, t.is_used,
                           e.title, e.event_time, e.location, e.status, COALESCE(t.paymongo_payment_reference, ''), COALESCE(t.paymongo_checkout_reference, ''),
                           COALESCE(t.ticket_unit_price, 0), COALESCE(t.ticket_subtotal, t.total_price), COALESCE(t.ticket_service_fee, 0),
                           COALESCE(t.ticket_service_fee_rate, 0), COALESCE(t.pricing_phase_name, 'Pre-Sale'), COALESCE(t.pricing_phase_percentage, 0),
                           COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 1) ELSE 0 END),
                           COALESCE(t.used_ticket_units, '')
                    FROM tickets t
                    JOIN events e ON t.event_id = e.id
                    WHERE t.customer_id = @cId
                    ORDER BY t.purchase_date DESC";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@cId", customerId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var quantity = reader.GetInt32(3);
                    var usedQuantity = reader.IsDBNull(20) ? (reader.IsDBNull(7) ? 0 : (reader.GetBoolean(7) ? quantity : 0)) : reader.GetInt32(20);
                    usedQuantity = Math.Clamp(usedQuantity, 0, Math.Max(quantity, 0));
                    tickets.Add(new
                    {
                        ticketId = reader.GetGuid(0),
                        eventId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
                        tierName = reader.GetString(2),
                        quantity = quantity,
                        totalPrice = reader.GetDecimal(4),
                        paymentMethod = reader.GetString(5),
                        purchaseDate = reader.GetDateTime(6),
                        isUsed = quantity > 0 && usedQuantity >= quantity,
                        usedQuantity = usedQuantity,
                        remainingQuantity = Math.Max(quantity - usedQuantity, 0),
                        hasScannedUnits = usedQuantity > 0,
                        usedTicketUnits = ParseUsedTicketUnits(reader.IsDBNull(21) ? "" : reader.GetString(21)),
                        eventTitle = reader.GetString(8),
                        eventTime = reader.GetDateTime(9),
                        location = reader.GetString(10),
                        eventStatus = reader.IsDBNull(11) ? "Upcoming" : reader.GetString(11),
                        paymentReference = reader.IsDBNull(12) ? "" : reader.GetString(12),
                        checkoutReference = reader.IsDBNull(13) ? "" : reader.GetString(13),
                        unitPrice = reader.IsDBNull(14) ? 0 : reader.GetDecimal(14),
                        subtotal = reader.IsDBNull(15) ? reader.GetDecimal(4) : reader.GetDecimal(15),
                        serviceFee = reader.IsDBNull(16) ? 0 : reader.GetDecimal(16),
                        serviceFeeRate = reader.IsDBNull(17) ? 0 : reader.GetDecimal(17),
                        pricingPhase = reader.IsDBNull(18) ? "Pre-Sale" : reader.GetString(18),
                        pricingPhasePercentage = reader.IsDBNull(19) ? 0 : reader.GetInt32(19)
                    });
                }
                return Ok(tickets);
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        [HttpPost("{ticketId}/payment/confirm")]
        public async Task<IActionResult> ConfirmTicketPayment(Guid ticketId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketPaymentColumnsExist(connection);

                const string sql = @"
                    SELECT COALESCE(paymongo_checkout_id, ''),
                           COALESCE(payment_method, ''),
                           COALESCE(paymongo_checkout_reference, '')
                    FROM tickets
                    WHERE id = @id";

                string checkoutId;
                string paymentMethod;
                string checkoutReference;

                using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.AddWithValue("@id", ticketId);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Ticket not found." });
                    }

                    checkoutId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    paymentMethod = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    checkoutReference = reader.IsDBNull(2) ? "" : reader.GetString(2);
                }

                if (paymentMethod != null && paymentMethod != "" && paymentMethod != "AwaitingPayment" && paymentMethod != "PayMongo")
                {
                    return Ok(new { message = "Ticket payment already confirmed.", paymentMethod });
                }

                if (string.IsNullOrWhiteSpace(checkoutId))
                {
                    return BadRequest(new { message = "No checkout session is attached to this ticket yet." });
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
                if (string.IsNullOrWhiteSpace(checkoutReference) && attributes.TryGetProperty("reference_number", out var checkoutReferenceProp))
                {
                    checkoutReference = checkoutReferenceProp.GetString() ?? "";
                }

                var payments = attributes.TryGetProperty("payments", out var paymentArray) && paymentArray.ValueKind == JsonValueKind.Array
                    ? paymentArray
                    : default;

                if (payments.ValueKind != JsonValueKind.Array || payments.GetArrayLength() == 0)
                {
                    return Ok(new { message = "Payment is still pending.", paymentMethod = "AwaitingPayment" });
                }

                string confirmedMethod = "PayMongo";
                string paymentReference = "";

                var firstPayment = payments[0];
                if (firstPayment.TryGetProperty("attributes", out var paymentAttributes))
                {
                    if (paymentAttributes.TryGetProperty("reference_number", out var paymentReferenceProp))
                    {
                        paymentReference = paymentReferenceProp.GetString() ?? "";
                    }

                    if (paymentAttributes.TryGetProperty("source", out var sourceProp) &&
                        sourceProp.ValueKind == JsonValueKind.Object &&
                        sourceProp.TryGetProperty("type", out var sourceTypeProp))
                    {
                        confirmedMethod = sourceTypeProp.GetString() ?? "PayMongo";
                    }
                }

                const string updateSql = @"
                    UPDATE tickets
                    SET payment_method = @paymentMethod,
                        paymongo_payment_reference = @paymentReference,
                        paymongo_checkout_reference = @checkoutReference
                    WHERE id = @ticketId";

                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("@paymentMethod", confirmedMethod);
                updateCmd.Parameters.AddWithValue("@paymentReference", (object?)paymentReference ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@checkoutReference", (object?)checkoutReference ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@ticketId", ticketId);
                await updateCmd.ExecuteNonQueryAsync();

                await PaymentLedgerService.MarkPaidAsync(
                    connection,
                    paymentScope: "ticket_purchase",
                    ticketId: ticketId,
                    bookingId: null,
                    paymentMethod: confirmedMethod,
                    paymentReference: paymentReference,
                    checkoutReference: checkoutReference,
                    featureUnlockState: "TicketIssued");

                var details = await GetTicketNotificationContextAsync(connection, ticketId);
                if (details.customerId != Guid.Empty)
                {
                    await NotificationSupport.InsertNotificationIfNotExistsAsync(
                        connection,
                        details.customerId,
                        "ticket_purchase",
                        "Ticket payment confirmed",
                        $"Your ticket for '{details.eventTitle}' is confirmed.",
                        ticketId,
                        "ticket",
                        24);
                }

                if (details.organizerId != Guid.Empty)
                {
                    await NotificationSupport.InsertNotificationIfNotExistsAsync(
                        connection,
                        details.organizerId,
                        "ticket_sale",
                        "New ticket purchase",
                        $"{details.customerName} purchased {details.quantity} ticket(s) for '{details.eventTitle}'.",
                        ticketId,
                        "ticket",
                        24);
                }

                return Ok(new { message = "Ticket payment confirmed.", paymentMethod = confirmedMethod });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Failed to confirm ticket payment.",
                        ex)
                });
            }
        }

        [HttpPost("scan")]
        public async Task<IActionResult> ScanTicket([FromBody] ScanTicketRequest? req, [FromQuery] Guid? eventId = null)
        {
            try
            {
                if (eventId == null || eventId == Guid.Empty)
                {
                    return BadRequest(new { message = "EVENT REQUIRED", details = "Open the scanner from a specific organizer event before validating tickets." });
                }

                var parsedScan = ParseTicketScan(req?.ticketValue);
                if (parsedScan == null || parsedScan.TicketId == Guid.Empty)
                {
                    return BadRequest(new
                    {
                        message = "INVALID QR",
                        details = "The scanned QR code does not contain a valid ticket ID."
                    });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketPaymentColumnsExist(connection);

                string checkSql = @"
                    SELECT t.is_used,
                           t.tier_name,
                           t.quantity,
                           e.title,
                           e.event_time,
                           e.status,
                           e.id,
                           COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 1) ELSE 0 END),
                           COALESCE(t.used_ticket_units, '')
                    FROM tickets t
                    JOIN events e ON t.event_id = e.id 
                    WHERE t.id = @id";
                    
                using var checkCmd = new NpgsqlCommand(checkSql, connection);
                checkCmd.Parameters.AddWithValue("@id", parsedScan.TicketId);

                using var reader = await checkCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "INVALID TICKET: This ticket does not exist in the database." });
                }

                bool isUsed = reader.IsDBNull(0) ? false : reader.GetBoolean(0);
                string tier = reader.GetString(1);
                int qty = reader.GetInt32(2);
                string evTitle = reader.GetString(3);
                DateTime eventTime = reader.GetDateTime(4);
                string eventStatus = reader.IsDBNull(5) ? "Upcoming" : reader.GetString(5);
                Guid ticketEventId = reader.IsDBNull(6) ? Guid.Empty : reader.GetGuid(6);
                int usedQuantity = reader.IsDBNull(7) ? (isUsed ? qty : 0) : reader.GetInt32(7);
                var usedUnits = ParseUsedTicketUnits(reader.IsDBNull(8) ? "" : reader.GetString(8));
                
                await reader.CloseAsync();

                if (ticketEventId != eventId.Value)
                {
                    return BadRequest(new
                    {
                        message = "WRONG EVENT",
                        details = $"This ticket belongs to '{evTitle}', not the event currently selected in the scanner."
                    });
                }

                if (eventStatus == "Finished" || DateTime.Now > eventTime.AddHours(24))
                {
                    return BadRequest(new { message = "EXPIRED TICKET", details = $"The event '{evTitle}' has already ended." });
                }

                if (qty > 1 && !parsedScan.UnitNumber.HasValue)
                {
                    return BadRequest(new
                    {
                        message = "INDIVIDUAL QR REQUIRED",
                        details = $"This purchase contains {qty} tickets. Use the specific QR for each ticket holder."
                    });
                }

                var unitNumber = parsedScan.UnitNumber.GetValueOrDefault(1);
                if (unitNumber < 1 || unitNumber > Math.Max(qty, 1))
                {
                    return BadRequest(new
                    {
                        message = "INVALID QR",
                        details = $"The scanned QR points to ticket {unitNumber}, but this order only has {qty} ticket(s)."
                    });
                }

                if (usedUnits.Contains(unitNumber))
                {
                    return BadRequest(new
                    {
                        message = "ALREADY SCANNED!",
                        details = $"Ticket {unitNumber} of {qty} for {evTitle} was already used."
                    });
                }

                if (isUsed || usedQuantity >= qty)
                {
                    return BadRequest(new { message = "ALREADY SCANNED!", details = $"{qty}x {tier} for {evTitle} was already used." });
                }

                usedUnits.Add(unitNumber);
                var nextUsedQuantity = usedUnits.Count;
                var fullyUsed = nextUsedQuantity >= qty;

                string updateSql = @"
                    UPDATE tickets
                    SET used_quantity = @usedQuantity,
                        used_ticket_units = @usedUnits,
                        is_used = @isUsed
                    WHERE id = @id";
                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("@id", parsedScan.TicketId);
                updateCmd.Parameters.AddWithValue("@usedQuantity", nextUsedQuantity);
                updateCmd.Parameters.AddWithValue("@usedUnits", SerializeUsedTicketUnits(usedUnits));
                updateCmd.Parameters.AddWithValue("@isUsed", fullyUsed);
                await updateCmd.ExecuteNonQueryAsync();

                return Ok(new
                {
                    message = "SUCCESS! Ticket is Valid.",
                    details = qty > 1
                        ? $"Ticket {unitNumber} of {qty} for {tier} at {evTitle} was accepted. {Math.Max(qty - nextUsedQuantity, 0)} remaining."
                        : $"1x {tier} for {evTitle}",
                    usedQuantity = nextUsedQuantity,
                    remainingQuantity = Math.Max(qty - nextUsedQuantity, 0),
                    isFullyUsed = fullyUsed
                });
            }
            catch (Exception ex) { return StatusCode(500, new { message = "Database Error: " + ex.Message }); }
        }

        private static ParsedTicketScan? ParseTicketScan(string? ticketValue)
        {
            if (string.IsNullOrWhiteSpace(ticketValue)) return null;

            var trimmed = ticketValue.Trim();
            if (Guid.TryParse(trimmed, out var directGuid))
            {
                return new ParsedTicketScan(directGuid, null);
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                if (query.TryGetValue("ticketId", out var ticketIdValues) && Guid.TryParse(ticketIdValues.FirstOrDefault(), out var queryGuid))
                {
                    int? unitNumber = null;
                    if (query.TryGetValue("unit", out var unitValues) && int.TryParse(unitValues.FirstOrDefault(), out var parsedUnit))
                    {
                        unitNumber = parsedUnit;
                    }

                    return new ParsedTicketScan(queryGuid, unitNumber);
                }
            }

            var pipeMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"(?<ticketId>[0-9a-fA-F]{8}\-(?:[0-9a-fA-F]{4}\-){3}[0-9a-fA-F]{12})\|(?<unit>\d+)$");
            if (pipeMatch.Success && Guid.TryParse(pipeMatch.Groups["ticketId"].Value, out var pipeGuid))
            {
                return new ParsedTicketScan(
                    pipeGuid,
                    int.TryParse(pipeMatch.Groups["unit"].Value, out var pipeUnit) ? pipeUnit : null);
            }

            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"[0-9a-fA-F]{8}\-(?:[0-9a-fA-F]{4}\-){3}[0-9a-fA-F]{12}");
            if (match.Success && Guid.TryParse(match.Value, out var embeddedGuid))
            {
                return new ParsedTicketScan(embeddedGuid, null);
            }

            return null;
        }

        internal static List<int> ParseUsedTicketUnits(string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new List<int>();
            }

            return rawValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
                .Where(value => value > 0)
                .Distinct()
                .OrderBy(value => value)
                .ToList();
        }

        private static string SerializeUsedTicketUnits(IEnumerable<int> units)
        {
            return string.Join(",", units
                .Where(value => value > 0)
                .Distinct()
                .OrderBy(value => value));
        }

        private static async Task EnsureTicketPaymentColumnsExist(NpgsqlConnection connection)
        {
            const string alterSql = @"
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_checkout_id text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_checkout_reference text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_payment_reference text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS ticket_unit_price numeric(12,2) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS ticket_subtotal numeric(12,2) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS ticket_service_fee numeric(12,2) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS ticket_service_fee_rate numeric(8,4) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS pricing_phase_name varchar(50) NOT NULL DEFAULT 'Pre-Sale';
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS pricing_phase_percentage integer NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS used_quantity integer NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS used_ticket_units text NOT NULL DEFAULT '';
                UPDATE tickets
                SET used_quantity = COALESCE(quantity, 1)
                WHERE COALESCE(is_used, FALSE) = TRUE
                  AND COALESCE(used_quantity, 0) = 0;";

            using var cmd = new NpgsqlCommand(alterSql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<(Guid customerId, Guid organizerId, string eventTitle, int quantity, string customerName)> GetTicketNotificationContextAsync(NpgsqlConnection connection, Guid ticketId)
        {
            const string sql = @"
                SELECT
                    t.customer_id,
                    COALESCE(e.organizer_id, '00000000-0000-0000-0000-000000000000'::uuid),
                    COALESCE(e.title, 'Your event'),
                    COALESCE(t.quantity, 1),
                    COALESCE(u.firstname, ''),
                    COALESCE(u.lastname, '')
                FROM tickets t
                INNER JOIN events e ON e.id = t.event_id
                LEFT JOIN users u ON u.id = t.customer_id
                WHERE t.id = @id
                LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@id", ticketId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var first = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var last = reader.IsDBNull(5) ? "" : reader.GetString(5);
                return (
                    reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0),
                    reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
                    reader.IsDBNull(2) ? "Your event" : reader.GetString(2),
                    reader.IsDBNull(3) ? 1 : reader.GetInt32(3),
                    string.IsNullOrWhiteSpace($"{first} {last}".Trim()) ? "A customer" : $"{first} {last}".Trim()
                );
            }

            return (Guid.Empty, Guid.Empty, "Your event", 1, "A customer");
        }

        private static string AppendQuery(string? url, string query)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "";
            }

            var separator = url.Contains('?') ? "&" : "?";
            return $"{url}{separator}{query}";
        }
    }
}
