using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;

namespace KeyVaultSp.Net48
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && (args[0] == "--help" || args[0] == "help"))
                {
                    Console.WriteLine("Usage: KeyVaultSp.Net48.exe [--help]");
                    Console.WriteLine("With no arguments, read one secret using ClientSecretCredential.");
                    Console.WriteLine("Required environment variables:");
                    Console.WriteLine("  AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET");
                    Console.WriteLine("  KEY_VAULT_URL, KV_SECRET_NAME");
                    Console.WriteLine("Azure CLI login is not required. Secret values are never printed.");
                    return 0;
                }

                if (args.Length != 0)
                    throw new ArgumentException("Unexpected arguments. Use --help; never pass credentials as arguments.");

                using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    await ServicePrincipalExample.ReadAsync(budget.Token);
                }
                return 0;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("CONFIG_ERROR: " + ex.Message);
                return 1;
            }
            catch (AuthenticationFailedException)
            {
                Console.Error.WriteLine("AUTH_FAILED: Check tenant, client ID, client secret VALUE/expiry, and tenant policy.");
                return 2;
            }
            catch (RequestFailedException ex)
            {
                Console.Error.WriteLine("KEY_VAULT_ERROR status=" + ex.Status + " code=" + ex.ErrorCode);
                Console.Error.WriteLine("403: RBAC/access policy/network; 404: secret name; 429: request load; 5xx/0: service/network.");
                return 2;
            }
            catch (HttpRequestException)
            {
                Console.Error.WriteLine("NETWORK_ERROR: Check DNS, proxy, TLS, and access to Entra ID and Key Vault.");
                return 2;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("TIMEOUT: The operation was cancelled or exceeded its 30-second budget.");
                return 3;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine("OPERATION_ERROR: " + ex.Message);
                return 1;
            }
        }
    }
}
