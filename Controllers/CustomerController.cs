using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    // Reusing the same structure as the Artist profile update, but we'll adapt it for customers
    public class UpdateCustomerProfileDto
    {
        public string? bio { get; set; }
        public string? profilePicture { get; set; } // Base64 string
        // We won't strictly enforce genres or spotifyLink for customers right now, 
        // but they are in the database if we want to use them later (e.g., "Favorite Genres").
    }

    [Route("api/[controller]")]
    [ApiController]
    public class CustomerController : ControllerBase
    {
        private readonly string _connectionString;

        public CustomerController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
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
                string sql = "SELECT firstname, lastname, profile_picture, bio, COALESCE(is_verified, FALSE) FROM users WHERE id = @id AND role = 'Customer'";
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
                    var profileSummary = CommunitySupport.CalculateProfileCompletion("Customer", first, last, bio, profilePicture, "", "", "", "", "", "");
                    await reader.CloseAsync();
                    await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Customer", first, last, bio, profilePicture, "", "", "", "", "", "");

                    return Ok(new
                    {
                        displayName = $"{first} {last}".Trim(),
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

                var sanitizedBio = SecuritySupport.SanitizePlainText(req.bio, 2500, true);
                var normalizedPicture = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.profilePicture, 2_500_000, out var imageError);
                if (imageError is not null)
                {
                    return BadRequest(new { message = imageError });
                }

                string sql = @"
                    UPDATE users SET 
                        bio = @bio, 
                        profile_picture = COALESCE(@pic, profile_picture) 
                    WHERE id = @id AND role = 'Customer'";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                cmd.Parameters.Add("@bio", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedBio) ? DBNull.Value : sanitizedBio;
                cmd.Parameters.Add("@pic", NpgsqlDbType.Text).Value = (object?)normalizedPicture ?? DBNull.Value;

                int rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return BadRequest(new { message = "Update failed. Make sure you are a Customer." });
                const string profileSql = @"
                    SELECT COALESCE(firstname, ''),
                           COALESCE(lastname, ''),
                           COALESCE(profile_picture, ''),
                           COALESCE(bio, '')
                    FROM users
                    WHERE id = @id AND role = 'Customer';";

                string first = "";
                string last = "";
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
                        profilePicture = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        bio = reader.IsDBNull(3) ? "" : reader.GetString(3);
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
                    profilePicture,
                    isVerified = profileSummary.IsVerified,
                    profileCompletionPercent = profileSummary.Percent,
                    profileCompletionLabel = profileSummary.Label
                });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }
    }
}
