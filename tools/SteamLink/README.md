# 통합 Steam 연결 모듈

.NET Framework 데스크톱과 표준 입출력 JSON으로 통신하는 .NET 10 Windows x64 보조 프로그램입니다.
QR·비밀번호·Guard 인증으로 얻은 새 토큰만 전달합니다. 서버가 토큰의 실제 SteamID를 확인하고
등록을 완료한 뒤 commit 명령을 받으면 DPAPI(CurrentUser)로 계정별 폴더에 저장합니다.
기존 웹 쿠키나 세이브 인증 파일을 자동으로 이전하지 않습니다. 암호화된 진단용 .hioauth 형식과 별개입니다.

`--cloud`는 V5 전용입니다. HTTPS 서버에서 세션 전용 권한으로 인증을 받고,
깨끗한 Windows 프로필에서 검증된 dpapi-utf8-v1 형식을 구성한 뒤 Steam을 실행합니다.
비밀번호·토큰·세션 권한을 명령줄이나 로그에 기록하지 않습니다. 기존 Steam 파일을 덮어쓰지 않습니다.
정상 계정 감지와 준비 성공 보고를 완료해야 ready를 반환합니다. 실제 온라인·게임 실행 검증은 별도로 수행합니다.

두 저장소를 Backend, PlaynitePorting 이름으로 같은 부모 폴더에 두고
build/Build-SteamLink.ps1로 빌드합니다. SteamKit DLL은 교체 가능한 별도 파일로 배포합니다.

## 로컬 설치·제거의 서버 요청

`install` / `uninstall` 명령은 저장된 연결 인증으로 Steam 서버의 ClientComm API에 요청합니다.
로컬 `steam.exe` 실행, Steam URL 호출 또는 실패 시 로컬 명령 재시도를 하지 않습니다.
PC Steam은 같은 계정으로 이미 실행·로그인되어 있어야 합니다. 꺼져 있으면 안내하고 중단합니다.
Steam 연결 모듈은 저장된 인증으로 별도 CM 세션에 로그인한 뒤 단기 Web API 토큰을 발급합니다.
SteamClient 토큰은 인증되지 않은 CM 연결에서 발급을 요청하면 AccessDenied(15)가 발생할 수 있습니다.
보조 세션은 별도 LoginID와 PC와 다른 기기 이름을 쓰며, 요청 종료 시 로그오프합니다.
기존 PC Steam 로그인 유지 여부는 실제 Steam 환경에서 확인해야 합니다.

서버 세션 중 이 PC 이름 및 로컬 IP와 일치하는 대상이 하나일 때만 설치·제거를 전송합니다.
일치 대상이 없거나 중복되면 중단하며, 별도 PC 선택 UI나 설치 폴더 지정은 없습니다.
API 수신 확인 후 실제 완료는 로컬 설치 정보로 확인합니다. 네트워크 오류 등 결과가 불명확한 요청을 자동 재전송하지 않습니다.

ClientComm은 Steam이 사용하는 내부 원격 다운로드 인터페이스로, 공개적으로 지원되는 Steamworks 설치 API가 아닙니다.
참조 프로토콜: https://github.com/SteamTracking/Protobufs/blob/master/webui/service_clientcomm.proto
인증 API: https://github.com/SteamRE/SteamKit/blob/3.4.0/SteamKit2/SteamKit2/Steam/Authentication/SteamAuthentication.cs
실제 Steam에서의 기본 설치 위치·창 표시·계정 권한 동작은 수동 검증이 필요합니다.

자동 검사: `dotnet run --project tests/SteamRemote.Checks`

## 설치 인증 오류 진단

진단 로그는 기본적으로 꺼져 있습니다. 환경 변수 `HOLEINONE_STEAM_DIAGNOSTICS=1`을 설정한 프로세스에서 앱을 새로 실행하면 `%LOCALAPPDATA%\HoleInOne\Logs\steam-debug-날짜-PID.log`에 기록합니다.
PowerShell에서는 `$env:HOLEINONE_STEAM_DIAGNOSTICS = '1'`을 설정하고 같은 창에서 앱을 실행합니다. 기존 앱은 먼저 종료해야 합니다.
진단 후 `Remove-Item Env:HOLEINONE_STEAM_DIAGNOSTICS`로 설정을 지우고 앱을 재시작합니다.
시각은 UTC이며 `attempt`가 같은 줄이 한 모듈 실행에 해당합니다. 최근 파일에서 아래 순서로 확인합니다.

- `GenerateAccessTokenForApp authentication_failed`: `result`가 SteamKit EResult의 실제 숫자 값입니다. 기존 UI는 모든 AuthenticationException을 재인증 안내로 합치므로, 이 값으로 원인을 구분합니다.
- `LogOn response result=1` 후 토큰 발급을 요청합니다. 로그인 성공 후 발급이 실패하면 `REMOTE_TOKEN_EXCHANGE_FAILED`로 구분합니다.
- `GenerateAccessTokenForApp ok` 이후 API `response`: `http`는 HTTP 상태, `result`는 숫자로 파싱한 x-eresult입니다. `-`는 값이 없거나 숫자가 아니라는 뜻입니다.
- `worker`: 최종 오류 코드 또는 예외 유형입니다. 마지막 `started` 단계와 함께 보면 중단 위치를 알 수 있습니다.

토큰·비밀번호·SteamID·PC 이름·IP·요청 URL·응답 본문·예외 메시지는 기록하지 않습니다.
로그 쓰기에 실패해도 설치 동작은 유지됩니다. 로그는 자동 삭제하지 않으므로 진단 후 해당 steam-debug 파일을 정리할 수 있습니다.
기존 실행 파일에는 적용되지 않습니다. `build/Build-SteamLink.ps1 -OutputDirectory <배포폴더>/Tools/SteamLink`로 모듈을 빌드한 배포본에서 재현해야 합니다.

## 동시 세션 수동 검증 (머지 전 필요)

Steam의 설치 위치와 여유 공간 관리는 Steam에서 수행합니다. HoleInOne의 사전 공간 확인·자동 제거·사용량 한도 및 자동 용량 계산은 Epic에만 적용됩니다. 기존 Steam 설치 폴더 설정은 무시되며 저장하지 않습니다.

같은 계정으로 PC Steam에서 게임을 실행한 상태에서 다른 게임의 설치·제거를 요청합니다.
요청 성공·실패·취소 후 기존 Steam 로그인과 실행 중 게임의 온라인 연결이 유지되는지 확인합니다.
이 검증은 모의 HTTP 자동 검사로 대체할 수 없으며 아직 완료되지 않았습니다.
