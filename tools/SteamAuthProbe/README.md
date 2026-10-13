# Steam 인증 호환성 검증 도구 — 1단계

제품의 Steam 연결 화면이 아니라, 사용자별 클라우드 로그인을 구현하기 전의 검증 도구입니다.
기존 PC의 Steam 파일·웹 쿠키·세이브 로그인 자료를 추출하지 않습니다.
백엔드 API, S3 저장, 계정 연결 및 세션 준비는 아직 변경하지 않았습니다.

## 실행

Windows 10/11 또는 Windows Server 2022 x64에서 배포 ZIP 전체를 압축 해제하고
`HoleInOne.SteamAuthProbe.exe`를 실행합니다. 배포본에는 .NET 런타임이 포함됩니다.

### A. 새 인증 (일반 PC에서도 가능)

1. `QR 로그인`을 누르고 Steam 모바일 앱으로 승인하거나, 로그인 이름·비밀번호를 입력하고
   `비밀번호 로그인`을 누릅니다. 비밀번호 방식에서 필요하면 이메일/모바일 Guard 코드를 입력합니다.
2. 표시된 Steam 계정이 의도한 계정인지 확인합니다. PC Steam 로그인은 전제하지 않습니다.
3. `인증을 암호화해 저장`을 눌러 16자 이상의 별도 암호를 두 번 입력하고 `.hioauth` 파일을 저장합니다.
   매번 다른 salt/nonce를 사용하며 PBKDF2-SHA256 600,000회와 AES-256-GCM으로 암호화합니다.
4. `민감정보 없는 결과 저장`으로 인증 결과를 저장합니다.

비밀번호·Guard 코드는 저장하지 않습니다. 인증 토큰은 도구 메모리에만 보관하며 명시적 저장을
눌렀을 때만 암호화 파일로 내보냅니다. 테스트 암호와 `.hioauth` 파일은 채팅·Git에 올리지 마세요.
암호화 파일도 로그인 권한을 담은 자료이므로 본인이 관리하는 안전한 방식으로만 시험 환경에 옮깁니다.
7일 제한은 진단 파일의 사용 기한이며 제품의 인증 보관 정책이 아닙니다.

### B. 실제 Steam 자동 로그인 (별도 깨끗한 Windows 환경만)

1. 별도 테스트용 Windows 사용자/VM과 로그인 이력이 없는 Steam 폴더를 준비합니다.
   평소 사용하는 Windows 프로필·Steam 폴더를 정리하거나 삭제해서 시험하지 마세요.
2. `암호화된 인증 열기`로 A에서 만든 파일을 엽니다.
3. `Steam.exe 선택 후 자동 로그인 시험`을 누르고 테스트 Steam을 선택합니다.
4. 도구가 Steam 서버에서 토큰과 계정 일치를 검증한 다음, 새 파일만 생성하고 Steam을 실행합니다.
5. Steam 로그인 화면을 직접 조작하지 말고 최대 180초 기다립니다. 추가 로그인이 나타나면 실패로 기록합니다.
6. 기대 계정·Steam PID·실행 경로 감지가 성공해도, 실제 Steam 화면에서 올바른 계정으로 온라인 상태인지
   사람이 확인해야 합니다. 이 감지는 Steam의 공식 로그인 완료 신호가 아닙니다.
7. 결과 JSON을 저장하고, 같은 암호화 파일로 두 번째 깨끗한 환경에서도 반복합니다.
8. 두 번째 사용자로도 A/B를 반복합니다. 검증 이후 Steam을 종료하고 테스트 VM/프로필을 폐기합니다.

다음 중 하나라도 있으면 도구는 덮어쓰지 않고 거부합니다:
- 실행 중인 Steam 프로세스
- `%LOCALAPPDATA%/Steam/local.vdf`
- 선택한 Steam 폴더의 `config/config.vdf` 또는 `config/loginusers.vdf`
- 기존 자동 로그인 레지스트리 값 또는 활성 Steam 계정

Steam을 실행한 뒤에는 이 도구가 Steam을 강제 종료하거나 로그인 파일을 삭제하지 않습니다.
로그인 시도가 실패하거나 취소돼도 시험용 로그인 자료가 남을 수 있습니다. 테스트 환경 폐기가 필요합니다.

## 공유할 결과

`steam-probe-result.json`과 다음 항목만 공유하세요:
- QR / 비밀번호 중 시험한 방식
- Windows 로컬 시험인지 GameLift 시험인지
- 추가 비밀번호/Guard 요구 여부
- 화면에서 기대 계정의 온라인 상태를 확인했는지
- 같은 파일로 두 번째 깨끗한 환경에 로그인됐는지

결과에는 계정 이름, SteamID, 비밀번호, 토큰, 파일 경로를 기록하지 않습니다.
`productionReady=false`, `cloudVerified=false`는 의도적입니다. 사람의 확인과 실제 GameLift 증거 없이
이 도구가 제품 또는 클라우드 검증 완료를 선언하지 않습니다.

## GameLift 검증

로컬 새 Windows 프로필 통과만으로 GameLift 통과를 선언하지 않습니다. 동일 도구를 별도 테스트
애플리케이션에서 실행하고 동일한 암호화 파일로 반복해야 합니다. 기존 서비스 애플리케이션은 바꾸지 않습니다.
테스트 GameLift 환경의 파일 반입 및 패키지 구성은 로컬 호환성 결과를 확인한 다음 진행합니다.
기존 V5/AuthStateImport나 실제 사용자의 Windows 프로필을 이 패키지에 넣지 않습니다.

## 구현 후보와 제한

`dpapi-utf8-v1`은 검증되지 않은 후보입니다. 신규 SteamClient refresh token을 현재 Windows 사용자의
DPAPI로 보호하고, 계정 이름의 CRC32 기반 ConnectCache 키 및 최소 VDF/로그인 선택자만 생성합니다.
기존 계정의 VDF, MachineGuid, 기억된 기기 정보를 복제하지 않습니다.
Steam 클라이언트가 이 형식을 받아들이는지는 실제 시험의 대상입니다. 실패하면 다음 구현 단계로 넘어가지 않습니다.

참고한 형식/API (외부 구현 코드를 복사하지 않음):
- SteamKit 3.4.0 인증 API: https://github.com/SteamRE/SteamKit/tree/3.4.0/Samples
- 네이티브 캐시 구조 관찰: https://github.com/utkarshdalal/GameNative/blob/master/app/src/main/java/app/gamenative/utils/SteamTokenLogin.kt
- DPAPI entropy 관찰: https://github.com/darknight1050/SteamJWT

## 재빌드

두 저장소를 같은 부모 디렉터리에 `Backend`, `PlaynitePorting` 이름으로 놓습니다. 두 저장소 모두
`codex/per-user-steam-auth` 브랜치를 사용합니다. .NET SDK 10 필요:

```powershell
dotnet run --project ../Backend/tests-steam-auth-probe/SteamAuthProbe.Checks.csproj -c Release
dotnet publish tools/SteamAuthProbe/SteamAuthProbe.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/SteamAuthProbe-win-x64
```

`build/Build-SteamAuthProbe.ps1`은 위 검사·게시와 라이선스/소스 포장을 함께 수행합니다.
SteamKit2 DLL은 교체 가능한 별도 파일로 배포합니다. 민감한 진단 파일은 배포 폴더에 넣지 않습니다.
