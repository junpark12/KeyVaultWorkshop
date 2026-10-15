using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;

namespace KeyVaultWorkshop
{
    internal static class OfflineDemos
    {
        private sealed class Clock
        {
            public DateTimeOffset Now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }

        private sealed class FakeSource : ISecretSource
        {
            private int calls;
            public readonly Dictionary<string, SecretSnapshot> Secrets =
                new Dictionary<string, SecretSnapshot>(StringComparer.OrdinalIgnoreCase);
            public int? FailureStatus;
            public int DelayMilliseconds;
            public int Calls => Volatile.Read(ref calls);

            public FakeSource(IEnumerable<string> names)
            {
                foreach (string name in names)
                    Secrets.Add(name, new SecretSnapshot(name, "NOT-A-REAL-CREDENTIAL", "v1"));
            }

            public async Task<SecretSnapshot> GetAsync(string name, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref calls);
                cancellationToken.ThrowIfCancellationRequested();
                if (DelayMilliseconds > 0)
                    await Task.Delay(DelayMilliseconds, cancellationToken);
                if (FailureStatus.HasValue)
                    throw new RequestFailedException(FailureStatus.Value, "Simulated offline failure.");
                return Secrets[name];
            }
        }

        private sealed class SequenceHandler : HttpMessageHandler
        {
            private readonly int[] statuses;
            public readonly List<int> Seen = new List<int>();

            public SequenceHandler(int[] statuses) { this.statuses = statuses; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int status = statuses[Math.Min(Seen.Count, statuses.Length - 1)];
                Seen.Add(status);
                var response = new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = new StringContent("{}"),
                    RequestMessage = request
                };
                return Task.FromResult(response);
            }
        }

        public static async Task RetryAsync()
        {
            await RetryCaseAsync(new[] { 429, 429, 200 }, 3, 200);
            await RetryCaseAsync(new[] { 503, 200 }, 2, 200);
            await RetryCaseAsync(new[] { 403 }, 1, 403);
            await RetryCaseAsync(new[] { 404 }, 1, 404);
            await RetryCaseAsync(new[] { 429 }, 4, 429);
        }

        private static async Task RetryCaseAsync(int[] statuses, int expectedAttempts, int expectedStatus)
        {
            using (var handler = new SequenceHandler(statuses))
            using (var http = new HttpClient(handler))
            {
                var options = AzureClientFactory.CreateOptions();
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.MaxDelay = TimeSpan.FromMilliseconds(4);
                options.Transport = new HttpClientTransport(http);
                HttpPipeline pipeline = HttpPipelineBuilder.Build(options);
                using (HttpMessage message = pipeline.CreateMessage())
                {
                    message.Request.Method = RequestMethod.Get;
                    message.Request.Uri.Reset(new Uri("https://offline.invalid/secrets/demo"));
                    await pipeline.SendAsync(message, CancellationToken.None);
                    Check(handler.Seen.Count == expectedAttempts, "SDK retry attempt limit");
                    Check(message.Response.Status == expectedStatus, "SDK retry final status");
                    Console.WriteLine("OFFLINE sdk_retry statuses=" + string.Join(",", handler.Seen) +
                        " attempts=" + handler.Seen.Count + " final=" + message.Response.Status);
                }
            }
        }

        public static async Task SelfTestAsync()
        {
            var clock = new Clock();
            var source = new FakeSource(new[] { "demo" }) { DelayMilliseconds = 10 };
            using (var cache = new CachedSecretReader(source, new[] { "demo" }, TimeSpan.FromSeconds(30), () => clock.Now))
            {
                SecretRead[] reads = await Task.WhenAll(Enumerable.Range(0, 20)
                    .Select(_ => cache.GetAsync("demo", CancellationToken.None)));
                Check(source.Calls == 1 && reads.Count(r => !r.FromCache) == 1, "single-flight");
                Check((await cache.GetAsync("DEMO", CancellationToken.None)).FromCache, "case-insensitive names");
                source.Secrets["demo"] = new SecretSnapshot("demo", "NOT-A-REAL-CREDENTIAL-V2", "v2");
                clock.Now = clock.Now.AddSeconds(29);
                Check((await cache.GetAsync("demo", CancellationToken.None)).Secret.Version == "v1", "TTL not elapsed");
                clock.Now = clock.Now.AddSeconds(1);
                Check((await cache.GetAsync("demo", CancellationToken.None)).Secret.Version == "v2", "rotation at TTL");
                Check(source.Calls == 2, "TTL refresh count");
                Console.WriteLine("PASS single-flight, case-insensitive names, TTL boundary, rotation");

                int before = source.Calls;
                bool rejected = false;
                try { await cache.GetAsync("not-allowed", CancellationToken.None); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected && source.Calls == before, "allowlist rejects before source call");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    bool observed = false;
                    try { await cache.GetAsync("demo", cancelled.Token); }
                    catch (OperationCanceledException) { observed = true; }
                    Check(observed && source.Calls == before, "cancelled request");
                }
                Console.WriteLine("PASS allowlist and cancellation");

                clock.Now = clock.Now.AddSeconds(30);
                source.FailureStatus = 503;
                bool failedClosed = false;
                try { await cache.GetAsync("demo", CancellationToken.None); }
                catch (RequestFailedException ex) when (ex.Status == 503) { failedClosed = true; }
                Check(failedClosed, "expired cache must not mask a source outage");
                source.FailureStatus = null;
                Check(!(await cache.GetAsync("demo", CancellationToken.None)).FromCache, "recovery after failure");
                Console.WriteLine("PASS expired-cache failure and recovery (no stale fallback)");
            }

            var expiring = new FakeSource(new[] { "demo" });
            expiring.Secrets["demo"] = new SecretSnapshot("demo", "NOT-A-REAL-CREDENTIAL", "v1", clock.Now.AddSeconds(5));
            using (var cache = new CachedSecretReader(expiring, new[] { "demo" }, TimeSpan.FromSeconds(30), () => clock.Now))
            {
                await cache.GetAsync("demo", CancellationToken.None);
                clock.Now = clock.Now.AddSeconds(5);
                bool expired = false;
                try { await cache.GetAsync("demo", CancellationToken.None); }
                catch (InvalidOperationException) { expired = true; }
                Check(expired && expiring.Calls == 2, "expiry bounds cache lifetime");
            }
            foreach (SecretSnapshot invalid in new[]
            {
                new SecretSnapshot("demo", "NOT-A-REAL-CREDENTIAL", "v1", enabled: false),
                new SecretSnapshot("demo", "NOT-A-REAL-CREDENTIAL", "v1", notBefore: clock.Now.AddMinutes(1))
            })
            {
                bool rejected = false;
                try { invalid.EnsureUsable(clock.Now); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "disabled/not-yet-valid secrets rejected");
            }
            Console.WriteLine("PASS expiration, disabled, not-before guards");

            var slow = new FakeSource(new[] { "demo" }) { DelayMilliseconds = 5000 };
            using (var cache = new CachedSecretReader(slow, new[] { "demo" }, TimeSpan.FromSeconds(30)))
            using (var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
            {
                bool timedOut = false;
                try { await cache.GetAsync("demo", budget.Token); }
                catch (OperationCanceledException) { timedOut = true; }
                Check(timedOut, "in-flight request budget");
                slow.DelayMilliseconds = 0;
                Check(!(await cache.GetAsync("demo", CancellationToken.None)).FromCache, "timeout does not poison cache lock");
            }
            Console.WriteLine("PASS in-flight timeout and subsequent recovery");
            await RetryAsync();
            Console.WriteLine("ALL OFFLINE CHECKS PASSED");
        }

        private static void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("CHECK FAILED: " + name);
        }
    }
}
