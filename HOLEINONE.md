# HoleInOne 개발 실행

## Steam 세이브 직접 업로드 제거 (2026-10-18)

- Steam Cloud 직접 업로드, QR 계정 연결 및 전용 보조 프로그램을 제거함.
- 로컬 전환은 Backend 세이브 다운로드 → 검증 → 기존 세이브 백업·복원 → 로컬 실행으로 동작함. Steam 세이브 계정 사전 연결은 필요하지 않음.
- 이전 배포 ZIP은 과거 빌드이며 이 변경은 새 빌드부터 적용됨.

## 저장공간 관리 (2026-10-18)

- 설정의 ‘자동 제거 제외 게임’에서 현재 설치된 게임을 체크하면 즉시 저장되며 재실행 후에도 유지됨. 보호 게임은 용량에는 포함되지만 자동 정리 계산과 실제 제거에서 제외함. 나머지 게임으로 공간이 부족하면 설치를 중단함. 설치·정리 중 보호 설정 변경은 막으며, 제거 직전에도 보호 설정을 재확인함. 사용자가 직접 요청하는 수동 제거와는 별개임.
- 설치 시 제거 확인창은 표시하지 않으며, 사전 계산된 목록의 게임을 추가 확인 없이 자동 제거함. 자동 제거 제외 설정은 계속 적용함.

- HoleInOne 설정의 저장공간 관리에서 Steam/Epic 설치 폴더와 합산 한도를 저장하고 실제 사용량을 조회할 수 있음. 최대 용량을 저장하지 않은 경우 설치 폴더가 위치한 드라이브의 전체 용량을 기본값으로 사용함(GiB 단위 내림). 같은 드라이브는 한 번만 계산하고 서로 다른 드라이브는 합산하며, 사용자가 저장한 한도는 유지함. Steam 기본값은 설치된 Steam의 `steamapps/common`, Epic 기본값은 `Program Files/Epic Games`이며 기존 Epic 경로 설정은 보존함.
- Steam은 등록된 라이브러리만 사용함. 다른 드라이브를 사용하려면 Steam 설정 → 저장공간에서 라이브러리를 추가하고 그 아래 `steamapps/common`을 지정함. 폴더 변경은 다음 설치부터 적용하며 기존 파일은 이동하지 않음.
- 설치 전 두 폴더의 실제 파일 용량과 대상 드라이브의 여유 공간을 계산함. Steam은 라이브러리에 수집한 설치 용량, Epic은 최신 파일 매니페스트 합계를 사용함. 용량이 없으면 설치를 중단하고 새로고침을 안내함.
- 마지막 플레이가 오래된 순서(플레이 기록 없음 우선)로 제거 목록 전체를 먼저 확정함. 실행/시작/설치/제거 중인 게임, 설정 폴더 밖 게임, 다른 게임과 폴더가 겹치는 항목은 제외함. 연결 폴더나 읽기 실패를 빈 용량으로 취급하지 않음.
- 현재 사용량보다 낮은 한도를 저장하면 경고와 저장 여부 확인을 표시함. 저장만으로 게임을 제거하지 않음. 설치 전 계획이 충분하면 추가 확인 없이 스토어 제거 기능을 호출함. 제거 직전 게임 상태와 보호 설정을 재검증하고 제거 완료를 기다림. 실제 여유 공간을 재검증한 뒤 다운로드함. 제거 실패·시간 초과·잔여 파일로 공간이 부족하면 중단하며 계획 외 게임을 추가로 제거하지 않음. 스토어가 제거 확인 창을 표시하면 그 절차를 완료해야 함.
- 게임 자체가 한도를 넘거나 전체 정리로도 부족하면 삭제 없이 경고함. 앱 내 설치는 한 번에 하나씩 진행하며 설치 중 설정 변경은 막음. 앱 외부 스토어의 다운로드·업데이트는 이 한도로 제어하지 않음.
- 공간 계획 검사: `dotnet run --project tests/Storage.Checks` (13개). 실제 계정의 설치·제거 및 화면 확인은 별도 검증 필요.

## V5 세션 사전 준비 (2026-10-10)

플랫폼 시작 시 Steam을 준비하고 플레이 시 기존 전체화면 웹뷰로 연결합니다. Backend 동반 배포·V5 설정·검증 범위는 [V5_PREWARM.md](V5_PREWARM.md)를 참고하세요.

## 배포 폴더 정리 (2026-10-11)

- 최신 정리본: `artifacts/HoleInOne-20261011.zip` 및 같은 이름의 폴더. 2026-10-09 Steam Cloud 시험 빌드를 재패키징함. 실행 코드·의존성 버전은 동일함.
- 최상위는 `Start-HoleInOne.cmd`, `READ-ME.txt`, `App`, `Docs`, `Diagnostics`의 5개 항목임. 전체 압축 해제 후 시작 파일 실행. 팀 테스트 사용자 데이터 경로는 유지함.
- 실행 파일·DLL·브라우저 리소스·라이선스는 상대 경로를 유지한 채 `App`에 보관함. 사용 안내는 `Docs`, 서버 확인 및 안전 모드 실행은 `Diagnostics`에 있음.
- 앞으로 `Prepare-DevelopmentPackage.ps1`도 이 구조의 ZIP과 `<ZIP 이름>-Package` 폴더를 생성함. 원본 빌드 폴더는 유지함.
- 로컬 테스트 전달도 `Build-HoleInOne.ps1`의 원시 출력물을 직접 전달하지 말고 `New-HoleInOnePackage.ps1`로 위 구조를 생성함. 원시 출력은 `artifacts/.build/<빌드명>`에 두고 사용자에게는 패키지 루트의 `Start-HoleInOne.cmd`를 안내함. 기존 테스트의 `--userdatadir` 경로를 시작 파일에 유지해야 함.
- 기존 빌드 재패키징: `./build/New-HoleInOnePackage.ps1 -ApplicationDirectory <빌드 폴더> -OutputDirectory <새 배포 폴더> -OutputZip <새 ZIP 경로>`. 기존 대상은 덮어쓰지 않음.
- 검증: 993개 보존 파일의 SHA-256 일치, ZIP 필수 파일·구조 확인. 독립 실행 검사는 원본과 정리본 모두 시작 직후 종료되어 완료하지 못함. 실제 로그인·게임 실행은 재검증하지 않음.

## 홀인원 전용 아이콘 (2026-10-04)

- 최신 공유 파일: `artifacts/HoleInOne-TeamTest-20261004-icon.zip`. 기존 홀인원을 모두 종료하고 새 폴더에 전체 압축 해제 후 `Start-HoleInOne.cmd` 실행. 팀 테스트 사용자 데이터 경로는 그대로 유지함.
- 민트색 H와 원형 포털을 결합한 전용 아이콘 적용. 실행 파일·작업 표시줄·트레이·메뉴·시작 화면·브라우저 보조 프로세스·확장 파일 아이콘을 교체함. 기본 테마의 기존 파일명은 유지함.
- 원본과 생성 프롬프트: [assets/branding](assets/branding/README.md). `build/Apply-HoleInOneBranding.ps1`로 16~256px ICO 9개 크기와 앱 리소스 재생성 가능.
- 데스크톱·브라우저 빌드, Windows 실행 파일 내장 아이콘 추출, ICO 각 프레임의 투명도, ZIP 내 아이콘 일치 확인 완료. 기존 실행 중인 앱은 종료하지 않았으며 이번 아이콘 변경에서 게임 실행·클라우드 전환은 재검증하지 않음.

## Playnite와 독립 실행 (2026-10-04)

- 이전 독립 실행 배포: `artifacts/HoleInOne-TeamTest-20261004-independent.zip`. 현재는 위 전용 아이콘 ZIP 사용. 새 폴더에 전체 압축 해제 후 `Start-HoleInOne.cmd` 실행. 이전 배포 폴더에 덮어쓰지 않음.
- 실행 파일은 `HoleInOne.exe`, 브라우저 보조 실행 파일은 `HoleInOne.BrowserProcess.exe`. 제품명·작업 표시줄 식별자·중복 실행 감지·명령 통신·`holeinone://` 링크·자동 시작 등록을 원본 Playnite와 분리함.
- 팀 테스트 사용자 데이터는 기존 `%LOCALAPPDATA%\HoleInOne-TeamTest`를 유지함. 기존 팀 테스트 앱을 먼저 종료하고 새 시작 파일을 사용하면 스토어 연동·서버 주소 설정을 유지함. 같은 사용자 데이터 폴더를 쓰는 구버전과 신버전 홀인원은 동시에 실행하지 않음.
- 설치형 기본 설정·라이브러리는 `%APPDATA%\HoleInOne`, 임시 파일은 `%TEMP%\HoleInOne` 사용. 포터블 기본 저장 위치와 명시적인 `--userdatadir`는 기존 방식 유지. 원본 Playnite 데이터를 자동 복사하거나 원본 등록을 수정하지 않음.
- 내부 SDK·소스 네임스페이스와 로그 파일명 `playnite.log`는 호환성을 위해 유지함. 현재 배포는 홀인원 전용 포터블 빌드이며 원본 Playnite 설치기와 업데이트 스크립트는 사용하지 않음.
- 독립 실행 검사 5개 통과: Playnite 이름의 기존 프로세스가 실행 중인 상태에서 시작, 제품 식별, 중복 실행 전달, 기존 등록 보존, 독립 종료. 런처 회귀 검사 95개 통과. 이번 변경으로 실제 클라우드 게임·세이브 전환을 다시 검증한 것은 아님.

```powershell
./build/Build-HoleInOne.ps1 -OutputDirectory artifacts/HoleInOne-Independent
./build/Prepare-HoleInOneTools.ps1 -OutputDirectory artifacts/HoleInOne-Independent
./tests/Identity/Identity.Tests.ps1 -ApplicationDirectory artifacts/HoleInOne-Independent
```

독립 실행 검사는 별도 임시 사용자 데이터로 실행하며 기존 HoleInOne을 닫은 상태에서 수행함. Windows 브라우저 프로세스 통신이 허용되는 환경이 필요함. 이전 Playnite 이름의 실행 파일이 남은 폴더로 빌드·패키징하면 혼합 배포 방지를 위해 오류 처리함.

## 팀원 PC용 개발 배포 (2026-10-04)

- 이전 주소 설정 UI 배포: `artifacts/HoleInOne-TeamTest-20261004-settings.zip` (약 264MB). 현재는 위 독립 실행 ZIP 사용. Windows 10/11 x64에서 전체 압축 해제 후 `Start-HoleInOne.cmd` 실행.
- 서버: `http://13.158.18.241:5010/`. EC2가 켜져 있고 보안 그룹 TCP 5010에 팀원 공인 IP(`/32`)가 허용되어야 함. 현재 PC에서 인증된 health 응답 및 인증 없는 요청 401 확인 완료.
- `holeinone.development.json`에 주소·공동 접속 토큰 포함. AWS 키·Backend 설치 불필요. HoleInOne 회원 로그인은 사용하지 않으며 Steam/Epic은 본인 계정으로 연동함.
- 사용자 데이터는 `%LOCALAPPDATA%\HoleInOne-TeamTest`에 저장. 연결 문제는 PowerShell에서 `Test-Backend.ps1` 실행 후 확인.
- 현재 클라우드 설정 게임은 Slay the Spire 2(`steam-2868840`). 팀원 PC의 실제 게임·다운로드·세이브 전환은 수동 검증 필요.
- EC2 IP 변경 시 서버 PublicUrl은 Backend 시작 시 자동 갱신됨. 앱 메뉴 → 라이브러리 → HoleInOne 설정 → 개발 서버 주소에서 새 IP 입력 → 연결 확인 → 주소 저장. IP만 입력하면 `http://IP:5010/`으로 처리함. 다음 게임 실행부터 사용하며 재실행 후에도 유지됨. 클라우드 게임 실행 중에는 주소 저장을 막음.
- 주소는 사용자 데이터의 `holeinone.development-address.json`에 별도로 저장하고, 패키지의 기본 주소보다 우선 적용함. 접속 토큰·스토어 설정·다운로드 경로는 유지. 정식 배포에서는 주소 변경 UI와 개발용 주소 덮어쓰기를 사용하지 않음.
- HTTP·공동 토큰 방식의 팀 개발용 Debug 빌드이므로 지정된 팀원에게만 공유하고 공개 배포하지 않음.

재생성: `Build-HoleInOne.ps1 -Configuration Debug -OutputDirectory artifacts/HoleInOne-TeamTest` → 같은 폴더에 `Prepare-HoleInOneTools.ps1` 적용 → `Prepare-DevelopmentPackage.ps1 -ApplicationDirectory <앱 폴더> -ConfigurationFile <개발 설정 파일> -OutputZip <새 ZIP 경로>` 실행. 패키지 스크립트는 사용자 데이터·서버 설정·개인 키 혼입과 기존 ZIP 덮어쓰기를 거부함.

주소 설정 UI 추가 후 런처 검사 78개 통과. 새 Debug 빌드의 실제 x86 .NET Framework 코드로 IP 단독 입력 처리와 EC2 인증 연결 확인 성공. 새 ZIP 필수 파일·개인 키 및 사용자 설정 혼입 검사 완료.

## 향후 정식 서비스 빌드

정식 서비스 전환 계획은 [Backend 배포 Plan](../Backend/DEPLOYMENT_PLAN.md) 참고. 외부 배포 빌드는 `Build-HoleInOne.ps1 -Configuration Release -Distribution -ServiceUrl <실제 HTTPS 주소>`로 생성함. 이 모드는 `holeinone.service.json` 누락 시 개발 PC 주소로 대체하지 않고 시작 오류를 표시함. 기본 개발 빌드는 기존 로컬 실행을 유지함. `Set-DemoService.ps1`는 로그인 기능을 제공하는 HoleInOne 서버의 상태 응답까지 확인하며, 개발 빌드 자체를 Distribution 빌드로 변경하지는 않음.

Playnite 데스크톱 기반으로 클라우드 플레이와 로컬 다운로드를 연결함. Steam·Epic 계정 연동은 내장되어 있으며 외부 애드온을 설치하지 않음.

## 구현된 동작

- 미설치 게임의 플레이: 선택한 게임의 AWS Stream URL을 Backend에서 받아 웹뷰 표시와 다운로드를 시작함.
- Steam: Steam 설치 확인 1회 후 다운로드. Epic: Legendary로 지정 폴더에 자동 다운로드.
- 설치된 게임: 바로 로컬 실행. 같은 게임의 클라우드 창이 열려 있으면 그 창을 활성화함.
- 모든 클라우드 게임: 설치 완료 시 게임 화면 오른쪽 위에 토스트 형태로 안내 문구·전환 버튼·60분 타이머 표시. 게임 화면 크기를 줄이지 않고 겹쳐 표시하며, 토스트 밖 입력은 게임에 전달함. 전환 또는 만료까지 유지하며 만료 시 웹뷰 종료, 로컬 게임은 자동 실행하지 않음.
- 전환 버튼: AWS 내보내기 예약 → 클라우드 세션 종료 → Backend에서 세이브 선별 → 홀인원에서 검증·기존 파일 백업·복원 → 웹뷰 종료 → 로컬 실행. 세이브 없음이 확인된 경우만 복원을 생략함.
- 게임 시작 전 다운로드가 완료되어도 설치 상태를 계속 관찰해 전환 버튼을 표시함. 설치 완료 정보는 경로·설치됨·설치 중 해제 순으로 반영하며, 스토어 갱신 중 일시적인 상태 변경으로 관찰을 종료하지 않음. 웹페이지 로딩과 무관하게 버튼을 가장 위에 표시함.
- 미시작/준비 중인 클라우드 세션 또는 저장 데이터 없음이 확인되면 세이브 복원 없이 웹뷰를 닫고 로컬 실행함. 이 경우 Steam 계정과 세이브 목적지 검사도 생략함. 실행 중이며 파일 유무를 모르는 경우에는 Backend에서 AWS 결과를 확인해야 함.
- 새 전환은 GameLink·GameLoader·Ludusavi 실행 파일을 사용하지 않음. 게임별 저장 위치 목록만 Backend에 포함함. 지원하지 않는 게임·저장 경로는 전환 전에 오류로 처리함.
- 클라우드 창을 직접 닫으면 전환을 중단하고 Backend에 클라우드 종료를 요청함. 이미 시작한 다운로드는 계속 진행함.
- 게임 웹뷰는 주소창·제목줄·테두리·브라우저 우클릭 메뉴 없는 전체화면이며 작업표시줄 영역까지 덮음. Alt+F4로 종료 가능하며 Alt+Tab으로 다른 앱으로 전환하면 최상위 표시를 해제함. 게임 웹뷰의 Leave Site 확인은 자동 통과하고 로그인 웹뷰는 기존 동작을 유지함.
- 설치 완료 상태를 먼저 저장·표시하고 용량 계산은 백그라운드로 진행함. Steam 완료 감지는 대상 게임의 설치 정보만 1초 간격으로 확인하며 일시적인 파일 읽기 실패는 재시도함. 상세 화면의 설치 완료 상태 변경 알림도 반영함.
- 설치 버튼은 상세 화면에만 표시함. 목록·검색·우클릭 메뉴는 플레이로 연결함.
- Playnite 업데이트·온라인 애드온·외부 확장 및 스크립트 로딩을 비활성화함. Steam·Epic은 내장 모듈로 등록함.

## 빌드와 실행

Windows x64, Visual Studio의 .NET 데스크톱 개발 도구 필요. 앱은 .NET Framework 4.6.2·x86이며 Epic 보조 도구는 x64임. COM 참조 때문에 Visual Studio MSBuild를 사용함.

```powershell
Set-Location D:/Project/HoleInOne/PlaynitePorting
./build/Build-HoleInOne.ps1 -OutputDirectory artifacts/HoleInOne-Independent
./build/Prepare-HoleInOneTools.ps1 -OutputDirectory artifacts/HoleInOne-Independent
& ./artifacts/HoleInOne-Independent/HoleInOne.exe --userdatadir "D:/Project/HoleInOne/PlaynitePorting/artifacts/UserData" --nolibupdate
```

준비 스크립트는 Legendary 0.21.1 공식 바이너리를 SHA-256으로 확인하고 라이선스·해당 버전 소스를 함께 보관함. GameLoader 준비는 `-GameLoaderDirectory`를 명시한 과거 모드 방식 빌드에서만 수행함. 새 AWS 전환에는 필요하지 않음. 두 스크립트 모두 `-OutputDirectory`로 실행 중인 앱과 다른 빌드 폴더 지정 가능함.

AWS 전환 설정은 [EXPORT_HANDOFF.md](../Backend/EXPORT_HANDOFF.md) 참고. 로컬 설정의 S3 경로는 `s3://holeinone-save/holeinone/exports/`이며 Windows 클라우드 실행 환경 조회 완료. 사용자가 클라우드 게임 실행 후 Steam이 기존 세이브를 동기화한다고 확인하여 테스트용 `Export:Enabled=true`, `StartsWithoutSaves=true`로 설정함. Backend 재시작 필요. 세션 시작 후 생성·변경된 파일만 내보내므로 이전 슬롯도 포함되는지 실제 검증 필요. 세이브 적용 후 자동으로 저장 슬롯을 선택하는 기능은 포함하지 않음.

토스트 UI 빌드: `artifacts/ExportHandoffToast/Playnite.DesktopApp.exe`. 기존 앱 종료 후 같은 사용자 데이터 경로를 지정해 실행함. UI 검사 10개 통과(게임 화면 크기 유지, 토스트 밖 입력 통과, 좁은 창, 중복 클릭, 타이머·종료·정리 포함). 실제 스트리밍 페이지 위 표시와 마우스 잠금 상태에서 전환 버튼 조작은 수동 확인 필요.

최신 세이브 경로·오류 진단 수정 빌드는 `artifacts/ExportHandoffDiagnostics/Playnite.DesktopApp.exe`. Steam Remote Storage 파일도 현재 로컬 Steam 계정으로 복원하며, 실패 단계와 요청 ID를 토스트에 표시함. Backend도 재시작해야 새 내보내기 상태 대기·Steam 저장 위치 규칙이 적용됨. 실제 ZIP의 109개 파일 선택을 확인했으며 게임 내 불러오기 결과는 재테스트 필요.

1. Backend를 실행하고 실제 AWS 리전·스트림 그룹·게임별 애플리케이션을 설정함. 기본 `Streaming:Enabled=false`에서는 클라우드 플레이가 시작되지 않음.
2. 홀인원 메뉴 → 라이브러리 → **HoleInOne 설정**에서 Steam·Epic 연동 설정을 선택함. Backend 주소·토큰·Epic 다운로드 폴더는 사용자 설정 화면에 표시하지 않음.
3. **Steam 연동 설정** 또는 **Epic 연동 설정**을 열면 원본 Playnite 애드온 설정 화면이 표시됨. 계정 연결·미설치 게임 가져오기를 켜고 인증을 완료한 뒤 **확인**을 누름. Steam 로그인 성공 시 인증 창은 자동으로 닫힘. Epic 자동 다운로드는 같은 인증을 사용하므로 Legendary에서 별도로 로그인하지 않음.
4. 메뉴에서 라이브러리를 새로고침하면 설치된 게임과 계정의 미설치 보유 게임이 표시됨.
5. 미설치 게임의 플레이 버튼을 누름. Steam 저장 위치가 하나면 바로 설치를 요청하고, 여러 개면 HoleInOne에서 위치를 선택함. AWS 스트리밍 페이지의 시작 조작이 표시되면 진행함.

Steam 다운로드는 `steam.exe -silent +app_install <AppID> <volumeindex>`로 요청함. Steam에 등록된 저장 위치만 사용하며, 위치 선택을 취소하면 다운로드하지 않음. Steam의 설치 확인 URL은 호출하지 않음. 로그인·클라이언트 업데이트 등의 Steam 화면까지 생략하는 것은 보장하지 않음. 2분 안에 설치 파일 갱신이나 설치 완료가 확인되지 않으면 HoleInOne에서 실패를 안내함. 명령은 Steam 내부 인터페이스이므로 클라이언트 업데이트에 따른 호환성 확인이 필요함. 자동 검사와 빌드 외에 실제 계정의 다운로드·창 표시 여부는 별도 실기기 검증 대상임.

Steam 게임 제거는 `steam.exe -silent +app_uninstall <AppID>`로 요청하며 `steam://uninstall` URL을 호출하지 않음. 기존 설치 상태 감시로 제거 완료를 반영함. Steam 내부 명령이므로 실제 제거 및 확인창 생략 여부는 사용자 실기기 검증 대상임.

2026-10-18 사용자 테스트에서 실제 제거 및 제거 확인창 생략을 확인함. Steam 메인 창은 여전히 표시됨. `-silent` 외에 이 제거 명령의 창 표시를 확실히 차단하는 지원 옵션은 확인되지 않음. Steam 요청 실행은 백그라운드로 이동하고 요청 반환 시간을 로그에 기록함. 기존 제거 컨트롤러는 기본 동기 호출을 유지하며 Steam만 비동기 실행함. 요청 실패는 기존 오류 표시·제거 중 상태 해제 경로로 처리함. 응답 없음 개선 여부는 후속 실기기 검증 대상임.

앱 설정은 지정한 사용자 데이터 폴더의 `holeinone.json`, 원본 스토어 연동 설정·인증 정보는 `ExtensionsData` 아래 해당 모듈 폴더에 저장됨. Steam 웹뷰 쿠키와 Epic 다운로드 도구의 `HoleInOne/Epic` 정보도 같은 사용자 데이터 폴더를 사용함. 기존 자체 연동에서 원본 애드온으로 바뀌었으므로 원본 설정 화면에서 최초 인증이 필요함. 설정·인증 데이터는 공유하거나 저장소에 추가하지 않아야 함.

개발 모드는 `holeinone.json`의 `BackendUrl`·`BearerToken`을 사용함. 사용자 로그인 배포는 실행 파일 옆 `holeinone.service.json`의 공개 HTTPS 주소를 사용하며, 기존 개발 주소·공통 토큰을 무시함. 이 배포에서는 앱 시작 시 아이디·비밀번호로 로그인해야 하며, 계정별 토큰은 프로세스 메모리에만 보관함. 로그인은 기본 12시간 유효하고 앱이나 서버를 다시 시작하면 다시 로그인함. 설정 화면에서 계정을 확인하거나 로그아웃할 수 있으며 클라우드 게임이 열려 있으면 먼저 닫도록 안내함.

Epic 다운로드 경로는 사용자 설정 파일의 `DownloadDirectory`를 사용하고, 지정하지 않으면 `%LOCALAPPDATA%/HoleInOne/Games`를 사용함. Steam/Epic 연동은 게임 소유권·다운로드를 위한 별도 로그인임. HoleInOne 로그인으로 스토어 로그인까지 대체하지 않음.

## 로그인부터 시작하는 시연 배포

백엔드 계정 생성과 CloudFront 구성은 [Backend 배포 절차](../Backend/README.md#도메인-없는-로그인-시연-배포) 참고.

앱 빌드와 배포 주소 확정은 개발 PC에서 한 번 수행함.

```powershell
Set-Location D:\Project\HoleInOne\PlaynitePorting
.\build\Build-HoleInOne.ps1 -OutputDirectory .\artifacts\HoleInOne-Login
.\build\Prepare-HoleInOneTools.ps1 -OutputDirectory .\artifacts\HoleInOne-Login
.\build\Set-DemoService.ps1
```

마지막 명령에서 실제 CloudFront HTTPS 주소를 입력함. 서버 건강 상태 확인에 성공하면 주소만 담은 `holeinone.service.json`을 생성함. 비밀번호·공용 토큰·AWS 키·개발 PC 사용자 데이터는 배포 폴더에 넣지 않음. 새 빌드 때는 `Build-HoleInOne.ps1 -ServiceUrl <실제 HTTPS 주소>`로 동일 설정 가능함.

`artifacts/HoleInOne-Login` 폴더 전체를 노트북으로 복사함. **주소 설정 전의 폴더는 아직 로그인 시연용 완성본이 아님.** 노트북에서는 `HoleInOne.exe`를 실행하고 시연 계정으로 로그인함. Steam 클라이언트와 해당 게임 소유 계정도 미리 준비함. 설치된 게임은 바로 로컬 실행되므로, 다운로드와 클라우드 전환 시연은 미설치 게임으로 시작해야 함. 기존 게임·세이브를 임의 삭제하지 말고 시연 준비 전에 백업함.

리허설: 앱 실행 → HoleInOne 로그인 → Steam/Epic 연동 및 라이브러리 새로고침 → 미설치 게임 플레이 → 스트리밍 중 게임 저장 → 다운로드 완료 → 로컬 전환 → 같은 저장 불러오기. 앱 실행과 로그인만으로 마지막 단계까지 성공한 것으로 간주하지 않음. 시연 당일 SSH·pem·공용 토큰 입력은 필요하지 않지만, 운영자가 미리 EC2와 GameLift 스트리밍 용량을 준비해야 함.

2026-10-04 확인: 로그인 API·계정 격리 30개, 기존 Backend 44개, AWS export 모의 전환 50개, 런처 계약 38개 검사 통과. Windows 앱 Debug 빌드와 linux-x64 백엔드 publish 완료. 실제 AWS 로그인·게임 실행·최종 저장 불러오기는 미검증임. 기존 의존성 AngleSharp 0.9.9, LiteDB 4.1.4, Newtonsoft.Json 10.0.3의 NuGet 보안 경고가 남아 있으며, 이번 로그인 변경에서 라이브러리 업그레이드는 수행하지 않음. 배포 폴더에는 서버 주소 확정 전 `holeinone.service.json`이 없으므로 아직 시연용 완성본으로 배포하지 않음.

Steam 2.44·Epic 2.30 원본 소스를 함께 빌드함. Steam의 Playnite 서버 조회는 Steam 직접 조회로 대체했고 Epic 자동 다운로드만 Legendary를 유지함. 원본 버전과 변경점은 [UPSTREAM.md](source/BuiltinLibraries/UPSTREAM.md) 참고.

게임 ID는 Steam `steam-<AppID>`, Epic `epic-<AppName>` 형식임. Backend에 해당 게임의 AWS 애플리케이션이 등록되어 있어야 함. 미등록 게임을 다른 게임으로 대신 실행하지 않음. 자세한 설정은 [Backend README](../Backend/README.md) 참고.

이전 슬더스2 모닥불·GameLoader 전환 코드는 호환성 검증용으로 남아 있으나 현재 데스크톱 전환에서는 호출하지 않음. 새 전환은 모드 활성화 설정·1회용 GameLoader 파일을 작성하지 않음.

## 검증

```powershell
dotnet run --project tests/HoleInOne.Checks/HoleInOne.Checks.csproj -c Release
# Visual Studio Developer PowerShell에서 원본 Steam 인증 처리 검증
MSBuild tests/BuiltinLibraries.Checks/BuiltinLibraries.Checks.csproj /restore /p:Platform=x86 /p:PostBuildEvent=
& ./tests/BuiltinLibraries.Checks/bin/x86/Debug/net462/BuiltinLibraries.Checks.exe
```

HTTP 전환·게임 식별·1회용 파일·Steam/Epic 설치 감지 33개 자동 검증 통과. 원본 Steam 로그인 처리의 성공·로그아웃·로그인 페이지·누락 토큰·빈 토큰·변경된 페이지 구조 6개 검증 통과. 데스크톱과 브라우저 보조 실행 파일 빌드, 분리된 사용자 데이터에서 Steam 2.44·Epic 2.30 모듈 로딩과 데스크톱 시작을 확인함. 기존 Backend의 로컬 HTTP 연동 검증은 44개 통과했음. 새 원본 연동의 실제 계정 로그인·보유 게임 가져오기·다운로드, AWS 웹뷰의 영상·소리·입력, 실게임 자동 전환은 아직 확인하지 않았음.

웹뷰 캐시 루트를 사용자 데이터 폴더에 명시함. 그래픽 환경 문제 시 실행 인수에 `--forcesoftrender`를 추가해 소프트웨어 렌더링을 사용할 수 있음.

게임 웹뷰는 CEF 151의 스캔 코드 형식에 맞춰 Windows 키 입력을 전달함. 기본 CefSharp WPF 처리와 기존 LPARAM 전달에서는 DOM `KeyboardEvent.code`가 빈 값인 문제를 재현했고, 게임용 입력 처리에서 W·Esc·방향키·Tab·Space의 누름/해제 5쌍을 실제 CefSharp 웹뷰로 확인함. Steam·Epic 인증 웹뷰에는 이 처리를 적용하지 않음. AWS 게임에서의 최종 입력 확인은 별도로 필요함.

입력 회귀 검증은 Visual Studio Developer PowerShell에서 `MSBuild tests/WebViewInput.Checks/WebViewInput.Checks.csproj /restore /p:Platform=x86 /p:PostBuildEvent=`로 빌드한 뒤 `tests/WebViewInput.Checks/bin/x86/Debug/net462/WebViewInput.Checks.exe`에 빌드된 `artifacts/HoleInOne`의 절대 경로를 인수로 전달함. Windows 데스크톱 세션과 브라우저 보조 실행 파일이 필요하며, 외부 사이트나 AWS에는 접속하지 않음.

기존 AngleSharp·LiteDB·Newtonsoft.Json 의존성의 NuGet 취약성 경고는 남아 있음. 이번 작업에는 의존성 전체 교체를 포함하지 않음.

범위와 구현 순서는 [TASKS.md](TASKS.md) 참고.
