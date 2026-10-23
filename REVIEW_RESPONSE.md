# PR #5 후속 수정 및 배포 조건

기준: 58b24014059ca2ff2787939121f590814ee28217. 기존 사용자 변경은 미추적 Task.md였으며 보존했다. Backend 파일은 변경하지 않았다.

## 적용 내용

- 개발 주소 JSON 오류·빈 값·비루프백 HTTP는 기본 주소로 복구하고 설정 진입 안내를 표시한다. 필수 배포 서비스 설정은 계속 엄격히 검증한다. 사용자 게임 경로·설치·계정 자료는 덮어쓰지 않는다. IP 단독 입력은 IPv4 루프백만 기존 HTTP:5010 변환을 유지하며 다른 IP에는 전체 HTTPS 주소를 요구한다.
- 복구 창은 닫힘 취소만 조용히 처리하고, 계정/Steam 컨텍스트 변경과 제한 시간을 구분한다. 목록 재조회와 복원 중 버튼 잠금을 제공한다. 완료 항목은 창 수명 동안 계정 generation/세션/게임 키로 차단하며, 서버 재조회 실패나 수신 확인 지연으로 재활성화하지 않는다.
- 복구는 기존 export GET만 사용한다. 서버의 반복 POST 재사용 계약이 확인되지 않아 복구 화면에서 새 export를 요청하지 않는다. failed/알 수 없는 상태는 복원 버튼을 비활성화한다. 게임 이름과 한글 상태를 표시한다.
- 수신 확인 전송은 기존 ApplyAsync → Enqueue → Start 경로가 이미 시작한다. Start 호출 누락이라는 리뷰 지적은 정정하며 중복 호출을 추가하지 않았다. outbox 영속화·재전송 경로를 유지한다.
- health 12초, 로그인·가입 30초, prepare/play 40초, 일반 요청 20초를 호출부에서 명시한다. 다운로드는 기존 10분, export 복구 확인 전체는 75초다. prepare 전체 준비 수명과 HTTP 응답 제한 시간은 다르다. 측정 근거가 없어 40초를 임의로 확대하지 않았다.
- 주요 작업 실패는 DescribeFailure의 안전한 예외 코드·HTTP 상태·원인 코드로 기록한다. 원문 응답·토큰·비밀번호·서명 URL을 로그에 추가하지 않는다. 기존 PerformanceTrace 단계/추적 ID를 유지한다.
- 과도한 들여쓰기·이중 괄호·FakeBackend 필드명과 Backend 문서 링크를 정리했다. 작업 이력 문서는 유지한다.

## 수명 구조 검토

LauncherLogin.Generation은 Forget 시 증가하며 계정 로그인과 주소 수명을 보호한다. SteamLinkContext.Generation은 Forget 시 증가하고 Steam 연결 응답의 늦은 반영을 차단한다. LauncherLogin.Forget은 SteamLinkContext.Forget도 호출하지만 Steam 연결 계층은 자체 Forget 진입점과 잠금을 가진다. 서로 다른 소유 계층이므로 통합하지 않았다.

CloudPreparation의 pendingPreparation/pendingCleanup은 이전 작업을 합쳐 종료·주소 변경·로그아웃 시 취소 및 대기를 보장한다. 반복 작업 수가 적고 측정된 메모리 문제가 없어 구조 변경을 보류했다. 기존 ResetAsync의 cancellation 교체, 이전 preparation 완료 후 Dispose, StopAsync의 연결 Dispose를 유지했다.

## 검증과 제한

- HoleInOne.Checks: 146개 통과. 손상된 JSON/null/빈 주소/이전 HTTP 주소의 서비스 fallback, 안내, 게임 설정 보존, 루프백 입력을 추가 검증했다.
- 제한된 실행에서는 Steam 레지스트리 읽기가 거부되어 권한 있는 실행으로 동일 검사를 통과했다.
- SteamSetup.Compile: 권한 있는 실행에서 패키지 복원 후 경고·오류 0개로 빌드 통과. 실제 Playnite.DesktopApp 및 연결 라이브러리 컴파일도 통과했다.
- 복구 창 실제 WPF 클릭·실게임·계정 로그인·10분 다운로드 제한 시간의 가짜 응답 기반 UI 검증은 아직 완료하지 못했다.
- 고정 Backend 소스가 로컬에 없어 SteamLink 전체 빌드를 완료하지 못했다. CI 정책/권한/로컬 준비는 build/DEPENDENCIES.md 참고. 원격 CI 성공 링크는 아직 없다.

## 배포 차단 조건

Backend 계약 2 배포 → 인증 없는 /health의 contractVersion=2, authentication=accounts 확인 → 실제 로그인 검사 → 앱 배포 순서다. 코드 머지와 사용자 배포는 별도다.

Backend PR #4는 연결 GitHub에서 404였다. 로컬 Backend 158a861은 failed POST 시 재실행 가능 코드가 있으나 PR #4 실제 커밋이 아니므로 해당 PR의 결함으로 단정하지 않는다. PR #4의 ready/failed/진행 중/received 및 서버 재시작 이후 요청 ID 재사용 검증은 배포 전 필요하다. 앱 복구 화면은 GET 전용으로 완화했다.

대상 서버 URL·실계정·배포 담당자가 확인되지 않았다. /health 실조회 및 로그인은 미수행이다. Backend 담당자가 PR #4 머지·재배포, 구형 앱 호환 중단 공지를 맡도록 팀에서 담당자를 지정해야 한다. 앱 배포 담당자는 health/로그인 결과와 두 CI 성공 링크를 기록한 뒤 배포한다.

이 문서는 PR 본문 및 리뷰 답변 초안이다. GitHub 답글·본문 등록, 커밋·push·머지·운영 배포는 수행하지 않았다.

## 2026-10-23 후속 검증 — 위 기록의 변경 사항

- 두 workflow에 수동 실행 경로를 추가했다. 기본 브랜치에 workflow 정의 등록 후 검토한 앱 브랜치를 선택하여 전체 빌드를 실행할 수 있다. 원격 secret 등록·실행은 아직 미완료다.
- ApplyAsync가 실제 복원 여부를 반환하여 no_save를 복원 성공으로 표시하지 않는다.
- GitHub 연결의 404와 달리 기존 Git 인증으로 Backend를 fetch할 수 있었다. 고정 커밋 소스를 별도 추출하고 해시 확인 및 SteamLink publish에 성공했다. Backend 작업 파일은 수정하지 않았다.
- Backend PR #4 head `2d80f37a1d47a9da0ec8e475f1e296225e737f42`에서 Start는 같은 transfer ID를 유지하며 Failed worker를 재개한다. 재시작 시 중단 상태는 failed/recovery_required가 되고 GET만으로는 재개되지 않는다. 따라서 앞선 GET 전용 완화는 부분 해결이었다.
- 앱은 failed/recovery_required에만 명시적 재개 버튼을 제공한다. 먼저 GET으로 기존 상태를 확인하고 POST한 뒤 요청 ID·게임 일치를 검사한다. 일반 실패는 자동 재시도하지 않는다. 서버가 RecoveryBlocked로 판단하면 재개를 거부한다.
- 실제 workflow와 HTTP 클라이언트를 사용하는 모의 검사를 추가해 총 159개가 통과했다. no_save·실패·취소·계정 변경·재개 시 동일 ID·다른 ID 거부를 검사한다. 실제 파일 복원·WPF 클릭 검사를 대신하지 않는다.
- SteamSetup.Compile과 데스크톱·내장 Steam/Epic 라이브러리·브라우저 빌드 성공. 최종 복구 변경 이후 데스크톱 재빌드도 성공했다. 고정 SteamLink 출력과 함께 `artifacts/ReviewValidation`에 저장했다. DesktopApplication의 기존 줄바꿈과 BOM은 보존하고 추가 부분을 정리하여 git diff --check 통과.
- 남은 항목: 실제 WPF 및 서버 재시작 통합 검사, 실제 서버 health·로그인, 원격 CI 및 배포 확인. 이전의 Backend 접근 권한 필요 설명은 로컬 Git 인증 경로에 대해서는 해결됐다.
