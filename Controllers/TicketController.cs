using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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

    [Route("api/[controller]")]
    [ApiController]
    public class TicketController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly string _paymongoSecretKey;

        public TicketController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _paymongoSecretKey = configuration["PayMongo:SecretKey"] ?? string.Empty;
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
                           sale_ends_at
                    FROM events
                    WHERE id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var basePrice = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
                    var tierPrice = reader.IsDBNull(4) ? null : (decimal?)reader.GetDecimal(4);
                    var saleName = reader.IsDBNull(9) ? null : reader.GetString(9);
                    var saleType = reader.IsDBNull(10) ? null : reader.GetString(10);
                    var saleValue = reader.IsDBNull(11) ? null : (decimal?)reader.GetDecimal(11);
                    var saleStartsAt = reader.IsDBNull(12) ? null : (DateTime?)reader.GetDateTime(12);
                    var saleEndsAt = reader.IsDBNull(13) ? null : (DateTime?)reader.GetDateTime(13);
                    var saleActive = IsSaleActive(saleStartsAt, saleEndsAt);
                    var adjustedBasePrice = ApplySale(basePrice, saleType, saleValue, saleActive);
                    decimal? adjustedTierPrice = tierPrice.HasValue
                        ? ApplySale(tierPrice.Value, saleType, saleValue, saleActive)
                        : null;

                    return Ok(new
                    {
                        title = reader.GetString(0),
                        posterUrl = reader.IsDBNull(1) ? "https://images.unsplash.com/photo-1492684223066-81342ee5ff30" : reader.GetString(1),
                        basePrice,
                        displayBasePrice = adjustedBasePrice,
                        tierName = reader.IsDBNull(3) ? null : reader.GetString(3),
                        tierPrice,
                        displayTierPrice = adjustedTierPrice,
                        slots = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                        ticketsSold = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                        bundles = reader.IsDBNull(7) ? null : reader.GetString(7),
                        time = reader.IsDBNull(8) ? DateTime.Now : reader.GetDateTime(8),
                        saleName,
                        saleType,
                        saleValue,
                        saleStartsAt,
                        saleEndsAt,
                        saleActive
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
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                // Safely parse the string IDs back to UUIDs for the database
                Guid parsedEventId = Guid.Parse(req.eventId);
                Guid parsedCustomerId = Guid.Parse(req.customerId);

                string eventTitle = "Imajination Ticket";
                string getTitleSql = "SELECT title FROM events WHERE id = @id";
                using (var titleCmd = new NpgsqlCommand(getTitleSql, connection))
                {
                    titleCmd.Parameters.AddWithValue("@id", parsedEventId);
                    var result = await titleCmd.ExecuteScalarAsync();
                    if (result != null) eventTitle = result.ToString();
                }

                string insertSql = @"
                    INSERT INTO tickets (event_id, customer_id, tier_name, quantity, total_price, payment_method) 
                    VALUES (@eId, @cId, @tier, @qty, @total, 'AwaitingPayment') RETURNING id";
                
                using var insertCmd = new NpgsqlCommand(insertSql, connection);
                insertCmd.Parameters.AddWithValue("@eId", parsedEventId);
                insertCmd.Parameters.AddWithValue("@cId", parsedCustomerId);
                insertCmd.Parameters.AddWithValue("@tier", req.tierName);
                insertCmd.Parameters.AddWithValue("@qty", req.quantity);
                insertCmd.Parameters.AddWithValue("@total", req.totalPrice);
                var newTicketId = await insertCmd.ExecuteScalarAsync();

                string updateSql = "UPDATE events SET tickets_sold = COALESCE(tickets_sold, 0) + @qty WHERE id = @eId";
                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("@qty", req.quantity);
                updateCmd.Parameters.AddWithValue("@eId", parsedEventId);
                await updateCmd.ExecuteNonQueryAsync();

                int amountInCentavos = (int)(req.totalPrice * 100); 
                var successUrl = AppendQuery(req.successUrl, $"ticketPaid=1&ticketId={newTicketId}");
                var cancelUrl = AppendQuery(req.cancelUrl, $"ticketPending=1&ticketId={newTicketId}");

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
                                new { currency = "PHP", amount = amountInCentavos, name = $"{req.tierName} Ticket - {eventTitle}", quantity = req.quantity }
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
                    return StatusCode((int)response.StatusCode, new { message = "PayMongo API Error", details = responseString });
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

                return Ok(new { message = "Checkout session created!", checkoutUrl = checkoutUrl });
            }
            catch (Exception ex) { return StatusCode(500, new { message = "Checkout Logic Error: " + ex.Message }); }
        }

        private static bool IsSaleActive(DateTime? startsAt, DateTime? endsAt)
        {
            var now = DateTime.UtcNow;
            var hasStarted = !startsAt.HasValue || startsAt.Value <= now;
            var hasNotEnded = !endsAt.HasValue || endsAt.Value >= now;
            return hasStarted && hasNotEnded;
        }

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

        [HttpGet("customer/{customerId}")]
        public async Task<IActionResult> GetCustomerTickets(Guid customerId)
        {
            try
            {
                var tickets = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketPaymentColumnsExist(connection);

                // FIX: Added 'e.status' so the frontend knows if the event is Finished!
                string sql = @"
                    SELECT t.id, t.tier_name, t.quantity, t.total_price, t.payment_method, t.purchase_date, t.is_used,
                           e.title, e.event_time, e.location, e.status, COALESCE(t.paymongo_payment_reference, ''), COALESCE(t.paymongo_checkout_reference, '')
                    FROM tickets t
                    JOIN events e ON t.event_id = e.id
                    WHERE t.customer_id = @cId
                    ORDER BY t.purchase_date DESC";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@cId", customerId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tickets.Add(new
                    {
                        ticketId = reader.GetGuid(0),
                        tierName = reader.GetString(1),
                        quantity = reader.GetInt32(2),
                        totalPrice = reader.GetDecimal(3),
                        paymentMethod = reader.GetString(4),
                        purchaseDate = reader.GetDateTime(5),
                        isUsed = reader.IsDBNull(6) ? false : reader.GetBoolean(6),
                        eventTitle = reader.GetString(7),
                        eventTime = reader.GetDateTime(8),
                        location = reader.GetString(9),
                        eventStatus = reader.IsDBNull(10) ? "Upcoming" : reader.GetString(10),
                        paymentReference = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        checkoutReference = reader.IsDBNull(12) ? "" : reader.GetString(12)
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
                return StatusCode(500, new { message = "Failed to confirm ticket payment: " + ex.Message });
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

                var ticketId = ExtractTicketId(req?.ticketValue);
                if (ticketId == null || ticketId == Guid.Empty)
                {
                    return BadRequest(new
                    {
                        message = "INVALID QR",
                        details = "The scanned QR code does not contain a valid ticket ID."
                    });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                string checkSql = @"
                    SELECT t.is_used, t.tier_name, t.quantity, e.title, e.event_time, e.status, e.id
                    FROM tickets t
                    JOIN events e ON t.event_id = e.id 
                    WHERE t.id = @id";
                    
                using var checkCmd = new NpgsqlCommand(checkSql, connection);
                checkCmd.Parameters.AddWithValue("@id", ticketId.Value);

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

                if (isUsed)
                {
                    return BadRequest(new { message = "ALREADY SCANNED!", details = $"{qty}x {tier} for {evTitle} was already used." });
                }

                string updateSql = "UPDATE tickets SET is_used = TRUE WHERE id = @id";
                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("@id", ticketId.Value);
                await updateCmd.ExecuteNonQueryAsync();

                return Ok(new { message = "SUCCESS! Ticket is Valid.", details = $"{qty}x {tier} for {evTitle}" });
            }
            catch (Exception ex) { return StatusCode(500, new { message = "Database Error: " + ex.Message }); }
        }

        private static Guid? ExtractTicketId(string? ticketValue)
        {
            if (string.IsNullOrWhiteSpace(ticketValue)) return null;

            var trimmed = ticketValue.Trim();
            if (Guid.TryParse(trimmed, out var directGuid))
            {
                return directGuid;
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                if (query.TryGetValue("ticketId", out var ticketIdValues) && Guid.TryParse(ticketIdValues.FirstOrDefault(), out var queryGuid))
                {
                    return queryGuid;
                }
            }

            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"[0-9a-fA-F]{8}\-(?:[0-9a-fA-F]{4}\-){3}[0-9a-fA-F]{12}");
            if (match.Success && Guid.TryParse(match.Value, out var embeddedGuid))
            {
                return embeddedGuid;
            }

            return null;
        }

        private static async Task EnsureTicketPaymentColumnsExist(NpgsqlConnection connection)
        {
            const string alterSql = @"
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_checkout_id text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_checkout_reference text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_payment_reference text NULL;";

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
