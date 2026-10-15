using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KeyVaultWorkshop
{
    internal sealed class SecretRead
    {
        public SecretRead(SecretSnapshot secret, bool fromCache)
        {
            Secret = secret;
            FromCache = fromCache;
        }

        public SecretSnapshot Secret { get; }
        public bool FromCache { get; }
    }

    internal sealed class CachedSecretReader : IDisposable
    {
        private sealed class Entry
        {
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            public SecretSnapshot Secret;
            public DateTimeOffset RefreshAfter;
        }

        private readonly ISecretSource source;
        private readonly TimeSpan ttl;
        private readonly Func<DateTimeOffset> utcNow;
        private readonly Dictionary<string, Entry> entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public CachedSecretReader(ISecretSource source, IEnumerable<string> allowedNames,
            TimeSpan ttl, Func<DateTimeOffset> utcNow = null)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            if (ttl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
            this.ttl = ttl;
            this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            foreach (string name in allowedNames)
            {
                WorkshopSettings.ValidateSecretName(name);
                if (!entries.ContainsKey(name))
                    entries.Add(name, new Entry());
            }
            if (entries.Count == 0)
                throw new ArgumentException("At least one allowed secret name is required.");
        }

        public async Task<SecretRead> GetAsync(string name, CancellationToken cancellationToken)
        {
            if (!entries.TryGetValue(name, out Entry entry))
                throw new ArgumentException("Secret name is not in the application's allowlist.");

            // One refresh per name; waiters reuse the successful result.
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DateTimeOffset now = utcNow();
                if (entry.Secret != null && now < entry.RefreshAfter)
                {
                    entry.Secret.EnsureUsable(now);
                    return new SecretRead(entry.Secret, true);
                }

                SecretSnapshot secret = await source.GetAsync(name, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                now = utcNow();
                secret.EnsureUsable(now);
                DateTimeOffset refreshAfter = now.Add(ttl);
                if (secret.ExpiresOn.HasValue && secret.ExpiresOn.Value < refreshAfter)
                    refreshAfter = secret.ExpiresOn.Value;
                entry.Secret = secret;
                entry.RefreshAfter = refreshAfter;
                return new SecretRead(secret, false);
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        public void Dispose()
        {
            foreach (Entry entry in entries.Values)
            {
                entry.Secret = null;
                entry.Gate.Dispose();
            }
        }
    }
}
