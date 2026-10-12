# V5 시작 시 사전 준비

`codex/v5-session-prewarm`은 `feature/user`의 기존 전체화면 게임 웹뷰·설치·세이브 전환 구현을 기반으로 합니다. 기본 브랜치는 변경하지 않습니다.

## 동작

1. 플랫폼의 기존 로그인 절차가 끝나면 `POST /v1/holeinone/sessions/prepare`를 백그라운드로 호출합니다. 라이브러리 갱신과 UI는 기다리지 않습니다. 창을 열거나 로컬 다운로드를 시작하지 않습니다.
2. Backend가 `a-8pXWFKeVC`를 `NO_CLIENT_CONNECTION`으로 시작합니다. V5 런처가 Steam 자동 로그인 후 대기합니다.
3. 미설치 Slay the Spire 2의 플레이를 누르면 준비 중인 요청과 세션을 재사용하고 `POST .../{sessionId}/play`로 연결 페이지를 받습니다. 기존 전체화면 WebView 및 로컬 설치 흐름을 유지합니다.
4. 페이지가 준비된 AWS 세션에 WebRTC로 연결하고 V5에 `play`를 보냅니다. 같은 세션의 재접속/새로고침은 동일한 requestId를 사용합니다.
5. 사용하지 않은 준비는 Backend에서 기본 9분 후 종료합니다. 만료 후 플레이를 누르면 한 번 새로 준비합니다. 앱을 열어 둔 것만으로 무한 재준비하지 않습니다.
6. 앱 종료·로그아웃·개발 서버 변경 시 준비한 세션을 정리합니다. 종료 통신이 실패하면 Backend의 만료 처리와 AWS 연결/세션 제한이 최종 정리를 담당합니다.

현재 V5에는 `steam-2868840`만 있습니다. 다른 미설치 게임은 준비한 V5 세션을 해제하고 기존 게임별 URL 방식으로 처리합니다. 설치된 게임은 기존처럼 로컬 실행합니다. 기존 클라우드 창의 중복 클릭은 해당 창을 활성화하며, 다른 클라우드 게임이 진행 중이면 먼저 닫도록 안내합니다.

## 함께 배포해야 하는 Backend

동일 조직의 **Backend `codex/v5-session-prewarm` 브랜치**가 필요합니다. 새 API뿐 아니라 준비된 세션에 연결하는 Web SDK 페이지, 세션 만료, 기존 AWS 세이브 내보내기의 직접 ARN 처리가 포함됩니다. 기존 AWS 호스팅 Stream URL만으로는 사전 준비한 세션에 V5 play 명령을 보낼 수 없어, 웹뷰 내부 페이지를 Backend 제공 페이지로 바꿉니다. 창 자체·오버레이·로컬 다운로드·전환 순서는 유지합니다.

Backend의 `appsettings.Local.json`에 다음 블록을 **기존 설정과 병합**합니다. 계정/세이브 설정을 덮어쓰지 않습니다.

```json
{
  "Prewarm": {
    "Enabled": true,
    "ApplicationId": "a-8pXWFKeVC",
    "GameId": "steam-2868840",
    "IdleSeconds": 540,
    "SessionSeconds": 7200
  }
}
```

기존 Streaming 설정은 `Enabled=true`, `Region=ap-northeast-1`, `StreamGroupId=sg-aFRBrPPkg`, `Locations=["ap-northeast-1"]`여야 합니다. `Backend:PublicUrl`과 런처의 Backend 주소는 같은 공개 주소여야 합니다. HTTPS 정식 서비스 설정을 사용하며 기존 명시적 HTTP 팀 개발 설정도 지원합니다. AWS 자격증명은 Backend에만 둡니다. IAM 추가 API와 배포 절차는 Backend `V5_PREWARM.md`를 참고하세요.

구버전 Backend 또는 Prewarm 비활성화 응답에서는 기존 플레이 방식으로 동작합니다. 사전 준비의 네트워크 오류는 앱 시작을 막지 않으며 플레이 시 같은 요청 ID로 다시 시도합니다. 이 코드를 받기만 해서는 현재 설치된 앱/운영 Backend가 바뀌지 않습니다. 양쪽 배포 및 위 설정 적용이 필요합니다.

## 검증

- `dotnet run --project tests/HoleInOne.Checks`: 시작과 조기 클릭 경쟁, 요청 응답 유실, 만료 재생성, 중복 플레이, 다른 게임, 준비 중 종료, URL 검증과 기존 회귀 검사.
- `.github/workflows/v5-prewarm.yml`: Windows에서 위 검사와 기존 빌드 스크립트로 Desktop/BrowserProcess 컴파일.
- Backend 검사: 실제 HTTP 서버와 모의 AWS를 사용한 권한·재사용·연결·정리 검사 및 기존 로그인/세이브 회귀 검사, Web SDK 페이지 전송/재접속 검사.
- 이 변경의 개발 환경은 macOS입니다. 이전 V5 런처의 AWS/Chrome 실기 검증과 이번 Windows/CefSharp 전체 앱 검증은 별개입니다. 실제 Windows에서 아래 절차를 확인해야 합니다.

1. 새 Backend와 앱을 실행하고 AWS 세션 한 개가 먼저 생기는지 확인.
2. 플레이 전에는 게임 창·다운로드가 생기지 않고 Steam만 준비되는지 확인.
3. 준비 중/준비 후 각각 플레이하고 기존 전체화면 웹뷰의 영상·소리·입력 및 다운로드 확인.
4. 창 닫기, 앱 종료, 로그아웃, 서버 변경, 9분 이상 대기 후 재플레이 확인.
5. 다운로드 완료 후 기존 세이브 내보내기→복원→로컬 실행 확인.

V5 인증 bundle은 현재 단일 테스트 Steam 계정입니다. 이 변경은 사용자별 Steam 인증 주입을 구현하지 않습니다.
