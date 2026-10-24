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
Steam 연결 모듈은 새 데스크톱 로그인을 하지 않고 단기 Web API 토큰만 발급합니다.

서버 세션 중 이 PC 이름 및 로컬 IP와 일치하는 대상이 하나일 때만 설치·제거를 전송합니다.
일치 대상이 없거나 중복되면 중단하며, 별도 PC 선택 UI나 설치 폴더 지정은 없습니다.
API 수신 확인 후 실제 완료는 로컬 설치 정보로 확인합니다. 네트워크 오류 등 결과가 불명확한 요청을 자동 재전송하지 않습니다.

ClientComm은 Steam이 사용하는 내부 원격 다운로드 인터페이스로, 공개적으로 지원되는 Steamworks 설치 API가 아닙니다.
참조 프로토콜: https://github.com/SteamTracking/Protobufs/blob/master/webui/service_clientcomm.proto
인증 API: https://github.com/SteamRE/SteamKit/blob/3.4.0/SteamKit2/SteamKit2/Steam/Authentication/SteamAuthentication.cs
실제 Steam에서의 기본 설치 위치·창 표시·계정 권한 동작은 수동 검증이 필요합니다.

자동 검사: `dotnet run --project tests/SteamRemote.Checks`
