using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using KeyVaultWorkshop;

internal static class Program
{
    private static Task<int> Main(string[] args) => FrameworkLabSupport.RunAsync(async () =>
    {
        string command = args.Length == 0 ? "cache" : args[0];
        if (args.Length > 1)
            throw new ArgumentException("Specify one command.", "command");
        if (command == "help" || command == "--help")
        {
            Console.WriteLine("Commands: cache (default) | compare | watch | self-test");
            Console.WriteLine("Live: KEY_VAULT_URL, KV_SECRET_NAME; KV_CACHE_TTL_SECONDS defaults to 30.");
            Console.WriteLine("Authentication: ClientSecretCredential with AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET.");
            Console.WriteLine("self-test checks cache/retry behavior without Azure requests.");
            return;
        }
        if (command == "self-test") { await OfflineDemos.SelfTestAsync(); return; }
        if (command != "cache" && command != "compare" && command != "watch")
            throw new ArgumentException("Unknown command; use --help.", "command");

        Uri vaultUri = FrameworkLabSupport.VaultUri();
        string secretName = FrameworkLabSupport.SecretName();
        TimeSpan ttl = FrameworkLabSupport.CacheTtl();
        ClientSecretCredential credential = FrameworkLabSupport.ServicePrincipalCredential();
        var client = new SecretClient(vaultUri, credential, AzureClientFactory.CreateOptions());
        var source = new AzureSecretSource(client);
        Console.WriteLine("credential=ClientSecretCredential cache_ttl_seconds=" + ttl.TotalSeconds);

        if (command == "compare")
        {
            for (int i = 0; i < 10; i++)
            {
                using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    SecretSnapshot secret = await source.GetAsync(secretName, budget.Token);
                    secret.EnsureUsable(DateTimeOffset.UtcNow);
                }
            }
            Console.WriteLine("without_cache_reads=10 logical_get_calls=" + source.Calls);
        }

        int before = source.Calls;
        using (var cache = new CachedSecretReader(source, new[] { secretName }, ttl))
        {
            int reads = command == "watch" ? 13 : 10;
            for (int i = 0; i < reads; i++)
            {
                using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    SecretRead read = await cache.GetAsync(secretName, budget.Token);
                    Console.WriteLine("read=" + (i + 1) + " source=" + (read.FromCache ? "cache" : "vault") +
                        " version=" + read.Secret.Version + " value=[REDACTED]");
                }
                if (command == "watch" && i + 1 < reads)
                    await Task.Delay(TimeSpan.FromSeconds(5));
            }
            Console.WriteLine("with_cache_reads=" + reads + " logical_get_calls=" + (source.Calls - before));
            Console.WriteLine("Logical calls exclude SDK retries and authentication HTTP exchanges.");
        }
    });
}
