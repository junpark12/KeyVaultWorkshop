using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace KeyVaultResilience
{
    internal sealed class LoadSettings
    {
        public Uri VaultUri { get; private set; }
        public string SecretName { get; private set; }
        public int TotalRequests { get; private set; }
        public int Concurrency { get; private set; }

        public static LoadSettings Load(bool probeOnly)
        {
            string vaultUrl = Required("KEY_VAULT_URL");
            if (!Uri.TryCreate(vaultUrl, UriKind.Absolute, out Uri vaultUri) ||
                vaultUri.Scheme != Uri.UriSchemeHttps ||
                !vaultUri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
                vaultUri.Port != 443 || vaultUri.AbsolutePath != "/" ||
                vaultUri.UserInfo.Length != 0 || vaultUri.Query.Length != 0 ||
                vaultUri.Fragment.Length != 0)
            {
                throw new ArgumentException(
                    "KEY_VAULT_URL must be https://<vault-name>.vault.azure.net/.");
            }

            string secretName = Required("KV_SECRET_NAME");
            if (!Regex.IsMatch(secretName, @"\A[a-zA-Z0-9-]{1,127}\z"))
                throw new ArgumentException(
                    "KV_SECRET_NAME must contain 1-127 letters, digits, or hyphens.");

            return new LoadSettings
            {
                VaultUri = vaultUri,
                SecretName = secretName,
                TotalRequests = probeOnly ? 1 :
                    ReadBoundedInt("KV_LOAD_TOTAL_REQUESTS", 20000, 1, 20000),
                Concurrency = probeOnly ? 1 :
                    ReadBoundedInt("KV_LOAD_CONCURRENCY", 40, 1, 40)
            };
        }

        private static string Required(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing environment variable: " + name);
            return value;
        }

        private static int ReadBoundedInt(
            string name, int defaultValue, int minimum, int maximum)
        {
            string text = Environment.GetEnvironmentVariable(name);
            if (text == null)
                return defaultValue;
            if (!int.TryParse(
                    text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
                value < minimum || value > maximum)
            {
                throw new ArgumentException(
                    name + " must be between " + minimum + " and " + maximum + ".");
            }
            return value;
        }
    }

    internal sealed class RetryObservationPolicy : HttpPipelinePolicy
    {
        private int attempts;
        private int throttles;
        private readonly object sync = new object();
        private string lastRetryAfter = "not-provided";

        public int Attempts => Volatile.Read(ref attempts);
        public int Throttles => Volatile.Read(ref throttles);

        public string LastRetryAfter
        {
            get
            {
                lock (sync)
                    return lastRetryAfter;
            }
        }

        public override void Process(
            HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Interlocked.Increment(ref attempts);
            ProcessNext(message, pipeline);
            Observe(message.Response);
        }

        public override async ValueTask ProcessAsync(
            HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Interlocked.Increment(ref attempts);
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
            Observe(message.Response);
        }

        private void Observe(Response response)
        {
            if (response == null || response.Status != 429)
                return;

            Interlocked.Increment(ref throttles);
            string retryAfter;
            if (!response.Headers.TryGetValue("x-ms-retry-after-ms", out retryAfter) &&
                !response.Headers.TryGetValue("Retry-After", out retryAfter))
            {
                retryAfter = "not-provided";
            }

            lock (sync)
                lastRetryAfter = retryAfter;
        }
    }

    internal sealed class LoadResult
    {
        public int Scheduled;
        public int Succeeded;
        public int Failed;
        public int Cancelled;
    }

    internal static class KeyVaultLoadRunner
    {
        public static async Task<int> RunAsync(bool probeOnly)
        {
            LoadSettings settings = LoadSettings.Load(probeOnly);
            var retryObserver = new RetryObservationPolicy();
            SecretClient client = CreateClient(settings, retryObserver);
            var result = new LoadResult();
            using (var stop = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancelHandler = (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    stop.Cancel();
                };
                Console.CancelKeyPress += cancelHandler;
                try
                {
                    Console.WriteLine(
                        "mode=" + (probeOnly ? "probe" : "bounded-load") +
                        " total_limit=" + settings.TotalRequests +
                        " concurrency_limit=" + settings.Concurrency);
                    Console.WriteLine(
                        "vault_host=" + settings.VaultUri.Host +
                        " secret_name=" + settings.SecretName +
                        " value_output=disabled");

                    var stopwatch = Stopwatch.StartNew();
                    if (probeOnly)
                    {
                        await ReadOnceAsync(client, settings.SecretName, result, stop.Token);
                    }
                    else
                    {
                        await RunWorkersAsync(
                            client, settings, retryObserver, result, stop);
                    }
                    stopwatch.Stop();

                    Console.WriteLine(
                        "summary logical_scheduled=" + result.Scheduled +
                        " succeeded=" + result.Succeeded +
                        " failed=" + result.Failed +
                        " cancelled=" + result.Cancelled +
                        " pipeline_attempts=" + retryObserver.Attempts +
                        " responses_429=" + retryObserver.Throttles +
                        " last_retry_after=" + retryObserver.LastRetryAfter +
                        " elapsed_ms=" + stopwatch.ElapsedMilliseconds);

                    if (retryObserver.Throttles > 0)
                    {
                        Console.WriteLine(
                            "THROTTLE_OBSERVED: SDK retry handled throttled responses; " +
                            "the bounded instructor load continued to its request limit.");
                    }
                    else if (!probeOnly)
                    {
                        Console.WriteLine(
                            "NO_429_OBSERVED: The bounded run completed without throttling. " +
                            "Do not raise the configured safety limits in a shared Vault.");
                    }

                    return result.Failed == 0 ? 0 : 2;
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                }
            }
        }

        private static SecretClient CreateClient(
            LoadSettings settings, RetryObservationPolicy retryObserver)
        {
            string tenantId = RequiredGuid("AZURE_TENANT_ID");
            string clientId = RequiredGuid("AZURE_CLIENT_ID");
            string clientSecret = Required("AZURE_CLIENT_SECRET");
            var credential =
                new ClientSecretCredential(tenantId, clientId, clientSecret);
            var options = new SecretClientOptions
            {
                Retry =
                {
                    Mode = RetryMode.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(8),
                    MaxRetries = 3,
                    NetworkTimeout = TimeSpan.FromSeconds(10)
                }
            };
            options.Diagnostics.IsLoggingContentEnabled = false;
            options.AddPolicy(retryObserver, HttpPipelinePosition.PerRetry);
            return new SecretClient(settings.VaultUri, credential, options);
        }

        private static async Task RunWorkersAsync(
            SecretClient client,
            LoadSettings settings,
            RetryObservationPolicy retryObserver,
            LoadResult result,
            CancellationTokenSource stop)
        {
            int next = 0;
            var workers = new List<Task>();
            for (int i = 0; i < settings.Concurrency; i++)
            {
                workers.Add(Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        int requestNumber = Interlocked.Increment(ref next);
                        if (requestNumber > settings.TotalRequests)
                            break;

                        try
                        {
                            await ReadOnceAsync(
                                client, settings.SecretName, result, stop.Token);
                        }
                        catch (RequestFailedException ex)
                        {
                            Interlocked.Increment(ref result.Failed);
                            Console.Error.WriteLine(
                                "REQUEST_FAILED status=" + ex.Status +
                                " code=" + ex.ErrorCode +
                                " request=" + requestNumber);
                            stop.Cancel();
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Increment(ref result.Cancelled);
                            break;
                        }
                    }
                }));
            }
            await Task.WhenAll(workers);
        }

        private static async Task ReadOnceAsync(
            SecretClient client,
            string secretName,
            LoadResult result,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref result.Scheduled);
            KeyVaultSecret secret = (await client.GetSecretAsync(
                secretName, cancellationToken: cancellationToken)
                .ConfigureAwait(false)).Value;
            if (string.IsNullOrEmpty(secret.Properties.Version))
                throw new InvalidOperationException(
                    "Key Vault returned a Secret without a version.");
            Interlocked.Increment(ref result.Succeeded);
        }

        private static string RequiredGuid(string name)
        {
            string value = Required(name);
            if (!Guid.TryParse(value, out _))
                throw new ArgumentException(name + " must be a GUID.");
            return value;
        }

        private static string Required(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing environment variable: " + name);
            return value;
        }
    }
}
