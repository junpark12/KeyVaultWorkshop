# Azure Key Vault Developer Workshop

.NET 개발자와 DevOps 담당자를 위한 **Azure Key Vault 개념·코드·실습 워크숍**입니다.
애플리케이션에서 Secret을 읽는 방법부터, 공유 Vault의 호출량 최적화와 장애 대응까지
개발자가 실제로 판단해야 하는 내용을 한국어 설명 문서와 실행 가능한 샘플로 다룹니다.

단순히 연결에 성공하는 데서 끝나지 않고, **누가 어떤 Secret을 읽는지, 값은 언제 갱신되는지,
Key Vault 호출이 실패하면 앱은 어떻게 동작하는지**를 코드와 관측 결과로 확인하는 것이 목표입니다.

## 교육 구성

| 모듈 | 주요 내용 | 설명 문서 | 발표 자료 |
|---|---|---|---|
| 01. 개요와 접근 | Secret·Key·Certificate, 인증과 권한, 개발 환경 접근 | [문서](docs/01-overview-and-access.md) | [PPT](Module01_KeyVault_Overview_and_Access_Revised.pptx) |
| 02. .NET 연동 | Configuration Provider와 직접 조회, 인증, 메모리 캐시, 버전 | [문서](docs/02-dotnet-integration.md) | [PPT](Module02_DotNet_KeyVault_Integration.pptx) |
| 03. 규모와 캐시 | 앱별·공유 Vault 경계, 필요한 값만 조회, TTL·Single-flight·Jitter | [문서](docs/03-scale-and-cache.md) | [PPT](Module03_Scale_and_Cache.pptx) |
| 04. 장애 대응 | 429와 SDK 재시도, 초기 로딩·reload 실패, Metrics와 감사 로그 | [문서](docs/04-resilience.md) | [PPT](Module04_Resilience_and_Throttling.pptx) |

교육은 **1~4장**으로 구성합니다. Module 03은 설계·코드 설명 중심이며 별도 Lab을 두지 않습니다.

## 실습

| 실습 | 확인할 내용 |
|---|---|
| [Lab 01 — SP로 Secret 읽기](labs/01-secret-read/README.md) | .NET Framework 4.8 콘솔에서 Service Principal 인증과 Secret 조회 |
| [Lab 02 — .NET 애플리케이션 연동](labs/02-dotnet-integration/README.md) | Framework 콘솔·.NET 10 웹 앱, Configuration·직접 조회·재로딩·캐싱 비교 |
| [Lab 04 — Throttling과 장애 대응](labs/04-resilience/README.md) | 강사의 제한된 부하 발생과 참가자의 SDK 재시도·Configuration 갱신 관찰 |

각 실습 README에 준비 조건, 실행할 프로젝트, 환경 변수와 명령을 안내합니다.
[공통 소스 안내](labs/common/README.md)는 기존 통합 콘솔의 구성과 사용법을 설명합니다.

### 준비 환경

- **Windows와 PowerShell**을 기준으로 진행합니다. Framework 4.8 실행에는 Windows가 필요합니다.
- **.NET 10 SDK**와 .NET Framework **4.8 또는 호환되는 4.8.1 런타임**을 준비합니다.
  프로젝트별 빌드 참조 어셈블리는 NuGet 패키지를 사용하며, 자세한 요구 사항은 각 실습에 따릅니다.
- NuGet 또는 승인된 사내 패키지 피드, Microsoft Entra ID와 실습 Vault의 HTTPS 엔드포인트에 접근할 수 있어야 합니다.
- 기본 참가자 경로는 **강사가 준비한 실습 Vault와 읽기 전용 SP**를 사용합니다.
  참가자 개인 Azure 구독이나 리소스 배포는 필요하지 않습니다.
- DefaultAzureCredential 실습의 인증 도구와 강사·관리자의 Azure CLI·Portal 접근은 해당 실습 안내에 따릅니다.

먼저 저장소를 내려받고, 사용할 실습의 README를 따라 진행합니다.

```powershell
git clone https://github.com/junpark12/KeyVaultWorkshop.git
Set-Location .\KeyVaultWorkshop
```

프로젝트마다 옵션이 다르므로 저장소 루트에서 `dotnet run`만 실행하는 방식은 사용하지 않습니다.
소스나 설정을 변경한 뒤 `--no-build`로 실행하려면 먼저 해당 프로젝트를 다시 빌드합니다.

## 핵심 학습 포인트

- **Configuration 읽기와 Vault 호출은 별개입니다.** 앱은 메모리 값을 읽고 Provider가 백그라운드에서 변경을 확인합니다.
- **최초 로딩 실패와 reload 실패는 다릅니다.** 신규 프로세스는 사용할 값이 없지만, 실행 중인 Provider는 갱신 실패 시 기존 값을 유지할 수 있습니다.
- **직접 조회의 캐시·실패 정책은 앱이 구현합니다.** Fail closed와 제한적 기존 값 사용은 업무·보안 요구에 따라 선택합니다.
- **캐시된 값이 있어도 자격 증명이 유효하다는 보장은 없습니다.** 원본에서 폐기한 비밀번호는 캐시만으로 계속 사용할 수 없습니다.
- **개별 Vault 한도와 구독·리전 합산 한도가 함께 적용됩니다.** 평균 호출량뿐 아니라 동시 시작과 짧은 시간의 집중을 고려합니다.

## 안전한 실습

**교육용 가상 Secret과 격리된 실습 Vault만 사용하세요.**

- Lab 02의 일부 콘솔·웹 예제는 학습을 위해 값을 평문으로 표시합니다. 실제 DB 비밀번호나 운영 API 키를 넣지 않습니다.
- Lab 04 부하 발생은 강사만 실습 전용 Vault에서 수행합니다. 운영·공용 Vault를 대상으로 실행하지 않습니다.
- Client Secret·토큰·실제 Secret 값을 소스, 설정 파일, 로그, 캡처, 커밋에 남기지 않습니다.
  자격 증명 입력은 각 실습에서 안내하는 숨김 입력 등 승인된 경로를 사용합니다.
- 환경 변수도 비밀 저장소가 아닙니다. 실습 후 자격 증명을 제거하고 강사는 교육용 접근 권한을 회수합니다.
- `.gitignore`는 빌드 산출물과 로컬 설정 등을 제외합니다. 이미 추적된 파일의 비밀을 자동으로 제거하지는 않습니다.

이 저장소는 교육용이며 운영 환경에 그대로 배포하는 완성형 솔루션이 아닙니다.
실제 적용 시 조직의 보안 정책, 네트워크·권한 경계, 회전 정책과 서비스 가용성 요구를 검토해야 합니다.

## 폴더 구성

```text
docs/                   모듈 01~04 한국어 설명 문서
labs/01-secret-read/     SP 인증 기반 독립 조회 실습
labs/02-dotnet-integration/
                        Configuration·직접 조회·캐시 실습
labs/04-resilience/     강사 부하·참가자 재시도 및 reload 실습
labs/common/            공통 통합 콘솔과 소스 안내
Images/                 문서·실습 캡처
Module*.pptx            모듈별 발표 자료
```

## 참고 자료

- [Azure Key Vault 개요](https://learn.microsoft.com/azure/key-vault/general/overview)
- [Key Vault Secrets 보안](https://learn.microsoft.com/azure/key-vault/secrets/secure-secrets)
- [Key Vault 서비스 한도](https://learn.microsoft.com/azure/key-vault/general/service-limits)
- [Throttling 대응 지침](https://learn.microsoft.com/azure/key-vault/general/overview-throttling)
- [Key Vault 감사 로그](https://learn.microsoft.com/azure/key-vault/general/logging)

세부 근거와 추가 링크는 각 모듈 문서에 수록되어 있습니다. 서비스 한도와 SDK 동작은
사용 중인 버전 및 최신 공식 문서를 함께 확인하세요.
