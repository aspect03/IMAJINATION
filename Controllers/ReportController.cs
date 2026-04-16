using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class CreateReportRequest
    {
        public Guid reporterUserId { get; set; }
        public Guid targetId { get; set; }
        public string? targetType { get; set; }
        public string? reason { get; set; }
        public string? details { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ReportController : ControllerBase
    {
        private readonly string _connectionString;

        public ReportController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        [HttpPost]
        public async Task<IActionResult> CreateReport([FromBody] CreateReportRequest req)
        {
            try
            {
                if (req.reporterUserId == Guid.Empty || req.targetId == Guid.Empty || string.IsNullOrWhiteSpace(req.targetType) || string.IsNullOrWhiteSpace(req.reason))
                {
                    return BadRequest(new { message = "Missing report details." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var normalizedType = (req.targetType ?? string.Empty).Trim().ToLowerInvariant();
                if (normalizedType is not ("artist" or "sessionist" or "organizer" or "event"))
                {
                    return BadRequest(new { message = "Unsupported report target." });
                }

                const string reporterSql = "SELECT COALESCE(role, '') FROM users WHERE id = @id LIMIT 1;";
                string reporterRole;
                await using (var reporterCmd = new NpgsqlCommand(reporterSql, connection))
                {
                    reporterCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = req.reporterUserId;
                    var roleResult = await reporterCmd.ExecuteScalarAsync();
                    if (roleResult is null)
                    {
                        return BadRequest(new { message = "Reporter account was not found." });
                    }

                    reporterRole = Convert.ToString(roleResult) ?? string.Empty;
                }

                var targetExistsSql = normalizedType == "event"
                    ? "SELECT 1 FROM events WHERE id = @id LIMIT 1;"
                    : "SELECT 1 FROM users WHERE id = @id LIMIT 1;";
                await using (var targetCmd = new NpgsqlCommand(targetExistsSql, connection))
                {
                    targetCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = req.targetId;
                    var targetExists = await targetCmd.ExecuteScalarAsync();
                    if (targetExists is null)
                    {
                        return BadRequest(new { message = "Reported target was not found." });
                    }
                }

                var sanitizedReason = SecuritySupport.SanitizePlainText(req.reason, 240, false);
                var sanitizedDetails = SecuritySupport.SanitizePlainText(req.details, 2000, true);

                const string sql = @"
                    INSERT INTO entity_reports (id, reporter_user_id, target_entity_id, target_entity_type, reason, details, status, created_at)
                    VALUES (@id, @reporterUserId, @targetId, @targetType, @reason, @details, 'Pending', NOW());";

                await using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                    cmd.Parameters.Add("@reporterUserId", NpgsqlDbType.Uuid).Value = req.reporterUserId;
                    cmd.Parameters.Add("@targetId", NpgsqlDbType.Uuid).Value = req.targetId;
                    cmd.Parameters.Add("@targetType", NpgsqlDbType.Text).Value = normalizedType;
                    cmd.Parameters.Add("@reason", NpgsqlDbType.Text).Value = sanitizedReason;
                    cmd.Parameters.Add("@details", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedDetails) ? DBNull.Value : sanitizedDetails;
                    await cmd.ExecuteNonQueryAsync();
                }

                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    req.reporterUserId,
                    reporterRole,
                    "report_submitted",
                    normalizedType,
                    req.targetId,
                    HttpContext,
                    $"Report submitted against {normalizedType} for reason: {sanitizedReason}");

                return Ok(new { message = "Report submitted successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to submit report: " + ex.Message });
            }
        }
    }
}
