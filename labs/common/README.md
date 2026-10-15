# 여러 Lab이 함께 사용하는 .NET 소스

조회·캐싱·버전 감시·모의 장애는 같은 클라이언트와 캐시 구현을 사용합니다. 이 폴더에는 기존 통합 콘솔의 진입점을 유지하고, 핵심 구현은 **Lab 2의 `BasicShared/`를 링크하여 재사용**합니다.

| 경로 | 용도 |
|---|---|
| `Net48/KeyVaultWorkshop.Net48.csproj` | .NET Framework 4.8, Windows 실행 |
| `Net10/KeyVaultWorkshop.Net10.csproj` | .NET 10 |
| `Shared/Program.cs` | 두 프로젝트의 기존 통합 콘솔 진입점 |
| `../02-dotnet-integration/BasicShared/` | 설정·조회·캐시·오프라인 데모 구현 |
| `Directory.Build.props` | 패키지 버전·공통 컴파일 설정 |

기존 통합 프로젝트를 전달할 때는 이 폴더와 **`labs/02-dotnet-integration/BasicShared/`**를 포함합니다. 프로젝트 하위 폴더만 따로 복사하면 공통 설정과 소스를 찾지 못합니다. 새로운 [Lab 2](../02-dotnet-integration/README.md)는 해당 폴더만으로 네 프로젝트를 빌드할 수 있습니다. [Lab 1의 SP 독립 예제](../01-secret-read/README.md)는 이 폴더에 의존하지 않습니다.

## Lab별 사용 명령과 코드

| Lab | 명령 | 핵심 소스 |
|---|---|---|
| 공통 소스 기본 확인 | `help`, `self-test` | 프로젝트 구성 |
| 공통 소스 기본 조회 | `get` | [SecretSource.cs](../02-dotnet-integration/BasicShared/SecretSource.cs), [WorkshopSettings.cs](../02-dotnet-integration/BasicShared/WorkshopSettings.cs) |
| [02 캐싱](../02-dotnet-integration/README.md#cache-net48) | `cache`, `watch` | [CachedSecretReader.cs](../02-dotnet-integration/BasicShared/CachedSecretReader.cs) |

## 빌드와 오프라인 실행

Windows PowerShell에서 **워크숍 루트 폴더** 기준:

```powershell
$Project = ".\labs\common\Net10\KeyVaultWorkshop.Net10.csproj"
# Framework 4.8: .\labs\common\Net48\KeyVaultWorkshop.Net48.csproj
dotnet build $Project
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
dotnet run --project $Project --no-build -- self-test
if ($LASTEXITCODE -ne 0) { throw "Offline checks failed" }
```

`retry-demo`, `self-test`는 Azure 인증 없이 실행합니다. `get`, `cache`, `watch`는 **강사가 제공한 SP를 ClientSecretCredential로 인증**하여 접근합니다. 고객의 구독이나 `az login`은 필요 없습니다. 필요한 설정은 `KEY_VAULT_URL`, `KV_SECRET_NAME`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`입니다.

`KV_AUTH_MODE`는 미설정 또는 `service-principal`만 허용합니다. 이전 실습의 `cli`/`managed-identity` 값이 남아 있으면 제거하거나 변경합니다. 자격 증명 입력은 [Lab 2 README](../02-dotnet-integration/README.md)를 따르고, 반복 실습 중 자동으로 지우지 않습니다.

DefaultAzureCredential은 [전용 접근 실습](../02-dotnet-integration/README.md#default-credential-net48)만 사용합니다. 그 밖의 Configuration·직접 조회·캐싱은 명시적 SP 방식입니다.
