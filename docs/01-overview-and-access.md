# 01. Key Vault 개념과 접근 구조

> 기반 자료: [Key Vault의 Keys, Secrets, Certificates](https://learn.microsoft.com/azure/key-vault/general/about-keys-secrets-certificates), [Key Vault RBAC 가이드](https://learn.microsoft.com/azure/key-vault/general/rbac-guide), [네트워크 보안](https://learn.microsoft.com/azure/key-vault/general/network-security)

## 0. 한눈에 보는 요약

Azure Key Vault는 **비밀 문자열(Secrets), 암호 키(Keys), 인증서(Certificates)**를 보관하고 사용을 통제하는 서비스입니다. 먼저 무엇을 저장하고 언제 사용하는지 구분한 뒤, 앱이 접근하기 위한 인증·권한·네트워크를 살펴봅니다.

## 1. 사전 개념 정리 — Key Vault가 제공하는 세 가지 객체

하나의 Vault 안에서 목적에 따라 세 가지 객체를 관리합니다. 모두 보안과 관련되지만 **앱이 값을 가져다 쓰는지, 암호 연산을 요청하는지, 인증서를 서비스에 적용하는지**가 다릅니다.

| 종류 | 무엇을 관리하나요? | 언제 사용하나요? |
|---|---|---|
| **Secret — 비밀 문자열** | DB 암호, API 키, 암호가 포함된 연결 문자열 | 앱이 외부 서비스에 접속하기 위해 자격 증명 값을 가져다 사용할 때 |
| **Key — 암호 키** | RSA·EC 등의 암호 연산용 키 | 키 종류에 따라 민감한 데이터를 암복호화하거나, 데이터 암호화 키 보호·전자서명에 사용할 때 |
| **Certificate — 인증서** | TLS 등의 인증서와 연결된 키·Secret, 유효 기간과 갱신 정책 | HTTPS 서비스의 인증서를 중앙에서 관리하고 서비스에 적용할 때 |

### 1.1 Secret 시나리오 — AKS Pod가 DB 연결 문자열을 사용

**상황:** AKS에 배포된 주문 API가 DB에 연결해야 합니다. DB 암호가 포함된 연결 문자열을 컨테이너 이미지나 배포 YAML에 직접 넣지 않고 Key Vault의 Secret으로 저장합니다.

```text
Key Vault의 Secret: orders-db-connection
    → 접근 권한이 있는 Pod에 전달
    → Pod 안의 앱이 연결 문자열을 읽음
    → DB에 연결
```

전달 방식은 앱이 **SDK로 직접 조회**하거나, **Secrets Store CSI Driver의 Azure Key Vault Provider**를 통해 Pod에 파일로 마운트하는 방식 등을 선택할 수 있습니다. AKS의 앱 신원은 Workload Identity 같은 적절한 방식을 구성하며, Secret을 저장해 두었다고 모든 Pod가 자동으로 읽을 수 있는 것은 아닙니다.

Key Vault의 Secret과 Kubernetes의 `Secret` 리소스는 서로 다른 객체입니다. CSI 파일 마운트와 Kubernetes Secret으로의 동기화도 구분합니다. DB가 Entra 인증을 지원한다면 암호를 저장하는 대신 토큰으로 직접 연결하는 방식도 검토합니다.

### 1.2 Key 시나리오 — 문자열을 암호화해서 DB에 저장

**상황:** 앱이 민감한 문자열을 DB에 저장해야 합니다. 원래 문자열 그대로 저장하지 않고, Key Vault의 Key를 사용해 암호화한 뒤 **암호문만 DB에 저장**합니다. 값을 사용할 때는 다시 복호화합니다.

```text
저장: 원래 문자열 → Key Vault의 Key로 암호화 → 암호문을 DB에 저장

조회: DB에서 암호문 조회 → Key Vault에 복호화 요청 → 원래 문자열 반환
```

이 예에서는 암호화·복호화를 지원하는 RSA Key 등을 사용합니다. 앱이 암복호화 흐름을 구현하고, Key Vault는 암호 키를 관리하며 허용된 연산을 수행합니다. **앱이 개인 키 파일을 내려받아 보관하는 대신 암호문이나 복호화 결과를 받는 방식**입니다.

**Key Vault의 Secret에 저장하는 것과는 어떻게 다른가요?**

```text
앱 → Key Vault에 문자열을 Secret으로 저장
   → Key Vault가 저장 시 암호화를 자동 처리
   → 권한 있는 앱이 Secret을 조회하면 원래 문자열 반환
```

Secret으로 저장할 때는 **사용자가 별도의 Key를 만들거나 직접 암복호화할 필요가 없습니다.** 반면 위의 DB 저장 시나리오는 앱이 Key를 이용해 데이터를 암호화하고, 그 결과를 Key Vault 밖의 DB에 보관하는 경우입니다.

| 구분 | Secret에 저장 | Key를 이용해 암호화 후 DB에 저장 |
|---|---|---|
| 데이터 보관 위치 | Key Vault의 Secret | 앱이 사용하는 DB |
| 앱이 구현할 흐름 | Secret 저장·조회 | 암호화·DB 저장·조회·복호화 |
| 별도 Key 관리 | Secret 사용을 위해 직접 만들 필요 없음 | 사용할 Key와 암호 연산 권한 관리 필요 |

키 종류·알고리즘에 따라 직접 암호화할 수 있는 데이터 크기에 제한이 있습니다. 큰 데이터는 보통 **별도의 AES 키로 암호화하고, 그 AES 키를 Key Vault의 Key로 감싸서 보호**합니다. 큰 파일 전체를 Key Vault에 보내 암호화하는 방식은 아닙니다.

> **핵심 구분:** Secret은 **비밀 값을 보관하는 기능**, Key는 **데이터를 암복호화하거나 서명할 때 쓰는 키를 관리·사용하는 기능**입니다.

API 서비스에서 발급받은 “API key”는 문자열 자격 증명이므로 보통 **Secret**에 저장합니다. 이름에 key가 들어간다고 Key Vault의 **Keys** 객체가 되는 것은 아닙니다.

### 1.3 Certificate 시나리오 — 웹 서비스에 HTTPS 인증서 적용

**상황:** Application Gateway v2가 웹 서비스 앞에서 HTTPS 요청을 받습니다. 서버 인증서를 Key Vault에서 관리하고 Gateway가 가져다 사용하도록 연동합니다.

```text
Key Vault에서 TLS 인증서 관리
    → Application Gateway가 인증서와 개인 키를 가져와 적용
    → 사용자가 HTTPS로 접속
    → Gateway에서 TLS 연결 처리 후 백엔드로 전달
```

Certificate 객체를 만들면 같은 이름의 Key와 Secret도 함께 만들어집니다. Application Gateway 연동은 인증서에 연결된 **Secret**을 통해 개인 키를 포함한 PFX를 읽으므로, 내보낼 수 있는 개인 키 등 해당 서비스의 요구 조건을 충족해야 합니다.

Key Vault에 인증서를 저장하는 것만으로 웹 서비스에 HTTPS가 설정되는 것은 아닙니다. 인증서 발급·갱신 정책과 사용하는 서비스의 연동·새 버전 반영을 각각 구성해야 합니다.

> **이번 교육 범위:** 세 가지 객체의 용도를 이해하고, 실제 실습은 **Secrets 조회·캐싱·버전 변경**에 집중합니다. 위 AKS·DB 암복호화·HTTPS 흐름은 개념 설명용이며, 해당 인프라를 배포하는 실습은 포함하지 않습니다.

## 2. Key Vault의 두 가지 권한 모델과 인증 방법

### 2.1 화면의 두 가지 선택은 무엇이 다른가요?

Portal의 **Permission model**에 표시되는 **Azure role-based access control(RBAC)**과 **Vault access policy**는, 정확히는 서로 다른 인증 방식이 아니라 **데이터 접근 권한을 부여하는 두 가지 방식**입니다.

**인증**은 “누가 접근하는가”, **권한 부여**는 “그 신원이 무엇을 할 수 있는가”를 결정합니다. 두 모델 모두 Microsoft Entra ID에서 인증받은 사용자, Service Principal(SP), Managed Identity 등을 사용할 수 있습니다.

```text
사용자 로그인 / SP 자격 증명 / Managed Identity
                    ↓
           Entra ID에서 토큰 발급          ← 인증
                    ↓
        Key Vault가 접근 권한 확인        ← 권한 부여
           ├─ Azure RBAC: 할당된 역할
           └─ Access Policy: 허용한 작업
                    ↓
         Secret 조회 / Key 연산 / 인증서 사용
```

한 Vault의 데이터 접근에는 **현재 선택된 권한 모델**이 적용됩니다. RBAC와 Access Policy의 허용 권한을 합쳐서 판단하는 방식이 아닙니다. Access Policy 모드에서 IAM에 `Key Vault Secrets User` 역할만 추가하거나, RBAC 모드에서 Access Policy만 설정해서는 원하는 데이터 접근 권한을 얻을 수 없습니다.

| 비교 항목 | Azure RBAC | Vault access policy |
|---|---|---|
| 권한 표현 | Secrets User, Crypto User 같은 **역할** 할당 | Get, Set, Encrypt 같은 **작업** 선택 |
| Portal 설정 위치 | **Access control (IAM)** | **Access policies** |
| 적용 범위 | 구독·리소스 그룹·Vault 등의 범위와 상속. 개별 객체 범위도 지원 | 해당 Vault 안의 객체 종류별 권한. 개별 Secret 이름별로 제한하지 못함 |
| 일반적인 권장 단위 | 앱·환경별 Vault에 필요한 역할 할당 | 기존 Vault에서 필요한 작업만 허용 |
| 권한 관리와 리소스 관리 분리 | 역할 할당 권한과 Vault 구성 변경 권한을 분리 가능 | Vault 구성 변경 권한 보유자가 정책을 수정할 수 있어 주의 |
| 신규 도입 | **Microsoft 권장 방식** | 기존 환경의 레거시 모델 |

> **관리 평면과 데이터 평면:** Vault 생성·네트워크 설정 같은 리소스 관리는 어느 모델에서도 Azure RBAC로 제어합니다. 위 두 가지 중 선택하는 것은 Secret 조회·Key 연산 같은 **데이터 평면**의 권한 모델입니다. Portal에 Vault가 보인다고 Secret 값을 읽을 수 있는 것은 아닙니다.

### 2.2 RBAC 설정, Secret 생성, 버전·IAM 확인 [Demo]

**핵심 메시지:** Secret의 **값과 버전**을 관리하는 것과, **누가 접근할 수 있는지** 정하는 것은 별개의 작업입니다.

**시연 환경:** 별도 교육용 구독의 Key Vault에서 가상 값만 사용합니다. 강사는 권한 모델 변경에 필요한 권한과 Secret 생성용 `Key Vault Secrets Officer` 역할을 사전에 준비합니다. 구독 Owner라는 이유만으로 Secret 데이터 작업이 자동 허용되는 것은 아닙니다.

| 순서 | Portal에서 보여줄 화면과 동작 | 설명할 포인트 |
|---|---|---|
| **1. RBAC 설정** | Key Vault → **Access configuration → Permission model → Azure role-based access control** 선택·적용 | 로그인 방법을 바꾸는 것이 아니라 **데이터 접근 권한을 RBAC로 판단하도록 설정** |
| **2. Secret 추가** | **Secrets → Generate/Import**에서 이름 `workshop-demo-secret`, 값 `TRAINING-ONLY-NOT-A-REAL-CREDENTIAL-V1`로 생성 | 앱이 사용할 문자열을 Secret으로 보관. **최초 버전이 함께 생성**됨 |
| **3. 버전·IAM 확인** | 생성한 Secret 선택 → **Versions**에서 현재 버전·Enabled 상태 확인 → 버전을 열어 식별자·속성 확인 → Secret의 **Access control (IAM)**에서 역할 할당과 범위 확인 | 버전은 **어떤 값의 이력인지**, IAM은 **누가 어떤 작업을 할 수 있는지**를 보여줌 |

```text
Vault의 권한 모델: RBAC
    → Secret 생성
    → Secret 상세: 버전·상태 확인
    → Secret IAM: 접근 주체·역할·범위 확인
```

### 2.3 Azure RBAC — 역할별로 할 수 있는 일

RBAC는 **신원 + 역할 + 범위**로 구성합니다. 예를 들어 “참가자 p01의 SP에게 실습 Vault 범위의 Key Vault Secrets User 역할”을 할당하면, 그 SP는 해당 범위에서 Secret 값을 읽을 수 있습니다.

아래는 역할이 다루는 **작업 대상**별 그룹입니다. 실제 RBAC의 **할당 범위(Scope)**와는 구분합니다. 예를 들어 `Key Vault Secrets User`는 Secrets를 읽는 역할이지만, Vault에 할당하면 해당 Vault의 Secrets에, 특정 Secret에 할당하면 그 Secret에 적용됩니다. `Reader`도 Vault뿐 아니라 구독·리소스 그룹 범위에 할당할 수 있습니다.

#### Vault 리소스 관리 — Vault 자체의 구성·수명 관리

| 역할 | 할 수 있는 일 | 주의점·사용 대상 |
|---|---|---|
| **Reader** | Vault 같은 Azure 리소스의 구성 정보 읽기 | 일반 Azure 관리 역할. Secret 값 읽기 권한은 아님 |
| **Key Vault Contributor** | Vault 생성·설정 변경·삭제 등 리소스 관리 | 이 역할 자체는 Secret 값 읽기를 허용하지 않음. Access Policy 모드에서는 정책 수정 가능성에 주의 |
| **Key Vault Purge Operator** | Soft-deleted **Vault 리소스** 영구 삭제 | 개별 Secret/Key의 purge 권한과 구분. Purge protection을 우회하지 못함 |

#### 전체 객체 공통 — Secrets·Keys·Certificates

| 역할 | 할 수 있는 일 | 주의점·사용 대상 |
|---|---|---|
| **Key Vault Reader** | Keys·Secrets·Certificates의 메타데이터 조회 | Secret 값이나 개인 키 같은 민감한 내용은 읽지 못함 |
| **Key Vault Administrator** | Keys·Secrets·Certificates 전체 데이터 작업 | 이름과 달리 Vault 리소스 관리나 역할 할당까지 포함하지 않음. 일반 앱에는 과도함 |

#### Secrets — 비밀 문자열 조회·관리

| 역할 | 할 수 있는 일 | 주의점·사용 대상 |
|---|---|---|
| **Key Vault Secrets User** | Secret 값 조회 | 읽기 전용 앱/참가자용. 인증서에 연결된 Secret의 내보낼 수 있는 개인 키에도 접근할 수 있어 범위에 주의 |
| **Key Vault Secrets Officer** | Secret 생성·값 조회·새 버전 저장·속성 변경·삭제·복구·백업 등 | Secret 관리자용. 역할 할당 권한은 아님 |

#### Keys — 암호 연산·키 관리

| 역할 | 할 수 있는 일 | 주의점·사용 대상 |
|---|---|---|
| **Key Vault Crypto User** | Key를 이용한 암복호화·서명/검증·wrap/unwrap 등 암호 연산 | 키 종류와 허용 연산에 따름. Key 생성·삭제 같은 수명 관리 역할과 구분 |
| **Key Vault Crypto Service Encryption User** | Key 메타데이터 조회, wrap/unwrap으로 데이터 암호화 키 보호 | 키 감싸기/풀기만 필요한 서비스용. 일반적인 Encrypt/Decrypt/Sign 전체 권한은 아님 |
| **Key Vault Crypto Officer** | Key 생성·가져오기·변경·회전·삭제·복구·암호 연산 등 | Key 관리자용. 역할 할당 권한은 아님 |

#### Certificates — 인증서 사용·관리

| 역할 | 할 수 있는 일 | 주의점·사용 대상 |
|---|---|---|
| **Key Vault Certificate User** | 인증서 및 연결된 Secret·Key 부분 읽기 | 개인 키 내보내기 가능 여부 등의 제한을 우회하지 않음 |
| **Key Vault Certificates Officer** | 인증서 생성·가져오기·갱신 정책 관리·삭제·복구 등 인증서 관리 | 인증서 관리자용. 역할 할당 권한은 아님 |

#### 권한 관리 — 누가 데이터 역할을 갖는지 관리

| 역할 | 할 수 있는 일 | 주의점·사용 대상 |
|---|---|---|
| **Key Vault Data Access Administrator** | 조건으로 허용된 Key Vault 데이터 역할의 할당·제거 | 권한 관리자용. 이 역할 자체가 Secret 값 조회 권한을 주지는 않음 |

**실습의 역할 분리:** 참가자/읽기 앱은 `Key Vault Secrets User`, 교육용 Secret을 만드는 강사는 `Key Vault Secrets Officer`, 역할을 부여하는 담당자는 별도의 역할 할당 권한을 사용합니다. 단순 조회를 위해 구독 `Contributor`나 `Key Vault Administrator`를 주지 않습니다.

Portal에서는 **Vault → Access control (IAM) → Add role assignment → 역할 선택 → 사용자/앱 신원 선택** 순서로 설정합니다. 역할 할당은 Owner, User Access Administrator 또는 허용 범위 내의 Key Vault Data Access Administrator 등 필요한 권한이 있는 담당자가 수행해야 합니다.

Vault 범위의 기본 Secrets User 역할은 특정 이름 한 개가 아니라 해당 Vault의 Secrets에 적용됩니다. 앱의 이름 허용 목록이나 `workshop-p01-` 접두사는 권한 격리가 아닙니다. 격리가 필요하면 참여자별 Vault를 우선 검토하고, 개별 객체 범위 할당은 관리 부담을 고려하여 필요한 경우에 사용합니다.

### 2.4 Vault access policy — 필요한 작업을 직접 허용

Access Policy는 사용자나 SP 같은 **Principal(접근 주체)**을 선택하고, **Secret permissions / Key permissions / Certificate permissions**에서 허용할 작업을 고릅니다. 정책은 “이 주체가 이 Vault 안에서 어떤 종류의 객체에 어떤 작업을 할 수 있는가”를 나타냅니다.

**Secrets의 주요 권한**

| 권한 | 할 수 있는 일 | 예 |
|---|---|---|
| **Get** | 이름·버전을 지정하여 Secret 값과 속성 조회 | 앱이 DB 연결 문자열 읽기 |
| **List** | Secret 이름·속성 또는 버전 목록 조회 | 관리 도구가 목록 표시. 값 읽기는 별도 Get 필요 |
| **Set** | 새 Secret 생성 또는 같은 이름의 새 버전 저장 | 교육용 값을 v1에서 v2로 변경 |
| **Delete** | Secret 삭제 | 해당 이름의 모든 버전에 영향. 개별 버전 삭제가 아님 |
| **Recover** | Soft-deleted Secret 복구 | 잘못 삭제한 Secret 복구 |
| **Backup / Restore** | Secret의 보호된 백업 및 복원 | 복원 대상 구독·지역권역 등 서비스 제약 확인 |
| **Purge** | Soft-deleted Secret 영구 삭제 | Purge protection을 우회하지 못하며 일반 앱에는 부여하지 않음 |

**Keys·Certificates의 주요 권한**

| 대상 | 대표 작업 | 사용 예 |
|---|---|---|
| Keys 조회 | Get, List | 공개 키·속성 조회. Get으로 개인 키를 내려받는 것은 아님 |
| Keys 사용 | Encrypt, Decrypt, Sign, Verify, Wrap Key, Unwrap Key | 암복호화, 전자서명, 데이터 암호화 키 보호 |
| Keys 관리 | Create, Import, Update, Rotate, Delete, Recover 등 | 암호 키 생성·회전·복구 |
| Certificates 조회 | Get, List | 인증서 및 속성 조회 |
| Certificates 관리 | Create, Import, Update, Delete, Recover, 발급자 관리 등 | 인증서 발급·가져오기·정책 관리 |

**Certificate Get만으로 개인 키가 포함된 PFX를 가져오는 것은 아닙니다.** 앞의 Application Gateway 예처럼 연결된 Secret을 읽는 연동에는 **Secret Get** 권한이 필요합니다. Key의 암호 연산도 키 종류·알고리즘과 해당 키의 허용 연산에 맞아야 합니다.

Portal에서는 **Vault → Access policies → Create → 필요한 작업 선택 → Principal 선택 → 검토·저장** 순서로 설정합니다. 예를 들어 이름을 알고 있는 Secret 한 개를 조회하는 샘플에는 Secret **Get**이면 됩니다. Portal이나 관리 도구에서 목록을 탐색해야 하면 **List**도 필요할 수 있습니다.

주의할 점은 **Access Policy의 Secret Get이 특정 이름 한 개에만 적용되는 것이 아니라 해당 Vault의 Secrets에 적용**된다는 것입니다. 또한 Contributor, Key Vault Contributor 또는 `Microsoft.KeyVault/vaults/write` 권한 보유자가 정책을 수정하여 자신에게 데이터 접근을 줄 수 있습니다. 따라서 신규 설계는 RBAC를 권장하고, 기존 Access Policy 환경에서는 Vault 구성 변경 권한도 엄격하게 관리합니다.

### 2.5 두 권한 모델은 언제 선택하나요?

| 상황 | 선택 기준 |
|---|---|
| 신규 앱·신규 워크숍 Vault | **RBAC 권장**. Secrets User/Officer 등 역할로 책임 분리 |
| 여러 팀·앱의 권한을 Azure 표준 역할 체계로 관리 | RBAC의 범위·상속·역할 관리 체계 활용 |
| 이미 Access Policy로 운영 중인 고객 Vault | 현재 방식에서 최소 권한으로 연동. 전환은 별도 변경 계획으로 수행 |
| SP 또는 Managed Identity를 사용하고 싶음 | **두 모델 모두 가능**. 신원 종류 때문에 Access Policy를 선택할 필요 없음 |
| 기존 앱이 작동하지 않아 권한 모델을 바꾸려는 경우 | 먼저 신원·역할/정책·네트워크 확인. 모델 전환으로 문제를 임의 해결하지 않음 |

기존 Vault를 Access Policy에서 RBAC로 바꾸면 기존 정책이 데이터 권한 판단에 사용되지 않습니다. 필요한 역할을 준비하고 영향도·전환·복구 절차를 확인하지 않으면 앱 접근이 중단될 수 있습니다. 고객의 Terraform 관리 대상은 승인된 IaC/변경 절차를 따릅니다.

### 2.6 누가 인증할 것인가 — 사용자·SP·Managed Identity 선택

| 실행 환경 | 권장 출발점 | 특징 |
|---|---|---|
| 본인 Azure 접근 권한이 있는 개발자 PC/개인 VDI | 사용자 로그인 + `AzureCliCredential` | `az login` 후 개인 신원으로 접근. 별도 Client Secret 불필요 |
| 참가자 계정에 강사 테넌트 접근을 준비하기 어려운 워크숍 | **참가자별 실습 전용 SP + Client Secret** | 앱이 직접 인증하면 참가자 Azure CLI/`az login` 불필요. 단기 자격 증명 배포·회수 필요 |
| 지원되는 Azure App Service·VM 등 | `ManagedIdentityCredential` | 운영 앱이 인증용 암호를 직접 관리하지 않음 |
| AKS Pod·지원되는 CI/CD 환경 | Workload Identity / 자격 증명 연합 | 연합 설정으로 토큰 교환. 단순 Client Secret 복사와 다른 구성 |
| Azure 밖의 무인 서비스 | 실행 환경에 맞는 SP 인증서·자격 증명 연합 등을 검토 | Client Secret을 사용한다면 저장·갱신·폐기까지 설계 |

SP는 별도의 테넌트 사용자 계정을 나눠 쓰는 대신 **앱 전용 신원**으로 인증하는 방식입니다. Client Secret은 그 앱의 비밀번호에 해당하며, Key Vault에 저장된 실습용 Secret과 다른 자격 증명입니다. **SP를 쓴다고 비밀번호 관리 문제가 없어지는 것은 아닙니다.**

### 2.7 SP로 Key Vault에 연동하는 절차 [Demo]

**예시 구성:** 강사의 별도 실습 Vault에 가상 데이터만 넣고, 참가자 p01에게 읽기 전용 SP를 제공합니다. 참가자가 강사 개인 계정이나 CLI 로그인 토큰을 공유받는 방식은 사용하지 않습니다.

```text
참가자 PC의 .NET 앱
  Tenant ID + Client ID + Client Secret
                    ↓ ClientSecretCredential
          Entra ID에서 앱 토큰 발급
                    ↓
        Key Vault의 Secret 조회 요청
                    ↓
  SP에 할당된 RBAC 역할 또는 Access Policy 확인
                    ↓
          권한이 있으면 Secret 반환
```

#### A. 강사/관리자 — 앱 신원과 단기 자격 증명 준비

1. Vault가 속한 테넌트의 **Microsoft Entra ID → App registrations → New registration**에서 `kv-workshop-p01` 같은 실습 전용 앱을 등록합니다. 동일 테넌트의 앱 신원으로 접근하므로 이 예시는 Single tenant로 구성합니다.
2. **Application (client) ID**와 **Directory (tenant) ID**를 기록합니다.
3. **Enterprise applications**에서 연결된 Service Principal의 **Object ID**를 확인합니다.
4. **Certificates & secrets → New client secret**에서 교육에 필요한 짧은 만료 기간을 설정합니다. 표시되는 **Value**를 승인된 안전한 경로로 참가자에게 개별 전달합니다.

| 식별자/값 | 사용처 | 혼동하지 않을 항목 |
|---|---|---|
| Tenant ID | 토큰을 발급받을 테넌트 선택 | Subscription ID와 다름 |
| Client ID | 앱 인증에서 어떤 앱인지 식별 | SP Object ID와 다름 |
| Client Secret **Value** | 앱 인증 자격 증명 | Portal의 Secret ID를 넣으면 인증되지 않음 |
| SP Object ID | RBAC/Access Policy의 권한 대상 | App registrations 객체 자체의 Object ID와 다름 |

앱 등록·Client Secret 생성과 Vault 권한 할당은 별개입니다. 필요한 담당 권한도 다를 수 있습니다. 이 앱 자격 증명 기반의 Key Vault 직접 접근을 위해 불필요한 Microsoft Graph API 권한을 추가하지 않습니다.

#### B. 강사/관리자 — 현재 권한 모델에 맞게 읽기 허용

**RBAC를 사용하는 Vault:** IAM에서 위 SP에 실습 Vault 범위의 **Key Vault Secrets User** 역할을 부여합니다.

**Access Policy를 사용하는 Vault:** Access policies에서 위 SP를 Principal로 선택하고 **Secret permissions → Get**을 부여합니다. 목록 조회가 필요한 경우에만 List를 추가합니다.

두 방식을 모두 적용하는 것이 아니라 **현재 Vault 모델에 해당하는 방식 하나**를 사용합니다. 역할·정책 변경 전파 시간을 고려하고, 강사의 관리자 계정이 아닌 실제 참가자용 SP로 읽기를 확인합니다. 참가자에게 Secret 생성·삭제나 구독 Contributor 권한을 주지 않습니다.

#### C. 참가자 — 앱이 SP 자격 증명으로 직접 인증

### 2.8 Lab1 실습
