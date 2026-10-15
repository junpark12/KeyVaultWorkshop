using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using KeyVaultIntegration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Azure.Security.KeyVault.Secrets;

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
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args, ContentRootPath = AppContext.BaseDirectory
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
            var app = builder.Build();
            string startupConnectionString = configuration.GetConnectionString("Default");
            int reloadNotifications = 0;
            using var reloadSubscription = ChangeToken.OnChange(
                configuration.GetReloadToken, () => Interlocked.Increment(ref reloadNotifications));
            bool enabled = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Demo:Enabled");
            if (enabled)
                app.Logger.LogWarning("Training endpoints return plaintext secret values. Use fake data only and bind to localhost.");
            app.MapGet("/health", () => Results.Ok(new { status = "up", mode }));
            app.MapGet("/demo/configuration", (HttpContext context) =>
            {
                if (!enabled || mode == "direct") return Results.NotFound();
                try
                {
                    string connectionString = configuration.GetConnectionString("Default");
                    IntegrationSupport.EnsureValuePresent(connectionString);
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(new
                    {
                        source = "IConfiguration",
                        configured = true,
                        differsFromStartup = !string.Equals(startupConnectionString, connectionString, StringComparison.Ordinal),
                        reloadNotifications = Volatile.Read(ref reloadNotifications),
                        value = connectionString
                    });
                }
                catch (Exception error) when (IntegrationSupport.IsExpected(error))
                {
                    app.Logger.LogError("Configuration read failed: {ErrorCode}", IntegrationSupport.ErrorCode(error));
                    return Results.Problem(title: IntegrationSupport.ErrorCode(error), statusCode: 503);
                }
            });
            app.MapGet("/demo/secret", async (HttpContext context) =>
            {
                if (!enabled || mode != "direct") return Results.NotFound();
                try
                {
                    var client = app.Services.GetRequiredService<SecretClient>();
                    string name = configuration["KeyVault:SecretName"] ?? "ConnectionStrings--Default";
                    string version = IntegrationSupport.SecretVersion(configuration);
                    KeyVaultSecret secret = await IntegrationSupport.ReadSecretAsync(
                        client, name, version, context.RequestAborted);
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(new
                    {
                        source = "SecretClient",
                        selection = version == null ? "latest" : "pinned",
                        version = secret.Properties.Version,
                        value = secret.Value
                    });
                }
                catch (Exception error) when (IntegrationSupport.IsExpected(error))
                {
                    app.Logger.LogError("Direct read failed: {ErrorCode}", IntegrationSupport.ErrorCode(error));
                    return Results.Problem(title: IntegrationSupport.ErrorCode(error), statusCode: 503);
                }
            });
            await app.RunAsync();
            return 0;
        }
        catch (Exception error) when (IntegrationSupport.IsExpected(error))
        {
            Console.Error.WriteLine("STARTUP_ERROR " + IntegrationSupport.ErrorCode(error));
            return 1;
        }
    }
}
