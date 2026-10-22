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
