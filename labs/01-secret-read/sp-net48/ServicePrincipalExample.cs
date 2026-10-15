using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace KeyVaultSp.Net48
{
    public static class ServicePrincipalExample
    {
        public static async Task ReadAsync(CancellationToken cancellationToken)
        {
            string tenantId = RequiredGuid("AZURE_TENANT_ID");
            string clientId = RequiredGuid("AZURE_CLIENT_ID");
            string clientSecret = Required("AZURE_CLIENT_SECRET");
            string vaultUrl = Required("KEY_VAULT_URL");
            string secretName = Required("KV_SECRET_NAME");

            if (!Uri.TryCreate(vaultUrl, UriKind.Absolute, out Uri vaultUri) ||
                vaultUri.Scheme != Uri.UriSchemeHttps ||
                !vaultUri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
                vaultUri.Port != 443 || vaultUri.AbsolutePath != "/" ||
                vaultUri.UserInfo.Length != 0 || vaultUri.Query.Length != 0 || vaultUri.Fragment.Length != 0)
            {
                throw new ArgumentException("KEY_VAULT_URL must be https://<vault-name>.vault.azure.net/ (public Azure).");
            }
            if (!Regex.IsMatch(secretName, @"\A[a-zA-Z0-9-]{1,127}\z"))
                throw new ArgumentException("KV_SECRET_NAME must contain 1-127 letters, digits, or hyphens.");

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var options = new SecretClientOptions();
            options.Retry.Mode = RetryMode.Exponential;
            options.Retry.Delay = TimeSpan.FromSeconds(1);
            options.Retry.MaxDelay = TimeSpan.FromSeconds(4);
            options.Retry.MaxRetries = 3;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            options.Diagnostics.IsLoggingContentEnabled = false;

            var client = new SecretClient(vaultUri, credential, options);
            KeyVaultSecret secret = (await client.GetSecretAsync(
                secretName, cancellationToken: cancellationToken).ConfigureAwait(false)).Value;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (secret.Properties.Enabled == false ||
                (secret.Properties.ExpiresOn.HasValue && now >= secret.Properties.ExpiresOn.Value) ||
                (secret.Properties.NotBefore.HasValue && now < secret.Properties.NotBefore.Value))
            {
                throw new InvalidOperationException("Secret is disabled, expired, or not yet valid; refusing to use it.");
            }

            Console.WriteLine("auth=service-principal");
            Console.WriteLine("version=" + secret.Properties.Version + " value=" + secret.Value);
        }

        private static string Required(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing environment variable: " + name);
            return value;
        }

        private static string RequiredGuid(string name)
        {
            string value = Required(name);
            if (!Guid.TryParse(value, out _))
                throw new ArgumentException(name + " must be a GUID.");
            return value;
        }
    }
}
