# PlaynitePorting Task — 백엔드 직접 연결과 세이브 복원 전환

기준일: 2026-10-01. 실행 계획과 로컬 구현·검증 기록이다. 실제 클라우드/game E2E 완료 보고는 아니다. [전체 Task](../TASK.md), [백엔드](../HoleInOneBackend/TASK.md), [GameLink](../GameLink/TASK.md), [GameLoader](../GameLoader/TASK.md)와 함께 수행한다. 과거 PROJECT_HANDOFF의 HTML 브리지·세이브 제외 전제는 이번 범위로 대체한다.

## 구현 전 조사와 목표

다음 문단은 변경 전 구조의 기록이다. 현재 네이티브 서비스·복원·패키징 및 검증 결과는 각 체크리스트와 문서 마지막의 구현 기록을 따른다.

`source/Playnite/GameLink/InstantPlay.cs`는 PC Node 서버/임시 hosted URL을 선택하고, 설치 완료와 모닥불 신호만으로 로컬 실행을 요청한다. `HandoffSession.cs`의 ready 경로는 별도로 존재하지만 기본 `UnavailableLocalHandoff`는 실패한다. `GamesEditor.cs`는 GameLoader를 복사한 뒤 일반 로컬 실행을 호출한다. 복원 서비스는 없다.

목표는 C# 연동 서비스가 백엔드 명령을 직접 받고, 세이브 복원과 같은 런의 로드 확인을 전환의 필수 조건으로 만드는 것이다. 영상·입력 화면은 현재 WebView2와 Web SDK 사용을 출발점으로 검토한다.

## P1 — 계약과 세션 연동

- [x] 전체 Task C1 계약을 사용한다. backend session ID와 현재 로컬 생성 UUID/AWS ARN을 혼용하지 않고, provider 게임 ID와 내부 Game.Id를 매핑한다.
- [ ] 사용자 로그인·토큰 갱신·세션 생성/조회/종료를 담당하는 취소 가능한 비동기 C# 서비스를 만든다. UI 스레드에서 네트워크 대기를 하지 않는다.
- [x] 백엔드 이벤트 구독 방식(SSE/WebSocket 등)을 대상 .NET Framework 4.6.2/x86 환경에서 검증하고 선택한다. 재접속 cursor·상태 조회·중복 ACK·종료 세션 거부를 구현한다.
- [ ] 설치 상태·게임 빌드·DLC/모드 호환성을 보고한다. 백엔드 ready를 받더라도 로컬 전제조건을 다시 확인하고, 다른 세션/시도·만료 artifact는 실행하지 않는다.
- [x] `GamesEditor.cs`에서 `Controllers_Stopped`에 들어간 `SetInstallationReady()` 호출을 정리하고 실제 `Controllers_Installed`와 설치 상태 재조회에 연결한다. 설치 취소·실패와 게임 종료를 설치 성공으로 보고하지 않는다.
- [ ] UI 종료, 계정 변경, 게임 변경, 네트워크 단절 시 구독·다운로드·전환 시도를 취소하고 백엔드에 종료 또는 실패를 보고한다.

산출물/완료 조건: API 클라이언트·구독 서비스·계약 테스트. 브라우저 명령 없이 올바른 세션만 명령을 받고 재연결 후 현재 상태를 복구한다.

## P2 — 세이브 다운로드와 복원 서비스

- [x] UI와 분리된 로컬 서비스가 artifact manifest와 세션 권한을 확인하고 staging 디렉터리에 다운로드한다. 크기·해시·버전·프로필·파일 목록을 검사하고 중단 재시도를 지원한다.
- [ ] 현재 게임 API/설치·사용자 환경으로 대상 저장 경로를 확인한다. 서버가 준 임의 절대 경로를 쓰지 않고 경로 탈출·링크·압축 해제 범위를 제한한다.
- [ ] 게임 설치뿐 아니라 프로세스 종료, 디스크 공간, 파일 잠금, 대상 프로필, 모드 활성화, Steam Cloud 동기화 영향을 확인한다.
- [x] 기존 저장을 백업하고 복원 journal을 남긴 뒤 같은 볼륨의 staging/교체 등 검증된 방식으로 적용한다. 여러 파일을 한 번에 원자적으로 바꿀 수 없으면 단계별 복구를 구현한다.
- [ ] 앱 재시작·중간 실패 때 미완료 복원을 감지한다. 이미 로컬 게임이 실행 중인 상태에서 세이브를 덮어쓰거나 롤백하지 않는다. 재시도/복구 전 프로세스·파일 소유 상태를 확인한다.
- [ ] 복원 완료 증거와 GameLoader가 검증할 profile/run/save generation/manifest 식별자를 연결한다. 성공 후 백업·임시 파일 보관/정리 정책을 적용한다.

산출물/완료 조건: 다운로드·검증·백업·복원 서비스와 실패 주입 검사. 손상/중단/경로 탈출에서 원본 저장을 보존하고, 정상 적용 파일이 해당 artifact와 일치한다.

## P3 — 전환 상태와 GameLoader 연계

- [x] `HandoffSession`과 `InstantPlayView`의 이중 전환 경로를 통합한다. 설치+모닥불만으로 실행하는 `TryRequestLocalLaunch`와 무조건 실패하는 기본 handoff를 교체한다.
- [x] 후보 관측 → artifact 준비 → 설치/호환성 확인 → 다운로드·복원 → 로컬 실행 → 런 확인 → 완료 보고를 구분한다. ready는 전환 시도 입력이며 성공 상태가 아니다.
- [x] 백엔드 취소/실패가 Switching 중에도 전환을 중단하도록 한다. 현재 `HandoffSession.Receive`는 Switching 중 모든 메시지를 거절하므로 새 상태 모델에 맞게 변경한다.
- [x] 시도별 GameLoader 컨텍스트·일회성 토큰·IPC endpoint를 실행 전에 만들고, 예상 런·프로필·시도와 로드 결과를 비교한다. 현재 고정 토큰 파일/8767/V2 계약은 양쪽 동시 교체한다.
- [x] GameLoader 성공 통지의 중복 수신은 안전하게 ACK한다. 지연 신호, 다른 프로세스/시도, 타임아웃 뒤 도착한 성공으로 새 세션을 닫지 않는다.
- [x] 백엔드 prepare/freeze lease와 최신 artifact를 확인한 시도만 복원·실행한다. lease 만료로 클라우드가 재개된 뒤 늦은 로컬 성공이 도착하면 commit하지 않고 충돌 상태를 복구한다.
- [ ] 실행·복원·로드 실패 시 스트림을 유지/복구하고 실패 상태를 서버에 보고한다. 늦게 끝난 비동기 작업이 다시 실행·성공 처리하지 못하게 한다.
- [ ] 같은 런 로드 확인 후 백엔드 완료 보고와 클라우드 종료를 멱등 처리하고 WebView를 닫는다. 완료 보고 직후 연결이 끊긴 경우 서버 상태 조회/종료 재시도로 정리한다.
- [x] `GamesEditor`의 설치·일반 실행 경로와 연결한다. 이미 설치된 게임은 기존 로컬 실행 동작을 유지하고 새 요구 없이 클라우드 경로를 강제하지 않는다.

산출물/완료 조건: 전환 서비스, IPC 수신기, 갱신된 상태 테스트. 복원·정확한 런 확인 중 하나라도 빠지면 Local 상태로 가지 않는다.

## P4 — 웹·개발 서버·중복 배포 제거

- [x] `WebFront/public/app.js`의 applicationMessage/첫 수신 플래그/mock 분기, `signal.js`, 브리지 전용 `signal.test.js`, `GameLink/mock.html`을 새 직접 구독 경로로 대체한다.
- [x] `InstantPlayWebView.cs`의 `window.CefSharp` 호환 주입과 GameLink 명령 이벤트, `InstantPlay.cs`의 웹 origin 기반 명령 수신을 제거한다. 시그널링/영상 초기화에 필요한 호스트 메시지는 별도 계약과 origin 검사로 제한한다.
- [x] 일반 `source/Playnite/WebView/WebView.cs`에도 남은 `GameLinkMessageReceived`, `Browser_JavascriptMessageReceived`와 구독/해제를 참조 검사 후 제거한다. 일반 웹뷰 구현과 다른 JavaScript 연동은 보존한다.
- [ ] 정적 웹 자산 제공 방식을 결정하고 영상·음성·입력·WebRTC 시그널링을 원격 백엔드와 연결한다. `index.html`, SDK, 스타일을 명령 브리지와 함께 무조건 삭제하지 않는다.
- [x] AWS 세션 API·허용 리소스 매핑을 백엔드로 옮긴 뒤 `WebFront/server.js`, PC Node 실행, ProcessHost, LoopbackFallbackServer, AWS 프로필 의존을 제품 경로에서 제거한다.
- [x] `hosted-stream.local.txt` 자동 선택과 빌드 복사, `GAMELINK_MOCK` 제품 진입을 제거/개발 전용으로 격리한다. 임시 URL만으로 세션 소유권·명령 계약을 대체하지 않는다.
- [x] Git 추적 중인 `source/Playnite/GameLink/WebClient` 중복 소스를 제거하고 원본 한 곳에서 출력한다. Playnite.csproj·Desktop csproj의 광범위 복사를 허용 목록으로 바꾸고 Fullscreen 패키지까지 검사한다.
- [x] `WebFront/build-server.mjs`, package 스크립트/SDK 의존성, `build/package-portable.ps1`을 새 정적 배포에 맞게 정리한다. GameLoader 번들·Web SDK 및 의존 라이선스는 보존한다.
- [x] `build/check-stream-webview.ps1`은 WebViewFix 고정 경로 대신 검사 대상 경로를 받도록 한다. 재생성 가능한 실험 빌드·dist·로그의 보존/정리·ignore 정책을 정한다.
- [x] PROJECT_HANDOFF, GameLink README, WebFront README/PLAYNITE_SETUP/GAMELIFT_WEB_CLIENT_TASK의 구형 설명을 갱신하거나 이력화한다. 일반 Playnite `GameLinks`, Steam/Epic, 다른 용도의 CefSharp는 삭제 대상이 아니다.

산출물/완료 조건: 정리된 빌드·패키지·문서. 사용자 PC가 Node/AWS 자격 증명 없이 실행되고, 새 배포물에 명령 브리지·서버·mock 복사본이 재생성되지 않는다.

## P5 — 검증

- [x] 기존 `source/Tests/Playnite.Tests/GameLinkHandoffTests.cs`를 새 상태/인증/중복/취소/동시성 계약에 맞게 갱신한다. 구형 형식 검사만 삭제하고 유효한 세션 분리 검사는 유지한다.
- [ ] 복원 실패, 디스크 부족, Steam Cloud 덮어쓰기, 다른 프로필, GameLoader 없음/비활성화, 늦은 완료, 앱 재시작, 실패 후 재시도를 검사한다.
- [ ] Desktop/Fullscreen 빌드와 설치·일반 로컬 실행·WebView 영상/입력·사용자 종료를 검증한다. 검사 스크립트의 WebView 초기화 성공을 실제 클라우드 재생 성공으로 기록하지 않는다.
- [ ] 실제 클라우드 세이브의 동일 런으로 로컬 이어하기한 증거와 실패 시 저장 보존 결과를 기록한다. 진단에는 correlation ID만 남기고 토큰·저장 원문을 노출하지 않는다.

의존성: P1 계약은 전체 C1, P2는 백엔드 artifact API·GameLink manifest, P3는 GameLoader 새 IPC/동일성 검증이 선행한다. P4의 구형 경로 제거는 대응 새 기능 검증과 함께 완료한다.

## 2026-10-01 구현·검증 기록

체크된 항목은 로컬 구현과 아래 fake/구조/컴파일 검증 범위다. 실제 클라우드/game E2E 통과를 의미하지 않는다. 범위와 제한은 `source/Playnite/GameLink/README.md`에 기록했다.

- 네이티브 사용자 인증 adapter (`HIO_BACKEND_URL` origin root + 외부 `HIO_USER_TOKEN`), 취소 가능한 비동기 session 생성/조회/close, persisted request/session/cursor, 계정 fingerprint 분리, snapshot cursor 복구, JSON replay/ACK 재접속을 구현했다. 실제 로그인 UI/IdP·stable user ID·자동 token refresh는 아직 미연동이다.
- 최초 연결 누락을 보완했다: 설정이 없으면 네이티브 주소/토큰 설정 창, current-user DPAPI 영속 보관, complete-pair 환경 override와 origin 변경 시 토큰 유출 방지. 백엔드 초기화 도구로 실행 서버/암호화 설정을 등록했고 실제 C# 인증·세션 생성/조회/종료와 서버 재시작을 검증했다. capabilities에서 fake 영상 없음은 세션 생성 전에 안내한다. 수동 토큰 설정 화면을 운영 IdP 로그인으로 취급하지 않는다.
- 설치+모닥불 우회, V2 고정 토큰/TCP8767, HTML 명령/CefSharp 호환 주입을 제거했다. ready→현재 prepare lease→producer freeze attestation→manifest/hash 검증→백업/journal/단독복원→실행→실제 GameLoader identity→commit만 진행한다.
- 세이브는 verified absolute-drive target의 단일 run 파일만 적용한다. SHA/크기/버전/profile/run/checkpoint/mods/만료를 검사한다. backup부터 replace까지 기존 파일의 writer 배제, 같은 볼륨 교체, receipt 전체의 workspace 단독 소유, durable CommitPending와 backend 재조회, 변경/실행 중 저장 롤백 거부를 구현했다. 불명확한 historical attempt 결과나 backend 만료/단절은 backup/journal을 보존한다.
- 실제 DLL의 SaveManager/ModManager API 조사에 따라 user://steam/account/modded/profileN/saves 범위를 확인했다. 설치된 sts2.dll MVID를 .NET Framework ReflectionOnlyLoad로 읽기 전용 확인했다. **Godot-globalized root와 Steam Cloud 정책의 자동 증명은 미확인**이며 product 기본값은 복원을 거부한다. manual development attestation은 증명이 아니다. 원래 저장과 unresolved backup의 자동 보관기간 정리는 아직 없다.
- 명시적 세션 종료가 성공하면 journal을 닫는다. 이전 cloud/attempt outcome을 알 수 없으면 즉시 성공 처리·자동 overwrite/rollback을 하지 않는다. 앱 crash 후 Preparing/Frozen/Restored는 다시 실행하지 않고 backend fail로 재개시킨다.
- 정적 Web SDK/라이선스·영상·음성·입력은 backend-hosted 단일 `WebFront/public`에 보존했다. native reconnect 버튼/F5는 기존 SDK를 분리하고 재접속 상태를 기다려 새 signaling ticket을 요청한다. 일반 CefSharp·Steam/Epic·GameLoader 유지. PC Node/AWS 서버·bridge/mock/hosted 설정·중복 WebClient·광범위복사·server bundle 패키징 제거. generated historical outputs는 user-data 확인 전 삭제하지 않고 ignore했다.

실행한 검증:

- Desktop/Fullscreen .NET Framework 4.6.2 x86 Release MSBuild 성공. 기존 의존성 NU190x 경고는 별도 업그레이드 범위이며 존재함.
- `source/Tests/HoleInOne.ContractTests`: 실제 변경 DLL 대상으로 27개 fake HTTP/native named-pipe 상태·fault 테스트 성공. 기존 20개에 DPAPI 저장/재읽기·손상 보존·비안전 URL·부분 환경 혼용 거부·origin 변경 시 토큰 송신 차단·실제 다른 런 실패 보존·실행 중 adapter 복원 거부 7개를 추가했다. `--probe-configured-backend`로 실제 등록 설정→실행 백엔드 auth/create/read/close도 통과했다. 종료 요청 접수와 종료 상태 확인을 구분했다.
- `WebFront/static.test.js`: 2개 정적 media/signaling/의존성 회귀 검사 성공. 전체 기존 NUnit suite는 실행하지 않았다.
- native offscreen WebView2 검사: 32비트 STA Windows PowerShell에서 viewport 800×532와 H.264 capabilities 확인. 실제 스트림 재생 증거는 아니다.
- 실제 portable ZIP 생성 성공; 865개 entry에서 server/WebClient/mock/hosted/Node 설정이 없고 Desktop/Fullscreen, CefSharp/WebView2, Steam/Epic와 GameLoader 런타임이 있는지 검사했다.

실행하지 않았거나 외부 조건이 필요한 검증: 실제 GameLift WebRTC 영상/음성/입력·same-run E2E, cloud freeze 실제 adapter, Godot save-root/Steam Cloud preflight, 직접 exe 실행 후 Steam 재시작 때 환경 전달 및 mod enable, 실제 디스크 부족/Steam Cloud 덮어쓰기, 모든 UI·일반 로컬 게임 실행·설치/취소의 실사용 회귀. 자동 cloud 종료는 native의 독립 run proof와 durable backend commit 이전에 수행하지 않는다. 배포·유료 리소스 생성은 실행하지 않았다.

## 2026-10-01 portable 시작 오류 보완

- [x] 실제 사용자 portable의 playnite.log에서 `CefSettings.BrowserSubprocessPath not found`와 누락된 `Playnite.BrowserProcess.exe`를 확인했다. 기존 ZIP의 CefSharp 일반 subprocess 검사만으로 앱에 필요한 custom subprocess를 보장하지 못했다.
- [x] Desktop/Fullscreen에 BrowserProcess 프로젝트 참조를 추가하고 앱 출력으로 exe/config를 복사한다. 같은 출력 경로의 두 앱 Release/x86 빌드 및 기본 프로젝트별 출력 경로의 실제 복사 검증이 통과했다.
- [x] 패키징은 custom exe/config 각각이 누락되면 ZIP 생성 전에 거부한다. 두 부정 검사 통과. 생성 `.log`를 배포에서 제외했다.
- [x] `build/check-cef-startup.ps1 -InputDir <build>`가 production ConfigureCef, custom subprocess, offscreen HTML 로드/JavaScript 결과를 실제 x86 런타임으로 검증했다. 격리된 cache를 사용했으며 Chromium Crashpad/OS encryption의 환경 경고가 있었지만 페이지 검사는 통과했다. 테스트 실행 파일은 검사 후 제거한다.
- [x] 사용 중인 OneDrive 바탕 화면의 HoleInOne-portable에서 기존 DLL의 동일 hash를 확인하고 누락된 exe/config 두 파일만 추가했다. 사용자 설정과 기존 DLL은 변경하지 않았다.
- 새 ZIP: 867 entries, 188148000 bytes, SHA-256 `198FCA629289D54844C193A84E26C2F3BD78F449016B8F67D047DB08555B5262`. main/custom exe/config의 압축 내용 hash가 검증한 빌드와 일치한다. 이전 865-entry 구조 검사는 cold startup 성공을 의미하지 않았다.
- 전체 Desktop/Fullscreen GUI 및 실제 GameLift 영상 회귀는 여전히 미실행이다. 기존 NuGet 취약성 경고를 기록한 채 `/p:TreatWarningsAsErrors=false`로 컴파일했다. 의존성 업그레이드는 이 시작 오류 수정에 포함하지 않았다.

## 2026-10-01 로컬 백엔드의 실제 AWS 재생 URL 연결

- [x] 네이티브 초기/SDK 재연결에서 서버의 `playbackMode`, `playUrl`, `expires` 응답을 사용한다. URL을 클라이언트에서 조립하지 않는다.
- [x] AWS hosted URL은 공식 HTTPS 호스트·stream URL 경로·단일 token·만료를 검사한다. SDK는 backend origin·fragment ticket 일치·query/userinfo 없음 조건을 검사한다. native 사용자 토큰은 WebView URL에 전달하지 않는다.
- [x] URL_READY를 URL 발급 준비 상태로 취급하며 게임 연결·로드 성공과 구분한다. AWS hosted F5/재시도는 페이지를 이동하거나 새 세션을 만들지 않고 명시 종료 후 새 실행 안내만 표시한다. 기존 SDK reconnect와 handoff/commit 검증은 유지한다.
- [x] 이전 ERROR/FAILED 세션은 새 명시 실행에서 close 후 journal을 재설정한다. 실패 때문에 새 세션을 무한 자동 생성하지 않는다. fake 안내의 원인을 서버 위치가 아닌 테스트 모드로 수정했다.
- [x] Desktop/Fullscreen Release/x86 빌드와 native 계약/fault 32개 성공. 네 URL 검사 그룹을 추가했고 기존 저장 보존/실패/실제 named-pipe 회귀가 통과했다. 기존 NuGet 취약성 및 System.Net.Http 버전 경고는 남아 있다.
- [ ] 실제 AWS URL 발급·WebView2 게임 영상/음성/입력·hosted session 추적·same-run handoff E2E. AWS 인증/허용 리소스와 실제 실행 환경이 필요하다. fake 및 URL 형식 검사 통과를 AWS E2E로 기록하지 않는다.

Playnite 프로세스는 AWS 자격 증명이나 Node를 로드하지 않는다. 로컬로 실행하는 별도 백엔드 프로세스의 Node/AWS 인증 구성과 클라우드 GameLink callback 연결은 백엔드 Task에서 다룬다. 영상 자체는 WebView2와 GameLift Streams가 직접 송수신한다.
- hosted WebView 자체의 F5/Ctrl+R browser accelerator와 Reload/BackOrForward navigation도 차단한다. GameLift 준비는 10분 제한이며 안전한 startupError 6종을 고정 한국어 안내로 매핑한다. 서버 원문 진단이나 URL/token을 오류 창에 돌려주지 않는다. 실제 키 입력 GUI와 AWS hosted 페이지 동작은 아직 실행하지 않았다.
