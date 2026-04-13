using Microsoft.AspNetCore.Mvc;
using Npgsql;
using BCrypt.Net;
using ImajinationAPI.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Mail;
using System.Net;
using System.Text.Json;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _config;

        public AuthController(IConfiguration configuration, IMemoryCache cache)
        {
            _config = configuration;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _cache = cache;
        }

        // ==========================================
        // 1. SEND OTP EMAIL
        // ==========================================
        [HttpPost("send-otp")]
        public async Task<IActionResult> SendOtp([FromBody] SendOtpDto req)
        {
            var normalizedEmail = NormalizeEmail(req.email);
            var attemptId = Guid.NewGuid();
            try
            {
                if (string.IsNullOrWhiteSpace(normalizedEmail))
                {
                    return BadRequest(new { message = "Email is required." });
                }

                await using var securityConnection = new NpgsqlConnection(_connectionString);
                await securityConnection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(securityConnection);
                var otpAllowance = await SecuritySupport.CheckOtpSendAllowanceAsync(securityConnection, normalizedEmail);
                if (!otpAllowance.Allowed)
                {
                    await SecuritySupport.LogSecurityEventAsync(
                        securityConnection,
                        null,
                        null,
                        "otp_request_throttled",
                        "otp",
                        null,
                        HttpContext,
                        $"OTP request blocked for {normalizedEmail}. Retry after {otpAllowance.RetryAfterSeconds}s.");

                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        message = otpAllowance.RemainingInWindow <= 0
                            ? "Too many OTP requests. Please wait before requesting another code."
                            : "Please wait a bit before requesting another OTP.",
                        retryAfterSeconds = otpAllowance.RetryAfterSeconds
                    });
                }

                Random random = new Random();
                string otpCode = random.Next(100000, 999999).ToString();

                _cache.Set(normalizedEmail, otpCode, TimeSpan.FromMinutes(5));

                var senderEmail = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:SenderEmail",
                    "EmailSettings__SenderEmail",
                    "Brevo sender email");
                var senderName = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:SenderName",
                    "EmailSettings__SenderName",
                    "Brevo sender name");
                var smtpServer = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:SmtpServer",
                    "EmailSettings__SmtpServer",
                    "Brevo SMTP server");
                var smtpPortRaw = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:Port",
                    "EmailSettings__Port",
                    "Brevo SMTP port");
                var smtpUsername = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:Username",
                    "EmailSettings__Username",
                    "Brevo SMTP username");
                var smtpPassword = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:Password",
                    "EmailSettings__Password",
                    "Brevo SMTP password");

                if (!int.TryParse(smtpPortRaw, out var smtpPort))
                {
                    throw new InvalidOperationException("Brevo SMTP port is invalid. Set EmailSettings__Port to a valid integer.");
                }

                using var mail = new MailMessage();
                mail.From = new MailAddress(senderEmail, senderName);
                mail.To.Add(normalizedEmail);
                mail.Subject = "Your Imajination Verification Code";

                string emailDesign = $@"
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset='utf-8'>
                    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                    <link href='https://fonts.googleapis.com/css2?family=Montserrat:wght@400;600;800&display=swap' rel='stylesheet'>
                </head>
                <body style='margin: 0; padding: 0; background-color: #0a0a0a; font-family: ""Montserrat"", Arial, sans-serif; color: #ffffff;'>
                    <table width='100%' cellpadding='0' cellspacing='0' style='background-color: #0a0a0a; padding: 40px 20px;'>
                        <tr>
                            <td align='center'>
                                <table width='100%' max-width='600' cellpadding='0' cellspacing='0' style='max-width: 600px; background-color: #171717; border: 1px solid #262626; border-radius: 24px; overflow: hidden; box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5);'>
                                    <tr>
                                        <td align='center' style='padding: 30px; border-bottom: 1px solid #262626; background-color: #1f1f1f;'>
                                            <h1 style='margin: 0; font-size: 20px; font-weight: 800; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                                                <span style='color: #dc2626;'>&bull;</span> IMAJINATION
                                            </h1>
                                        </td>
                                    </tr>
                                    <tr>
                                        <td align='center' style='padding: 40px 30px;'>
                                            <h2 style='margin: 0 0 15px 0; font-size: 24px; font-weight: 600; color: #ffffff;'>Verify your email</h2>
                                            <p style='margin: 0 0 30px 0; font-size: 14px; line-height: 24px; color: #a3a3a3;'>
                                                You are almost ready to start exploring indie gigs and booking tickets. Enter the code below to securely verify your account.
                                            </p>
                                            <div style='background-color: #2a0a0a; border: 1px solid #450a0a; border-radius: 12px; padding: 20px; margin-bottom: 30px;'>
                                                <div style='font-family: monospace; font-size: 36px; font-weight: 800; letter-spacing: 12px; color: #ef4444; margin-left: 12px;'>
                                                    {otpCode}
                                                </div>
                                            </div>
                                            <p style='margin: 0; font-size: 12px; font-weight: 600; color: #ef4444; text-transform: uppercase; letter-spacing: 1px;'>
                                                Expires in 5 minutes
                                            </p>
                                        </td>
                                    </tr>
                                    <tr>
                                        <td align='center' style='padding: 30px; background-color: #121212; border-top: 1px solid #262626;'>
                                            <p style='margin: 0; font-size: 12px; color: #525252;'>
                                                If you did not attempt to register for an Imajination account, you can safely ignore this email.
                                            </p>
                                            <p style='margin: 10px 0 0 0; font-size: 12px; color: #525252;'>
                                                &copy; {DateTime.Now.Year} Imajination. All rights reserved.
                                            </p>
                                        </td>
                                    </tr>
                                </table>
                            </td>
                        </tr>
                    </table>
                </body>
                </html>";

                mail.Body = emailDesign;
                mail.IsBodyHtml = true;

                using (var smtp = new SmtpClient(smtpServer, smtpPort))
                {
                    smtp.Credentials = new NetworkCredential(smtpUsername, smtpPassword);
                    smtp.EnableSsl = true;
                    await smtp.SendMailAsync(mail);
                }

                await SecuritySupport.MarkOtpSentAsync(securityConnection, normalizedEmail);
                await TryLogOtpEmailAttemptAsync(attemptId, normalizedEmail, senderEmail, "AcceptedBySmtp", "OTP email accepted by SMTP provider.");
                await SecuritySupport.LogSecurityEventAsync(
                    securityConnection,
                    null,
                    null,
                    "otp_sent",
                    "otp",
                    null,
                    HttpContext,
                    $"OTP sent to {normalizedEmail}.");

                return Ok(new { message = "OTP sent successfully.", attemptId });
            }
            catch (SmtpException ex)
            {
                await TryLogOtpEmailAttemptAsync(
                    attemptId,
                    normalizedEmail,
                    ConfigurationFallbacks.GetSetting(_config, "EmailSettings:SenderEmail", "EmailSettings__SenderEmail") ?? string.Empty,
                    "SmtpRejected",
                    ex.Message);
                var providerHint = ex.Message.IndexOf("sender", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "The mail provider rejected the sender identity. Verify the Brevo sender email or authenticated domain."
                    : "The mail provider rejected the OTP email.";
                return StatusCode(500, new { message = $"{providerHint} Details: {ex.Message}", attemptId });
            }
            catch (Exception ex)
            {
                await TryLogOtpEmailAttemptAsync(
                    attemptId,
                    normalizedEmail,
                    ConfigurationFallbacks.GetSetting(_config, "EmailSettings:SenderEmail", "EmailSettings__SenderEmail") ?? string.Empty,
                    "ServerError",
                    ex.Message);
                return StatusCode(500, new { message = "Failed to send email: " + ex.Message, attemptId });
            }
        }

        // ==========================================
        // 2. REGISTER ACCOUNT
        // ==========================================
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDto req)
        {
            var normalizedEmail = NormalizeEmail(req.email);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return BadRequest(new { message = "Email is required." });
            }

            if (!IsStrongPassword(req.password))
            {
                return BadRequest(new
                {
                    message = "Password must be at least 8 characters and include uppercase, lowercase, number, and special character."
                });
            }

            if (!_cache.TryGetValue(normalizedEmail, out string savedOtp))
            {
                return BadRequest(new { message = "OTP expired or not requested. Please request a new code." });
            }

            if (savedOtp != req.otp)
            {
                return BadRequest(new { message = "Invalid OTP code." });
            }

            try
            {
                string passwordHash = BCrypt.Net.BCrypt.HashPassword(req.password);
                
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTalentRegistrationColumnsAsync(connection);
                await EnsureUserModerationColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var normalizedRole = SecuritySupport.SanitizePlainText(req.role, 40, false);
                var requiresAdminApproval = string.Equals(normalizedRole, "Organizer", StringComparison.OrdinalIgnoreCase);
                var accountStatus = requiresAdminApproval ? "PendingApproval" : "Active";

                string sql = @"
                    INSERT INTO users 
                    (role, firstname, middlename, lastname, suffix, username, email, contactnumber, address, birthday, age, passwordhash, stagename, productionname, talent_category, member_names, is_banned, account_status) 
                    VALUES 
                    (@r, @fn, @mn, @ln, @sx, @un, @em, @cn, @ad, @bd, @ag, @ph, @sn, @pn, @tc, @members, FALSE, @accountStatus)";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@r", normalizedRole ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@fn", SecuritySupport.SanitizePlainText(req.firstName, 120, false) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@mn", (object?)SecuritySupport.SanitizePlainText(req.middleName, 120, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ln", SecuritySupport.SanitizePlainText(req.lastName, 120, false) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@sx", (object?)SecuritySupport.SanitizePlainText(req.suffix, 40, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@un", SecuritySupport.SanitizePlainText(req.username, 60, false) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@em", normalizedEmail);
                cmd.Parameters.AddWithValue("@cn", SecuritySupport.SanitizePlainText(req.contactNumber, 60, false) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ad", SecuritySupport.SanitizePlainText(req.address, 240, true) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@bd", req.birthday != default ? req.birthday : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ag", req.age);
                cmd.Parameters.AddWithValue("@ph", passwordHash);
                cmd.Parameters.AddWithValue("@sn", (object?)SecuritySupport.SanitizePlainText(req.stageName, 120, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@pn", (object?)SecuritySupport.SanitizePlainText(req.productionName, 160, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tc", (object?)SecuritySupport.SanitizePlainText(req.talentCategory, 60, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@members", (object?)SecuritySupport.SanitizePlainText(req.memberNames, 600, true) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@accountStatus", accountStatus);

                await cmd.ExecuteNonQueryAsync();
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    null,
                    normalizedRole,
                    "account_registered",
                    "user",
                    null,
                    HttpContext,
                    $"{normalizedRole} registration submitted for {normalizedEmail} with status {accountStatus}.");

                _cache.Remove(normalizedEmail);

                return Ok(new
                {
                    message = requiresAdminApproval
                        ? "Organizer registration submitted. Your account is pending admin approval before you can log in."
                        : "Registration successful.",
                    accountStatus
                });
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new { message = "Email or Username already exists." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Backend Error: " + ex.Message });
            }
        }

        // ==========================================
        // 3. LOGIN ACCOUNT (CRITICAL FIX ADDED HERE)
        // ==========================================
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto req)
        {
            try 
            {
                var normalizedEmail = NormalizeEmail(req.email);
                if (string.IsNullOrWhiteSpace(normalizedEmail))
                {
                    return BadRequest(new { message = "Email is required." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureUserModerationColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var lockoutState = await SecuritySupport.GetLockoutStateAsync(connection, normalizedEmail);
                if (lockoutState.IsLocked)
                {
                    await SecuritySupport.LogSecurityEventAsync(
                        connection,
                        null,
                        null,
                        "login_blocked_lockout",
                        "user",
                        null,
                        HttpContext,
                        $"Locked-out login attempt for {normalizedEmail}.");

                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        message = "Too many failed login attempts. Please wait before trying again.",
                        retryAfterSeconds = lockoutState.RetryAfterSeconds
                    });
                }

                // Match email case-insensitively so normal login and Google login land on the same account.
                using var cmd = new NpgsqlCommand("SELECT id, passwordhash, role, firstname, username, profile_picture, COALESCE(is_banned, FALSE), COALESCE(account_status, 'Active') FROM users WHERE LOWER(TRIM(email)) = @email", connection);
                cmd.Parameters.AddWithValue("@email", normalizedEmail);

                Guid authenticatedUserId = Guid.Empty;
                string authenticatedRole = string.Empty;
                string authenticatedFirstName = string.Empty;
                string authenticatedUsername = string.Empty;
                string authenticatedProfilePicture = string.Empty;
                string? loginBlockMessage = null;
                int? loginBlockStatusCode = null;
                var authenticated = false;

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        Guid userId = reader.GetGuid(0);
                        string storedHash = reader.GetString(1);
                        bool isBanned = !reader.IsDBNull(6) && reader.GetBoolean(6);
                        string accountStatus = reader.IsDBNull(7) ? "Active" : reader.GetString(7);

                        if (isBanned || string.Equals(accountStatus, "Banned", StringComparison.OrdinalIgnoreCase))
                        {
                            loginBlockStatusCode = StatusCodes.Status403Forbidden;
                            loginBlockMessage = "This account has been blocked by admin.";
                        }
                        else if (string.Equals(accountStatus, "PendingApproval", StringComparison.OrdinalIgnoreCase))
                        {
                            loginBlockStatusCode = StatusCodes.Status403Forbidden;
                            loginBlockMessage = "This organizer account is still waiting for admin approval.";
                        }
                        else if (string.Equals(accountStatus, "Denied", StringComparison.OrdinalIgnoreCase))
                        {
                            loginBlockStatusCode = StatusCodes.Status403Forbidden;
                            loginBlockMessage = "This organizer account was denied by admin. Please contact support before trying again.";
                        }
                        else if (BCrypt.Net.BCrypt.Verify(req.password, storedHash))
                        {
                            authenticated = true;
                            authenticatedUserId = userId;
                            authenticatedRole = reader.GetString(2);
                            authenticatedFirstName = reader.GetString(3);
                            authenticatedUsername = reader.GetString(4);
                            authenticatedProfilePicture = reader.IsDBNull(5) ? "" : reader.GetString(5);
                        }
                    }
                }

                if (loginBlockStatusCode.HasValue)
                {
                    return StatusCode(loginBlockStatusCode.Value, new { message = loginBlockMessage });
                }

                if (authenticated)
                {
                    await SecuritySupport.ClearFailedLoginAsync(connection, normalizedEmail);
                    var trackedSession = await SecuritySupport.CreateTrackedSessionAsync(
                        connection,
                        authenticatedUserId,
                        authenticatedRole,
                        HttpContext);
                    await SecuritySupport.LogSecurityEventAsync(
                        connection,
                        authenticatedUserId,
                        authenticatedRole,
                        "login_success",
                        "user",
                        authenticatedUserId,
                        HttpContext,
                        $"Successful login for {normalizedEmail}.");

                    return Ok(new
                    {
                        id = authenticatedUserId,
                        role = authenticatedRole,
                        firstName = authenticatedFirstName,
                        username = authenticatedUsername,
                        profilePicture = authenticatedProfilePicture,
                        sessionToken = trackedSession.SessionToken,
                        signedOutOtherDevices = trackedSession.RevokedCount > 0
                    });
                }

                var failedAttempts = await SecuritySupport.RecordFailedLoginAsync(connection, normalizedEmail);
                var updatedLockoutState = await SecuritySupport.GetLockoutStateAsync(connection, normalizedEmail);
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    null,
                    null,
                    updatedLockoutState.IsLocked ? "login_lockout_triggered" : "login_failed",
                    "user",
                    null,
                    HttpContext,
                    $"Failed login for {normalizedEmail}. Failed attempts: {failedAttempts}.");

                if (updatedLockoutState.IsLocked)
                {
                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        message = "Too many failed login attempts. Please wait before trying again.",
                        retryAfterSeconds = updatedLockoutState.RetryAfterSeconds
                    });
                }

                return Unauthorized(new { message = "Invalid email or password." });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Unable to sign in right now. Please try again in a moment." });
            }
        }

        [HttpGet("google-client-id")]
        public IActionResult GetGoogleClientId()
        {
            try
            {
                var config = GetGoogleClientConfiguration();
                if (string.IsNullOrWhiteSpace(config.ClientId))
                {
                    return NotFound(new { message = config.Message });
                }

                return Ok(new { clientId = config.ClientId });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load Google client ID: " + ex.Message });
            }
        }

        [HttpPost("google-login")]
        public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginDto req)
        {
            if (string.IsNullOrWhiteSpace(req?.credential))
            {
                return BadRequest(new { message = "Google credential is required." });
            }

            try
            {
                var config = GetGoogleClientConfiguration();
                var clientId = config.ClientId;
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    return StatusCode(500, new { message = config.Message });
                }

                using var httpClient = new HttpClient();
                var verifyUrl = $"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(req.credential)}";
                using var verifyResponse = await httpClient.GetAsync(verifyUrl);

                if (!verifyResponse.IsSuccessStatusCode)
                {
                    return Unauthorized(new { message = "Google Sign-In verification failed." });
                }

                var payloadJson = await verifyResponse.Content.ReadAsStringAsync();
                using var payloadDoc = JsonDocument.Parse(payloadJson);
                var payload = payloadDoc.RootElement;

                var audience = payload.TryGetProperty("aud", out var audElement) ? audElement.GetString() : string.Empty;
                if (!string.Equals(audience, clientId, StringComparison.OrdinalIgnoreCase))
                {
                    return Unauthorized(new { message = "Google Sign-In client mismatch." });
                }

                var email = payload.TryGetProperty("email", out var emailElement) ? emailElement.GetString() : string.Empty;
                var emailVerified = payload.TryGetProperty("email_verified", out var verifiedElement) ? verifiedElement.GetString() : string.Empty;
                var givenName = payload.TryGetProperty("given_name", out var givenNameElement) ? givenNameElement.GetString() : string.Empty;
                var familyName = payload.TryGetProperty("family_name", out var familyNameElement) ? familyNameElement.GetString() : string.Empty;
                var picture = payload.TryGetProperty("picture", out var pictureElement) ? pictureElement.GetString() : string.Empty;
                var fullName = payload.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : string.Empty;

                var normalizedEmail = NormalizeEmail(email);
                if (string.IsNullOrWhiteSpace(normalizedEmail) || !string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return Unauthorized(new { message = "Your Google account email is not verified." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureUserModerationColumnsAsync(connection);

                var existingUser = await FindUserByEmailAsync(connection, normalizedEmail);
                if (existingUser is null)
                {
                    var firstName = !string.IsNullOrWhiteSpace(givenName)
                        ? givenName
                        : (fullName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Google");
                    var lastName = !string.IsNullOrWhiteSpace(familyName)
                        ? familyName
                        : (fullName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? string.Empty);
                    var suggestedUsername = await GenerateUniqueUsernameAsync(connection, fullName, normalizedEmail);

                    return Conflict(new
                    {
                        message = "No existing Imajination account was found for this Google email. Please continue to signup first.",
                        signUpRequired = true,
                        email = normalizedEmail,
                        firstName,
                        lastName,
                        fullName = fullName ?? string.Empty,
                        suggestedUsername,
                        profilePicture = picture ?? string.Empty
                    });
                }

                if (existingUser.IsBanned || string.Equals(existingUser.AccountStatus, "Banned", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new { message = "This account has been blocked by admin." });
                }

                if (string.Equals(existingUser.AccountStatus, "PendingApproval", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new { message = "This organizer account is still waiting for admin approval." });
                }

                if (string.Equals(existingUser.AccountStatus, "Denied", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new { message = "This organizer account was denied by admin. Please contact support before trying again." });
                }

                if (string.IsNullOrWhiteSpace(existingUser.ProfilePicture) && !string.IsNullOrWhiteSpace(picture))
                {
                    const string updatePictureSql = "UPDATE users SET profile_picture = @profilePicture WHERE id = @id";
                    await using var updateCmd = new NpgsqlCommand(updatePictureSql, connection);
                    updateCmd.Parameters.AddWithValue("@profilePicture", picture);
                    updateCmd.Parameters.AddWithValue("@id", existingUser.Id);
                    await updateCmd.ExecuteNonQueryAsync();
                    existingUser = existingUser with { ProfilePicture = picture };
                }

                var trackedSession = await SecuritySupport.CreateTrackedSessionAsync(
                    connection,
                    existingUser.Id,
                    existingUser.Role,
                    HttpContext);

                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    existingUser.Id,
                    existingUser.Role,
                    "google_login_success",
                    "user",
                    existingUser.Id,
                    HttpContext,
                    $"Successful Google Sign-In for {normalizedEmail}.");

                return Ok(new
                {
                    id = existingUser.Id,
                    role = existingUser.Role,
                    firstName = existingUser.FirstName,
                    username = existingUser.Username,
                    profilePicture = existingUser.ProfilePicture ?? string.Empty,
                    sessionToken = trackedSession.SessionToken,
                    signedOutOtherDevices = trackedSession.RevokedCount > 0
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Google Sign-In error: " + ex.Message });
            }
        }

        [HttpGet("session-status")]
        public async Task<IActionResult> GetSessionStatus([FromQuery] Guid userId)
        {
            var sessionToken = Request.Headers["X-Session-Token"].ToString();
            if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionToken))
            {
                return Unauthorized(new { message = "Session validation requires a valid user and session token." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var sessionState = await SecuritySupport.ValidateTrackedSessionAsync(connection, userId, sessionToken);
                if (!sessionState.IsValid)
                {
                    return Unauthorized(new
                    {
                        message = sessionState.Message ?? "This session is no longer active.",
                        replacedElsewhere = sessionState.ReplacedElsewhere
                    });
                }

                await SecuritySupport.TouchTrackedSessionAsync(connection, userId, sessionToken);
                return Ok(new { active = true });
            }
            catch
            {
                return StatusCode(500, new { message = "Unable to verify session right now." });
            }
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] SessionLogoutDto req)
        {
            if (req.userId == Guid.Empty || string.IsNullOrWhiteSpace(req.sessionToken))
            {
                return BadRequest(new { message = "Session details are required to log out." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);
                await SecuritySupport.RevokeTrackedSessionAsync(connection, req.userId, req.sessionToken, "Signed out by the user");
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    req.userId,
                    Request.Headers["X-Actor-Role"].ToString(),
                    "logout",
                    "user",
                    req.userId,
                    HttpContext,
                    "User signed out of the current device.");
                return Ok(new { message = "Signed out successfully." });
            }
            catch
            {
                return StatusCode(500, new { message = "Unable to sign out right now." });
            }
        }

        // ==========================================
        // 4. RESET PASSWORD
        // ==========================================
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto req)
        {
            var normalizedEmail = NormalizeEmail(req.email);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return BadRequest(new { message = "Email is required." });
            }

            if (!_cache.TryGetValue(normalizedEmail, out string savedOtp))
            {
                return BadRequest(new { message = "OTP expired or not requested." });
            }

            if (savedOtp != req.otp)
            {
                return BadRequest(new { message = "Invalid OTP code." });
            }

            try
            {
                string newPasswordHash = BCrypt.Net.BCrypt.HashPassword(req.newPassword);

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                string sql = "UPDATE users SET passwordhash = @ph WHERE LOWER(TRIM(email)) = @em";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@ph", newPasswordHash);
                cmd.Parameters.AddWithValue("@em", normalizedEmail);

                int rowsAffected = await cmd.ExecuteNonQueryAsync();

                if (rowsAffected == 0)
                {
                    return BadRequest(new { message = "User not found." });
                }

                _cache.Remove(normalizedEmail);
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    null,
                    null,
                    "password_reset",
                    "user",
                    null,
                    HttpContext,
                    $"Password reset completed for {normalizedEmail}.");

                return Ok(new { message = "Password updated successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Backend Error: " + ex.Message });
            }
        }

        private GoogleClientConfiguration GetGoogleClientConfiguration()
        {
            var configuredClientId = ConfigurationFallbacks.GetSetting(_config, "GoogleAuth:ClientId", "GoogleAuth__ClientId");
            if (!string.IsNullOrWhiteSpace(configuredClientId))
            {
                return new GoogleClientConfiguration(configuredClientId, string.Empty);
            }

            var credentialsPath = Path.Combine(Directory.GetCurrentDirectory(), "credentials.json");
            if (!System.IO.File.Exists(credentialsPath))
            {
                return new GoogleClientConfiguration(null, "Google Sign-In is not configured. Set GoogleAuth__ClientId or add a local credentials.json.");
            }

            var json = System.IO.File.ReadAllText(credentialsPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("web", out var web) &&
                web.TryGetProperty("client_id", out var webClientId))
            {
                return new GoogleClientConfiguration(webClientId.GetString(), string.Empty);
            }

            if (doc.RootElement.TryGetProperty("installed", out _))
            {
                return new GoogleClientConfiguration(
                    null,
                    "Google Sign-In needs a Web application OAuth client. Your credentials.json is using an Installed/Desktop client, which causes the 'no registered origin' error."
                );
            }

            return new GoogleClientConfiguration(null, "Google Sign-In credentials are invalid. Expected the 'web' client format from Google Cloud.");
        }

        private async Task<UserLoginResult?> FindUserByEmailAsync(NpgsqlConnection connection, string email)
        {
            const string query = "SELECT id, role, firstname, username, profile_picture, COALESCE(is_banned, FALSE), COALESCE(account_status, 'Active') FROM users WHERE LOWER(TRIM(email)) = @email LIMIT 1";
            await using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@email", email);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            return new UserLoginResult(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? "Customer" : reader.GetString(1),
                reader.IsDBNull(2) ? "Google" : reader.GetString(2),
                reader.IsDBNull(3) ? email.Split('@')[0] : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                !reader.IsDBNull(5) && reader.GetBoolean(5),
                reader.IsDBNull(6) ? "Active" : reader.GetString(6)
            );
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

        private static async Task EnsureTalentRegistrationColumnsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS talent_category VARCHAR(60);

                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS member_names TEXT;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task TryLogOtpEmailAttemptAsync(Guid attemptId, string recipientEmail, string senderEmail, string deliveryState, string providerMessage)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureOtpAuditTableAsync(connection);

                const string sql = @"
                    INSERT INTO otp_email_audit (id, recipient_email, sender_email, delivery_state, provider_message, created_at)
                    VALUES (@id, @recipientEmail, @senderEmail, @deliveryState, @providerMessage, NOW());";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", attemptId);
                cmd.Parameters.AddWithValue("@recipientEmail", recipientEmail ?? string.Empty);
                cmd.Parameters.AddWithValue("@senderEmail", senderEmail ?? string.Empty);
                cmd.Parameters.AddWithValue("@deliveryState", deliveryState ?? string.Empty);
                cmd.Parameters.AddWithValue("@providerMessage", providerMessage ?? string.Empty);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Sending OTP should not fail because audit logging is unavailable.
            }
        }

        private static async Task EnsureOtpAuditTableAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS otp_email_audit (
                    id uuid PRIMARY KEY,
                    recipient_email text NOT NULL,
                    sender_email text NOT NULL,
                    delivery_state varchar(40) NOT NULL,
                    provider_message text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<string> GenerateUniqueUsernameAsync(NpgsqlConnection connection, string? fullName, string email)
        {
            var baseName = string.IsNullOrWhiteSpace(fullName) ? email.Split('@')[0] : fullName;
            var normalized = new string(baseName
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());

            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "user";
            }

            if (normalized.Length > 20)
            {
                normalized = normalized[..20];
            }

            var candidate = normalized;
            var suffix = 1;

            while (await UsernameExistsAsync(connection, candidate))
            {
                candidate = $"{normalized}{suffix}";
                if (candidate.Length > 24)
                {
                    candidate = candidate[..24];
                }
                suffix++;
            }

            return candidate;
        }

        private async Task<bool> UsernameExistsAsync(NpgsqlConnection connection, string username)
        {
            const string query = "SELECT 1 FROM users WHERE username = @username LIMIT 1";
            await using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@username", username);
            var result = await cmd.ExecuteScalarAsync();
            return result is not null;
        }

        private static string NormalizeEmail(string? email)
        {
            return (email ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static bool IsStrongPassword(string? password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            {
                return false;
            }

            return password.Any(char.IsUpper)
                && password.Any(char.IsLower)
                && password.Any(char.IsDigit)
                && password.Any(ch => !char.IsLetterOrDigit(ch));
        }

        private sealed record UserLoginResult(Guid Id, string Role, string FirstName, string Username, string ProfilePicture, bool IsBanned, string AccountStatus);
        private sealed record GoogleClientConfiguration(string? ClientId, string Message);
        public sealed class SessionLogoutDto
        {
            public Guid userId { get; set; }
            public string sessionToken { get; set; } = string.Empty;
        }
    }
}
