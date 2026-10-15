using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;

namespace KeyVaultWorkshop
{
    internal static class FrameworkLabSupport
    {
        public static Uri VaultUri()
        {
            string value = Required("KEY_VAULT_URL");
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
                !uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Use a public Azure vault HTTPS URL.", "KEY_VAULT_URL");
            return uri;
        }

        public static string SecretName()
        {
            string name = Required("KV_SECRET_NAME");
            try { WorkshopSettings.ValidateSecretName(name); }
            catch (ArgumentException)
            {
                throw new ArgumentException("Use a valid secret name.", "KV_SECRET_NAME");
            }
            return name;
        }

        public static string CredentialSelector()
        {
            string selector = Environment.GetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS");
            if (string.IsNullOrEmpty(selector)) return "default-chain";
            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dev", "prod", "EnvironmentCredential", "WorkloadIdentityCredential",
                "ManagedIdentityCredential", "VisualStudioCredential", "VisualStudioCodeCredential",
                "AzureCliCredential", "AzurePowerShellCredential", "AzureDeveloperCliCredential",
                "InteractiveBrowserCredential", "BrokerCredential"
            };
            if (!supported.Contains(selector))
                throw new ArgumentException("Use a supported credential selector.", "AZURE_TOKEN_CREDENTIALS");
            return selector;
        }

        public static ClientSecretCredential ServicePrincipalCredential()
        {
            string tenantId = Required("AZURE_TENANT_ID");
            string clientId = Required("AZURE_CLIENT_ID");
            string secret = Required("AZURE_CLIENT_SECRET");
            if (!Guid.TryParse(tenantId, out _))
                throw new ArgumentException("Use a tenant GUID.", "AZURE_TENANT_ID");
            if (!Guid.TryParse(clientId, out _))
                throw new ArgumentException("Use an application client GUID.", "AZURE_CLIENT_ID");
            return new ClientSecretCredential(tenantId, clientId, secret);
        }

        public static TimeSpan CacheTtl()
        {
            string value = Environment.GetEnvironmentVariable("KV_CACHE_TTL_SECONDS") ?? "30";
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ||
                seconds < 1 || seconds > 3600)
                throw new ArgumentException("Use a TTL from 1 to 3600 seconds.", "KV_CACHE_TTL_SECONDS");
            return TimeSpan.FromSeconds(seconds);
        }

        public static async Task<int> RunAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return 0;
            }
            catch (ArgumentException error)
            {
                Console.Error.WriteLine("CONFIG_ERROR parameter=" + (error.ParamName ?? "configuration"));
                return 1;
            }
            catch (AuthenticationFailedException)
            {
                Console.Error.WriteLine("AUTH_FAILED: Check the credential required by this exercise and its tenant/settings.");
                return 2;
            }
            catch (RequestFailedException error)
            {
                Console.Error.WriteLine("KEY_VAULT_ERROR status=" + error.Status + " code=" + error.ErrorCode);
                Console.Error.WriteLine("403: role/policy/network; 404: name/version; 429: request load; 5xx/0: service/network.");
                return 2;
            }
            catch (HttpRequestException)
            {
                Console.Error.WriteLine("NETWORK_ERROR: Check DNS, proxy, TLS and access to Entra ID and Key Vault.");
                return 2;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("TIMEOUT: The 30-second request budget was exceeded or the request was cancelled.");
                return 3;
            }
            catch (InvalidOperationException)
            {
                Console.Error.WriteLine("OPERATION_ERROR: Secret validity, cache state or an offline check failed.");
                return 1;
            }
        }

        private static string Required(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Required configuration is missing.", name);
            return value;
        }
    }
}
