using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KeyVaultWorkshop
{
    internal sealed class WorkshopSettings
    {
        public Uri VaultUri { get; private set; }
        public string SecretName { get; private set; }
        public string AuthMode { get; private set; }
        public TimeSpan CacheTtl { get; private set; }
        public TimeSpan OperationTimeout { get; private set; }

        public static WorkshopSettings Load()
        {
            string url = Required("KEY_VAULT_URL");
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
                uri.Port != 443 || uri.AbsolutePath != "/" ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            {
                throw new ArgumentException(
                    "KEY_VAULT_URL must be https://<vault-name>.vault.azure.net/ (public Azure).");
            }

            string name = Required("KV_SECRET_NAME");
            ValidateSecretName(name);
            string mode = Environment.GetEnvironmentVariable("KV_AUTH_MODE") ?? "service-principal";
            if (mode != "service-principal")
                throw new ArgumentException("Unset KV_AUTH_MODE or use service-principal. Developer login is only demonstrated in the dedicated DefaultAzureCredential lab.");

            return new WorkshopSettings
            {
                VaultUri = uri,
                SecretName = name,
                AuthMode = mode,
                CacheTtl = TimeSpan.FromSeconds(ReadInt("KV_CACHE_TTL_SECONDS", 30, 1, 3600)),
                OperationTimeout = TimeSpan.FromSeconds(ReadInt("KV_OPERATION_TIMEOUT_SECONDS", 30, 1, 300))
            };
        }

        public static void ValidateSecretName(string name)
        {
            if (name == null || !Regex.IsMatch(name, @"\A[a-zA-Z0-9-]{1,127}\z"))
                throw new ArgumentException("Secret names must contain 1-127 letters, digits, or hyphens.");
        }

        private static string Required(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing environment variable: " + name);
            return value;
        }

        private static int ReadInt(string name, int defaultValue, int minimum, int maximum)
        {
            string text = Environment.GetEnvironmentVariable(name);
            if (text == null)
                return defaultValue;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
                value < minimum || value > maximum)
                throw new ArgumentException(name + " must be between " + minimum + " and " + maximum + ".");
            return value;
        }
    }
}
