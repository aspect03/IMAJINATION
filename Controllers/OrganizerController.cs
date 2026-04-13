using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class UpdateOrganizerProfileDto
    {
        public string? firstName { get; set; }
        public string? lastName { get; set; }
        public string? productionName { get; set; }
        public string? contactNumber { get; set; }
        public string? address { get; set; }
        public string? bio { get; set; }
        public string? profilePicture { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class OrganizerController : ControllerBase
    {
        private readonly string _connectionString;

        public OrganizerController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetOrganizerById(Guid id)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                const string sql = @"
                    SELECT firstname, lastname, productionname, profile_picture, email, username, contactnumber, address, bio, COALESCE(is_verified, FALSE)
                    FROM users
                    WHERE id = @id AND role = 'Organizer'";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "Organizer not found." });
                }

                string first = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string last = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string productionName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                string profilePicture = reader.IsDBNull(3) ? "" : reader.GetString(3);
                string email = reader.IsDBNull(4) ? "" : reader.GetString(4);
                string username = reader.IsDBNull(5) ? "" : reader.GetString(5);
                string contactNumber = reader.IsDBNull(6) ? "" : reader.GetString(6);
                string address = reader.IsDBNull(7) ? "" : reader.GetString(7);
                string bio = reader.IsDBNull(8) ? "" : reader.GetString(8);
                bool isVerified = !reader.IsDBNull(9) && reader.GetBoolean(9);
                var profileSummary = CommunitySupport.CalculateProfileCompletion("Organizer", first, last, bio, profilePicture, "", "", productionName, contactNumber, address, "");
                await reader.CloseAsync();
                await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Organizer", first, last, bio, profilePicture, "", "", productionName, contactNumber, address, "");

                decimal averageRating = 0;
                int reviewCount = 0;
                const string ratingSql = @"
                    SELECT COALESCE(AVG(er.rating), 0), COUNT(er.id)
                    FROM event_reviews er
                    INNER JOIN events e ON e.id = er.event_id
                    WHERE e.organizer_id = @id;";

                using (var ratingCmd = new NpgsqlCommand(ratingSql, connection))
                {
                    ratingCmd.Parameters.AddWithValue("@id", id);
                    using var ratingReader = await ratingCmd.ExecuteReaderAsync();
                    if (await ratingReader.ReadAsync())
                    {
                        averageRating = ratingReader.IsDBNull(0) ? 0 : ratingReader.GetDecimal(0);
                        reviewCount = ratingReader.IsDBNull(1) ? 0 : Convert.ToInt32(ratingReader.GetInt64(1));
                    }
                }

                var recentGigs = new List<object>();
                const string gigsSql = @"
                    SELECT id,
                           COALESCE(title, 'Untitled Event'),
                           event_time,
                           COALESCE(city, ''),
                           COALESCE(location, ''),
                           COALESCE(status, 'Upcoming'),
                           COALESCE(poster_url, '')
                    FROM events
                    WHERE organizer_id = @id
                    ORDER BY event_time DESC
                    LIMIT 6;";

                using (var gigsCmd = new NpgsqlCommand(gigsSql, connection))
                {
                    gigsCmd.Parameters.AddWithValue("@id", id);
                    using var gigsReader = await gigsCmd.ExecuteReaderAsync();
                    while (await gigsReader.ReadAsync())
                    {
                        recentGigs.Add(new
                        {
                            id = gigsReader.GetGuid(0),
                            title = gigsReader.IsDBNull(1) ? "Untitled Event" : gigsReader.GetString(1),
                            time = gigsReader.IsDBNull(2) ? DateTime.UtcNow : gigsReader.GetDateTime(2),
                            city = gigsReader.IsDBNull(3) ? "" : gigsReader.GetString(3),
                            location = gigsReader.IsDBNull(4) ? "" : gigsReader.GetString(4),
                            status = gigsReader.IsDBNull(5) ? "Upcoming" : gigsReader.GetString(5),
                            posterUrl = gigsReader.IsDBNull(6) ? "" : gigsReader.GetString(6)
                        });
                    }
                }

                return Ok(new
                {
                    displayName = !string.IsNullOrWhiteSpace(productionName)
                        ? productionName
                        : $"{first} {last}".Trim(),
                    firstName = first,
                    lastName = last,
                    productionName,
                    profilePicture,
                    email,
                    username,
                    contactNumber,
                    address,
                    bio,
                    isVerified = profileSummary.IsVerified || isVerified,
                    profileCompletionPercent = profileSummary.Percent,
                    profileCompletionLabel = profileSummary.Label,
                    averageRating = Math.Round(averageRating, 1),
                    reviewCount,
                    recentGigs
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("{id}/profile")]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> UpdateOrganizerProfile(Guid id, [FromBody] UpdateOrganizerProfileDto req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var sanitizedFirstName = SecuritySupport.SanitizePlainText(req.firstName, 120, false);
                var sanitizedLastName = SecuritySupport.SanitizePlainText(req.lastName, 120, false);
                var sanitizedProductionName = SecuritySupport.SanitizePlainText(req.productionName, 160, false);
                var sanitizedContactNumber = SecuritySupport.SanitizePlainText(req.contactNumber, 60, false);
                var sanitizedAddress = SecuritySupport.SanitizePlainText(req.address, 240, true);
                var sanitizedBio = SecuritySupport.SanitizePlainText(req.bio, 2500, true);
                var normalizedPicture = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.profilePicture, 2_500_000, out var imageError);
                if (imageError is not null)
                {
                    return BadRequest(new { message = imageError });
                }

                const string sql = @"
                    UPDATE users
                    SET firstname = COALESCE(@firstName, firstname),
                        lastname = COALESCE(@lastName, lastname),
                        productionname = COALESCE(@productionName, productionname),
                        contactnumber = COALESCE(@contactNumber, contactnumber),
                        address = COALESCE(@address, address),
                        bio = COALESCE(@bio, bio),
                        profile_picture = COALESCE(@profilePicture, profile_picture)
                    WHERE id = @id AND role = 'Organizer'";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                cmd.Parameters.Add("@firstName", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedFirstName) ? DBNull.Value : sanitizedFirstName;
                cmd.Parameters.Add("@lastName", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedLastName) ? DBNull.Value : sanitizedLastName;
                cmd.Parameters.Add("@productionName", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedProductionName) ? DBNull.Value : sanitizedProductionName;
                cmd.Parameters.Add("@contactNumber", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedContactNumber) ? DBNull.Value : sanitizedContactNumber;
                cmd.Parameters.Add("@address", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedAddress) ? DBNull.Value : sanitizedAddress;
                cmd.Parameters.Add("@bio", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedBio) ? DBNull.Value : sanitizedBio;
                cmd.Parameters.Add("@profilePicture", NpgsqlDbType.Text).Value = (object?)normalizedPicture ?? DBNull.Value;

                var updated = await cmd.ExecuteNonQueryAsync();
                if (updated == 0)
                {
                    return BadRequest(new { message = "Update failed. Make sure you are an Organizer." });
                }
                await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Organizer", sanitizedFirstName, sanitizedLastName, sanitizedBio, normalizedPicture, "", "", sanitizedProductionName, sanitizedContactNumber, sanitizedAddress, "");
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    id,
                    "Organizer",
                    "profile_updated",
                    "user",
                    id,
                    HttpContext,
                    "Organizer profile updated.");

                return Ok(new { message = "Organizer profile updated successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}
