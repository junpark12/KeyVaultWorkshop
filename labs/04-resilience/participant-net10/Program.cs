using System;
using System.Globalization;
using System.Text.RegularExpressions;
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

namespace KeyVaultRetryParticipant
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length != 1 || (args[0] != "direct" && args[0] != "configuration"))
                    throw new ArgumentException("Specify direct or configuration.");

                Settings settings = Settings.Load();
                var observer = new RetryObservationPolicy();
                SecretClient client = CreateClient(settings, observer);

                if (args[0] == "direct")
                    await RunDirectAsync(client, settings, observer);
                else
                    await RunConfigurationAsync(client, settings, observer);

                return 0;
            }
            catch (AuthenticationFailedException)
            {
                Console.Error.WriteLine(
                    "AUTH_FAILED: SP tenant, client ID, secret VALUE/expiry를 확인하세요.");
                return 2;
            }
            catch (RequestFailedException ex)
            {
                Console.Error.WriteLine(
                    "KEY_VAULT_ERROR status=" + ex.Status + " code=" + ex.ErrorCode);
                return 2;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("CANCELLED: 작업이 취소되었습니다.");
                return 3;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("CONFIG_ERROR: " + ex.Message);
                return 1;
            }
        }

        private static SecretClient CreateClient(
            Settings settings, RetryObservationPolicy observer)
        {
            var credential = new ClientSecretCredential(
                settings.TenantId, settings.ClientId, settings.ClientSecret);
            var options = new SecretClientOptions
            {
                Retry =
                {
                    // 429이면 SDK가 Retry-After 계열 헤더를 우선 사용합니다.
                    // 헤더가 없으면 아래 지수 백오프 설정으로 기다린 뒤 재시도합니다.
                    Mode = RetryMode.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(16),
                    MaxRetries = 5,
                    NetworkTimeout = TimeSpan.FromSeconds(10)
                }
            };
            options.Diagnostics.IsLoggingContentEnabled = false;

            // SDK 내부 재시도마다 응답 상태와 Retry-After 헤더를 관찰합니다.
            options.AddPolicy(observer, HttpPipelinePosition.PerRetry);
            return new SecretClient(settings.VaultUri, credential, options);
        }

        private static async Task RunDirectAsync(
            SecretClient client, Settings settings, RetryObservationPolicy observer)
        {
            Console.WriteLine("mode=direct retry_owner=Azure_SDK value_output=disabled");
            Console.WriteLine(
                "SDK가 하나의 GetSecretAsync 내부에서 429를 자동 재시도합니다.");

            KeyVaultSecret secret = (await client.GetSecretAsync(settings.SecretName)).Value;
            Console.WriteLine(
                "result=success version=" + secret.Properties.Version +
                " value_output=disabled");
            observer.PrintSummary();
        }

        private static async Task RunConfigurationAsync(
            SecretClient client, Settings settings, RetryObservationPolicy observer)
        {
            Console.WriteLine(
                "mode=configuration reload_seconds=" +
                settings.ReloadInterval.TotalSeconds +
                " duration_seconds=" + settings.ObserveDuration.TotalSeconds);
            Console.WriteLine(
                "초기 Load가 성공한 뒤 공급자가 ReloadInterval마다 변경 여부를 조회합니다.");

            var builder = new ConfigurationBuilder();
            builder.AddAzureKeyVault(client, new AzureKeyVaultConfigurationOptions
            {
                ReloadInterval = settings.ReloadInterval,
                Manager = new SingleSecretManager(settings.SecretName)
            });

            // Build 시점의 최초 로딩은 동기적으로 수행됩니다.
            // SDK 재시도 후에도 실패하면 앱 시작 자체가 실패합니다.
            IConfigurationRoot configuration = builder.Build();
            int reloadNotifications = 0;
            using IDisposable subscription = ChangeToken.OnChange(
                configuration.GetReloadToken,
                () => Interlocked.Increment(ref reloadNotifications));

            string initialValue = configuration[settings.SecretName];
            if (string.IsNullOrEmpty(initialValue))
                throw new ArgumentException("초기 Configuration 값이 없습니다.");

            Console.WriteLine(
                "initial_load=success value_present=true value_output=disabled");
            Console.WriteLine(
                "이제 강사가 부하를 시작하면 10초 단위 polling의 429 처리를 관찰합니다.");

            DateTimeOffset finishAt = DateTimeOffset.UtcNow.Add(settings.ObserveDuration);
            while (DateTimeOffset.UtcNow < finishAt)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                string currentValue = configuration[settings.SecretName];
                Console.WriteLine(
                    "watch time=" + DateTimeOffset.Now.ToString("HH:mm:ss") +
                    " value_present=" + (!string.IsNullOrEmpty(currentValue)).ToString().ToLowerInvariant() +
                    " reload_notifications=" + Volatile.Read(ref reloadNotifications) +
                    " observed_429=" + observer.Throttles);
            }

            Console.WriteLine(
                "configuration_result=completed existing_value_remained_available=" +
                (!string.IsNullOrEmpty(configuration[settings.SecretName]))
                    .ToString().ToLowerInvariant());
            observer.PrintSummary();
        }
    }

    internal sealed class Settings
    {
        public Uri VaultUri { get; private set; }
        public string SecretName { get; private set; }
        public string TenantId { get; private set; }
        public string ClientId { get; private set; }
        public string ClientSecret { get; private set; }
        public TimeSpan ReloadInterval { get; private set; }
        public TimeSpan ObserveDuration { get; private set; }

        public static Settings Load()
        {
            string vaultUrl = Required("KEY_VAULT_URL");
            if (!Uri.TryCreate(vaultUrl, UriKind.Absolute, out Uri vaultUri) ||
                vaultUri.Scheme != Uri.UriSchemeHttps ||
                !vaultUri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
                vaultUri.Port != 443 || vaultUri.AbsolutePath != "/")
            {
                throw new ArgumentException(
                    "KEY_VAULT_URL must be https://<vault-name>.vault.azure.net/.");
            }

            string secretName = Required("KV_SECRET_NAME");
            if (!Regex.IsMatch(secretName, @"\A[a-zA-Z0-9-]{1,127}\z"))
                throw new ArgumentException("KV_SECRET_NAME 형식이 잘못되었습니다.");

            string tenantId = RequiredGuid("AZURE_TENANT_ID");
            string clientId = RequiredGuid("AZURE_CLIENT_ID");

            return new Settings
            {
                VaultUri = vaultUri,
                SecretName = secretName,
                TenantId = tenantId,
                ClientId = clientId,
                ClientSecret = Required("AZURE_CLIENT_SECRET"),
                ReloadInterval = TimeSpan.FromSeconds(
                    ReadInt("KV_RELOAD_SECONDS", 10, 1, 60)),
                ObserveDuration = TimeSpan.FromSeconds(
                    ReadInt("KV_OBSERVE_SECONDS", 60, 10, 300))
            };
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

        private static int ReadInt(
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

    internal sealed class SingleSecretManager : KeyVaultSecretManager
    {
        private readonly string secretName;

        public SingleSecretManager(string secretName)
        {
            this.secretName = secretName;
        }

        public override bool Load(SecretProperties secret) =>
            string.Equals(
                secret.Name, secretName, StringComparison.OrdinalIgnoreCase);

        public override string GetKey(KeyVaultSecret secret) => secret.Name;

        // public override bool Load(SecretProperties secret)
        // {
        //     Console.WriteLine("Loading secret: " + secret.Name);
        //     return true;
        // }

        // public override string GetKey(KeyVaultSecret secret)
        // {
        //     Console.WriteLine(
        //         $"Secret value loaded: {secret.Name}, version={secret.Properties.Version}");

        //     return secret.Name;
        // }
    }

    internal sealed class RetryObservationPolicy : HttpPipelinePolicy
    {
        private int attempts;
        private int throttles;

        public int Throttles => Volatile.Read(ref throttles);

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
            if (response == null)
                return;

            if (response.Status == 429)
                Interlocked.Increment(ref throttles);

            // 최종 인증 실패는 호출자에서 표시하고, 초기 challenge는 출력하지 않습니다.
            if (response.Status < 400 || response.Status == 401)
                return;

            string retryAfter = "not-provided";
            if (!response.Headers.TryGetValue("x-ms-retry-after-ms", out retryAfter) &&
                !response.Headers.TryGetValue("Retry-After", out retryAfter))
            {
                retryAfter = "not-provided";
            }

            Console.WriteLine(
                "pipeline_response time=" + DateTimeOffset.Now.ToString("HH:mm:ss.fff") +
                " status=" + response.Status +
                " retry_after=" + retryAfter);
        }

        public void PrintSummary()
        {
            Console.WriteLine(
                "pipeline_summary attempts=" + Volatile.Read(ref attempts) +
                " responses_429=" + Volatile.Read(ref throttles));
        }
    }
}
