# Steam Cloud bridge

SteamKit2 3.4.0 기반 Windows 전용 사용자 계정 업로드 모듈. .NET Framework 데스크톱과 버전을 격리함. 게임별 개발자 OAuth Client ID를 요구하지 않으며 사용자 Steam 인증을 사용함. 실제 Steam 서버의 접근 허용과 앱별 호환성은 실환경 확인 대상임.

## 통합 연결 경로

계정 로그인 배포에서는 최초 설정의 Steam 연결을 사용합니다. 서버 등록 후 저장된
`HoleInOne/SteamLinks/<서버·HoleInOne 사용자 해시>/login.dat`를 재사용하며, 세이브 업로드의 켜짐/꺼짐 설정은 유지합니다.
이 경로는 보유 게임 조회·클라우드 로그인과 동일한 새 Steam 연결을 사용합니다. 기존 세이브 연결 파일은 자동 이전하거나 서버에 올리지 않습니다.
PC Steam 계정이 연결 계정과 다르면 로컬 전환을 보류합니다. 아래 별도 QR 연결 절차는 기존 개발 모드에만 남아 있습니다.

## 기존 개발 모드 사용

`build/Build-HoleInOne.ps1`로 빌드하면 `Tools/SteamCloud/HoleInOne.SteamCloud.exe`가 함께 배포됨. 설정 → Steam 세이브 계정 연결에서 Steam 모바일 앱으로 QR을 스캔함. PC의 Steam 로그인 계정과 같은 계정만 저장함. 이후 `전환할 때 Steam Cloud에도 세이브 업로드 (시험 기능)`을 켬. 로그인은 전환 전에 수행하며 전환 도중 QR이나 별도 확인창을 표시하지 않음. 기본은 비활성화임.

자격 증명은 앱 사용자 데이터의 `HoleInOne/SteamCloud/login.dat`에 Windows CurrentUser DPAPI로 암호화하여 저장함. Backend 또는 AWS에 전달하지 않음. 사용자별 토큰 경로 잠금으로 동일 계정 업로더의 중복 실행을 막음. 설정의 연결 해제는 로컬 암호화 파일을 제거하며 Steam 측의 전체 로그인 세션을 취소하는 기능은 아님.

클라우드 게임 시작 전에 로그인·게임 저장 규칙 조회·원격 기준 목록 확보를 완료함. 전환 시 Backend가 만든 동일 ZIP의 전체·개별 SHA-256을 검증하고 원격 내용과 SHA-1 비교함. 변경 파일만 업로드하며 파일 전송 확정·배치 종료·원격 재조회로 검증함. 업로드 및 다른 기기의 미완료 작업 확인이 성공한 뒤 기존 로컬 백업·복원·실행 경로로 진행함. 로컬 복원 실패 시 ZIP은 유지되며 다음 전환 재시도에서 이미 같은 내용인 파일을 재업로드하지 않음.

## 현재 지원 범위와 한계

- App ID와 사용자 ID를 매개변수로 처리함. 특정 게임 ID 하드코딩 없음.
- Steam `userdata/<account>/<appid>/remote`와 Windows Auto-Cloud의 기본 루트·파일 패턴·재귀 규칙 지원. 기존 Cloud 파일명과 플랫폼 플래그를 보존함.
- Auto-Cloud의 공유 앱 저장소·root override·동적 와일드카드 디렉터리 등 검증하지 않은 규칙은 추측하지 않고 중단함. 모든 게임의 지원을 의미하지 않음.
- ZIP에 없는 원격 파일은 삭제하지 않음. 현재 전환 ZIP은 전체 원격 스냅샷이 아니므로 삭제를 추론하지 않음.
- 기준 확보 이후 다른 기기가 수정·삭제한 대상 파일은 자동으로 덮어쓰지 않음. API가 원자적인 compare-and-swap을 제공하는 것으로 가정하지 않으며 동시 기기 사용에 대한 절대적 충돌 방지를 주장하지 않음.
- 준비 중인 게임의 저장 내용을 메모리에서 디스크로 강제 저장하는 기능은 없음. 기존 내보내기에 포함된 저장 완료 파일만 취급함.
- 기존 클라우드 PC의 pending operation을 다른 client ID로 위장하여 지우지 않음. 업로드 후에도 pending이 있으면 성공으로 위장하지 않고 로컬 실행을 보류함. 이 조건 때문에 실제 Steam 경고 제거는 아직 검증되지 않음.
- 설정이 꺼져 있으면 이전 전환 동작 유지. 켠 상태에서 인증·규칙·동기화가 실패하면 기존 게임/전환 오류 영역에서 상태를 안내하고 실행을 보류함. Steam 자체의 모든 경고창을 차단하는 기능은 아님.
- 현재는 Steam 업로드 후 로컬 파일을 교체함. 원격 충돌을 확인하기 전에 로컬을 바꾸지 않기 위한 순서이며, 병렬 복원·업로드는 구현하지 않음. 전송 지연 0초를 보장하지 않음.
- 준비 단계 45초, 업로드 요청 120초, 개별 네트워크 요청 25~30초 제한. 취소 시 실패 배치 정리에 별도 제한 시간을 사용함. 이전 기기 pending을 자동 무시하지 않음.

## 비파괴 검증

`dotnet run --project tests/SteamCloud.Checks/SteamCloud.Checks.csproj`

배포된 보조 프로그램의 종료 처리까지 검사하려면 마지막에 `-- <절대 경로>/HoleInOne.SteamCloud.exe`를 추가함.

2026-10-09 검증: 새 모듈 37개 검사(파이프 종료 검사 포함), 기존 런처 95개 검사 통과. DesktopApp/BrowserProcess Release 빌드 및 win-x64 독립 실행 배포 성공. 실계정 로그인·업로드·Steam 경고 제거·전환 지연 측정은 미실행임.

기존 런처 의존성 AngleSharp 0.9.9(NU1902), LiteDB 4.1.4(NU1904), Newtonsoft.Json 10.0.3(NU1903)의 취약성 경고 때문에 기본 엄격 빌드는 중단됨. 이번 시험 빌드에 한해 MSBuild의 `WarningsNotAsErrors`에 위 세 코드만 지정하여 경고를 유지하고 컴파일함. 프로젝트의 배포 기준은 완화하지 않았으며 외부 배포 전 기존 의존성 정비가 별도로 필요함.

위 검사는 합성 ZIP·가짜 Cloud 전송기·테스트 토큰만 사용함. 실 Steam 로그인·업로드 없음. 두 게임 ID의 공통 매핑, Auto-Cloud 매핑, 계정/게임 불일치, 원격 수정/삭제, 미완료 작업, 취소 후 배치 정리, 업로드 재검증, ZIP 변조, DPAPI 암호화를 검사함.

실환경에서는 테스트용 계정/세이브를 별도로 보존하고 계정 연결 → 읽기 전용 계획 → 업로드 → 로컬 실행 → 재동기화 → 다른 게임 순서로 확인해야 함. 시간과 경고 여부를 각각 측정하며 업로드 성공만으로 해결로 판정하지 않음.

## 내부 파이프 계약

명령줄 인수나 HTTP 포트 대신 stdin/stdout JSON Lines 사용. 토큰 값·QR URL·파일 내용·서명된 업로드 URL을 로그에 기록하지 않음. stdout의 QR PNG는 로그인 창에만 전달함.

첫 명령은 `{"operation":"auth","steamId":"...","tokenPath":"절대 경로"}` 또는 `{"operation":"session","steamId":"...","appId":2868840,"tokenPath":"절대 경로"}`임. auth는 `qr` 이벤트 후 `authenticated`, session은 기준 목록을 확보한 후 `ready` 이벤트를 반환함.

session이 ready인 상태에서 `{"operation":"preview","archivePath":"절대 ZIP 경로","sha256":"검증값"}`로 쓰기 없이 파일 계획과 개수를 확인함. `sync`는 같은 입력으로 실제 업로드함. `complete`에는 업로드 예정/완료 파일 수·변경 없는 수·로컬 전용 수·바이트 수·경과 시간이 포함됨. `preview:true`는 업로드하지 않았다는 의미임. `error`는 비밀값을 포함하지 않는 고정 code만 반환함. `cancel` 또는 stdin 종료 시 작업을 취소하고 배치를 정리함.

실제 Steam 로그인과 Cloud 쓰기를 사용하지 않는 모의 검사까지 자동 실행하며, 사용자 계정 인증 이후의 실환경 결과는 별도로 기록해야 함.

참고: https://github.com/SteamRE/SteamKit (LGPL-2.1), https://github.com/codebude/QRCoder (MIT). 패키지는 수정하지 않은 별도 DLL로 배포함.
