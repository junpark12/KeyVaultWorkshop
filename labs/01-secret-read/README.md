# SP로 Key Vault Secret 읽기 — .NET Framework 4.8

문서의 `ServicePrincipalExample.ReadAsync()`를 **실행 가능한 Windows 콘솔 앱**으로 구성한 독립 실습입니다. 이 폴더만 복사해도 빌드할 수 있으며, `labs/common/`에 의존하지 않습니다.

**인증:** `ClientSecretCredential`을 사용한 Service Principal 인증입니다. `DefaultAzureCredential`이나 사용자 로그인으로 자동 전환하지 않습니다. 참가자는 **Azure CLI 설치와 `az login`이 필요 없습니다.**

## 1. 준비 조건

| 항목 | 내용 |
|---|---|
| 실행 환경 | Windows, .NET Framework 4.8 또는 호환되는 4.8.1 런타임 |
| 빌드 도구 | .NET 10 SDK 또는 호환되는 Visual Studio Build Tools |
| 패키지 복원 | NuGet 또는 승인된 사내 패키지 피드 접근 |
| 교육용 Vault | Azure Public Cloud의 실습 전용 Vault, 가상 Secret만 사용 |
| SP 자격 증명 | Tenant ID, Client ID, 만료되지 않은 Client Secret **Value** |
| 읽기 권한 | RBAC 모드이면 SP에 **Key Vault Secrets User**, Access Policy 모드이면 **Secret Get** |
| 네트워크 | 참가자 PC에서 Entra ID 토큰 발급과 Vault HTTPS endpoint 접근 가능 |

SP 생성·권한 부여·교육용 Secret 생성은 강사/관리자가 수행합니다. 이 앱은 이미 존재하는 Secret 한 개를 읽으며 리소스나 값을 생성·변경하지 않습니다. SP 인증도 방화벽이나 Private Endpoint 제한을 우회하지 않습니다.

개인 계정의 역할이 아니라 **SP의 권한**을 확인합니다. Client ID, SP Object ID, Client Secret의 Value/ID는 서로 다릅니다.

## 2. 파일 구성

| 파일 | 역할 |
|---|---|
| `sp-net48/KeyVaultSp.Net48.csproj` | `net48` 대상, Azure SDK와 빌드용 참조 어셈블리 |
| `sp-net48/Program.cs` | `Main()` 실행 진입점, 30초 작업 예산, 오류·종료 코드 처리 |
| `sp-net48/ServicePrincipalExample.cs` | 환경 설정 확인 → SP 인증 → Secret 조회 → 값 없이 결과 출력 |
| `sp-net48/packages.lock.json` | 패키지 복원 버전 잠금 |

`.NETFramework.ReferenceAssemblies.net48` 패키지는 빌드용 참조 어셈블리이며 Windows Framework 실행 런타임을 설치하지 않습니다.

## 3. 빌드와 도움말

**Windows PowerShell**에서 소스가 있는 `sp-net48/` 폴더로 이동한 뒤 아래 명령을 실행합니다. 이 README가 있는 폴더에서는 `Set-Location .\sp-net48`, 워크숍 루트에서는 `Set-Location .\labs\01-secret-read\sp-net48`을 실행합니다.

```powershell
dotnet build .\KeyVaultSp.Net48.csproj
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

dotnet run --project .\KeyVaultSp.Net48.csproj --no-build -- --help
```

도움말은 Azure나 환경 변수에 접근하지 않습니다. 회사 실행 정책을 변경할 필요 없이 아래 명령을 PowerShell에서 직접 실행할 수 있습니다.

Visual Studio의 **Developer PowerShell**을 사용하는 경우에는 다음 빌드도 가능합니다.

```powershell
msbuild .\KeyVaultSp.Net48.csproj /restore /p:Configuration=Debug
```

## 4. 접속 설정

이 실습용 **새 PowerShell 창**에서 아래 비밀이 아닌 설정을 지정합니다. `<...>`는 강사에게 받은 값으로 바꿉니다.

```powershell
$env:AZURE_TENANT_ID = "<tenant-guid>"
$env:AZURE_CLIENT_ID = "<application-client-guid>"
$env:KEY_VAULT_URL = "https://<training-vault-name>.vault.azure.net/"
$env:KV_SECRET_NAME = "workshop-demo-secret"
```

Client Secret은 소스·MD·명령줄 리터럴에 쓰지 않고 다음 단계에서 숨김 입력합니다. **Secret ID가 아니라 Value**를 사용합니다.

## 5. Client Secret 한 번 입력 후 반복 실행

아래 블록 전체를 실행하고 프롬프트에서 Client Secret을 한 번 입력합니다. 입력 내용은 표시되지 않으며, 명령 기록에 비밀 값을 리터럴로 남기지 않습니다. **실행 후에도 현재 PowerShell 창의 환경 변수에 자격 증명을 유지**하여 다시 입력하지 않고 반복 실행할 수 있습니다. 회사 보안 정책에서 승인된 입력 경로인지 확인합니다.

```powershell
$secureSecret = Read-Host "Client Secret Value" -AsSecureString
try {
    $env:AZURE_CLIENT_SECRET = (
        New-Object System.Net.NetworkCredential -ArgumentList "", $secureSecret
    ).Password

    dotnet run --project .\KeyVaultSp.Net48.csproj --no-build
    if ($LASTEXITCODE -ne 0) { throw "Secret read failed. See the error category above." }
}
finally {
    $secureSecret.Dispose()
}
```

`finally`는 입력에 사용한 임시 `SecureString` 객체만 정리합니다. **`AZURE_CLIENT_SECRET` 환경 변수는 삭제하지 않습니다.** 이후 같은 창과 `sp-net48/` 폴더에서 다음 명령만 실행하면 됩니다.

```powershell
dotnet run --project .\KeyVaultSp.Net48.csproj --no-build
```

소스를 수정하여 다시 빌드해야 할 때는 `--no-build`를 빼거나 `dotnet run`을 사용합니다. PowerShell 창을 닫으면 해당 프로세스의 환경은 사라지므로 새 창에서는 접속 설정과 Client Secret을 다시 입력해야 합니다. 사용자/시스템의 영구 환경 변수로 저장하지 않습니다.

환경 변수는 암호화된 저장소가 아닙니다. 창을 유지하는 동안 비밀이 PowerShell과 여기서 실행한 자식 앱의 메모리에 존재합니다. 실제 비밀을 로그·디버거·덤프·스크린샷에 남기지 않습니다.

정상 연결 시 예상 출력:

```text
auth=service-principal
version=<version-id> value=[REDACTED]
```

`[REDACTED]`는 Secret을 못 읽었다는 의미가 아니라 **실제 값을 콘솔에 출력하지 않았다는 표시**입니다. 실제 연결 확인에는 유효한 SP·권한·네트워크가 모두 필요합니다.

EXE를 직접 실행하려면 위 블록의 `dotnet run` 줄 대신 아래 명령을 사용합니다. 의존 DLL이 필요하므로 EXE만 다른 폴더로 옮기지 않습니다.

```powershell
.\bin\Debug\net48\KeyVaultSp.Net48.exe
```

## 6. 코드에서 확인할 핵심

```text
Program.Main()
    → ServicePrincipalExample.ReadAsync()
    → ClientSecretCredential(Tenant ID, Client ID, Client Secret)
    → SecretClient.GetSecretAsync(Secret 이름)
    → 버전과 마스킹 결과 출력
```

한 번 조회하고 종료하는 최소 예제입니다. 캐싱·회전·대량 호출 데모는 포함하지 않습니다. 여러 요청을 처리하는 실제 서비스로 확장한다면 자격 증명과 SecretClient를 재사용하고 적절한 캐시 정책을 적용합니다.

SDK는 일시적 실패에 최대 세 번 재시도하며, 앱은 취소 토큰으로 30초 작업 예산을 전달합니다. disabled·expired·not-before 상태는 성공으로 처리하지 않습니다.

## 7. 오류 확인

| 출력 / 종료 코드 | 확인 항목 |
|---|---|
| `CONFIG_ERROR` / 1 | 환경 변수 누락, Tenant/Client ID 형식, Vault URL, Secret 이름 |
| `OPERATION_ERROR` / 1 | 비활성·만료·아직 유효하지 않은 Secret |
| `AUTH_FAILED` / 2 | SP 정보, Client Secret Value/만료, 테넌트 정책 |
| `KEY_VAULT_ERROR status=403` / 2 | SP의 역할/정책·범위·변경 전파 및 Vault 네트워크 제한 |
| `KEY_VAULT_ERROR status=404` / 2 | Vault와 Secret 이름, 삭제 여부 |
| `KEY_VAULT_ERROR status=429` / 2 | 호출량과 재시도 정책 |
| `NETWORK_ERROR` 또는 서비스 오류 / 2 | DNS·프록시·TLS·방화벽·서비스 상태 |
| `TIMEOUT` / 3 | 작업 시간 예산 초과 또는 취소 |

CLI 로그인을 다시 하는 것으로 SP의 잘못된 Client Secret을 해결할 수는 없습니다. 이 앱은 SP 인증만 사용하며 다른 신원으로 대체하지 않습니다.

## 8. 실습 종료 후 수동 정리

**반복 실행 중에는 아래 명령을 실행하지 않습니다.** 실습이 모두 끝난 뒤, 창을 유지하면서 자격 증명을 지우고 싶을 때만 다음 명령으로 현재 창의 설정을 제거합니다. 이 명령은 위 실행 절차에서 자동 호출되지 않습니다.

```powershell
foreach ($name in @("AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "KEY_VAULT_URL", "KV_SECRET_NAME")) {
    if (Test-Path "Env:$name") { Remove-Item "Env:$name" }
}
```

관리자는 교육 종료 후 교육용 Client Secret과 역할/정책을 회수합니다. 참가자는 공용 Vault나 다른 사람의 자원을 삭제하지 않습니다.

## 참고 링크

- [로컬 .NET 앱의 SP 인증](https://learn.microsoft.com/dotnet/azure/sdk/authentication/local-development-service-principal)
- [Key Vault RBAC 역할](https://learn.microsoft.com/azure/key-vault/general/rbac-guide)
- [Access Policy 설정](https://learn.microsoft.com/azure/key-vault/general/assign-access-policy)
