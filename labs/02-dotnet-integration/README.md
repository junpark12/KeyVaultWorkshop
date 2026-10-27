# 모듈 2 실습 — .NET 애플리케이션과 Key Vault 연동

**진행:** 설명 → 강사 실행 시연 → 소스 설명 → 고객이 같은 소스로 반복합니다.  
**설명 원고:** [docs/02-dotnet-integration.md](../../docs/02-dotnet-integration.md)

**설명과 실습의 번호·방법 이름을 맞췄습니다.** 준비는 0절에서 한 번 수행하고, 본 실습은 설명 원고와 같은 1~7절 순서로 진행합니다.

| 설명 원고 | 같은 순서의 실습 |
|---|---|
| 1. 큰 그림 | 1. 앱이 값을 가져오는 경로 확인 |
| 2. 인증·DefaultAzureCredential | [2.2 DefaultAzureCredential 접근](#default-credential-net48) |
| 3.2 방법 A — Configuration 통합 | [3.2 Configuration 통합](#configuration) |
| 3.3 방법 B — SecretClient 직접 호출 | [3.3 직접 조회](#secret-client) |
| 4. 로딩 시점·갱신·캐싱 | [4.1 재로딩](#reload-and-cache) → [4.2 메모리 캐싱](#cache-net48) |
| 5. 환경별 구성 | [5. 로컬 설정과 user-secrets](#environments) |
| 6. 버전과 접근 방식 | [6. Latest / Pinned 비교](#versions) |
| 7. 자주 겪는 오류 | [7. 오류 확인](#troubleshooting) |

Lab 2는 이 폴더와 README 하나로 진행합니다. DefaultAzureCredential 기본 접근과 메모리 캐싱은 **.NET Framework 4.8 전용**입니다. Configuration 비교에는 Framework 콘솔과 .NET 10 웹 앱을 사용합니다. **Net48Console과 Net10Web은 읽은 Secret·연결 문자열을 옵션 없이 평문으로 출력합니다. 교육용 가상 값만 사용하고 실제 DB에는 연결하지 않습니다.** DefaultAzureCredential·캐싱 전용 프로젝트의 기존 마스킹은 유지합니다.

**고객은 본인 Azure 구독을 사용하지 않습니다.** 강사가 제공한 Vault와 참가자별 읽기 전용 SP를 사용합니다. 2.2의 전용 실습만 DefaultAzureCredential을 사용하고, **Configuration·직접 조회·캐싱은 명시적인 ClientSecretCredential**을 사용합니다. 참가자에게 Azure CLI 로그인·리소스 생성·배포 권한은 필요 없습니다.

제공받은 Vault에도 접근할 수 없으면 0절 오프라인 검사와 5절 로컬 설정을 진행합니다. 모든 콘솔·웹 앱은 고객 PC에서 실행하고 강사 Vault를 조회합니다.

## 0. 사전 준비·프로젝트·빌드

### 사용할 프로젝트

| 순서 / 경로 | 용도 |
|---|---|
| `default-credential/net48/` | 2절: Framework 4.8 기본 인증·조회 |
| `Net48Console/` | 3~6절: Configuration·직접 조회·설정·버전 비교 |
| `Net10Web/` | 같은 기능의 로컬 웹 앱 |
| `cache/net48/` | 4.2절: Framework 4.8 메모리 캐시·호출 수·TTL |
| `BasicShared/` | 기본 접근·캐시·오프라인 데모 공통 구현 |
| `Shared/IntegrationDemo.cs` | SP 자격 증명 생성·입력 검증·재시도·네이티브 SDK 결과 처리 |
| `Shared/OfflineChecks.cs` | 실제 SDK/공급자를 사용하는 메모리 HTTP 검사 |
| [appsettings.json](appsettings.json) | 두 Configuration 프로젝트가 공유하는 설정 원본. 실제 비밀 값 없음 |

**설정 파일 위치:** 원본은 `Net10Web/` 안이 아니라 **`labs/02-dotnet-integration/appsettings.json`**입니다. [Directory.Build.props](Directory.Build.props)가 이 파일을 두 프로젝트에 링크하고, 빌드 시 각각의 `bin/Debug/<대상 프레임워크>/appsettings.json`으로, 게시 시 게시 폴더로 복사합니다. 앱은 실행 파일이 있는 폴더를 ContentRoot로 사용합니다.

설정을 바꿀 때는 상위 원본을 수정합니다. 출력 폴더의 복사본을 직접 수정하지 않습니다. 원본을 변경한 후에는 다시 빌드하거나 `--no-build` 없이 실행해야 새 복사본에 반영됩니다. 환경 변수는 이 파일의 설정을 덮어쓸 수 있습니다.

### 빌드·오프라인 사전 확인

Windows, .NET 10 SDK, Framework 4.8/4.8.1 런타임과 패키지 피드 접근이 필요합니다. Framework 콘솔에 IIS는 필요 없습니다.

아래는 **워크숍 루트의 Windows PowerShell** 기준입니다.

```powershell
$Lab = (Resolve-Path ".\labs\02-dotnet-integration").Path
$Web = Join-Path $Lab "Net10Web\KeyVaultIntegration.Web.csproj"
$Fx = Join-Path $Lab "Net48Console\KeyVaultIntegration.Net48.csproj"
$DefaultFx = Join-Path $Lab "default-credential\net48\DefaultCredential.Net48.csproj"
$CacheFx = Join-Path $Lab "cache\net48\KeyVaultCache.Net48.csproj"

dotnet build $DefaultFx
if ($LASTEXITCODE -ne 0) { throw "Default credential build failed" }
dotnet build $Web
if ($LASTEXITCODE -ne 0) { throw "Web build failed" }
dotnet build $Fx
if ($LASTEXITCODE -ne 0) { throw "Framework build failed" }
dotnet build $CacheFx
if ($LASTEXITCODE -ne 0) { throw "Cache build failed" }

dotnet run --project $Web --no-build -- --self-test
if ($LASTEXITCODE -ne 0) { throw "Web offline checks failed" }
dotnet run --project $Fx --no-build -- --self-test
if ($LASTEXITCODE -ne 0) { throw "Framework offline checks failed" }
dotnet run --project $DefaultFx --no-build -- --help
dotnet run --project $CacheFx --no-build -- self-test
if ($LASTEXITCODE -ne 0) { throw "Cache offline checks failed" }
```

Configuration 두 프로젝트의 검사 마지막 줄은 다음과 같습니다. 캐시 검사는 `ALL OFFLINE CHECKS PASSED`로 끝나며, 기본 접근의 도움말은 Azure에 접속하지 않습니다.

```text
ALL INTEGRATION OFFLINE CHECKS PASSED (no Azure requests)
```

오프라인 확인은 `--` 이름 변환, IConfiguration 직접 조회·재로딩·변경 알림, Latest/Pinned, 만료, 잘못된 설정, 취소와 SP 필수 설정을 다룹니다. 중간의 모의 404·TIMEOUT 로그는 의도한 오류 검사입니다. 실제 Azure 인증·역할·네트워크를 검증하는 것은 아닙니다.

위 검사는 Azure에 접속하지 않는 사전 점검입니다. 실습 진행 순서를 바꾸는 단계가 아닙니다. 이후 명령도 **워크숍 루트와 같은 PowerShell 창**을 기준으로 하며, `$DefaultFx`, `$Fx`, `$Web`, `$CacheFx`를 구분해 사용합니다.

웹 앱을 실행한 뒤 다른 모드로 전환할 때는 해당 실행 창에서 `Ctrl+C`로 종료하고 다시 시작합니다. 환경 변수 변경은 이미 실행 중인 프로세스에 자동 적용되지 않습니다.

## 1. 큰 그림 — 앱이 값을 가져오는 경로

Secret은 연결 문자열·API 키 같은 문자열, Key는 암호 연산용 키, Certificate는 인증서입니다. 이번 실습에서는 **Secret을 앱 설정 또는 SDK 결과로 사용하는 방식**을 비교합니다.

```text
앱 측 SDK 경로: 앱 → Configuration 공급자 / SecretClient → Key Vault
```

인프라 담당자는 Vault·앱 신원·권한·네트워크를 준비하고, 개발자는 값을 읽고 갱신·캐시·오류 처리를 구현합니다.

아래 모드는 `Net48Console`과 `Net10Web`에 적용됩니다. 기본 접근과 캐싱 전용 프로젝트에는 `KeyVault:Mode`가 없습니다.
| `KeyVault:Mode` | 동작 | Azure 접근 |
|---|---|---|
| `configuration` | SP 인증 + AddAzureKeyVault → IConfiguration 직접 조회 | 시작 시 List/Get, 선택적으로 주기 조회 |
| `direct` | Singleton SecretClient로 이름·버전 직접 조회 | 명시적 조회 때 Get |
| `local` — 기본값 | 로컬 환경 변수 또는 개발용 user-secrets를 일반 설정으로 읽기 | 없음 |

실패한 인증을 local 모드나 가짜 성공으로 자동 대체하지 않습니다.

## 2. 인증과 권한 — DefaultAzureCredential로 먼저 접근

### 2.1 고객 실습과 Azure 운영 신원 구분

고객의 기본 실습 신원은 **제공받은 SP**입니다. 강사가 준비한 Tenant ID·Client ID·Client Secret Value와 Vault URI를 사용합니다. 구독 ID나 `az account set`은 SDK 조회에 필요하지 않습니다.

이 실습에서는 앱을 Azure에 배포하지 않고 고객 PC의 SP로 조회합니다. DefaultAzureCredential의 체인 비교 외에는 사용자 로그인이나 Managed Identity로 인증을 대체하지 않습니다.
<a id="default-credential-net48"></a>

### 2.2 DefaultAzureCredential 접근

[기본 접근 Program.cs](default-credential/net48/Program.cs)는 `new DefaultAzureCredential()`과 SecretClient를 생성하여 Secret 한 개를 읽습니다. 기존 Lab 1의 `ClientSecretCredential` 고정 방식과 비교합니다.

#### 공통 접속 설정

```powershell
$env:KEY_VAULT_URL = "https://<training-vault>.vault.azure.net/"
$env:KV_SECRET_NAME = "test"
```

실제 교육용 Vault와 이름으로 바꿉니다. 이 기본 실습은 `KEY_VAULT_URL`·`KV_SECRET_NAME`을 사용하고, 뒤의 Configuration 실습은 `KeyVault__...` 설정을 사용하므로 구분합니다. 실행 신원에는 RBAC Secrets User 또는 기존 Access Policy의 Secret Get과 네트워크 접근이 필요합니다.

#### 인증 선택 1 — 참가자별 SP 사용

Azure CLI 설치나 `az login` 없이 실행할 수 있습니다. Lab 1에서 같은 PowerShell 창에 자격 증명을 입력했다면 selector만 지정하고 다시 실행해도 됩니다.

```powershell
$env:AZURE_TOKEN_CREDENTIALS = "EnvironmentCredential"
$env:AZURE_TENANT_ID = "<tenant-guid>"
$env:AZURE_CLIENT_ID = "<application-client-guid>"

$secureSecret = Read-Host "Client Secret Value" -AsSecureString
try {
    $env:AZURE_CLIENT_SECRET = (
        New-Object System.Net.NetworkCredential -ArgumentList "", $secureSecret
    ).Password
}
finally {
    $secureSecret.Dispose()
}

dotnet run --project $DefaultFx --no-build
if ($LASTEXITCODE -ne 0) { throw "Secret read failed" }
```

예상 출력:

```text
authentication=DefaultAzureCredential credential selected: Azure.Identity.EnvironmentCredential
credential=DefaultAzureCredential selector=EnvironmentCredential
version=<version-id> value=[REDACTED]
```

코드는 DefaultAzureCredential이지만 selector로 환경 변수 인증 하나를 선택합니다. Client Secret ID가 아니라 **Value**를 사용합니다. 환경 변수는 실행 후 자동 삭제하지 않으므로 같은 창에서 반복 실행할 수 있습니다.

#### 강사 선택 비교 — 개발자 로그인 사용

이 단계는 **DefaultAzureCredential 전용 실습에서만** 수행하는 선택 비교입니다. 접근 권한이 있는 강사 계정이 준비된 경우 같은 Windows CLI에서 실행합니다. 고객은 제공받은 SP 경로만 진행하면 됩니다.

```powershell
az login --tenant "<tenant-guid>"
if ($LASTEXITCODE -ne 0) { throw "Login failed" }
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
dotnet run --project $DefaultFx --no-build
if ($LASTEXITCODE -ne 0) { throw "Secret read failed" }
```

Visual Studio 계정을 쓰려면 해당 로그인·권한을 준비하고 selector를 `VisualStudioCredential`로 바꿉니다. Windows 앱의 개발 도구 로그인과 WSL 로그인은 별개입니다.

Azure CLI 인증을 선택하여 조회에 성공했을 때의 예상 출력:

```text
authentication=DefaultAzureCredential credential selected: Azure.Identity.AzureCliCredential
credential=DefaultAzureCredential selector=AzureCliCredential
version=<version-id> value=[REDACTED]
```

`authentication`은 SDK가 실제 선택한 인증 방식을 표시합니다. 이 경우 `az login`으로 로그인한
계정을 사용하며, 해당 계정에 대상 Vault의 Secret 읽기 권한이 있어야 합니다.

#### 기본 체인 비교 [Demo]

```powershell
Remove-Item Env:AZURE_TOKEN_CREDENTIALS -ErrorAction SilentlyContinue
dotnet run --project $DefaultFx --no-build
```

```text
Environment → Workload Identity → Managed Identity
  → Visual Studio → Visual Studio Code* → Azure CLI
  → Azure PowerShell → Azure Developer CLI → 선택적 브라우저/Broker*
```

별표 경로에는 확장·패키지 등 조건이 있으며 브라우저는 기본 제외됩니다. `selector=default-chain`은 기본 체인을 사용한다는 표시이지, 실제 성공한 신원을 추적한 결과가 아닙니다.

기본 체인에서 환경 변수의 SP + Client Secret으로 인증했을 때의 출력 예시:

```text
authentication=DefaultAzureCredential credential selected: Azure.Identity.EnvironmentCredential
credential=DefaultAzureCredential selector=default-chain
version=<version-id> value=[REDACTED]
```

기본 체인에서 Azure CLI 인증이 선택되어 성공했을 때의 예상 출력:

```text
authentication=DefaultAzureCredential credential selected: Azure.Identity.AzureCliCredential
credential=DefaultAzureCredential selector=default-chain
version=<version-id> value=[REDACTED]
```

두 경우 모두 `selector`는 `default-chain`이지만 **`authentication`에 출력된 방식이 실제 선택 결과**입니다.
버전 ID는 조회한 Secret 버전에 따라 달라집니다.

SP 환경 변수가 남아 있으면 먼저 선택될 수 있습니다. 잘못된 SP 비밀번호가 있다고 항상 CLI로 넘어가는 것도 아닙니다. 실습에서는 먼저 selector를 고정하여 실행 신원을 분명히 합니다. PC에서 selector를 MI로 바꾼다고 Managed Identity가 생성되지는 않습니다.

### 2.3 로컬 인증 확인 후 다음 단계로 이동

다음 Configuration·직접 조회·캐싱은 selector를 사용하지 않고 **ClientSecretCredential로 SP를 명시**합니다. `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `KEY_VAULT_URL`을 현재 창에 유지합니다. SP 설정이 없으면 실패하며 개인 로그인이나 MI로 대체하지 않습니다.

고객은 제공받은 SP의 Tenant ID·Client ID·Secret Value/만료와 읽기 권한을 확인합니다. Windows/WSL·Visual Studio/CLI 로그인 차이는 DefaultAzureCredential의 강사 선택 비교에만 해당합니다. 오류별 확인 항목은 7절에 있습니다.

## 3. .NET 코드 레벨 사용법

### 3.1 패키지와 런타임

`Azure.Identity`, `Azure.Security.KeyVault.Secrets`, `Azure.Extensions.AspNetCore.Configuration.Secrets`를 사용합니다. `$Fx`는 Framework 4.8 콘솔, `$Web`은 .NET 10 ASP.NET Core 앱이며 같은 `Shared/` 구현을 컴파일합니다. 아래 A/B/C는 **설명 원고의 방법 A/B/C와 같은 이름**입니다.

<a id="configuration"></a>

### 3.2 방법 A — Configuration 통합

#### 현재 샘플과 기존 web.config 방식의 차이

현재 샘플은 **.NET Framework 4.8과 .NET 10 모두 `appsettings.json` 기반의
`Microsoft.Extensions.Configuration`(`IConfiguration`)을 사용**합니다. Key Vault 모드에서는
`AddAzureKeyVault()`로 Provider를 추가하여 같은 설정 키를 Key Vault 값으로 공급합니다.
`GetConnectionString("Default")`는 `ConnectionStrings:Default`를 읽으며, 기존 `web.config`를 자동으로 읽지는 않습니다.

| 구분 | 설정 원본·연동 | 앱의 읽기 코드 |
|---|---|---|
| 현재 샘플 | `appsettings.json` + 환경 변수 + Key Vault Provider | `configuration.GetConnectionString("Default")` |
| 기존 ASP.NET Framework | `web.config`의 `<connectionStrings>` + Key Vault Configuration Builder | `ConfigurationManager.ConnectionStrings["Default"].ConnectionString` |

**기존 읽기 코드를 유지하려면**, .NET Framework 4.7.1 이상에서
`Microsoft.Configuration.ConfigurationBuilders.Azure` 패키지를 설치하고, `web.config`의
`<configBuilders>`에 `AzureKeyVaultConfigBuilder`를 등록한 뒤 `<connectionStrings configBuilders="등록한이름">`으로
연결합니다. Vault 주소·인증·Secret 이름 매핑을 별도로 구성해야 하며, 현재 샘플의 `AddAzureKeyVault()`나
`ReloadInterval` 설정이 이 방식에 그대로 적용되는 것은 아닙니다. 이 경로는 이번 실행 실습에는 포함하지 않습니다.
자세한 구성은 [ASP.NET Configuration Builders 공식 문서](https://learn.microsoft.com/aspnet/config-builder)를 참고합니다.

`Microsoft.Configuration.ConfigurationBuilders.Azure` **3.x 기준**의 `web.config` 구성 예시입니다.
기존 파일에 병합할 때 `<configSections>`는 `<configuration>`의 첫 번째 자식 요소로 둡니다.

```xml
<configuration>
  <configSections>
    <section name="configBuilders"
             type="System.Configuration.ConfigurationBuildersSection, System.Configuration, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"
             restartOnExternalChanges="false"
             requirePermission="false" />
  </configSections>

  <configBuilders>
    <builders>
      <add name="KeyVault"
           type="Microsoft.Configuration.ConfigurationBuilders.AzureKeyVaultConfigBuilder, Microsoft.Configuration.ConfigurationBuilders.Azure"
           vaultName="keyvaultworkshop2"
           mode="Strict"
           enabled="enabled"
           prefix="ConnectionStrings--"
           stripPrefix="true"
           preloadSecretNames="false" />
    </builders>
  </configBuilders>

  <connectionStrings configBuilders="KeyVault">
    <add name="Default"
         connectionString=""
         providerName="System.Data.SqlClient" />
  </connectionStrings>
</configuration>
```

이 예시는 `prefix`를 붙여 Vault의 **`ConnectionStrings--Default`** Secret을 조회하고,
앱에서는 기존 이름 **`Default`**로 읽도록 매핑합니다.

```csharp
string connectionString =
    ConfigurationManager.ConnectionStrings["Default"].ConnectionString;
```

인증은 Builder 내부의 `DefaultAzureCredential`을 사용합니다. 로컬 SP 인증이면
`AZURE_TENANT_ID`·`AZURE_CLIENT_ID`·`AZURE_CLIENT_SECRET`을 **실제 앱 프로세스에 전달**해야 합니다.
이미 실행 중인 IIS에 PowerShell의 `$env:` 값이 자동 전달되는 것은 아닙니다.
위 `Strict` + `preloadSecretNames="false"`는 등록된 이름을 개별 조회하며 전체 이름 List를 생략합니다.
Secret 누락 시 빈 연결 문자열이 남을 수 있으므로 앱 시작 시 필수 값 검증도 필요합니다.

#### 준비할 Secret

교육용 Vault에 `ConnectionStrings--Default`라는 Secret을 가상 값 v1으로 준비합니다. 강사는 Secrets Officer, 읽기 앱 신원은 Secrets User를 사용합니다. Access Policy 모드이면 이 경로는 **Get + List**가 필요합니다.

2절에서 입력한 **SP 자격 증명과 강사 Vault**를 사용합니다. 여기서는 최초 로딩 후 **IConfiguration에서 직접 읽는 코드**를 확인하고, 주기적 갱신은 4.1절에서 비교합니다.

**같은 PowerShell 창에서 먼저 확인:** Client Secret 하나뿐 아니라 Tenant ID·Client ID도 필요합니다. 아래는 값 대신 설정 여부만 표시합니다.

```powershell
foreach ($name in @("AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET")) {
    [pscustomobject]@{
        Variable = $name
        IsSet = -not [string]::IsNullOrWhiteSpace(
            [Environment]::GetEnvironmentVariable($name)
        )
    }
}
```

모두 `True`여야 합니다. 새 PowerShell 창에는 다른 창의 설정이 자동 전달되지 않습니다. Vault에 `test`만 있는 경우 이 예제의 `GetConnectionString("Default")`로 읽을 값은 없으므로, 강사가 **`ConnectionStrings--Default`라는 교육용 Secret도 준비**해야 합니다. `KeyVault__SecretName`만 바꾼다고 Configuration 키 이름이 바뀌는 것은 아닙니다.

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:KeyVault__Mode = "configuration"
$env:KeyVault__VaultUri = $env:KEY_VAULT_URL
$env:KeyVault__SecretName = "ConnectionStrings--Default"
$env:KeyVault__ReloadSeconds = "0"
if (Test-Path Env:KeyVault__Version) { Remove-Item Env:KeyVault__Version }
if (Test-Path Env:ConnectionStrings__Default) { Remove-Item Env:ConnectionStrings__Default }

dotnet run --project $Fx --no-build
if ($LASTEXITCODE -ne 0) { throw "Configuration load failed" }
```

`source=IConfiguration`, `configured=True`, `differs_from_startup=False`, `reload_notifications=0`, `value=<Vault의 실제 교육용 값>`을 확인합니다. configured는 설정값을 읽었다는 뜻이지 실제 DB 연결 성공이 아닙니다.

추가 출력 옵션 없이 실행합니다. 소스 변경 후에는 다시 빌드하거나 아래처럼 `--no-build` 없이 실행합니다.

```powershell
# 현재 폴더가 Net48Console이면:
dotnet run

# 워크숍 루트에서 $Fx를 사용하는 경우:
dotnet run --project $Fx
```

이 명령은 다시 빌드한 뒤 `IConfiguration`에서 읽은 문자열을 그대로 표시합니다. `direct` 모드에서는 SDK에서 읽은 Secret 값을 표시합니다. 반복 관찰에는 `--watch`만 사용합니다. 이전 `--show-value` 옵션은 제거했습니다.

**실제 운영 암호·API 키에는 사용하지 않습니다.** 콘솔 기록·웹 응답·캡처·화면 공유에 평문이 남을 수 있습니다. 웹 앱은 `http://127.0.0.1:5080`에만 실행하고 API를 외부에 공개하지 않습니다. SDK 인증용 SP의 `AZURE_CLIENT_SECRET`은 별도로 출력하지 않습니다.

`KeyVault:SecretName`은 뒤의 direct 모드용 이름입니다. Configuration 모드의 필터가 아니며, 기본 공급자는 Vault 목록을 열거합니다. `ConnectionStrings--Default`는 `ConnectionStrings:Default`로 바뀝니다.

웹 앱으로 같은 동작을 보려면 다음을 실행하고, 다른 PowerShell 창에서 `/demo/configuration`을 조회합니다.

```powershell
dotnet run --project $Web --no-build -- --urls http://127.0.0.1:5080
```

```powershell
Invoke-RestMethod http://127.0.0.1:5080/health
Invoke-RestMethod http://127.0.0.1:5080/demo/configuration
```

이전에 5절의 user-secrets 예제를 먼저 실행했다면 동일 이름의 가상 값을 정리한 뒤 비교합니다. 사용 중인 실제 개발 설정을 임의로 지우지는 않습니다.

#### 소스와 값 사용 방식

**Net48Console/Program.cs와 Net10Web/Program.cs에서 흐름을 직접 확인합니다.**

```csharp
IConfiguration configuration = builder.Configuration;
string mode = configuration["KeyVault:Mode"] ?? "local";

// configuration 모드에서 SP로 만든 SecretClient를 공급자로 등록
builder.Configuration.AddAzureKeyVault(client, new AzureKeyVaultConfigurationOptions
{
    ReloadInterval = IntegrationSupport.ReloadInterval(configuration)
});

// 값을 사용하는 지점에서 IConfiguration을 직접 읽음
string connectionString = configuration.GetConnectionString("Default");
// 같은 값: configuration["ConnectionStrings:Default"]
```

`client`는 `ClientSecretCredential`로 인증하는 SecretClient입니다. `ConnectionStrings--Default`는 `ConnectionStrings:Default`로 변환됩니다. DemoSettings·ConfigurationReader·ConfigurationResult·Options 클래스를 만들지 않습니다. 콘솔과 HTTP 응답은 읽은 교육용 값을 보여주는 결과이지 설정값 저장 객체가 아닙니다.

기본 공급자는 만료된 Secret을 포함할 수 있고, 백그라운드 조회 실패 후 이전 설정이 남을 수 있습니다. 샘플은 `SafeFailurePolicy`로 HTTP 실패를 기록합니다. 또한 같은 키가 여러 공급자에 있으면 Vault에서 키가 제거될 때 하위 공급자의 값이 다시 보일 수 있습니다. 기존의 TTL 기반 캐시 실습과 동일한 fail-closed 정책이라고 가정하지 않습니다.

<a id="secret-client"></a>

### 3.3 방법 B — SecretClient 직접 호출

```powershell
$env:KeyVault__Mode = "direct"
$env:KeyVault__VaultUri = $env:KEY_VAULT_URL
$env:KeyVault__SecretName = "ConnectionStrings--Default"
$env:KeyVault__ReloadSeconds = "0"
if (Test-Path Env:KeyVault__Version) { Remove-Item Env:KeyVault__Version }

dotnet run --project $Fx --no-build
```

예상 출력은 `selection=latest version=<현재 버전> value=<교육용 값>`입니다.

웹 앱에서는 같은 direct 설정으로 실행한 후 `/demo/secret`을 호출합니다. 이 엔드포인트는 **명시적 SDK 조회를 관찰하기 위한 데모용**입니다. Singleton SecretClient는 값 캐시가 아니므로 호출할 때마다 Get이 발생합니다. 운영 업무 요청 경로에 이 패턴을 그대로 복사하지 말고 [값 캐싱](#cache-net48)을 적용합니다.

여기서는 이름을 지정한 조회를 확인합니다. Latest/Pinned 비교는 설명 원고와 동일하게 6절에서 진행합니다.

### 3.5 두 방식의 관찰 결과 비교

| 설명 / 실습 이름 | 실행 모드 | 확인할 결과 |
|---|---|---|
| 방법 A — Configuration | `configuration` | SP로 로딩한 값을 IConfiguration에서 직접 읽음 |
| 방법 B — SecretClient | `direct` | SP로 Get하여 SDK의 KeyVaultSecret과 버전을 받음 |

모드 전환은 프로세스 시작 시 적용됩니다. 다른 모드로 바꿀 때는 웹 앱을 종료·재시작하고, Version과 ReloadSeconds도 해당 모드에 맞게 초기화합니다.

<a id="reload-and-cache"></a>

## 4. Secret 로딩 시점과 갱신

### 4.1 IConfiguration 재조회와 변경 알림

#### `KeyVault__ReloadSeconds` 옵션

| 환경 변수 값 | 공급자에 전달되는 값 | 동작 |
|---|---|---|
| `"0"` | `ReloadInterval = null` | 시작 시 1회 로딩. 자동 재로딩하지 않음 |
| `"15"` | `TimeSpan.FromSeconds(15)` | 약 15초 간격으로 Vault 변경 확인 |
| `"30"` | `TimeSpan.FromSeconds(30)` | 약 30초 간격으로 Vault 변경 확인 |

**`"0"`은 대기 없이 계속 재로딩한다는 뜻이 아닙니다.** 샘플의 `IntegrationSupport.ReloadInterval()`이 0을 null로 변환합니다. 0일 때 Vault 값 변경을 반영하려면 앱을 다시 실행하거나 코드에서 `((IConfigurationRoot)configuration).Reload()`를 호출해야 합니다.

`--watch`는 **5초마다 현재 IConfiguration 값을 출력**하는 콘솔 옵션입니다. Vault를 갱신하는 주기와 별개이며, `ReloadSeconds=0`이라면 반복 출력 중에도 Vault를 자동 재조회하지 않습니다. 예를 들어 15초 갱신과 5초 출력을 함께 사용하면 출력 사이에 공급자의 갱신이 반영됩니다.

환경 변수 값을 바꾼 후에는 앱을 다시 실행해야 새 주기가 적용됩니다. Configuration 모드의 자동 갱신 옵션이며, 직접 조회나 별도 값 캐시의 갱신 주기를 제어하는 값은 아닙니다.

방법 A를 기준으로 **시작 시 1회 로딩 → 주기적 재로딩** 순서로 비교합니다. 기존 웹 앱을 종료하고, 로컬 실습 창을 Configuration 모드로 명시적으로 되돌립니다.

#### 시작 시 1회 로딩

```powershell
$env:KeyVault__Mode = "configuration"
$env:KeyVault__VaultUri = $env:KEY_VAULT_URL
$env:KeyVault__SecretName = "ConnectionStrings--Default"
$env:KeyVault__ReloadSeconds = "0"
if (Test-Path Env:KeyVault__Version) { Remove-Item Env:KeyVault__Version }
if (Test-Path Env:ConnectionStrings__Default) { Remove-Item Env:ConnectionStrings__Default }

dotnet run --project $Fx --no-build -- --watch
```

약 60초간 5초 간격으로 출력합니다. 강사가 같은 이름에 다른 가상 값을 새 버전으로 저장해도, 이 프로세스는 자동 재로딩하지 않습니다. 재시작하면 현재 버전을 새로 읽습니다.

#### 주기적 재로딩과 IConfiguration 변경 알림

```powershell
$env:KeyVault__ReloadSeconds = "15"
dotnet run --project $Fx --no-build -- --watch
```

첫 출력 뒤 강사가 같은 이름에 **이전과 다른 가상 값**을 새 버전으로 저장합니다. 주기 조회 후 다음을 관찰합니다.

```text
differs_from_startup=True reload_notifications=<1 이상>
```

웹 앱도 같은 환경에서 실행하고 `/demo/configuration`을 반복 호출하여 `differsFromStartup`과 `reloadNotifications`를 확인할 수 있습니다. 이 API는 설정 캐시를 읽으며, API 요청마다 Key Vault Get을 수행하지 않습니다.

같은 값으로 새 버전만 만들면 변경 알림은 발생할 수 있어도 `differsFromStartup`은 false일 수 있습니다. 이 표시는 실제 설정값과 시작 시 값의 차이를 비교하며, 콘솔·웹의 `value`에서도 교육용 문자열의 변화를 볼 수 있습니다.

시작 시 `GetConnectionString()`으로 읽은 문자열과 매번 IConfiguration에서 다시 읽는 문자열을 비교합니다. 알림 횟수는 `ChangeToken.OnChange(configuration.GetReloadToken, ...)`로 관찰합니다. Options나 사용자 정의 결과 객체는 사용하지 않습니다. 알림은 구성 전체의 변경 신호이므로 Secret 값 변경 횟수와 항상 같지는 않습니다. 설정 변경이 DB 연결 풀·외부 클라이언트를 자동 재생성하는 것도 아닙니다.

<a id="cache-net48"></a>

### 4.2 Singleton과 값 캐시 비교 — .NET Framework 4.8

[캐시 Program.cs](cache/net48/Program.cs)는 **ClientSecretCredential과 SecretClient**를 재사용하고, [CachedSecretReader.cs](BasicShared/CachedSecretReader.cs)로 값 캐시를 별도로 적용합니다. 2절에서 입력한 SP 자격 증명·`KEY_VAULT_URL`·`KV_SECRET_NAME`을 사용합니다. `$DefaultFx`가 아니라 **`$CacheFx`**를 실행하며, selector로 다른 인증을 선택하지 않습니다.

#### 실제 Vault: 같은 값을 10번 사용

```powershell
$env:KV_CACHE_TTL_SECONDS = "30"
dotnet run --project $CacheFx --no-build -- cache
```

```text
read=1 source=vault version=<version-id> value=[REDACTED]
read=2 source=cache version=<version-id> value=[REDACTED]
...
read=10 source=cache version=<version-id> value=[REDACTED]
with_cache_reads=10 logical_get_calls=1
```

정상적인 TTL 안에서는 논리적 Get 한 번으로 10번 사용합니다. SDK 재시도·토큰 발급 HTTP는 별도입니다. Secret 자체 만료가 더 가까우면 캐시도 더 일찍 만료됩니다.

#### 캐시 없음과 비교

```powershell
dotnet run --project $CacheFx --no-build -- compare
```

```text
without_cache_reads=10 logical_get_calls=10
...
with_cache_reads=10 logical_get_calls=1
```

같은 SecretClient를 재사용해도 값 캐시가 없으면 Get이 10번 발생합니다. 이 명령은 교육용 Secret 한 개를 소량 비교하는 용도입니다.

#### TTL과 새 버전 관찰

```powershell
$env:KV_CACHE_TTL_SECONDS = "10"
dotnet run --project $CacheFx --no-build -- watch
```

약 60초간 5초 간격으로 총 13번 읽습니다. 최초 조회와 TTL 이후에는 `source=vault`, 그 사이에는 `source=cache`가 표시됩니다. 강사가 같은 이름에 새 가상 값을 저장하면 다음 갱신에서 버전이 바뀝니다. TTL 10초와 30초를 비교합니다.

캐시는 프로세스 내부에만 있습니다. 같은 명령을 새로 실행하면 첫 조회는 다시 Vault로 갑니다. 읽기 앱에는 Secret 쓰기 권한을 추가하지 않습니다. TTL은 관찰용 값이며 운영 요구에 맞게 결정합니다.

#### 공통 소스와 실패 처리

| 소스 | 확인할 부분 |
|---|---|
| [SecretSource.cs](BasicShared/SecretSource.cs) | 실제 Get과 논리적 호출 수, Secret 메타데이터 |
| [CachedSecretReader.cs](BasicShared/CachedSecretReader.cs) | 허용 이름, 이름별 동시 갱신 제어, TTL·만료 경계 |
| [FrameworkLabSupport.cs](BasicShared/FrameworkLabSupport.cs) | SP 생성·접속 설정·TTL 검증, 오류·종료 코드. selector는 Default 전용 실습만 사용 |
| [OfflineDemos.cs](BasicShared/OfflineDemos.cs) | 내부 동시성·만료·갱신 실패 검사 |

`self-test`로 동시 요청 20개가 성공한 조회 한 번을 공유하는지, TTL 이후 실패하면 오래된 값을 조용히 반환하지 않는지 확인합니다. 운영에는 실패 시 부하 제어·회로 차단 등 추가 정책도 검토합니다.

환경의 자격 증명은 다음 실습을 위해 현재 창에 유지합니다. 전체 실습 종료 후에만 마지막 정리 절차를 사용합니다.

<a id="environments"></a>

## 5. 환경별 구성 전략

### 5.1 dev / stg / prod의 Vault URI 구분

| 환경 | 주입할 값 | 주의 |
|---|---|---|
| dev | 개발용 Vault URI | 개발자 또는 실습 SP 신원 |
| stg | 사전 운영 Vault URI | 운영과 구분된 앱 MI·권한 |
| prod | 운영 Vault URI | 운영 MI·최소 권한·네트워크 통제 |

Vault URI는 비밀이 아니므로 환경 변수 또는 환경별 설정 파일로 관리할 수 있습니다. Secret 값은 소스·일반 설정·Git에 넣지 않습니다. 동일한 소스로 바꿔 보는 것은 URI와 실행 신원이며, 이름 접두사로 환경의 권한 격리를 대신하지 않습니다.

### 5.2 로컬 설정과 IConfiguration

앞의 웹 프로세스를 종료하고 로컬 실습 창에서 다음을 설정합니다. local 모드에서는 Azure를 조회하지 않습니다. 아래 문자열은 **가상 데이터**이며 실제 DB 자격 증명이 아닙니다.

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:KeyVault__Mode = "local"
$env:KeyVault__ReloadSeconds = "0"
if (Test-Path Env:KeyVault__Version) { Remove-Item Env:KeyVault__Version }
$env:ConnectionStrings__Default = "TRAINING-ONLY-LOCAL"

dotnet run --project $Fx --no-build
```

예상 결과:

```text
mode=local source=IConfiguration configured=True differs_from_startup=False reload_notifications=0 value=TRAINING-ONLY-LOCAL
```

웹 앱은 같은 설정으로 실행합니다.

```powershell
dotnet run --project $Web --no-build -- --urls http://127.0.0.1:5080
```

앱 실행 창은 유지하고, **다른 PowerShell 창**에서 조회합니다.

```powershell
Invoke-RestMethod http://127.0.0.1:5080/health
Invoke-RestMethod http://127.0.0.1:5080/demo/configuration
```

`configured=true`, `differsFromStartup=false`, `value=TRAINING-ONLY-LOCAL`을 확인합니다. 여기서 configured는 값이 설정되어 있다는 뜻이지 DB 연결 성공을 뜻하지 않습니다. 웹 앱의 실행 창에서 `Ctrl+C`로 종료합니다.

### 5.3 dotnet user-secrets와 Vault 설정의 우선순위

환경 변수 대신 user-secrets를 사용하려면:

```powershell
if (Test-Path Env:ConnectionStrings__Default) { Remove-Item Env:ConnectionStrings__Default }
dotnet user-secrets set "ConnectionStrings:Default" "TRAINING-ONLY-LOCAL" --project $Web
dotnet run --project $Fx --no-build
```

두 프로젝트는 같은 개발용 UserSecretsId를 사용하며 Development 환경에서 로딩합니다. Windows와 WSL의 프로필 저장소는 다릅니다. user-secrets는 개발 편의 기능이지 암호화된 운영 비밀 저장소가 아닙니다. 실제 비밀을 위 예시처럼 명령줄 리터럴로 입력하지 않습니다.

Configuration 모드로 다시 돌아갈 때는 이 절에서 넣은 가상 환경 변수 값을 제거하고, user-secrets 실습을 진행했다면 해당 교육용 키만 정리합니다. 뒤에 등록한 Vault 공급자가 우선하지만, Vault에서 키가 제거되면 하위 공급자의 같은 이름 값이 드러날 수 있습니다.

```powershell
if (Test-Path Env:ConnectionStrings__Default) { Remove-Item Env:ConnectionStrings__Default }
# 위 user-secrets 실습을 진행한 경우에만:
dotnet user-secrets remove "ConnectionStrings:Default" --project $Web
```

<a id="versions"></a>

## 6. Key Vault 버전과 Latest / Pinned 비교

### 6.1 Portal에서 버전 확인

강사가 `ConnectionStrings--Default`에 서로 다른 가상 값을 저장하여 두 버전을 준비합니다. 값 변경은 새 버전을 만들며, Version ID는 값의 해시가 아닌 서비스 식별자입니다. 이전 버전의 enabled·만료 등 상태도 확인합니다. 개별 버전 삭제는 지원하지 않습니다.

### 6.2 동일 소스로 Latest와 Pinned 읽기

5절의 local 모드에서 **direct 모드로 되돌리고**, 필요한 설정을 다시 명시합니다.

```powershell
$env:KeyVault__Mode = "direct"
$env:KeyVault__VaultUri = $env:KEY_VAULT_URL
$env:KeyVault__SecretName = "ConnectionStrings--Default"
$env:KeyVault__ReloadSeconds = "0"
if (Test-Path Env:KeyVault__Version) { Remove-Item Env:KeyVault__Version }

dotnet run --project $Fx --no-build
```

`selection=latest version=<현재 버전> value=<교육용 값>`을 확인합니다.
Portal에서 **이전에 생성된 사용 가능한 버전 ID**를 확인하고 지정합니다.

```powershell
$env:KeyVault__Version = "<32-character-version-id>"
dotnet run --project $Fx --no-build
```

`selection=pinned`와 지정한 버전·교육용 값을 확인합니다. 새 버전을 추가해도 Pinned는 이전 버전을 계속 요청합니다.

사용이 불가능한 최신 버전에서 이전 활성 버전으로 자동 복귀한다고 가정하지 않습니다. 과거 Secret을 읽는 것만으로 외부 DB/API의 과거 자격 증명이 다시 유효해지는 것도 아닙니다. 암호화 Key의 과거 암호문 처리처럼 원래 버전 식별자가 필요한 경우도 구분합니다.

비교가 끝난 후 Configuration 실습을 반복하려면 `KeyVault__Version`을 제거하고 모드·ReloadSeconds를 다시 맞춥니다.

<a id="troubleshooting"></a>

## 7. 실습 오류 해결과 주의사항

| 상황 | 동작 |
|---|---|
| 잘못된 모드·Vault URI·버전·시간 값 | 시작 실패, 비정상 종료 코드 |
| 최초 Vault 로딩 실패 | `STARTUP_ERROR` 또는 `DEMO_ERROR` |
| 웹 요청에서 조회 실패 | 값 없는 오류 로그와 HTTP 503 |
| 백그라운드 SDK 갱신 오류 | `KEY_VAULT_HTTP_ERROR` / `KEY_VAULT_REQUEST_FAILED` 로그 |
| 설정 없음 | 정상 설정처럼 반환하지 않음 |

Configuration·직접 조회 콘솔의 세부 오류는 다음처럼 확인합니다. `--no-build`는 이전 EXE를 실행하므로 코드가 변경된 뒤에는 **`dotnet run` 또는 `dotnet build`로 다시 빌드**합니다.

| 오류 코드 | 확인·조치 |
|---|---|
| `MISSING_ENV_AZURE_TENANT_ID` / `...CLIENT_ID` / `...CLIENT_SECRET` | 같은 창에 해당 SP 환경 변수 설정 |
| `INVALID_SETTING_AZURE_TENANT_ID` / `...CLIENT_ID` | Tenant ID·Client ID의 GUID 형식 확인 |
| `INVALID_SETTING_KeyVault__VaultUri` | Vault URI 설정·형식 확인 |
| `INVALID_SETTING_KeyVault__Version` | Configuration에서는 이전 direct 실습의 Version 환경 변수 제거 |
| `INVALID_SETTING_KeyVault__ReloadSeconds` | 모드와 시간 설정 확인 |
| `MISSING_SETTING_ConnectionStrings__Default` | Configuration에 값이 없음. Vault의 `ConnectionStrings--Default` 이름·활성 상태 확인 |
| `SECRET_NOT_USABLE` | 직접 조회한 Secret의 유효 메타데이터 확인 |

Client Secret을 터미널 명령의 리터럴이나 질문·화면에 붙여 넣지 않습니다. 노출되었다면 담당자가 해당 앱 자격 증명을 삭제·재발급하고 새 Value를 숨김 입력합니다. Secret 값이나 원본 예외 본문을 오류 로그에 추가하지 않습니다.

웹 데모는 고객 PC의 `http://127.0.0.1:5080`에서 실행하며 **Development 또는 `Demo:Enabled=true`일 때만** 엔드포인트가 열립니다. **API가 Secret 값을 평문으로 반환하므로 외부 네트워크에 공개하지 않습니다.** 값 응답에는 `Cache-Control: no-store`를 설정하지만 캡처·기록까지 막지는 못합니다. `/health`는 프로세스 상태이며 Vault 연결 성공을 보장하지 않습니다.

환경 변수·메모리·로컬 user-secrets도 호스트 접근 권한을 가진 사용자에게 노출될 수 있습니다. 캡처·로그·덤프에 비밀을 남기지 않습니다.

기본 접근·캐싱 전용 콘솔의 출력도 구분합니다.

| 기본 실습의 출력 | 확인할 것 |
|---|---|
| `CONFIG_ERROR parameter=...` | 해당 환경 변수 누락·형식 또는 명령 오류 |
| `AUTH_FAILED` | SP의 Tenant/Client ID, Secret Value·만료. selector는 Default 전용 실습만 해당 |
| `KEY_VAULT_ERROR status=403/404` | 권한·네트워크 또는 Secret 이름·버전 |
| `TIMEOUT` / `NETWORK_ERROR` | 30초 요청 예산, DNS·프록시·TLS |
| `OPERATION_ERROR` | 비활성·만료·활성 시작 전 상태 또는 오프라인 검사 실패 |

## 8. 실습 완료 확인과 정리

DefaultAzureCredential → 방법 A/B → 갱신·캐시 → 환경 → 버전 → 오류 순서로 확인했는지 점검합니다.

실행 중인 웹 앱은 `Ctrl+C`로 종료합니다. 현재 세션에서 반복 실행할 동안 자격 증명을 유지하고, 실습이 끝났을 때만 아래에서 **이번 실습에 설정한 항목**을 제거합니다.

```powershell
foreach ($name in @(
  "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT",
  "KeyVault__Mode", "KeyVault__VaultUri", "KeyVault__SecretName",
  "KeyVault__Version", "KeyVault__ReloadSeconds", "ConnectionStrings__Default",
  "KEY_VAULT_URL", "KV_SECRET_NAME", "KV_CACHE_TTL_SECONDS",
  "AZURE_TOKEN_CREDENTIALS", "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET"
)) {
  if (Test-Path "Env:$name") { Remove-Item "Env:$name" }
}
```

user-secrets 예제를 직접 실행했다면 교육용 키만 제거합니다.

```powershell
dotnet user-secrets remove "ConnectionStrings:Default" --project $Web
```

Azure 자원·역할·교육용 자격 증명 정리는 해당 실습 환경 담당자가 수행합니다. 공용 Vault나 다른 사용자의 자원을 삭제하지 않습니다.

## 참고 링크

- [Key Vault 설정 공급자](https://learn.microsoft.com/aspnet/core/security/key-vault-configuration?view=aspnetcore-10.0)
- [DefaultAzureCredential 체인](https://learn.microsoft.com/dotnet/azure/sdk/authentication/credential-chains)
- [Key Vault 호출량·캐싱 지침](https://learn.microsoft.com/azure/key-vault/general/overview-throttling)
