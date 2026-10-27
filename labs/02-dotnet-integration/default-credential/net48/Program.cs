using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core.Diagnostics;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using KeyVaultWorkshop;

internal static class Program
{
    private static Task<int> Main(string[] args) => FrameworkLabSupport.RunAsync(async () =>
    {
        if (args.Length == 1 && (args[0] == "help" || args[0] == "--help"))
        {
            Console.WriteLine("Usage: DefaultCredential.Net48.exe [--help]");
            Console.WriteLine("Required: KEY_VAULT_URL, KV_SECRET_NAME");
            Console.WriteLine("Optional: AZURE_TOKEN_CREDENTIALS to select a supported credential.");
            Console.WriteLine("No arguments: read one secret using DefaultAzureCredential. Values are never printed.");
            return;
        }
        if (args.Length != 0)
            throw new ArgumentException("Use no arguments or --help.", "command");

        Uri vaultUri = FrameworkLabSupport.VaultUri();
        string secretName = FrameworkLabSupport.SecretName();
        string selector = FrameworkLabSupport.CredentialSelector();

        using var identityListener = new AzureEventSourceListener((eventData, message) =>
        {
            if (eventData.EventSource.Name == "Azure-Identity" &&
                eventData.EventName == "DefaultAzureCredentialCredentialSelected")
            {
                Console.WriteLine("authentication=" + message);
            }
        }, EventLevel.Informational);

        var credential = new DefaultAzureCredential();
        var client = new SecretClient(vaultUri, credential, AzureClientFactory.CreateOptions());
        var source = new AzureSecretSource(client);
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            SecretSnapshot secret = await source.GetAsync(secretName, budget.Token);
            secret.EnsureUsable(DateTimeOffset.UtcNow);
            Console.WriteLine("credential=DefaultAzureCredential selector=" + selector);
            Console.WriteLine("version=" + secret.Version + " value=[REDACTED]");
        }
    });
}
