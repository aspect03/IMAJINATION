using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Controllers;

namespace ImajinationAPI.Services
{
    public sealed class SessionTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "SessionToken";

        private readonly IConfiguration _configuration;

        public SessionTokenAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IConfiguration configuration)
            : base(options, logger, encoder)
        {
            _configuration = configuration;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var sessionToken = Request.Headers["X-Session-Token"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(sessionToken))
            {
                sessionToken = Request.Cookies["IMAJINATION-SESSION"]?.Trim() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(sessionToken))
            {
                return AuthenticateResult.NoResult();
            }

            var connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(_configuration);

            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                const string sql = @"
                    SELECT
                        s.user_id,
                        COALESCE(NULLIF(TRIM(u.role), ''), COALESCE(NULLIF(TRIM(s.actor_role), ''), '')),
                        COALESCE(u.firstname, ''),
                        COALESCE(u.username, ''),
                        s.revoked_at,
                        COALESCE(s.revoked_reason, '')
                    FROM user_active_sessions s
                    INNER JOIN users u ON u.id = s.user_id
                    WHERE s.session_token = @sessionToken
                    LIMIT 1;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@sessionToken", NpgsqlDbType.Text).Value = sessionToken;

                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return AuthenticateResult.Fail("This session is no longer active.");
                }

                var userId = reader.GetGuid(0);
                var role = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                var firstName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var username = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var revokedAt = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
                var revokedReason = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                await reader.CloseAsync();

                if (revokedAt.HasValue)
                {
                    return AuthenticateResult.Fail(string.IsNullOrWhiteSpace(revokedReason)
                        ? "This session has ended."
                        : revokedReason);
                }

                await SecuritySupport.TouchTrackedSessionAsync(connection, userId, sessionToken);

                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, userId.ToString()),
                    new(ClaimTypes.Name, firstName),
                    new("username", username)
                };

                if (!string.IsNullOrWhiteSpace(role))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                }

                var identity = new ClaimsIdentity(claims, SchemeName);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, SchemeName);
                return AuthenticateResult.Success(ticket);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Session-token authentication failed.");
                return AuthenticateResult.Fail("Unable to validate this session.");
            }
        }
    }
}
