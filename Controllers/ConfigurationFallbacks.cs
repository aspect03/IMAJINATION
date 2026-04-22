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

        public static bool GetBooleanSetting(
            IConfiguration configuration,
            string sectionPath,
            string environmentKey,
            bool defaultValue = false)
        {
            var rawValue = GetSetting(configuration, sectionPath, environmentKey);
            return bool.TryParse(rawValue, out var parsed) ? parsed : defaultValue;
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

        public static string BuildSafeErrorMessage(
            IConfiguration configuration,
            string publicMessage,
            Exception? ex = null)
        {
            var exposeDetails = GetBooleanSetting(
                configuration,
                "AppSecurity:ExposeDetailedErrors",
                "AppSecurity__ExposeDetailedErrors");

            if (!exposeDetails || ex is null || string.IsNullOrWhiteSpace(ex.Message))
            {
                return publicMessage;
            }

            return $"{publicMessage} Details: {ex.Message}";
        }
    }
}
