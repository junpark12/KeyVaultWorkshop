using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace KeyVaultWorkshop
{
    internal sealed class SecretSnapshot
    {
        public SecretSnapshot(string name, string value, string version,
            DateTimeOffset? expiresOn = null, DateTimeOffset? notBefore = null, bool enabled = true)
        {
            Name = name;
            Value = value;
            Version = version;
            ExpiresOn = expiresOn;
            NotBefore = notBefore;
            Enabled = enabled;
        }

        public string Name { get; }
        public string Value { get; }
        public string Version { get; }
        public DateTimeOffset? ExpiresOn { get; }
        public DateTimeOffset? NotBefore { get; }
        public bool Enabled { get; }

        public void EnsureUsable(DateTimeOffset now)
        {
            if (!Enabled || (ExpiresOn.HasValue && now >= ExpiresOn.Value) ||
                (NotBefore.HasValue && now < NotBefore.Value))
                throw new InvalidOperationException("Secret is disabled, expired, or not yet valid; refusing to use it.");
        }
    }

    internal interface ISecretSource
    {
        Task<SecretSnapshot> GetAsync(string name, CancellationToken cancellationToken);
    }

    internal sealed class AzureSecretSource : ISecretSource
    {
        private readonly SecretClient client;
        private int calls;

        public AzureSecretSource(SecretClient client) { this.client = client; }
        public int Calls => Volatile.Read(ref calls);

        public async Task<SecretSnapshot> GetAsync(string name, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            KeyVaultSecret secret = (await client.GetSecretAsync(
                name, cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            return new SecretSnapshot(secret.Name, secret.Value, secret.Properties.Version,
                secret.Properties.ExpiresOn, secret.Properties.NotBefore, secret.Properties.Enabled != false);
        }
    }

    internal static class AzureClientFactory
    {
        public static SecretClient Create(WorkshopSettings settings)
        {
            var credential = FrameworkLabSupport.ServicePrincipalCredential();
            return new SecretClient(settings.VaultUri, credential, CreateOptions());
        }

        public static SecretClientOptions CreateOptions()
        {
            var options = new SecretClientOptions();
            options.Retry.Mode = RetryMode.Exponential;
            options.Retry.Delay = TimeSpan.FromSeconds(1);
            options.Retry.MaxDelay = TimeSpan.FromSeconds(4);
            options.Retry.MaxRetries = 3;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            options.Diagnostics.IsLoggingContentEnabled = false;
            return options;
        }
    }
}
