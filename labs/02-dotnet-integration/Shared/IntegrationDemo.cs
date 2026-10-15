using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace KeyVaultIntegration
{
    public static class IntegrationSupport
    {
        public static void Validate(IConfiguration configuration)
        {
            string mode = configuration["KeyVault:Mode"] ?? "local";
            if (mode != "local" && mode != "configuration" && mode != "direct")
                throw new ArgumentException("Use local, configuration, or direct.", "KeyVault:Mode");

            Uri uri = null;
            if (mode == "configuration" || mode == "direct")
            {
                if (!Uri.TryCreate(configuration["KeyVault:VaultUri"], UriKind.Absolute, out uri) ||
                    uri.Scheme != "https" || uri.Port != 443 ||
                    !uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
                    uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                    throw new ArgumentException("Use https://<vault>.vault.azure.net/.", "KeyVault:VaultUri");
            }
            string name = configuration["KeyVault:SecretName"] ?? "ConnectionStrings--Default";
            if (!Regex.IsMatch(name, @"\A[a-zA-Z0-9-]{1,127}\z"))
                throw new ArgumentException("Use 1-127 letters, digits, or hyphens.", "KeyVault:SecretName");
            string version = configuration["KeyVault:Version"];
            if (string.IsNullOrEmpty(version))
                version = null;
            else if (!Regex.IsMatch(version, @"\A[0-9a-fA-F]{32}\z"))
                throw new ArgumentException("Use an empty value or a 32-character hexadecimal version ID.", "KeyVault:Version");
            if (version != null && mode != "direct")
                throw new ArgumentException("Version is only used in direct mode.", "KeyVault:Version");
            string seconds = configuration["KeyVault:ReloadSeconds"] ?? "0";
            if (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out int reload) ||
                reload < 0 || reload > 86400)
                throw new ArgumentException("Use an integer from 0 to 86400.", "KeyVault:ReloadSeconds");
            if (reload != 0 && mode != "configuration")
                throw new ArgumentException("ReloadSeconds applies only to configuration mode.", "KeyVault:ReloadSeconds");

        }

        public static ClientSecretCredential CreateCredential()
        {
            string tenantId = RequiredEnvironment("AZURE_TENANT_ID");
            string clientId = RequiredEnvironment("AZURE_CLIENT_ID");
            string clientSecret = RequiredEnvironment("AZURE_CLIENT_SECRET");
            if (!Guid.TryParse(tenantId, out _))
                throw new ArgumentException("Use a tenant GUID.", "AZURE_TENANT_ID");
            if (!Guid.TryParse(clientId, out _))
                throw new ArgumentException("Use an application client GUID.", "AZURE_CLIENT_ID");
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }

        public static TimeSpan? ReloadInterval(IConfiguration configuration)
        {
            int seconds = int.Parse(configuration["KeyVault:ReloadSeconds"] ?? "0", CultureInfo.InvariantCulture);
            return seconds == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(seconds);
        }

        public static string SecretVersion(IConfiguration configuration)
        {
            string value = configuration["KeyVault:Version"];
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static string RequiredEnvironment(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Required environment variable is missing.", name);
            return value;
        }

        public static SecretClientOptions ClientOptions()
        {
            var options = new SecretClientOptions();
            options.Retry.Mode = RetryMode.Exponential;
            options.Retry.MaxRetries = 3;
            options.Retry.Delay = TimeSpan.FromSeconds(1);
            options.Retry.MaxDelay = TimeSpan.FromSeconds(4);
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            options.Diagnostics.IsLoggingContentEnabled = false;
            options.AddPolicy(new SafeFailurePolicy(), HttpPipelinePosition.PerCall);
            return options;
        }

        public static bool IsExpected(Exception error) =>
            error is ArgumentException || error is InvalidOperationException ||
            error is AuthenticationFailedException || error is RequestFailedException ||
            error is HttpRequestException || error is OperationCanceledException;

        public static string ErrorCode(Exception error)
        {
            if (error is OperationCanceledException) return "TIMEOUT";
            if (error is AuthenticationFailedException) return "AUTH_FAILED";
            if (error is RequestFailedException request) return "KEY_VAULT_HTTP_" + request.Status;
            if (error is HttpRequestException) return "NETWORK_ERROR";
            if (error is ArgumentException argument)
            {
                switch (argument.ParamName)
                {
                    case "AZURE_TENANT_ID":
                    case "AZURE_CLIENT_ID":
                    case "AZURE_CLIENT_SECRET":
                    case "KeyVault:Mode":
                    case "KeyVault:VaultUri":
                    case "KeyVault:SecretName":
                    case "KeyVault:Version":
                    case "KeyVault:ReloadSeconds":
                        string prefix = argument.Message.StartsWith(
                            "Required environment variable is missing.", StringComparison.Ordinal)
                            ? "MISSING_ENV_" : "INVALID_SETTING_";
                        return prefix + argument.ParamName.Replace(":", "__");
                }
                return "CONFIG_ERROR";
            }
            if (error is InvalidOperationException)
            {
                switch (error.Message)
                {
                    case "ConnectionStrings:Default is missing.":
                        return "MISSING_SETTING_ConnectionStrings__Default";
                    case "Secret metadata disallows use.":
                        return "SECRET_NOT_USABLE";
                }
            }
            return "OPERATION_ERROR";
        }

        public static async Task<KeyVaultSecret> ReadSecretAsync(
            SecretClient client, string name, string version, CancellationToken cancellationToken)
        {
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                budget.CancelAfter(TimeSpan.FromSeconds(30));
                KeyVaultSecret secret = (await client.GetSecretAsync(
                    name, version, budget.Token).ConfigureAwait(false)).Value;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (secret.Properties.Enabled == false ||
                    (secret.Properties.ExpiresOn.HasValue && now >= secret.Properties.ExpiresOn.Value) ||
                    (secret.Properties.NotBefore.HasValue && now < secret.Properties.NotBefore.Value))
                    throw new InvalidOperationException("Secret metadata disallows use.");
                EnsureValuePresent(secret.Value);
                return secret;
            }
        }

        public static void EnsureValuePresent(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("ConnectionStrings:Default is missing.");
        }

    }

    internal sealed class SafeFailurePolicy : HttpPipelinePolicy
    {
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            try { ProcessNext(message, pipeline); Report(message); }
            catch (Exception error) when (IntegrationSupport.IsExpected(error))
            {
                Console.Error.WriteLine("KEY_VAULT_REQUEST_FAILED " + IntegrationSupport.ErrorCode(error));
                throw;
            }
        }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            try { await ProcessNextAsync(message, pipeline).ConfigureAwait(false); Report(message); }
            catch (Exception error) when (IntegrationSupport.IsExpected(error))
            {
                Console.Error.WriteLine("KEY_VAULT_REQUEST_FAILED " + IntegrationSupport.ErrorCode(error));
                throw;
            }
        }

        private static void Report(HttpMessage message)
        {
            if (message.Response.Status >= 400)
                Console.Error.WriteLine("KEY_VAULT_HTTP_ERROR status=" + message.Response.Status);
        }
    }
}
