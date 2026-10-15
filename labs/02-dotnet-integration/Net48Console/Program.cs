using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using KeyVaultIntegration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--self-test")
            {
                await OfflineChecks.RunAsync();
                return 0;
            }
            bool watch = false;
            foreach (string argument in args)
            {
                if (argument == "--watch" && !watch)
                    watch = true;
                else
                    throw new ArgumentException("Use no arguments, --watch, or --self-test alone.");
            }
            Console.Error.WriteLine("WARNING: This training demo prints secret values in plaintext. Use fake data only; do not record or share real credentials.");
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory
            });
            IConfiguration configuration = builder.Configuration;
            IntegrationSupport.Validate(configuration);
            string mode = configuration["KeyVault:Mode"] ?? "local";
            if (mode == "configuration" || mode == "direct")
            {
                ClientSecretCredential credential = IntegrationSupport.CreateCredential();
                var client = new SecretClient(new Uri(configuration["KeyVault:VaultUri"]),
                    credential, IntegrationSupport.ClientOptions());
                builder.Services.AddSingleton(client);
                if (mode == "configuration")
                {
                    builder.Configuration.AddAzureKeyVault(client, new AzureKeyVaultConfigurationOptions
                    {
                        ReloadInterval = IntegrationSupport.ReloadInterval(configuration)
                    });
                }
            }
            string startupConnectionString = configuration.GetConnectionString("Default");
            int reloadNotifications = 0;
            using (IHost host = builder.Build())
            using (IDisposable reloadSubscription = ChangeToken.OnChange(
                configuration.GetReloadToken, () => Interlocked.Increment(ref reloadNotifications)))
            {
                int count = watch ? 13 : 1;
                for (int i = 0; i < count; i++)
                {
                    if (mode == "direct")
                    {
                        string name = configuration["KeyVault:SecretName"] ?? "ConnectionStrings--Default";
                        string version = IntegrationSupport.SecretVersion(configuration);
                        KeyVaultSecret secret = await IntegrationSupport.ReadSecretAsync(
                            host.Services.GetRequiredService<SecretClient>(), name, version, CancellationToken.None);
                        Console.WriteLine("mode=direct credential=ClientSecretCredential selection=" +
                            (version == null ? "latest" : "pinned") +
                            " version=" + secret.Properties.Version + " value=" +
                            secret.Value);
                    }
                    else
                    {
                        string connectionString = configuration.GetConnectionString("Default");
                        IntegrationSupport.EnsureValuePresent(connectionString);
                        bool changed = !string.Equals(startupConnectionString, connectionString, StringComparison.Ordinal);
                        Console.WriteLine("mode=" + mode + " source=IConfiguration configured=True" +
                            " differs_from_startup=" + changed +
                            " reload_notifications=" + Volatile.Read(ref reloadNotifications) + " value=" +
                            connectionString);
                    }
                    if (i + 1 < count) await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
            return 0;
        }
        catch (Exception error) when (IntegrationSupport.IsExpected(error))
        {
            Console.Error.WriteLine("DEMO_ERROR " + IntegrationSupport.ErrorCode(error));
            return 1;
        }
    }
}
