using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace KeyVaultIntegration
{
    internal static class OfflineChecks
    {
        private const string Vault = "https://offline.vault.azure.net/";
        private const string Name = "ConnectionStrings--Default";
        private static readonly string V1 = new string('1', 32);
        private static readonly string V2 = new string('2', 32);

        private sealed class OfflineCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new AccessToken("offline-not-a-real-token", DateTimeOffset.UtcNow.AddHours(1));
            public override ValueTask<AccessToken> GetTokenAsync(
                TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        private sealed class VaultHandler : HttpMessageHandler
        {
            public int Current = 1;
            public int GetCalls;
            public int ListCalls;
            public bool Expired;

#if NET8_0_OR_GREATER
            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Respond(request, cancellationToken);
#endif
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(Respond(request, cancellationToken));

            private HttpResponseMessage Respond(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int current = Volatile.Read(ref Current);
                string[] path = request.RequestUri.AbsolutePath.Trim('/').Split('/');
                string attributes = "\"attributes\":{\"enabled\":true,\"updated\":" + (1700000000 + current) +
                    (Expired ? ",\"exp\":1600000000" : "") + "}";
                string json;
                HttpStatusCode status = HttpStatusCode.OK;
                if (path.Length == 1 && path[0] == "secrets")
                {
                    Interlocked.Increment(ref ListCalls);
                    json = "{\"value\":[{\"id\":\"" + Vault + "secrets/" + Name + "\"," + attributes + "}]}";
                }
                else if (path.Length >= 2 && path[0] == "secrets" && path[1] == Name)
                {
                    Interlocked.Increment(ref GetCalls);
                    string version = path.Length == 3 ? path[2] : current == 1 ? V1 : V2;
                    if (version != V1 && version != V2)
                    {
                        status = HttpStatusCode.NotFound;
                        json = "{\"error\":{\"code\":\"SecretNotFound\",\"message\":\"Offline missing version\"}}";
                    }
                    else
                    {
                        string value = version == V1 ? "TRAINING-ONLY-V1" : "TRAINING-ONLY-V2";
                        json = "{\"value\":\"" + value + "\",\"id\":\"" + Vault + "secrets/" + Name + "/" +
                            version + "\"," + attributes + "}";
                    }
                }
                else
                {
                    throw new InvalidOperationException("Unexpected offline request path.");
                }
                return new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        }

        public static async Task RunAsync()
        {
            using (var handler = new VaultHandler())
            using (var http = new HttpClient(handler))
            {
                var options = IntegrationSupport.ClientOptions();
                options.Retry.MaxRetries = 0;
                options.Transport = new HttpClientTransport(http);
                var client = new SecretClient(new Uri(Vault), new OfflineCredential(), options);
                using (var config = new ConfigurationManager())
                {
                    config.AddAzureKeyVault(client, new AzureKeyVaultConfigurationOptions
                    {
                        ReloadInterval = TimeSpan.FromMilliseconds(100)
                    });
                    Check(config.GetConnectionString("Default") == "TRAINING-ONLY-V1", "double-hyphen to configuration key mapping");
                    string startupValue = config.GetConnectionString("Default");
                    int notifications = 0;
                    using (IDisposable subscription = ChangeToken.OnChange(
                        ((IConfiguration)config).GetReloadToken, () => Interlocked.Increment(ref notifications)))
                    {
                        Check(config["ConnectionStrings:Default"] == startupValue, "IConfiguration indexer and GetConnectionString agree");
                        Volatile.Write(ref handler.Current, 2);
                        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                        while ((config.GetConnectionString("Default") == startupValue || Volatile.Read(ref notifications) == 0) &&
                               DateTime.UtcNow < deadline)
                            await Task.Delay(50);
                        if (config.GetConnectionString("Default") == startupValue || Volatile.Read(ref notifications) == 0)
                            Console.Error.WriteLine("RELOAD_DIAGNOSTIC list_calls=" + handler.ListCalls +
                                " get_calls=" + handler.GetCalls +
                                " configuration_changed=" + (config["ConnectionStrings:Default"] == "TRAINING-ONLY-V2") +
                                " notifications=" + Volatile.Read(ref notifications));
                        Check(config.GetConnectionString("Default") != startupValue && Volatile.Read(ref notifications) > 0,
                            "ReloadInterval updates IConfiguration and its change token");
                    }
                }

                using (var config = new ConfigurationManager())
                {
                    config.AddAzureKeyVault(client, new AzureKeyVaultConfigurationOptions());
                    Check(config["ConnectionStrings:Default"] == "TRAINING-ONLY-V2", "startup-only load");
                    Volatile.Write(ref handler.Current, 1);
                    Check(config["ConnectionStrings:Default"] == "TRAINING-ONLY-V2", "no automatic refresh without ReloadInterval");
                    ((IConfigurationRoot)config).Reload();
                    Check(config["ConnectionStrings:Default"] == "TRAINING-ONLY-V1", "explicit configuration reload");
                }
                Volatile.Write(ref handler.Current, 2);
                KeyVaultSecret result = await IntegrationSupport.ReadSecretAsync(client, Name, null, CancellationToken.None);
                Check(result.Properties.Version == V2 && result.Value == "TRAINING-ONLY-V2", "latest returns the native KeyVaultSecret");
                result = await IntegrationSupport.ReadSecretAsync(client, Name, V1, CancellationToken.None);
                Check(result.Properties.Version == V1, "pinned version remains stable");
                int before = handler.GetCalls;
                await IntegrationSupport.ReadSecretAsync(client, Name, null, CancellationToken.None);
                await IntegrationSupport.ReadSecretAsync(client, Name, null, CancellationToken.None);
                Check(handler.GetCalls == before + 2, "singleton SecretClient does not cache secret values");
                handler.Expired = true;
                bool expiryRejected = false;
                try { await IntegrationSupport.ReadSecretAsync(client, Name, null, CancellationToken.None); }
                catch (InvalidOperationException) { expiryRejected = true; }
                Check(expiryRejected, "direct sample enforces secret expiration metadata");
                using (var config = new ConfigurationManager())
                {
                    config.AddAzureKeyVault(client, new AzureKeyVaultConfigurationOptions());
                    Check(config["ConnectionStrings:Default"] == "TRAINING-ONLY-V2",
                        "default configuration provider includes expired secrets");
                }
                handler.Expired = false;
                bool notFound = false;
                try { await IntegrationSupport.ReadSecretAsync(client, Name, new string('3', 32), CancellationToken.None); }
                catch (RequestFailedException error) when (error.Status == 404) { notFound = true; }
                Check(notFound, "missing pinned version surfaces 404");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    bool observed = false;
                    try { await IntegrationSupport.ReadSecretAsync(client, Name, null, cancelled.Token); }
                    catch (OperationCanceledException) { observed = true; }
                    Check(observed, "cancellation reaches direct request");
                }
            }

            foreach (string invalid in new[] { null, "", " " })
            {
                bool rejected = false;
                try { IntegrationSupport.EnsureValuePresent(invalid); }
                catch (InvalidOperationException error)
                {
                    rejected = true;
                    Check(IntegrationSupport.ErrorCode(error) == "MISSING_SETTING_ConnectionStrings__Default",
                        "missing configuration has a safe diagnostic");
                }
                Check(rejected, "missing configuration rejected");
            }
            using (var config = new ConfigurationManager())
            {
                config.AddInMemoryCollection(new Dictionary<string, string> { ["KeyVault:Mode"] = "unknown" });
                bool rejected = false;
                try { IntegrationSupport.Validate(config); }
                catch (ArgumentException error)
                {
                    rejected = true;
                    Check(IntegrationSupport.ErrorCode(error) == "INVALID_SETTING_KeyVault__Mode",
                        "invalid mode identifies its setting without printing its value");
                }
                Check(rejected, "invalid mode rejected");
            }
            var previous = new Dictionary<string, string>();
            foreach (string name in new[] { "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_TOKEN_CREDENTIALS" })
                previous[name] = Environment.GetEnvironmentVariable(name);
            try
            {
                Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "00000000-0000-0000-0000-000000000000");
                Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "11111111-1111-1111-1111-111111111111");
                Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "OFFLINE-NOT-A-REAL-CREDENTIAL");
                Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", "AzureCliCredential");
                Check(IntegrationSupport.CreateCredential() is ClientSecretCredential, "SDK demos explicitly select SP, not the default chain");
                Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);
                bool rejected = false;
                try { IntegrationSupport.CreateCredential(); }
                catch (ArgumentException error)
                {
                    rejected = true;
                    Check(IntegrationSupport.ErrorCode(error) == "MISSING_ENV_AZURE_CLIENT_SECRET",
                        "missing SP secret identifies the environment variable");
                }
                Check(rejected, "missing SP secret never falls back to developer login");
            }
            finally
            {
                foreach (var entry in previous) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
            Check(IntegrationSupport.ErrorCode(new ArgumentException(
                "DO-NOT-LOG-THIS-VALUE", "unknown")) == "CONFIG_ERROR",
                "unrecognized exception messages are not disclosed");
            Check(IntegrationSupport.ErrorCode(new InvalidOperationException(
                "DO-NOT-LOG-THIS-VALUE")) == "OPERATION_ERROR",
                "unrecognized operation messages are not disclosed");
            Console.WriteLine("ALL INTEGRATION OFFLINE CHECKS PASSED (no Azure requests)");
        }

        private static void Check(bool condition, string description)
        {
            if (!condition)
            {
                Console.Error.WriteLine("OFFLINE_CHECK_FAILED: " + description);
                throw new InvalidOperationException("Offline check failed.");
            }
            Console.WriteLine("PASS: " + description);
        }
    }
}
