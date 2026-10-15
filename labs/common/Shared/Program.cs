using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;

namespace KeyVaultWorkshop
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || (args.Length == 1 && args[0] == "help"))
                {
                    Console.WriteLine("Commands: get | cache | watch | retry-demo | self-test");
                    Console.WriteLine("Live: KEY_VAULT_URL, KV_SECRET_NAME, AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET.");
                    Console.WriteLine("Authentication: instructor-provided SP via ClientSecretCredential; no az login required.");
                    Console.WriteLine("Offline: retry-demo, self-test need no Azure account.");
                    return 0;
                }
                if (args.Length != 1)
                    throw new ArgumentException("Specify exactly one command. Run help for usage.");

                switch (args[0])
                {
                    case "retry-demo":
                        await OfflineDemos.RetryAsync();
                        return 0;
                    case "self-test":
                        await OfflineDemos.SelfTestAsync();
                        return 0;
                    case "get":
                    case "cache":
                    case "watch":
                        await RunLiveAsync(args[0]);
                        return 0;
                    default:
                        throw new ArgumentException("Unknown command. Run help for usage.");
                }
            }
            catch (CredentialUnavailableException)
            {
                Console.Error.WriteLine("AUTH_UNAVAILABLE: Check the instructor-provided SP environment variables.");
                return 2;
            }
            catch (AuthenticationFailedException)
            {
                Console.Error.WriteLine("AUTH_FAILED: Check the SP tenant, client ID, secret VALUE/expiry and tenant policy.");
                return 2;
            }
            catch (RequestFailedException ex)
            {
                Console.Error.WriteLine("KEY_VAULT_ERROR status=" + ex.Status + " code=" + ex.ErrorCode);
                Console.Error.WriteLine("403: RBAC/network; 404: name/version; 429: load; 5xx/0: service/network.");
                return 2;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("TIMEOUT: Operation budget exceeded or operation cancelled; no stale secret returned.");
                return 3;
            }
            catch (HttpRequestException)
            {
                Console.Error.WriteLine("NETWORK_ERROR: Check DNS, proxy, Private Endpoint routing, and TLS.");
                return 2;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("CONFIG_ERROR: " + ex.Message);
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine("OPERATION_ERROR: " + ex.Message);
                return 1;
            }
        }

        private static async Task RunLiveAsync(string command)
        {
            WorkshopSettings settings = WorkshopSettings.Load();
            var source = new AzureSecretSource(AzureClientFactory.Create(settings));
            Console.WriteLine("auth=" + settings.AuthMode + " cache_ttl_seconds=" + settings.CacheTtl.TotalSeconds);
            using (var reader = new CachedSecretReader(source, new[] { settings.SecretName }, settings.CacheTtl))
            {
                int count = command == "get" ? 1 : command == "cache" ? 10 : 13;
                for (int i = 0; i < count; i++)
                {
                    using (var budget = new CancellationTokenSource(settings.OperationTimeout))
                    {
                        SecretRead read = await reader.GetAsync(settings.SecretName, budget.Token);
                        Console.WriteLine("read=" + (i + 1) + " source=" + (read.FromCache ? "cache" : "vault") +
                            " version=" + read.Secret.Version + " value=[REDACTED]");
                    }
                    if (command == "watch" && i + 1 < count)
                        await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
            Console.WriteLine("logical_get_calls=" + source.Calls + " (SDK retries/auth HTTP exchanges not counted)");
        }
    }
}
