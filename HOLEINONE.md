# HoleInOne 구현 현황·개발 실행

Playnite 데스크톱을 포팅하는 프로젝트. 현재는 기반 코드 구현 단계이며, 인스턴트 플레이의 전체 UI 연결은 아직 완료되지 않았음.

## 현재 구현

- HoleInOne Backend HTTP 클라이언트: 세션·Stream URL, 모닥불 알림, 세이브 요청과 상태 조회.
- 전환 상태 처리: 모닥불 도달 + 설치 완료 → 세이브 요청 → `ready` → GameLoader 전환 정보 1회 반환.
- GameLoader 전환 파일 작성: 모드 파일 존재 확인, 임시 파일 작성 후 pending으로 변경. 이미 대기 중인 파일은 덮어쓰지 않음.
- Playnite 온라인 서비스 차단: 자동 업데이트·애드온 메뉴와 검색·첫 실행 애드온 다운로드·후원자 조회·진단 업로드 비활성화. 진단 파일의 로컬 생성은 유지함.
- 표시 이름 HoleInOne, Windows PowerShell 참조 경로 수정.

`BackendConnection`과 `HandoffSession`은 실제 제품 코드지만, 플레이 버튼·웹뷰·다운로드 실행 루프에는 아직 연결하지 않았음. 현재 앱을 실행해도 클라우드 플레이가 시작되지 않음.

## 빌드

Windows와 Visual Studio의 .NET 데스크톱 개발 도구가 필요함. 프로젝트는 .NET Framework 4.6.2·x86이며, COM 참조 때문에 `dotnet build` 대신 Visual Studio MSBuild를 사용함.

```powershell
Set-Location D:/Project/HoleInOne/PlaynitePorting
./build/Build-HoleInOne.ps1
```

데스크톱 실행 파일과 필수 브라우저 보조 실행 파일을 `artifacts/HoleInOne`에 함께 빌드함. Playnite 업데이트 서버를 통해 패키지를 받는 작업은 하지 않음. 첫 빌드에는 기존 프로젝트의 NuGet 의존성 복원이 필요함.

기존 Playnite 데이터와 분리한 개발 실행:

```powershell
& ./artifacts/HoleInOne/Playnite.DesktopApp.exe --userdatadir "D:/Project/HoleInOne/PlaynitePorting/artifacts/UserData" --nolibupdate
```

기존 의존성 AngleSharp·LiteDB·Newtonsoft.Json의 NuGet 보안 경고가 있음. 이번 작업에서 데이터베이스·의존성 전체 업그레이드는 수행하지 않았음. 기본 Debug 빌드 기준이며 배포 검증 완료를 뜻하지 않음.

## 자동 검증

.NET SDK 10 필요. 게임·AWS 계정 없이 실행 가능함.

```powershell
dotnet run --project tests/HoleInOne.Checks/HoleInOne.Checks.csproj -c Release
```

제품의 Backend 클라이언트·전환 소스를 직접 포함해 HTTP 규격, 설치·알림 도착 순서, 요청 중복 방지, `ready` 대기, 다른 세션 거부, 전환 파일 보존·작성을 검증함. HTTP 응답은 테스트용 처리기를 사용함. 실제 AWS·CefSharp 스트리밍·스토어 계정·게임 실행은 별도 검증 대상임.

## 남은 작업

- Steam·Epic 계정 연결과 미설치 보유 게임 감지를 내장함.
- 확인 창 없는 다운로드 가능성을 먼저 검증하고 설치 방식을 확정함.
- 미설치 게임 플레이 버튼과 웹뷰·다운로드·전환 실행을 연결함. 설치된 게임은 바로 로컬 실행함.
- Backend는 현재 게임 선택을 받지 않고 슬더스2 모드만 구현돼 있으므로, 다른 보유 게임을 잘못 스트리밍하지 않도록 대상 식별 방식을 확정함.
- 수동 웹뷰 종료와 중복 클릭, 모드 설치 준비 방식을 확정하고 실제 전환을 검증함.

세부 범위와 결정 사항은 [TASKS.md](TASKS.md)에 기록함.
