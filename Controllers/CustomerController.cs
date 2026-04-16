using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Services;
using BCrypt.Net;

namespace ImajinationAPI.Controllers
{
    // Reusing the same structure as the Artist profile update, but we'll adapt it for customers
    public class UpdateCustomerProfileDto
    {
        public string? firstName { get; set; }
        public string? lastName { get; set; }
        public string? username { get; set; }
        public string? email { get; set; }
        public string? currentPassword { get; set; }
        public string? newPassword { get; set; }
        public string? bio { get; set; }
        public string? profilePicture { get; set; } // Base64 string
    }

    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = "Customer")]
    public class CustomerController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly UploadScanningService _uploadScanningService;

        public CustomerController(IConfiguration configuration, UploadScanningService uploadScanningService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _uploadScanningService = uploadScanningService;
        }

        [HttpGet("{id}/favorites")]
        public async Task<IActionResult> GetCustomerFavorites(Guid id)
        {
            try
            {
                var favorites = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await EnsureFavoriteTablesExist(connection);

                const string sql = @"
                    SELECT
                        f.target_user_id,
                        CASE
                            WHEN LOWER(TRIM(COALESCE(f.target_role, ''))) = 'sessionist'
                                OR LOWER(TRIM(COALESCE(u.role, ''))) = 'sessionist'
                                THEN 'Sessionist'
                            WHEN LOWER(TRIM(COALESCE(f.target_role, ''))) = 'artist'
                                OR LOWER(TRIM(COALESCE(u.role, ''))) = 'artist'
                                THEN 'Artist'
                            ELSE COALESCE(NULLIF(u.role, ''), COALESCE(f.target_role, ''))
                        END AS normalized_role,
                        COALESCE(u.stagename, ''),
                        COALESCE(u.firstname, ''),
                        COALESCE(u.lastname, ''),
                        COALESCE(u.profile_picture, ''),
                        COALESCE(u.genres, ''),
                        COALESCE(u.is_verified, FALSE),
                        f.created_at
                    FROM customer_favorites f
                    INNER JOIN users u ON u.id = f.target_user_id
                    WHERE f.customer_id = @customerId
                      AND LOWER(TRIM(COALESCE(u.role, COALESCE(f.target_role, '')))) IN ('artist', 'sessionist')
                    ORDER BY f.created_at DESC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = id;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var role = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var stageName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var first = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    var last = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    var displayName = !string.IsNullOrWhiteSpace(stageName) ? stageName : $"{first} {last}".Trim();

                    favorites.Add(new
                    {
                        targetUserId = reader.GetGuid(0),
                        targetRole = role,
                        displayName,
                        profilePicture = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        genres = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        isVerified = !reader.IsDBNull(7) && reader.GetBoolean(7),
                        createdAt = reader.IsDBNull(8) ? DateTime.UtcNow : reader.GetDateTime(8)
                    });
                }

                return Ok(favorites);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load favorites: " + ex.Message });
            }
        }

        private static async Task EnsureFavoriteTablesExist(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS customer_favorites (
                    id uuid PRIMARY KEY,
                    customer_id uuid NOT NULL,
                    target_user_id uuid NOT NULL,
                    target_role varchar(50) NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    CONSTRAINT uq_customer_favorite UNIQUE (customer_id, target_user_id, target_role)
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        // GET SINGLE CUSTOMER DETAILS
        [HttpGet("{id}")]
        public async Task<IActionResult> GetCustomerById(Guid id)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);

                // We don't care about stagename for customers, so we just construct their full name
                string sql = @"
                    SELECT
                        COALESCE(firstname, ''),
                        COALESCE(lastname, ''),
                        COALESCE(profile_picture, ''),
                        COALESCE(bio, ''),
                        COALESCE(is_verified, FALSE),
                        COALESCE(username, ''),
                        COALESCE(email, '')
                    FROM users
                    WHERE id = @id AND role = 'Customer'";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    string first = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    string last = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string profilePicture = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string bio = reader.IsDBNull(3) ? "This user hasn't written a bio yet." : reader.GetString(3);
                    bool isVerified = !reader.IsDBNull(4) && reader.GetBoolean(4);
                    string username = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    string email = reader.IsDBNull(6) ? "" : reader.GetString(6);
                    var profileSummary = CommunitySupport.CalculateProfileCompletion("Customer", first, last, bio, profilePicture, "", "", "", "", "", "");
                    await reader.CloseAsync();
                    await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Customer", first, last, bio, profilePicture, "", "", "", "", "", "");

                    return Ok(new
                    {
                        displayName = $"{first} {last}".Trim(),
                        firstName = first,
                        lastName = last,
                        username,
                        email,
                        profilePicture,
                        bio,
                        isVerified = profileSummary.IsVerified || isVerified,
                        profileCompletionPercent = profileSummary.Percent,
                        profileCompletionLabel = profileSummary.Label
                    });
                }
                return NotFound(new { message = "Customer not found." });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // UPDATE CUSTOMER PROFILE
        [HttpPut("{id}/profile")]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> UpdateProfile(Guid id, [FromBody] UpdateCustomerProfileDto req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var sanitizedFirstName = SecuritySupport.SanitizePlainText(req.firstName, 80, false);
                var sanitizedLastName = SecuritySupport.SanitizePlainText(req.lastName, 80, false);
                var sanitizedUsername = SecuritySupport.SanitizePlainText(req.username, 60, false);
                var normalizedEmail = NormalizeEmail(req.email);
                var sanitizedBio = SecuritySupport.SanitizePlainText(req.bio, 2500, true);
                var normalizedPicture = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.profilePicture, 2_500_000, out var imageError);
                if (imageError is not null)
                {
                    return BadRequest(new { message = imageError });
                }
                var pictureScan = await _uploadScanningService.ScanDataUrlAsync(normalizedPicture, "customer profile image");
                if (!pictureScan.IsClean)
                {
                    return BadRequest(new { message = pictureScan.Message });
                }

                if (string.IsNullOrWhiteSpace(sanitizedFirstName) || string.IsNullOrWhiteSpace(sanitizedLastName))
                {
                    return BadRequest(new { message = "First name and last name are required." });
                }

                if (string.IsNullOrWhiteSpace(sanitizedUsername))
                {
                    return BadRequest(new { message = "Username is required." });
                }

                if (string.IsNullOrWhiteSpace(normalizedEmail))
                {
                    return BadRequest(new { message = "A valid email address is required." });
                }

                var wantsPasswordChange = !string.IsNullOrWhiteSpace(req.newPassword) || !string.IsNullOrWhiteSpace(req.currentPassword);
                if (wantsPasswordChange)
                {
                    if (string.IsNullOrWhiteSpace(req.currentPassword) || string.IsNullOrWhiteSpace(req.newPassword))
                    {
                        return BadRequest(new { message = "Enter both current and new password to change your password." });
                    }

                    if (!IsStrongPassword(req.newPassword))
                    {
                        return BadRequest(new { message = "New password must be at least 8 characters and include uppercase, lowercase, number, and special character." });
                    }
                }

                const string currentSql = @"
                    SELECT COALESCE(passwordhash, ''),
                           COALESCE(username, ''),
                           COALESCE(email, '')
                    FROM users
                    WHERE id = @id AND role = 'Customer';";

                string currentPasswordHash = "";
                string currentUsername = "";
                string currentEmail = "";
                using (var currentCmd = new NpgsqlCommand(currentSql, connection))
                {
                    currentCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                    using var currentReader = await currentCmd.ExecuteReaderAsync();
                    if (!await currentReader.ReadAsync())
                    {
                        return NotFound(new { message = "Customer not found." });
                    }

                    currentPasswordHash = currentReader.IsDBNull(0) ? "" : currentReader.GetString(0);
                    currentUsername = currentReader.IsDBNull(1) ? "" : currentReader.GetString(1);
                    currentEmail = currentReader.IsDBNull(2) ? "" : currentReader.GetString(2);
                }

                if (wantsPasswordChange && !BCrypt.Net.BCrypt.Verify(req.currentPassword, currentPasswordHash))
                {
                    return BadRequest(new { message = "Current password is incorrect." });
                }

                if (!string.Equals(currentUsername, sanitizedUsername, StringComparison.Ordinal))
                {
                    const string usernameSql = "SELECT 1 FROM users WHERE username = @username AND id <> @id LIMIT 1;";
                    using var usernameCmd = new NpgsqlCommand(usernameSql, connection);
                    usernameCmd.Parameters.Add("@username", NpgsqlDbType.Text).Value = sanitizedUsername;
                    usernameCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                    var usernameExists = await usernameCmd.ExecuteScalarAsync();
                    if (usernameExists is not null)
                    {
                        return BadRequest(new { message = "Username is already taken." });
                    }
                }

                if (!string.Equals(currentEmail, normalizedEmail, StringComparison.OrdinalIgnoreCase))
                {
                    const string emailSql = "SELECT 1 FROM users WHERE LOWER(TRIM(email)) = @email AND id <> @id LIMIT 1;";
                    using var emailCmd = new NpgsqlCommand(emailSql, connection);
                    emailCmd.Parameters.Add("@email", NpgsqlDbType.Text).Value = normalizedEmail;
                    emailCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                    var emailExists = await emailCmd.ExecuteScalarAsync();
                    if (emailExists is not null)
                    {
                        return BadRequest(new { message = "Email address is already in use." });
                    }
                }

                var updatedPasswordHash = wantsPasswordChange ? BCrypt.Net.BCrypt.HashPassword(req.newPassword) : null;

                string sql = @"
                    UPDATE users SET
                        firstname = @firstName,
                        lastname = @lastName,
                        username = @username,
                        email = @email,
                        bio = @bio,
                        profile_picture = COALESCE(@pic, profile_picture),
                        passwordhash = COALESCE(@passwordHash, passwordhash)
                    WHERE id = @id AND role = 'Customer'";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                cmd.Parameters.Add("@firstName", NpgsqlDbType.Text).Value = sanitizedFirstName;
                cmd.Parameters.Add("@lastName", NpgsqlDbType.Text).Value = sanitizedLastName;
                cmd.Parameters.Add("@username", NpgsqlDbType.Text).Value = sanitizedUsername;
                cmd.Parameters.Add("@email", NpgsqlDbType.Text).Value = normalizedEmail;
                cmd.Parameters.Add("@bio", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedBio) ? DBNull.Value : sanitizedBio;
                cmd.Parameters.Add("@pic", NpgsqlDbType.Text).Value = (object?)normalizedPicture ?? DBNull.Value;
                cmd.Parameters.Add("@passwordHash", NpgsqlDbType.Text).Value = (object?)updatedPasswordHash ?? DBNull.Value;

                int rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return BadRequest(new { message = "Update failed. Make sure you are a Customer." });
                const string profileSql = @"
                    SELECT COALESCE(firstname, ''),
                           COALESCE(lastname, ''),
                           COALESCE(username, ''),
                           COALESCE(email, ''),
                           COALESCE(profile_picture, ''),
                           COALESCE(bio, '')
                    FROM users
                    WHERE id = @id AND role = 'Customer';";

                string first = "";
                string last = "";
                string username = "";
                string email = "";
                string profilePicture = "";
                string bio = "";
                using (var profileCmd = new NpgsqlCommand(profileSql, connection))
                {
                    profileCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                    using var reader = await profileCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        first = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        last = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        username = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        email = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        profilePicture = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        bio = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    }
                }

                var profileSummary = CommunitySupport.CalculateProfileCompletion("Customer", first, last, bio, profilePicture, "", "", "", "", "", "");
                await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Customer", first, last, bio, profilePicture, "", "", "", "", "", "");
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    id,
                    "Customer",
                    "profile_updated",
                    "user",
                    id,
                    HttpContext,
                    "Customer profile updated.");

                return Ok(new
                {
                    message = "Profile updated successfully!",
                    firstName = first,
                    lastName = last,
                    username,
                    email,
                    displayName = $"{first} {last}".Trim(),
                    bio,
                    profilePicture,
                    isVerified = profileSummary.IsVerified,
                    profileCompletionPercent = profileSummary.Percent,
                    profileCompletionLabel = profileSummary.Label
                });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
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

            var hasUpper = password.Any(char.IsUpper);
            var hasLower = password.Any(char.IsLower);
            var hasDigit = password.Any(char.IsDigit);
            var hasSpecial = password.Any(ch => !char.IsLetterOrDigit(ch));
            return hasUpper && hasLower && hasDigit && hasSpecial;
        }
    }
}
