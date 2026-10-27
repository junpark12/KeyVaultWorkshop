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

            // 같은 Secret의 요청을 직렬화하고, 대기한 요청은 잠금 획득 후 갱신된 캐시를 확인합니다.
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DateTimeOffset now = utcNow();
                // 캐시가 있고 갱신 시각 전이면 Vault 호출 없이 재사용합니다.
                if (entry.Secret != null && now < entry.RefreshAfter)
                {
                    entry.Secret.EnsureUsable(now);
                    return new SecretRead(entry.Secret, true);
                }

                // 캐시가 없거나 TTL이 지났을 때, 이 요청에서 Vault를 조회합니다.
                SecretSnapshot secret = await source.GetAsync(name, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                now = utcNow();
                secret.EnsureUsable(now);
                // 다음 갱신 시각은 TTL과 Secret 만료 시각 중 더 빠른 시각으로 제한합니다.
                DateTimeOffset refreshAfter = now.Add(ttl);
                if (secret.ExpiresOn.HasValue && secret.ExpiresOn.Value < refreshAfter)
                    refreshAfter = secret.ExpiresOn.Value;
                entry.Secret = secret;
                entry.RefreshAfter = refreshAfter;
                return new SecretRead(secret, false);
            }
            finally
            {
                // 조회 실패나 취소가 발생해도 잠금을 해제하여 후속 요청이 진행되게 합니다.
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
