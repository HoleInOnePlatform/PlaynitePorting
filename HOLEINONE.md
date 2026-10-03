# HoleInOne 개발 실행

Playnite 데스크톱 기반으로 클라우드 플레이와 로컬 다운로드를 연결함. Steam·Epic 계정 연동은 내장되어 있으며 외부 애드온을 설치하지 않음.

## 구현된 동작

- 미설치 게임의 플레이: 선택한 게임의 AWS Stream URL을 Backend에서 받아 웹뷰 표시와 다운로드를 시작함.
- Steam: Steam 설치 확인 1회 후 다운로드. Epic: Legendary로 지정 폴더에 자동 다운로드.
- 설치된 게임: 바로 로컬 실행. 같은 게임의 클라우드 창이 열려 있으면 그 창을 활성화함.
- 슬더스2: 모닥불 알림 + 설치 완료 → 세이브 요청 → 서버 보관 완료 → GameLoader 설치·모드 설정 활성화·1회용 전환 파일 작성 → 웹뷰 종료 → 로컬 실행.
- 다른 게임: 클라우드 플레이·다운로드만 진행하며 세이브 이전과 자동 로컬 전환은 하지 않음.
- 클라우드 창을 직접 닫으면 자동 전환을 중단하고 이미 시작한 다운로드는 계속 진행함.
- 설치 버튼은 상세 화면에만 표시함. 목록·검색·우클릭 메뉴는 플레이로 연결함.
- Playnite 업데이트·온라인 애드온·외부 확장 및 스크립트 로딩을 비활성화함. Steam·Epic은 내장 모듈로 등록함.

## 빌드와 실행

Windows x64, Visual Studio의 .NET 데스크톱 개발 도구 필요. 앱은 .NET Framework 4.6.2·x86이며 Epic 보조 도구는 x64임. COM 참조 때문에 Visual Studio MSBuild를 사용함.

```powershell
Set-Location D:/Project/HoleInOne/PlaynitePorting
./build/Build-HoleInOne.ps1
./build/Prepare-HoleInOneTools.ps1
& ./artifacts/HoleInOne/Playnite.DesktopApp.exe --userdatadir "D:/Project/HoleInOne/PlaynitePorting/artifacts/UserData" --nolibupdate
```

준비 스크립트는 Legendary 0.21.1 공식 바이너리를 SHA-256으로 확인하고 라이선스·해당 버전 소스를 함께 보관함. GameLoader는 기본적으로 형제 저장소의 `GameLoader/dist/GameLoader`에서 가져옴. 다른 위치라면 `-GameLoaderDirectory <경로>` 지정. GameLoader DLL과 manifest를 앱의 `Mods/GameLoader`에 준비하며, 실제 게임에는 슬더스2 전환 시 복사함.

1. Backend를 실행하고 실제 AWS 리전·스트림 그룹·게임별 애플리케이션을 설정함. 기본 `Streaming:Enabled=false`에서는 클라우드 플레이가 시작되지 않음.
2. 홀인원 메뉴 → 라이브러리 → **HoleInOne 설정**에서 Steam·Epic 연동 설정을 선택함. Backend 주소·토큰·Epic 다운로드 폴더는 사용자 설정 화면에 표시하지 않음.
3. **Steam 연동 설정** 또는 **Epic 연동 설정**을 열면 원본 Playnite 애드온 설정 화면이 표시됨. 계정 연결·미설치 게임 가져오기를 켜고 인증을 완료한 뒤 **확인**을 누름. Steam 로그인 성공 시 인증 창은 자동으로 닫힘. Epic 자동 다운로드는 같은 인증을 사용하므로 Legendary에서 별도로 로그인하지 않음.
4. 메뉴에서 라이브러리를 새로고침하면 설치된 게임과 계정의 미설치 보유 게임이 표시됨.
5. 미설치 게임의 플레이 버튼을 누름. Steam은 설치 확인 창을 한 번 승인함. AWS 스트리밍 페이지의 시작 조작이 표시되면 진행함.

앱 설정은 지정한 사용자 데이터 폴더의 `holeinone.json`, 원본 스토어 연동 설정·인증 정보는 `ExtensionsData` 아래 해당 모듈 폴더에 저장됨. Steam 웹뷰 쿠키와 Epic 다운로드 도구의 `HoleInOne/Epic` 정보도 같은 사용자 데이터 폴더를 사용함. 기존 자체 연동에서 원본 애드온으로 바뀌었으므로 원본 설정 화면에서 최초 인증이 필요함. 설정·인증 데이터는 공유하거나 저장소에 추가하지 않아야 함.

현재 MVP의 Backend 주소·공통 토큰은 개발자가 로컬 연결을 준비할 때 `holeinone.json`의 `BackendUrl`·`BearerToken`에 설정함. Epic 다운로드 경로는 같은 파일의 `DownloadDirectory`를 사용하고, 지정하지 않으면 `%LOCALAPPDATA%/HoleInOne/Games`를 사용함. 사용자가 직접 입력하지 않으며, 연동 설정 창을 열고 닫아도 이 값들은 변경하지 않음. 공통 토큰 방식은 개발 테스트용이며 사용자별 인증·자동 발급은 아직 구현하지 않았음.

Steam 2.44·Epic 2.30 원본 소스를 함께 빌드함. Steam의 Playnite 서버 조회는 Steam 직접 조회로 대체했고 Epic 자동 다운로드만 Legendary를 유지함. 원본 버전과 변경점은 [UPSTREAM.md](source/BuiltinLibraries/UPSTREAM.md) 참고.

게임 ID는 Steam `steam-<AppID>`, Epic `epic-<AppName>` 형식임. Backend에 해당 게임의 AWS 애플리케이션이 등록되어 있어야 함. 미등록 게임을 다른 게임으로 대신 실행하지 않음. 자세한 설정은 [Backend README](../Backend/README.md) 참고.

슬더스2 v0.107.1은 전환 직전에 현재 Steam 계정의 `SlayTheSpire2/steam/<SteamID64>/settings.save`에서 `mod_settings.mods_enabled=true`로 설정하고 GameLoader를 활성화함. 최초 동의 화면 없이 모드를 로드하기 위한 설정이며, 기존 화면·음량·다른 모드의 개별 설정은 유지함. 설정 파일이 없으면 현재 게임의 설정 schema 5로 생성함. `nomods` 실행 옵션은 제거해야 하며, 게임 업데이트로 설정 형식이 바뀌면 다시 확인해야 함. 클라우드에는 GameLink만, 로컬에는 GameLoader를 사용함.

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
