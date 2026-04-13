using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class CreateCommunityPostDto
    {
        public Guid userId { get; set; }
        public string? role { get; set; }
        public string? content { get; set; }
        public string? imageUrl { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class CommunityController : ControllerBase
    {
        private readonly string _connectionString;

        public CommunityController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        [HttpGet("feed")]
        public async Task<IActionResult> GetFeed([FromQuery] string? role = null, [FromQuery] int limit = 24)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);

                var normalizedRole = (role ?? string.Empty).Trim();
                var hasRoleFilter =
                    normalizedRole.Equals("Artist", StringComparison.OrdinalIgnoreCase) ||
                    normalizedRole.Equals("Sessionist", StringComparison.OrdinalIgnoreCase) ||
                    normalizedRole.Equals("Organizer", StringComparison.OrdinalIgnoreCase);

                var safeLimit = Math.Clamp(limit, 1, 60);
                var posts = new List<object>();

                var sql = @"
                    SELECT cp.id,
                           cp.user_id,
                           cp.role,
                           cp.content,
                           cp.image_url,
                           cp.created_at,
                           COALESCE(u.firstname, ''),
                           COALESCE(u.lastname, ''),
                           COALESCE(u.stagename, ''),
                           COALESCE(u.productionname, ''),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.is_verified, FALSE)
                    FROM community_posts cp
                    JOIN users u ON u.id = cp.user_id
                    WHERE (@hasRoleFilter = FALSE OR LOWER(cp.role) = LOWER(@role))
                    ORDER BY cp.created_at DESC
                    LIMIT @limit;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@hasRoleFilter", NpgsqlDbType.Boolean).Value = hasRoleFilter;
                cmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = normalizedRole;
                cmd.Parameters.Add("@limit", NpgsqlDbType.Integer).Value = safeLimit;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var postRole = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    posts.Add(new
                    {
                        id = reader.GetGuid(0),
                        userId = reader.GetGuid(1),
                        role = postRole,
                        content = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        imageUrl = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        createdAt = reader.IsDBNull(5) ? DateTime.UtcNow : reader.GetDateTime(5),
                        authorName = CommunitySupport.BuildDisplayName(
                            reader.IsDBNull(6) ? "" : reader.GetString(6),
                            reader.IsDBNull(7) ? "" : reader.GetString(7),
                            reader.IsDBNull(8) ? "" : reader.GetString(8),
                            reader.IsDBNull(9) ? "" : reader.GetString(9),
                            postRole),
                        authorProfilePicture = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        authorVerified = !reader.IsDBNull(11) && reader.GetBoolean(11)
                    });
                }

                return Ok(posts);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet("posts/{role}/{userId}")]
        public async Task<IActionResult> GetPosts(string role, Guid userId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);

                const string sql = @"
                    SELECT cp.id,
                           cp.content,
                           cp.image_url,
                           cp.created_at,
                           COALESCE(u.firstname, ''),
                           COALESCE(u.lastname, ''),
                           COALESCE(u.stagename, ''),
                           COALESCE(u.productionname, ''),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.is_verified, FALSE)
                    FROM community_posts cp
                    JOIN users u ON u.id = cp.user_id
                    WHERE cp.user_id = @userId
                      AND LOWER(cp.role) = LOWER(@role)
                    ORDER BY cp.created_at DESC
                    LIMIT 20;";

                var posts = new List<object>();
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                cmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = role;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    posts.Add(new
                    {
                        id = reader.GetGuid(0),
                        content = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        imageUrl = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        createdAt = reader.IsDBNull(3) ? DateTime.UtcNow : reader.GetDateTime(3),
                        authorName = CommunitySupport.BuildDisplayName(
                            reader.IsDBNull(4) ? "" : reader.GetString(4),
                            reader.IsDBNull(5) ? "" : reader.GetString(5),
                            reader.IsDBNull(6) ? "" : reader.GetString(6),
                            reader.IsDBNull(7) ? "" : reader.GetString(7),
                            role),
                        authorProfilePicture = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        authorVerified = !reader.IsDBNull(9) && reader.GetBoolean(9)
                    });
                }

                return Ok(posts);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("posts")]
        [RequestSizeLimit(20_000_000)]
        public async Task<IActionResult> CreatePost([FromBody] CreateCommunityPostDto req)
        {
            try
            {
                var normalizedRole = (req.role ?? "").Trim();
                if (req.userId == Guid.Empty || string.IsNullOrWhiteSpace(normalizedRole))
                {
                    return BadRequest(new { message = "Missing post author." });
                }

                if (!normalizedRole.Equals("Artist", StringComparison.OrdinalIgnoreCase)
                    && !normalizedRole.Equals("Sessionist", StringComparison.OrdinalIgnoreCase)
                    && !normalizedRole.Equals("Organizer", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "Only artists, sessionists, and organizers can create public posts right now." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                if (!await SecuritySupport.UserMatchesRoleAsync(connection, req.userId, normalizedRole))
                {
                    return BadRequest(new { message = "Post author does not match the selected role." });
                }

                var sanitizedContent = SecuritySupport.SanitizePlainText(req.content, 2000, true);
                var normalizedImage = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.imageUrl, 2_500_000, out var imageError);
                if (imageError is not null)
                {
                    return BadRequest(new { message = imageError });
                }

                if (string.IsNullOrWhiteSpace(sanitizedContent) && string.IsNullOrWhiteSpace(normalizedImage))
                {
                    return BadRequest(new { message = "Add text or an image before posting." });
                }

                var postId = Guid.NewGuid();

                const string sql = @"
                    INSERT INTO community_posts (id, user_id, role, content, image_url, created_at)
                    VALUES (@id, @userId, @role, @content, @imageUrl, NOW());";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = postId;
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = req.userId;
                cmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = normalizedRole;
                cmd.Parameters.Add("@content", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedContent) ? DBNull.Value : sanitizedContent;
                cmd.Parameters.Add("@imageUrl", NpgsqlDbType.Text).Value = (object?)normalizedImage ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    req.userId,
                    normalizedRole,
                    "community_post_created",
                    "community_post",
                    postId,
                    HttpContext,
                    $"Community post created by {normalizedRole}.");

                var authorName = await GetAuthorDisplayNameAsync(connection, req.userId, normalizedRole);
                var followerIds = await GetFollowerIdsAsync(connection, req.userId);
                foreach (var followerId in followerIds)
                {
                    await NotificationSupport.InsertNotificationIfNotExistsAsync(
                        connection,
                        followerId,
                        "community_post",
                        "New public post",
                        $"{authorName} shared a new post on their profile.",
                        postId,
                        "community_post",
                        12);
                }

                return Ok(new { message = "Post published successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        private static async Task<string> GetAuthorDisplayNameAsync(NpgsqlConnection connection, Guid userId, string role)
        {
            const string sql = @"
                SELECT COALESCE(firstname, ''),
                       COALESCE(lastname, ''),
                       COALESCE(stagename, ''),
                       COALESCE(productionname, '')
                FROM users
                WHERE id = @id;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = userId;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return CommunitySupport.BuildDisplayName(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    role);
            }

            return role;
        }

        private static async Task<List<Guid>> GetFollowerIdsAsync(NpgsqlConnection connection, Guid userId)
        {
            const string ensureSql = @"
                CREATE TABLE IF NOT EXISTS customer_favorites (
                    id uuid PRIMARY KEY,
                    customer_id uuid NOT NULL,
                    target_user_id uuid NOT NULL,
                    target_role varchar(50) NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    CONSTRAINT uq_customer_favorite UNIQUE (customer_id, target_user_id, target_role)
                );";

            await using (var ensureCmd = new NpgsqlCommand(ensureSql, connection))
            {
                await ensureCmd.ExecuteNonQueryAsync();
            }

            const string sql = @"
                SELECT DISTINCT customer_id
                FROM customer_favorites
                WHERE target_user_id = @userId;";

            var ids = new List<Guid>();
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    ids.Add(reader.GetGuid(0));
                }
            }

            return ids;
        }
    }
}
