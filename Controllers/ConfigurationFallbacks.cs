namespace ImajinationAPI.Controllers
{
    internal static class ConfigurationFallbacks
    {
        public static string GetRequiredSupabaseConnectionString(IConfiguration configuration)
        {
            var connectionString =
                Environment.GetEnvironmentVariable("ConnectionStrings__SupabaseConnection") ??
                configuration.GetConnectionString("SupabaseConnection");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Supabase connection is not configured. Set ConnectionStrings__SupabaseConnection or restore ConnectionStrings:SupabaseConnection in appsettings.json.");
            }

            return connectionString;
        }

        public static string? GetSetting(IConfiguration configuration, string sectionPath, string environmentKey)
        {
            var envValue = Environment.GetEnvironmentVariable(environmentKey);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                return envValue;
            }

            return configuration[sectionPath];
        }

        public static string GetRequiredSetting(IConfiguration configuration, string sectionPath, string environmentKey, string friendlyName)
        {
            var value = GetSetting(configuration, sectionPath, environmentKey);

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"{friendlyName} is not configured. Set {environmentKey} or restore {sectionPath} in appsettings.json.");
            }

            return value;
        }
    }
}
