# PR #5 리뷰 대응 작업

작성일: 2026-10-23

- 대상: [PlaynitePorting PR #5](https://github.com/HoleInOnePlatform/PlaynitePorting/pull/5)
- 검토 기준 커밋: `58b24014059ca2ff2787939121f590814ee28217`
- 원문: [필수 항목 리뷰](https://github.com/HoleInOnePlatform/PlaynitePorting/pull/5#pullrequestreview-5609585296), [권장·정리 리뷰](https://github.com/HoleInOnePlatform/PlaynitePorting/pull/5#pullrequestreview-5609886083)
- 연계: [Backend PR #4](https://github.com/HoleInOnePlatform/Backend/pull/4)

이 문서는 리뷰 검증 결과를 구현 가능한 작업으로 정리한다. 체크되지 않은 항목은 아직 구현·검증 완료를 뜻하지 않는다. 기존 `TASKS.md`와 `REFACTORING_TASKS.md`의 작업 이력은 별도로 유지한다.

## 작업 시작 전

- [x] 현재 브랜치·커밋·미커밋 변경을 확인하고 PR 기준 이후 변경과 대조한다. 기존 사용자 변경을 덮어쓰지 않는다.
- [x] 아래 문제들이 최신 코드에도 남아 있는지 확인한다. 이미 수정된 항목은 변경 근거와 검증 결과를 기록한다.

## 1. 머지 전 수정

### 1-1. CI의 Backend 소스 의존 해결

대상: `.github/workflows/refactoring-checks.yml`, `.github/workflows/v5-prewarm.yml`, `build/Build-SteamLink.ps1`, `tools/SteamLink/SteamLink.csproj`

확인된 문제:
- 새 CI는 Backend checkout에서 `Repository not found`로 실패한다.
- 기존 CI는 Backend checkout 없이 빌드하여 `SteamLink.Core/Credential.cs` 누락으로 실패한다.

작업:
- [x] SteamLink.Core 공급 방식을 결정한다. Backend 읽기 권한을 가진 전용 인증으로 고정 리비전을 checkout하거나, 버전이 고정된 공유 패키지·소스 배포 방식으로 전환한다.
- [x] 두 workflow 모두 같은 의존성 준비 절차와 예상 디렉터리 구조를 사용하도록 수정한다.
- [x] 공개 저장소·외부 기여 PR에서도 빌드할 수 있어야 하는지 결정하고, secret을 사용할 수 없는 PR의 검증 방식을 명시한다. 비신뢰 PR 코드를 실행하면서 secret을 노출하는 방식은 사용하지 않는다.
- [x] 기존 소스 해시 검증을 유지하고, 의존성 누락 시 필요한 준비 절차를 알 수 있는 오류를 제공한다.
- [x] `push` 대상의 `feature/refactoring_sonch`를 유지할 필요가 없으면 제거한다.

완료 기준:
- [ ] 깨끗한 환경에서 두 CI의 검사·데스크톱 빌드·SteamLink 빌드가 성공한다.
- [ ] 필요한 권한·의존성 버전·로컬 빌드 절차를 문서화하고 성공한 실행 링크를 기록한다.

### 1-2. 잘못 저장된 개발 서버 주소의 시작 실패 복구

대상: `source/Playnite/HoleInOne/ServiceConfiguration.cs`, `LauncherSettings.cs`, `source/Playnite.DesktopApp/DesktopApplication.cs`

작업:
- [x] 개발용 `holeinone.development-address.json`이 유효하지 않으면 앱 종료 대신 경고를 기록하고 유효한 기본 서비스 주소로 돌아가도록 처리한다.
- [x] 기본 주소가 없어 로컬 개발 기본값을 사용하는 경우에도 주소를 다시 설정할 수 있는 안내·진입 경로를 제공한다. 사용 가능한 서버가 있다고 가정하지 않는다.
- [x] 정상 배포의 필수 `holeinone.service.json` 검증은 유지한다. 모든 설정 오류를 일괄 무시하지 않는다.
- [x] 사용자에게 수정 가능한 주소 오류를 재설치 문제로 안내하지 않도록 메시지를 조정한다.
- [x] 공인·사설 IP 단독 입력을 HTTP로 변환한 뒤 거부하는 혼란을 제거한다. 루프백 IP 단독 입력은 현재 허용되므로 유지 여부를 명시적으로 결정한다.

완료 기준:
- [ ] 이전 형식의 비루프백 HTTP 주소, 손상된 JSON, 빈 값에서 시작 복구와 안내가 동작한다.
- [ ] 정상 HTTPS·HTTP 루프백 설정은 유지되며, 비루프백 HTTP 허용 범위를 넓히지 않는다.
- [ ] 사용자 게임 경로·설치 설정·계정 자료를 변경하지 않는다.

### 1-3. 세이브 복구의 취소·타임아웃 구분

대상: `source/Playnite.DesktopApp/HoleInOne/SaveRecoveryWindow.cs`

작업:
- [x] 빈 `catch (OperationCanceledException)`을 제거하고 창 닫힘에 따른 취소만 조용히 처리한다. 예: `when (closed.IsCancellationRequested)`.
- [x] 목록 조회·다운로드 시간 초과는 실패 안내와 재시도 방법을 표시한다.
- [x] Steam 연결 컨텍스트 변경 등 다른 취소 원인도 확인하여 시간 초과로 잘못 안내하지 않도록 한다.
- [x] 실패 뒤 진행 중 문구가 남지 않도록 상태·버튼을 정리한다.

완료 기준:
- [ ] 목록 조회 시간 초과, 다운로드 시간 초과, 창 닫힘, 계정 변경을 각각 재현하여 안내와 작업 종료를 확인한다.
- [ ] 실패 시 기존 세이브·복구 기록을 보존한다. 실제 10분 대기 대신 제어 가능한 가짜 응답·취소를 사용한다.

### 1-4. 복구 완료 UI와 중복 실행 방지

대상: `SaveRecoveryWindow.cs`, `SaveHandoffWorkflow.cs`, `ExportReceipts.cs`

작업:
- [x] 성공한 항목은 즉시 제거하거나 완료 상태로 비활성화하여 같은 창에서 다시 적용할 수 없게 한다.
- [x] 복원 완료와 서버 수신 확인 완료를 구분한다. 수신 확인 실패가 이미 성공한 로컬 복원을 실패로 바꾸지 않도록 한다.
- [x] 목록을 재조회하되, 서버 반영 전 항목이 다시 나타나도 완료 항목이 재활성화되지 않도록 처리한다.
- [x] 재조회 실패 시에도 로컬 복원 성공 상태를 유지한다.

중요한 리뷰 정정:
`SaveHandoffWorkflow.ApplyAsync → receipts.Enqueue → Start()` 경로로 수신 확인 전송은 이미 시작된다. “Start를 호출하지 않아 다음 앱 시작까지 전송되지 않는다”는 지적은 사실과 다르다. 이 이유로 `Start()`를 중복 호출하지 않는다.

완료 기준:
- [ ] 복원 성공 직후 연속 클릭, 수신 확인 지연·실패, 목록 재조회 실패에서 중복 적용을 막는다.
- [ ] 수신 확인의 기존 영속 기록·재전송 동작이 유지된다.

## 2. 연계 확인 및 배포 조건

### 2-1. 계약 2 서버와 앱 적용 순서

- [ ] 테스트 서버의 현재 `/health` 응답과 인증 정책을 확인한다. 리뷰의 맥미니 서버 상태는 이번 코드 검토에서 직접 확인하지 않았다.
- [ ] PR 본문에 Backend 계약 2 배포 → health 계약 확인 → 새 앱 배포 순서를 명시한다. 코드 머지와 사용자에게 앱을 배포하는 시점을 구분한다.
- [ ] Backend PR #4 머지·재배포를 실제로 누가 수행할지, 구형 앱과의 호환 중단을 어떻게 안내할지 정한다.
- [ ] 인증 없는 health 조회에서 `contractVersion == 2`, `authentication == "accounts"`를 확인한 뒤 로그인 검증 결과를 기록한다.

### 2-2. 복구 POST의 기존 요청 재사용 확인

대상: Backend PR #4의 export-handoff 처리 및 앱 `StartExportAsync`

- [ ] Backend PR #4의 실제 커밋에서 `ready`, `failed`, 진행 중인 요청에 대한 반복 POST 동작을 확인한다.
- [ ] 반복 호출과 서버 재시작 이후 호출이 같은 요청 ID를 반환하고 새 내보내기를 만들지 않는지 검증한다.
- [ ] 수신 완료된 요청에 대한 재호출 동작도 확인하고 앱의 완료 항목 처리와 맞춘다.
- [x] 문제가 있으면 Backend의 기존 요청 재사용 동작 또는 앱의 복구 전용 조회 흐름을 수정한다. 무조건 재시도하는 방식으로 해결하지 않는다.

검증 제한: 이번 검토에서는 연결된 GitHub에서 Backend PR #4가 404를 반환하여 해당 서버 구현을 확인하지 못했다. 현재 확인된 버그로 단정하지 않는다.

## 3. 권장 개선

### 3-1. API별 제한 시간 명시

- [x] `BackendConnection.SendAsync`의 경로 문자열 판별을 API 메서드가 전달하는 명시적 제한 시간 또는 소수의 이름 있는 정책으로 바꾼다.
- [x] health·로그인·prepare/play·일반 요청·다운로드의 기존 제한 시간을 정리한다.
- [ ] prepare/play 40초가 충분한지 서버 응답 계약과 지연 측정으로 판단하고 근거를 기록한다. 준비 전체 시간과 개별 HTTP 응답 시간을 구분한다.
- [ ] 호출자 취소와 요청 제한 시간이 모두 동작하는지 검증한다.

참고: 요청별 제한 시간이 존재하므로 `HttpClient.Timeout = InfiniteTimeSpan` 자체는 결함이 아니다.

### 3-2. 안전한 진단 로그 보강

- [x] `InstantPlayCoordinator` 등 예외 유형만 남기는 로그를 확인한다.
- [x] `BackendConnection.DescribeFailure` 등을 활용해 작업 단계·안전한 HTTP 정보·추적 ID를 일관되게 남긴다.
- [ ] 필요한 스택 정보는 보존하되 인증 토큰·비밀번호·서명 URL·원문 응답이 로그에 포함되지 않는지 확인한다.

### 3-3. 복구 목록 표시 개선

- [x] 게임 ID 대신 라이브러리의 게임 이름을 우선 표시한다. 매칭되지 않을 때만 ID를 보조 정보로 사용한다.
- [x] 상태를 사용자용 한글 문구로 변환하고, 알 수 없는 상태도 안전하게 표시한다.
- [x] 복원할 수 없는 상태에 동일한 복원 버튼을 표시할지 검토한다.

### 3-4. PR 범위와 변경 설명 정리

- [ ] 계약 변경·복구 UI·저장용량·빌드 변경의 목적과 검증을 PR 본문에서 구분한다.
- [ ] 후속 수정은 기능별 커밋으로 구성한다.
- [ ] 독립 변경은 필요할 때만 별도 PR로 분리한다. 기존 커밋 재작성·분리는 필수 완료 조건으로 두지 않는다.

### 3-5. generation과 Task 추적 구조 검토

- [x] `LauncherLogin.Generation`과 `SteamLinkContext.Generation`의 증가 지점·소유 계층·보호 대상 비동기 작업을 문서화한다.
- [x] 두 카운터가 완전히 같은 수명을 가지는 경우에만 통합한다. 현재는 단순 중복이나 버그로 확정하지 않는다.
- [x] `CloudPreparation`의 Task 누적 구조를 단순화할 실익이 있는지 검토한다.
- [ ] 변경한다면 로그아웃·주소 변경·앱 종료 시 이전 작업 취소·대기·자원 해제를 보존하고, 늦게 도착한 결과가 새 계정에 반영되지 않는지 검증한다.

## 4. 사소한 정리

- [x] `REFACTORING_TASKS.md`의 `../Backend/...` 링크를 GitHub에서 유효한 저장소 링크로 바꾼다. 필요하면 커밋을 고정한다.
- [x] 작업 이력을 저장소에 유지할지 PR·위키로 옮길지 팀 기준에 따라 결정한다. 문서 삭제는 필수가 아니다.
- [x] `SaveHandoffWorkflow.cs`의 과도한 들여쓰기를 정리한다.
- [ ] `InstantPlayCoordinator.cs`, `CloudPreparation.cs`의 이중 괄호와 `SteamLibrary.cs`의 불필요한 블록을 정리한다.
- [x] `FakeBackend.Authorized`를 실제 의미에 맞는 `NoCredentialsSent` 등으로 변경한다.

## 완료 확인

- [ ] 1번의 필수 수정과 관련 회귀 검사를 완료한다.
- [ ] 두 CI 성공 링크, 추가 검증 결과, 남은 서버 확인 항목을 PR 본문에 기록한다.
- [ ] Backend 기존 요청 재사용 동작과 배포 순서를 확인하거나, 미확인 상태를 명시하고 배포 조건으로 남긴다.
- [ ] 권장·정리 항목은 적용 또는 후속 처리 사유를 기록한다.
- [x] 리뷰 답변 초안에는 수신 확인 전송 지적의 정정 근거를 포함한다. GitHub 답글 등록·머지·실제 배포는 별도 요청 시 수행한다.

실게임 실행·실제 계정 로그인·운영 배포를 이 문서 작성만으로 완료했다고 표시하지 않는다. 모의 검사와 실환경 확인 결과를 구분해 기록한다.


## 2026-10-23 구현 결과

상세 적용 내용, 검증 결과, 리뷰 답변·PR 본문 초안은 [REVIEW_RESPONSE.md](REVIEW_RESPONSE.md)에 기록했다. CI 의존성 정책은 [build/DEPENDENCIES.md](build/DEPENDENCIES.md)를 참고한다. 146개 계약·설정 검사가 통과했다. 실제 WPF 복구 UI의 모든 취소 시나리오, 통합 빌드, 원격 CI, 서버 계약·로그인 검증은 미완료이므로 해당 완료 기준은 체크하지 않았다. Backend 파일은 수정하지 않았다.

## 2026-10-23 재검토 후 추가 작업

아래 기록이 앞선 접근 제한·검사 수 기록보다 최신이다.

- [x] 두 workflow에 수동 실행 경로 추가. 기본 브랜치의 workflow 등록과 token 설정은 원격 실행 전 필요하다.
- [x] `ApplyAsync`가 실제 파일 복원 여부를 반환하도록 변경하고 `no_save`를 복원 성공과 구분한다.
- [x] 실제 복원 workflow를 가짜 HTTP와 호스트 대역으로 검사한다. 즉시/진행 중 no_save, 일반 복구 GET, failed 처리, polling 취소, 계정 변경 차단, 명시적 재시작 복구와 요청 ID 변경 거부를 포함하여 전체 159개 검사 통과.
- [x] DesktopApplication 줄바꿈 정리. `git diff --check` 통과.
- [x] SteamSetup.Compile 경고·오류 0개로 빌드.
- [x] 최종 변경으로 데스크톱·내장 Steam/Epic 라이브러리·브라우저 빌드 성공. 출력: `artifacts/ReviewValidation`.
- [x] 기존 Git 인증으로 고정 Backend 커밋 확보. 별도 추출 소스로 해시 검증·SteamLink publish 성공. 기존 Backend 작업 파일은 유지.
- [x] Backend PR #4 실제 head `2d80f37a1d47a9da0ec8e475f1e296225e737f42`를 fetch하여 상태 동작 확인.
- [x] 서버 재시작 뒤 `failed/recovery_required`만 사용자가 명시적으로 재개할 수 있도록 버튼을 제공한다. 기존 상태를 GET으로 확인한 뒤 POST하며 같은 요청 ID인지 재검사한다. 일반 failed는 재시도하지 않는다. Backend의 RecoveryBlocked 거부 정책을 유지한다.
- [ ] 실제 Backend 재시작·예약 보존·RecoveryBlocked 거부를 서버 통합 검사로 검증한다. 클라이언트 모의 검사는 통과했으나 실서버 검증은 별도다.
- [ ] 실제 WPF 화면의 연속 클릭·재조회 실패·다운로드 시간 초과와 시작 설정→로그인 흐름을 검증한다. 위 159개 검사는 실제 UI 검증을 대신하지 않는다.
- [ ] 원격 secret 등록, 수동 CI 실행 및 성공 링크 기록, 실제 서버 계약·로그인·배포 순서 확인.

Backend 확인 결과: `Get`은 상태 조회만 한다. `Start`는 기존 transfer를 유지하면서 Pending/Failed에서 worker를 다시 실행하고, Ready/Received/진행 중 상태에서는 기존 상태를 반환한다. 재시작 시 Ready/Received/NoSave 외 상태는 Failed로 복원된다. 이미 Scheduled이거나 파일이 있는 요청은 재개 여지가 있지만 `RecoveryBlocked` 요청은 거부한다. 따라서 모든 failed POST가 단순 조회라는 기존 가정은 틀리며, GET만으로 전체 재시작 복구가 된다고 판단해서도 안 된다. 재개 구현 시 기존 요청 ID에 묶인 명시적 재개와 중복 예약 방지 검증이 필요하다.
