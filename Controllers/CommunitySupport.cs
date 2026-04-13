using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    public record ProfileCompletionResult(int Percent, bool IsVerified, string Label);

    public static class CommunitySupport
    {
        public static async Task EnsureCommunitySchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users ADD COLUMN IF NOT EXISTS is_verified boolean NOT NULL DEFAULT FALSE;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS profile_completed_at timestamptz NULL;

                CREATE TABLE IF NOT EXISTS community_posts (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    role varchar(30) NOT NULL,
                    content text NOT NULL,
                    image_url text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS idx_community_posts_user_id ON community_posts(user_id);
                CREATE INDEX IF NOT EXISTS idx_community_posts_created_at ON community_posts(created_at DESC);";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        public static string BuildDisplayName(string? firstName, string? lastName, string? stageName, string? productionName, string role)
        {
            if (role.Equals("Organizer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(productionName))
            {
                return productionName.Trim();
            }

            if ((role.Equals("Artist", StringComparison.OrdinalIgnoreCase) || role.Equals("Sessionist", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(stageName))
            {
                return stageName.Trim();
            }

            var fullName = $"{firstName ?? ""} {lastName ?? ""}".Trim();
            return string.IsNullOrWhiteSpace(fullName) ? role : fullName;
        }

        public static ProfileCompletionResult CalculateProfileCompletion(
            string role,
            string? firstName,
            string? lastName,
            string? bio,
            string? profilePicture,
            string? genres,
            string? spotifyLink,
            string? productionName,
            string? contactNumber,
            string? address,
            string? stageName)
        {
            var checks = new List<bool>();
            var hasIdentity = !string.IsNullOrWhiteSpace(BuildDisplayName(firstName, lastName, stageName, productionName, role));
            var hasBio = !string.IsNullOrWhiteSpace(bio) && !bio.Contains("hasn't written", StringComparison.OrdinalIgnoreCase);
            var hasPicture = !string.IsNullOrWhiteSpace(profilePicture);

            checks.Add(hasIdentity);
            checks.Add(hasBio);
            checks.Add(hasPicture);

            if (role.Equals("Artist", StringComparison.OrdinalIgnoreCase) || role.Equals("Sessionist", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(!string.IsNullOrWhiteSpace(genres) && !genres.Equals("Music", StringComparison.OrdinalIgnoreCase) && !genres.Equals("Sessionist", StringComparison.OrdinalIgnoreCase));
                checks.Add(!string.IsNullOrWhiteSpace(spotifyLink));
            }
            else if (role.Equals("Organizer", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(!string.IsNullOrWhiteSpace(productionName));
                checks.Add(!string.IsNullOrWhiteSpace(contactNumber));
                checks.Add(!string.IsNullOrWhiteSpace(address));
            }

            var completed = checks.Count(item => item);
            var percent = checks.Count == 0 ? 0 : (int)Math.Round(completed * 100d / checks.Count);
            var isVerified = completed == checks.Count && checks.Count > 0;
            var label = isVerified ? "Verified Profile" : percent >= 70 ? "Almost Verified" : "Profile Incomplete";
            return new ProfileCompletionResult(percent, isVerified, label);
        }

        public static async Task SyncProfileVerificationAsync(
            NpgsqlConnection connection,
            Guid userId,
            string role,
            string? firstName,
            string? lastName,
            string? bio,
            string? profilePicture,
            string? genres,
            string? spotifyLink,
            string? productionName,
            string? contactNumber,
            string? address,
            string? stageName)
        {
            await EnsureCommunitySchemaAsync(connection);
            var summary = CalculateProfileCompletion(role, firstName, lastName, bio, profilePicture, genres, spotifyLink, productionName, contactNumber, address, stageName);

            const string sql = @"
                UPDATE users
                SET is_verified = @isVerified,
                    profile_completed_at = CASE WHEN @isVerified THEN COALESCE(profile_completed_at, NOW()) ELSE NULL END
                WHERE id = @id;";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@isVerified", NpgsqlDbType.Boolean).Value = summary.IsVerified;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
