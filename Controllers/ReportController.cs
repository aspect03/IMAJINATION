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

    public class ReviewReportRequest
    {
        public string? action { get; set; }
        public string? adminNote { get; set; }
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

        [HttpGet("admin/list")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAdminReportList([FromQuery] string? status, [FromQuery] string? targetType, [FromQuery] int page = 1)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
                var normalizedType = string.IsNullOrWhiteSpace(targetType) ? null : targetType.Trim().ToLowerInvariant();
                var offset = Math.Max(0, page - 1) * 20;

                const string sql = @"
                    SELECT
                        r.id,
                        r.reporter_user_id,
                        COALESCE(u_reporter.firstname || ' ' || u_reporter.lastname, 'Unknown'),
                        COALESCE(u_reporter.role, ''),
                        r.target_entity_id,
                        r.target_entity_type,
                        CASE
                            WHEN r.target_entity_type = 'event' THEN COALESCE(e.title, 'Deleted Event')
                            ELSE COALESCE(u_target.stagename, u_target.firstname || ' ' || COALESCE(u_target.lastname, ''), 'Unknown')
                        END AS target_name,
                        r.reason,
                        COALESCE(r.details, ''),
                        r.status,
                        COALESCE(r.admin_note, ''),
                        r.created_at,
                        r.reviewed_at
                    FROM entity_reports r
                    LEFT JOIN users u_reporter ON u_reporter.id = r.reporter_user_id
                    LEFT JOIN users u_target ON u_target.id = r.target_entity_id AND r.target_entity_type != 'event'
                    LEFT JOIN events e ON e.id = r.target_entity_id AND r.target_entity_type = 'event'
                    WHERE (@status IS NULL OR LOWER(r.status) = LOWER(@status))
                      AND (@targetType IS NULL OR r.target_entity_type = @targetType)
                    ORDER BY r.created_at DESC
                    LIMIT 20 OFFSET @offset;";

                var reports = new List<object>();
                await using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = (object?)normalizedStatus ?? DBNull.Value;
                    cmd.Parameters.Add("@targetType", NpgsqlDbType.Text).Value = (object?)normalizedType ?? DBNull.Value;
                    cmd.Parameters.Add("@offset", NpgsqlDbType.Integer).Value = offset;

                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        reports.Add(new
                        {
                            id = reader.GetGuid(0),
                            reporterUserId = reader.GetGuid(1),
                            reporterName = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
                            reporterRole = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            targetId = reader.GetGuid(4),
                            targetType = reader.IsDBNull(5) ? "" : reader.GetString(5),
                            targetName = reader.IsDBNull(6) ? "Unknown" : reader.GetString(6),
                            reason = reader.IsDBNull(7) ? "" : reader.GetString(7),
                            details = reader.IsDBNull(8) ? "" : reader.GetString(8),
                            status = reader.IsDBNull(9) ? "Pending" : reader.GetString(9),
                            adminNote = reader.IsDBNull(10) ? "" : reader.GetString(10),
                            createdAt = reader.IsDBNull(11) ? DateTime.UtcNow : reader.GetDateTime(11),
                            reviewedAt = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12)
                        });
                    }
                }

                const string countSql = @"
                    SELECT COUNT(*) FROM entity_reports r
                    WHERE (@status IS NULL OR LOWER(r.status) = LOWER(@status))
                      AND (@targetType IS NULL OR r.target_entity_type = @targetType);";

                long totalCount = 0;
                await using (var countCmd = new NpgsqlCommand(countSql, connection))
                {
                    countCmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = (object?)normalizedStatus ?? DBNull.Value;
                    countCmd.Parameters.Add("@targetType", NpgsqlDbType.Text).Value = (object?)normalizedType ?? DBNull.Value;
                    totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync() ?? 0L);
                }

                return Ok(new { reports, totalCount, page, pageSize = 20 });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load reports: " + ex.Message });
            }
        }

        [HttpPatch("admin/{reportId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ReviewReport(Guid reportId, [FromBody] ReviewReportRequest req)
        {
            try
            {
                var normalizedAction = (req.action ?? string.Empty).Trim().ToLowerInvariant();
                if (normalizedAction is not ("dismiss" or "warn" or "suspend" or "ban" or "resolve"))
                {
                    return BadRequest(new { message = "Invalid action. Use: dismiss, warn, suspend, ban, or resolve." });
                }

                var newStatus = normalizedAction switch
                {
                    "dismiss" => "Dismissed",
                    "resolve" => "Resolved",
                    _ => "ActionTaken"
                };

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                const string updateSql = @"
                    UPDATE entity_reports
                    SET status = @status,
                        admin_note = @adminNote,
                        reviewed_at = NOW()
                    WHERE id = @id
                    RETURNING reporter_user_id, target_entity_id, target_entity_type, reason;";

                Guid reporterUserId = Guid.Empty;
                Guid targetId = Guid.Empty;
                string targetType = "";
                string reason = "";

                await using (var updateCmd = new NpgsqlCommand(updateSql, connection))
                {
                    updateCmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = newStatus;
                    updateCmd.Parameters.Add("@adminNote", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(req.adminNote) ? DBNull.Value : req.adminNote.Trim();
                    updateCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = reportId;

                    await using var reader = await updateCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Report not found." });
                    }

                    reporterUserId = reader.GetGuid(0);
                    targetId = reader.GetGuid(1);
                    targetType = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    reason = reader.IsDBNull(3) ? "" : reader.GetString(3);
                }

                if (normalizedAction is "warn" or "suspend" or "ban" && targetType != "event")
                {
                    var moderationAction = normalizedAction switch
                    {
                        "warn" => "warned",
                        "suspend" => "suspended",
                        "ban" => "banned",
                        _ => normalizedAction
                    };

                    const string moderateSql = "UPDATE users SET moderation_status = @action WHERE id = @id;";
                    await using var moderateCmd = new NpgsqlCommand(moderateSql, connection);
                    moderateCmd.Parameters.Add("@action", NpgsqlDbType.Text).Value = moderationAction;
                    moderateCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = targetId;
                    await moderateCmd.ExecuteNonQueryAsync();
                }

                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await NotificationSupport.InsertNotificationIfNotExistsAsync(
                    connection,
                    reporterUserId,
                    "report_reviewed",
                    "Report Reviewed",
                    $"Your report ({reason}) has been reviewed and {newStatus.ToLower()} by our team.",
                    reportId,
                    "report",
                    0);

                return Ok(new { message = $"Report {newStatus.ToLower()} successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to review report: " + ex.Message });
            }
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
