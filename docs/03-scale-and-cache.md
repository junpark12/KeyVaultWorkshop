# 03. 수천 개의 Secrets를 효율적으로 사용하는 방법

> 기반 자료: [서비스 한도](https://learn.microsoft.com/azure/key-vault/general/service-limits), [Secrets 성능·회전](https://learn.microsoft.com/azure/key-vault/secrets/secure-secrets)

## 0. 핵심 결론

일반적인 앱 하나가 서로 다른 Secret 수천 개를 직접 사용하는 경우는 드뭅니다. 한 Vault에 Secret이
수천 개라면 여러 앱·팀이 같은 Vault를 공유하고 있을 가능성이 높습니다.

따라서 첫 번째 질문은 “캐시를 어떻게 구현할까?”가 아니라 다음입니다.

> 여러 앱이 하나의 Vault를 공유해도 되는가?

먼저 Vault의 권한·장애·트래픽 경계를 정하고, 그다음 필요한 Secret만 조회하며
TTL·Single-flight·Jitter로 반복·동시 호출을 최적화합니다.

## 1. 앱별 Vault와 공유 Vault

환경별 dev/stg/prod 분리는 기본 전제입니다. 같은 환경 안에서 Vault를 앱별로 나눌지 여러 앱이
공유할지 추가로 결정합니다.

### 1.1 앱별 Vault

```text
orders-prod-kv
payment-prod-kv
shipping-prod-kv
```

| 장점 | 단점 |
|---|---|
| 앱별 최소 권한 적용이 단순 | Vault·Private Endpoint·진단 설정 수 증가 |
| 한 앱의 429·장애가 다른 앱에 미치는 영향 감소 | IaC와 운영 관리 대상 증가 |
| 소유 팀·회전·삭제 책임이 명확 | 공통 Secret이 있다면 중복 관리 가능 |
| 앱별 트래픽·감사 분석이 쉬움 | 중앙 관리 체계 필요 |

### 1.2 공유 Vault

```text
shared-prod-kv
├── orders-db-password
├── payment-api-key
└── shipping-token
```

| 장점 | 단점 |
|---|---|
| Vault와 네트워크 리소스 수 감소 | 한 앱의 호출 폭주가 다른 앱의 429를 유발 |
| 중앙 정책·진단 설정 적용이 쉬움 | Vault 장애·네트워크 변경의 영향 범위 확대 |
| 공통 Secret을 한 곳에서 관리 가능 | 앱별 최소 권한과 소유 책임이 복잡 |
| 플랫폼 팀의 일괄 관리가 쉬움 | 삭제·회전·배포 수명주기가 결합 |

여러 앱이 실제로 같은 DB/API 자격 증명을 공유하는 것은 가급적 피합니다. 가능하면 대상 시스템에서
앱별 자격 증명이나 Entra 인증을 사용하여 한 앱의 침해가 다른 앱 권한으로 확산되지 않게 합니다.

### 1.3 공유 Vault의 권한 모델

| 권한 모델 | 개별 Secret 범위 권한 | 공유 Vault에서의 의미 |
|---|---|---|
| Azure RBAC | 가능 | 앱별로 특정 Secret scope에 Secrets User 할당 가능 |
| Access Policy | 불가능 | Secret Get/List 권한이 Vault 안의 모든 Secret에 적용 |

Access Policy에서 앱에 Secret Get을 허용하면 앱은 이름을 알고 있는 같은 Vault의 다른 Secret도
조회할 수 있습니다. List까지 허용하면 다른 앱 Secret의 이름도 열거할 수 있습니다.

Azure RBAC은 개별 Secret scope를 사용할 수 있지만 다음 운영 비용이 생깁니다.

- Secret이 추가될 때마다 역할 할당 관리
- 앱과 Secret 관계를 IaC로 유지
- 역할 누락·과다 권한 점검
- 많은 개별 역할 할당에 대한 감사

개별 Secret RBAC을 사용할 때 Configuration Provider는 주의해야 합니다. Provider는 값을 가져오기 전에
Vault의 Secret 속성 목록을 조회하므로 Vault 수준의 metadata List 권한이 추가로 필요합니다.
값은 특정 Secret에만 허용하더라도 다른 Secret의 이름·메타데이터가 노출될 수 있습니다.

엄격한 개별 Secret 권한이 필요하면 `SecretClient`로 허용된 이름을 직접 조회하는 방식이 더 단순할 수 있습니다.

### 1.4 Vault 경계 결정 기준

다음 질문에 모두 “예”라면 공유 Vault를 검토할 수 있습니다.

```text
같은 소유 팀인가?
같은 권한 경계인가?
같은 네트워크 경계인가?
같이 장애가 발생해도 되는가?
같은 트래픽 한도를 공유해도 되는가?
각 Secret을 사용하는 앱과 회전·삭제 영향 범위를 추적할 수 있는가?
```

일부만 같다면 모든 앱을 한 Vault에 모으기보다 업무·보안 도메인별 Vault를 사용하는 절충안이 적합합니다.

## 2. 공유 Vault의 전체 로딩이 만드는 부하

공유 Vault에 Secret이 5,000개 있고, 배포 과정에서 앱 인스턴스 100개가 동시에 시작한다고 가정합니다.

| 조회 방식 | 시작 시 논리적 값 조회 수 | 영향 |
|---|---:|---|
| 각 인스턴스가 전체 5,000개 로드 | 최대 500,000 | 시작 지연·429·다른 앱 영향 |
| 각 인스턴스가 필요한 10개만 조회 | 1,000 | 순간 집중은 여전히 가능 |
| 필요한 이름을 지연 조회하고 TTL 캐시 | 실제 사용된 이름 수에 비례 | 첫 요청 지연과 캐시 정책 필요 |

### 2.1 공유 대형 Vault에서 피해야 할 전체 열거·전체 로딩 패턴

`AddAzureKeyVault()` 호출 자체가 잘못된 것은 아닙니다. 앱 전용의 작은 Vault에서는 일반적이고 편리한
사용 방식입니다.

```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri(vaultUri),
    credential);
```

Configuration Provider가 만드는 요청 구조를 C# 코드로 풀어 쓰면 다음과 같습니다. 실제 운영
애플리케이션에서 이 코드를 직접 작성하는 경우는 드물지만, **Secret 목록 조회 후 각 Secret 값을 개별
조회하는 구조**를 명시적으로 보여주기 위한 예시입니다.

```csharp
var client = new SecretClient(new Uri(vaultUri), credential);
var configuration = new Dictionary<string, string>();

// List: Vault에 있는 Secret의 이름과 속성을 열거합니다.
await foreach (SecretProperties properties
    in client.GetPropertiesOfSecretsAsync())
{
    if (properties.Enabled == false)
    {
        continue;
    }

    // Get: 목록에서 확인한 각 이름의 현재 Secret 값을 개별 조회합니다.
    KeyVaultSecret secret =
        (await client.GetSecretAsync(properties.Name)).Value;

    configuration[properties.Name] = secret.Value;
}
```

여러 앱이 공유하고 Secret이 수천 개인 Vault에서 별도의 필터 없이 사용하면 각 앱 인스턴스가 이 목록
기반 로딩을 반복합니다. 인스턴스 100개가 동시에 시작하면 동일한 목록 조회와 값 조회도 반복됩니다.
앱에서 사용하지 않는 다른 앱의 Secret까지 메모리에 적재할 수 있으므로 권한·노출·호출량 모두 문제가 됩니다.

### 2.2 필요한 이름을 직접 조회

엄격한 앱별 권한과 적은 수의 Secret을 사용할 때는 이름을 명시하여 직접 조회할 수 있습니다.

```csharp
var client = new SecretClient(new Uri(vaultUri), credential);

KeyVaultSecret dbSecret =
    (await client.GetSecretAsync("orders-db-password")).Value;
```

이 방식은 필요한 Secret만 Get하지만, 반복적인 업무 요청마다 호출하지 않도록 앱 캐시 정책이 필요합니다.

### 2.3 Configuration Provider에서 값 로딩 대상 제한

기존 `IConfiguration` 접근 방식을 유지해야 한다면 `KeyVaultSecretManager`로 값 로딩 대상을 제한할 수 있습니다.

```csharp
internal sealed class OrdersSecretManager : KeyVaultSecretManager
{
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "orders-db-password",
            "orders-api-key"
        };

    public override bool Load(SecretProperties secret) =>
        Allowed.Contains(secret.Name);
}

builder.Configuration.AddAzureKeyVault(
    secretClient,
    new AzureKeyVaultConfigurationOptions
    {
        Manager = new OrdersSecretManager()
    });
```

이 필터는 선택한 Secret의 **값 Get을 줄이는 앱 로직**입니다. Provider의 Secret 속성 목록 조회 자체를
없애지 않으며 RBAC·Access Policy를 대신하는 보안 경계도 아닙니다.

## 3. Vault 경계를 정한 뒤 호출 최적화

### 3.1 Configuration reload와 앱 캐시를 중복하지 않기

Configuration Provider는 조회한 값을 `IConfiguration` 내부 메모리에 보관합니다. 애플리케이션이
`IConfiguration`에서 값을 읽을 때마다 Key Vault를 호출하는 것이 아닙니다. `ReloadInterval`을 30분으로
설정하면 Provider가 약 30분 간격으로 Key Vault를 다시 확인합니다.

```text
Key Vault
    │  ReloadInterval = 30분
    ▼
IConfiguration 내부 메모리
    │  업무 요청은 이 값을 읽음
    ▼
애플리케이션
```

따라서 이 값 위에 별도의 1시간 TTL 캐시를 추가해도 Provider의 Key Vault 호출은 줄지 않습니다. 오히려
Provider가 새 값을 가져온 뒤에도 앱 캐시가 이전 값을 유지할 수 있습니다. 타이밍에 따라 Key Vault 변경
후 Provider 확인까지 최대 30분, 앱 캐시 만료까지 최대 1시간이 이어져 반영이 약 90분 늦어질 수 있습니다.

| 갱신 방식 | Key Vault 호출 제어 | 권장 앱 캐시 |
|---|---|---|
| Configuration Provider | `ReloadInterval` | Secret 값의 별도 TTL 캐시는 보통 불필요 |
| 직접 `SecretClient` 조회 | 앱이 구현한 TTL | 메모리 캐시 사용 |
| Provider 값의 가공 결과 캐시 | `ReloadInterval` | Configuration reload 시 즉시 무효화 |

Configuration Provider를 선택했다면 `ReloadInterval`을 갱신 기준으로 사용하고 앱은 Provider의 메모리
값을 읽습니다. Vault 호출을 더 줄이려면 그 위에 긴 TTL을 중복하는 대신 `ReloadInterval` 자체를 늘립니다.
반대로 직접 조회 방식을 선택했다면 앱의 TTL·Single-flight·Jitter가 갱신과 호출량을 제어합니다.

### 3.2 TTL — 앱이 정한 시간 동안 캐시 사용

여기서 TTL은 Key Vault Secret에 설정하는 값이 아니라 **애플리케이션 메모리 캐시 정책**입니다.

```csharp
DateTimeOffset now = DateTimeOffset.UtcNow;

if (entry.Secret != null && now < entry.RefreshAfter)
{
    // TTL 안이면 Key Vault를 다시 호출하지 않습니다.
    return entry.Secret;
}

KeyVaultSecret secret =
    (await secretClient.GetSecretAsync(secretName)).Value;

entry.Secret = secret;
entry.RefreshAfter = now.Add(TimeSpan.FromMinutes(5));
return secret;
```

Secret의 `ExpiresOn`이 앱 TTL보다 빠르면 더 빠른 시점을 사용합니다.

```csharp
DateTimeOffset refreshAfter = now.Add(ttl);

if (secret.Properties.ExpiresOn.HasValue &&
    secret.Properties.ExpiresOn.Value < refreshAfter)
{
    refreshAfter = secret.Properties.ExpiresOn.Value;
}
```

TTL이 길면 Vault 호출은 줄지만 변경 반영이 늦어지고, 짧으면 최신성은 좋아지지만 호출량과 Vault 장애
의존성이 증가합니다.

### 3.3 Single-flight — 같은 Secret의 동시 조회를 한 번으로 통합

함수에 **Secret 이름(`secretName`)을 파라미터로 전달**하고, `ConcurrentDictionary`에서 해당 이름의
Gate와 캐시 항목을 가져옵니다. 같은 이름은 하나의 Entry를 공유하고, 다른 이름은 서로 다른 Gate를
사용하므로 병렬 조회할 수 있습니다. 여기서 이름은 암호화용 Key가 아니라 Secret의 이름입니다.

캐시 확인을 **Gate 안에서 다시 수행**하는 것이 핵심입니다. 아래는 직접 조회 방식에서 사용할 수 있는 예입니다.

```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Azure.Security.KeyVault.Secrets;

public sealed class CachedSecretReader
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public KeyVaultSecret? Secret;
        public DateTimeOffset RefreshAfter;
    }

    private readonly ConcurrentDictionary<string, Entry> entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SecretClient secretClient;
    private readonly TimeSpan ttl;

    public CachedSecretReader(SecretClient secretClient, TimeSpan ttl)
    {
        this.secretClient = secretClient
            ?? throw new ArgumentNullException(nameof(secretClient));
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));
        this.ttl = ttl;
    }

    public async Task<KeyVaultSecret> GetSecretAsync(
        string secretName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretName))
            throw new ArgumentException("Secret name is required.", nameof(secretName));

        // 같은 이름의 요청은 같은 캐시와 Gate를 공유합니다.
        Entry entry = entries.GetOrAdd(secretName, _ => new Entry());

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // 첫 번째 요청이 저장한 값을 대기 중이던 요청들이 사용합니다.
            if (entry.Secret != null && now < entry.RefreshAfter)
                return entry.Secret;

            KeyVaultSecret secret = (await secretClient.GetSecretAsync(
                secretName,
                cancellationToken: cancellationToken)).Value;

            DateTimeOffset refreshAfter = DateTimeOffset.UtcNow.Add(ttl);
            if (secret.Properties.ExpiresOn is { } expiresOn && expiresOn < refreshAfter)
                refreshAfter = expiresOn;

            entry.Secret = secret;
            entry.RefreshAfter = refreshAfter;
            return secret;
        }
        finally
        {
            entry.Gate.Release();
        }
    }
}
```

`CachedSecretReader` 인스턴스는 요청마다 새로 만들지 않고 같은 앱 프로세스의 요청들이 공유하도록
유지합니다(예: DI Singleton). `orders-db-password` 요청끼리는 같은 Gate를 사용하지만,
`orders-api-key` 요청은 별도 Gate로 진행합니다. 다른 프로세스나 AKS Pod의 호출까지 통합하지는 않습니다.
예제는 앱이 사용하는 제한된 Secret 이름을 전제로 하며, 요청 입력을 그대로 이름으로 받는 경우에는
앱의 허용 목록 검증도 필요합니다.

동시 요청 20개가 같은 빈 캐시에 접근하면 첫 요청이 Vault를 조회하고, 나머지 19개는 Gate에서 기다린 뒤
첫 요청이 저장한 값을 사용합니다. 첫 조회가 실패하면 값이 저장되지 않으므로 다음 대기 요청이 다시 조회할
수 있습니다. 반복 장애에는 실패 후 대기, rate limiter, circuit breaker 같은 추가 정책이 필요합니다.

### 3.4 Jitter — 여러 인스턴스의 동시 갱신 분산

예를 들어 AKS의 동일 Deployment에 속한 Pod 100개가 각각 프로세스 메모리 캐시를 사용하고 비슷한
시각에 시작하면, 동일한 TTL 5분이 지난 뒤 갱신 요청이 다시 집중될 수 있습니다. TTL에 작은 무작위
차이를 추가하면 Pod별 갱신 시점을 분산할 수 있습니다.

```csharp
static TimeSpan AddJitter(TimeSpan baseTtl)
{
    // 기본 TTL에 -30초에서 +30초 사이의 차이를 추가합니다.
    int jitterSeconds = Random.Shared.Next(-30, 31);
    TimeSpan result = baseTtl + TimeSpan.FromSeconds(jitterSeconds);

    return result > TimeSpan.Zero
        ? result
        : TimeSpan.FromSeconds(1);
}

TimeSpan instanceTtl = AddJitter(TimeSpan.FromMinutes(5));
entry.RefreshAfter = DateTimeOffset.UtcNow.Add(instanceTtl);
```

```text
기본 TTL: 300초
인스턴스별 실제 TTL: 270~330초
```

Jitter는 전체 호출량을 직접 줄이는 기능이 아니라 **호출 시점을 시간대별로 분산**하여 순간 부하를
완화합니다. 범위는 필요한 최신성과 인스턴스 수를 고려해 정합니다.

### 3.5 세 패턴을 함께 사용

```text
Secret이 처음 필요함
    ↓
캐시에 없음
    ↓
Single-flight로 같은 이름의 동시 요청 통합
    ↓
첫 요청만 Key Vault 조회
    ↓
TTL + Jitter 동안 메모리 캐시 사용
    ↓
만료 후 다음 요청에서 동일한 방식으로 한 번만 갱신
```

## 4. 서비스 한도를 용량 계획에 반영

Key Vault의 한도는 초당 고정값이 아니라 Vault·리전별 10초 구간과 구독·리전 집계를 사용합니다.

### 4.1 Secret 관련 공식 트랜잭션 한도

| 트랜잭션 분류 | Vault·리전별 최대값 | 구독·리전 전체 최대값 |
|---|---:|---:|
| Secret Create | 300회/10초 | 1,500회/10초 |
| Get Secret, List 등 기타 모든 Vault 트랜잭션 | 4,000회/10초 | 20,000회/10초 |

일반 트랜잭션 4,000회/10초는 평균으로 약 400회/초이지만, 이를 매초 400회가 보장된다는 의미로
해석하지 않습니다. 짧은 순간의 집중, 다른 앱의 공유 사용량, SDK 재시도, 구독·리전 전체 사용량을
함께 고려해야 합니다.

### 4.2 객체·버전 개수와 백업 한도

| 항목 | 공식 제한 또는 동작 |
|---|---|
| Vault에 저장할 Key·Secret·Certificate 개수 | 개수 자체에 서비스 제한 없음 |
| Secret·Key·Certificate 버전 개수 | 버전 개수 자체에 서비스 제한 없음 |
| 개별 객체 백업 | 최대 500개 버전 |
| 버전이 500개를 초과한 객체 | 백업 성능에 영향을 주며 개별 객체 백업이 실패할 수 있음 |

저장 개수에 명시적인 제한이 없다는 것은 한 Vault에 무제한으로 모으는 설계를 권장한다는 뜻이 아닙니다.
권한 경계, 목록 조회 비용, 백업, 회전, 장애 범위와 앱·환경별 분리를 함께 판단합니다.

HTTP 429, Metrics 확인 및 재시도 방식은 [04. Key Vault 장애 대응](04-resilience.md)에서 설명합니다.

## 5. 설계 권고

1. 환경별 Vault를 분리합니다.
2. 같은 환경에서도 권한·소유 팀·네트워크·장애 범위가 다르면 앱 또는 업무 도메인별로 분리합니다.
3. 공유 Vault를 선택했다면 각 앱이 모든 Secret을 로드하지 않게 합니다.
4. 직접 조회에는 허용된 이름의 명시적 매핑을 사용합니다.
5. TTL·Single-flight·Jitter로 반복·동시 호출을 줄입니다.
6. 일반 설정은 Azure App Configuration 같은 설정 저장소에, 비밀은 Key Vault에 둡니다.
7. 대상 서비스가 Entra 인증을 지원하면 저장해야 할 암호 자체를 줄입니다.

분산 캐시에 Secret을 복제하는 것은 기본 해법이 아닙니다. 또 다른 비밀 저장소·권한·암호화·운영 경계가
생기므로 별도 위협 모델과 승인 없이 도입하지 않습니다.

## 6. 실습 연결

[Lab 02](../labs/02-dotnet-integration/README.md#cache-net48)에서는 실제 교육용 Vault의 Secret 한 개로
캐시 없음·있음의 조회 수를 비교하고, TTL 이후 갱신을 관찰합니다.

429와 재시도 실습은 [04. Key Vault 장애 대응](04-resilience.md)과
[Lab 04](../labs/04-resilience/README.md)에서 진행합니다.

## 학습 체크리스트

- [ ] 앱별 Vault와 공유 Vault의 장단점을 설명할 수 있습니다.
- [ ] Azure RBAC과 Access Policy의 Secret 범위 차이를 설명할 수 있습니다.
- [ ] 공유 Vault에서 필터 없는 Configuration Provider의 위험을 설명할 수 있습니다.
- [ ] TTL·Single-flight·Jitter의 역할 차이를 설명할 수 있습니다.
- [ ] 저장 개수와 호출량을 구분할 수 있습니다.

## 참고 링크

- [서비스 한도](https://learn.microsoft.com/azure/key-vault/general/service-limits)
- [Secrets 성능·회전](https://learn.microsoft.com/azure/key-vault/secrets/secure-secrets)
