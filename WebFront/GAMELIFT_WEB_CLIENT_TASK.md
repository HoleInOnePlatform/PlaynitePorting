# Task: GameLift Streams 모닥불 신호를 Playnite WebView에서 받기

## 목표

Playnite WebView에 열리는 **자체 GameLift Streams 웹 클라이언트 페이지**를 만든다. 이 페이지가 STS2 화면을 스트리밍하면서, 게임 모드가 데이터 채널로 보낸 `{"type":"rest_site_reached"}`를 받아 Playnite에 전달한다. Playnite는 **모닥불 도달 사실만 기록**한다. 이 신호만으로 클라우드 화면을 닫거나 로컬 게임을 실행하지 않는다.

```text
클라우드 STS2 모드
  → GameLift Streams 데이터 채널
  → 자체 웹 클라이언트 페이지 (Playnite WebView)
  → CefSharp.PostMessage
  → Playnite: 모닥불 도달 상태 기록
```

## 현재 상태

- 이 저장소의 `GameLinkPoint` v0.3.0 모드는 실제 모닥불 진입을 감지하고 GameLift Streams application-side 데이터 채널의 `127.0.0.1:40712`로 `{"type":"rest_site_reached"}`를 보낸다.
- 모드 빌드, 감지 로직, 가짜 데이터 채널 테스트는 통과했다. **실제 AWS 스트림에서의 송수신은 아직 검증하지 않았다.**
- 자체 GameLift Streams 웹 클라이언트 페이지와 세션 시작용 백엔드는 아직 없다.
- Playnite의 현재 `LoopbackInstantPlayAddressProvider`는 개발용 `mock.html`을 연다. `HandoffSession.Receive`는 세션 정보가 포함된 `handoff.*` 메시지만 처리하므로 단순 `rest_site_reached`는 현재 수신하지 못한다.

## 구현 작업

### 1. 웹 클라이언트와 스트림 시작

- Amazon GameLift Streams Web SDK를 사용하는 페이지를 만든다. 스트림 영상과 입력을 Playnite WebView에서 사용할 수 있어야 한다.
- 게임 애플리케이션과 스트림 그룹을 선택해 세션을 시작할 백엔드 API를 연결한다. AWS 자격 증명을 웹페이지에 넣지 않는다. 처음에는 AWS의 샘플 백엔드·웹 클라이언트 구성을 출발점으로 사용할 수 있다.
- Playnite의 `IInstantPlayAddressProvider`가 개발용 `mock.html` 대신 이 페이지의 현재 스트림 URL을 열도록 연결한다. 운영 URL은 HTTPS를 사용한다.
- AWS 콘솔의 **Test stream**이나 기본 **stream URL**은 게임 화면을 빠르게 확인하는 데 사용할 수 있지만, 이 작업의 `applicationMessage` 처리 코드를 넣으려면 수정 가능한 자체 웹 클라이언트가 필요하다.

### 2. 모닥불 신호 수신

- Web SDK의 `clientConnection.applicationMessage` 콜백을 등록한다.
- 수신 바이트를 UTF-8 JSON으로 해석하고 정확히 `{"type":"rest_site_reached"}`인 신호만 처리한다. 크기와 형식을 검사하고 다른 메시지는 무시한다.
- 웹페이지에서 신호 수신을 로그 또는 화면 표시로 먼저 확인한다. 웹페이지가 재연결되는 동안 발생한 신호는 AWS 데이터 채널에서 유실될 수 있으므로, 이 단계에서는 수신 보장이나 재생을 구현했다고 주장하지 않는다.

### 3. Playnite 전달

- 웹페이지의 메인 프레임에서 `CefSharp.PostMessage(JSON.stringify({type: "rest_site_reached"}))`를 호출한다.
- Playnite의 현재 즉시 플레이 WebView 수신부에 **이 단순 신호를 처리하는 경로**를 추가한다. 기존의 메인 프레임·현재 페이지 origin 검사는 유지하고, 현재 열린 즉시 플레이 뷰에 대해서만 모닥불 도달 상태를 기록한다.
- `rest_site_reached` 수신으로 `handoff.ready`를 만들거나, WebView를 닫거나, 로컬 실행을 호출하지 않는다. 기존 `handoff.*` 계약은 별도로 유지한다.
- Playnite 쪽 구현을 바꾸지 않는 대안은 웹페이지/조정 계층이 기존 `handoff.point.reached` 전체 계약을 생성하는 것이다. 이 작업에서는 **단순 신호를 Playnite에서 직접 수신하는 방식**을 우선한다.

### 4. 검증

1. 로컬 가짜 데이터 채널에서 모닥불 신호가 웹 클라이언트의 `applicationMessage`에 들어온다.
2. 실제 GameLift Streams 세션에서 STS2 모닥불에 들어가면 웹페이지가 신호를 받는다.
3. Playnite WebView의 메인 프레임에서만 신호가 수락되고, 다른 origin·프레임의 메시지는 무시된다.
4. Playnite가 모닥불 도달 상태를 표시하거나 기록하되 클라우드 스트림을 유지한다.
5. 연결 끊김·웹페이지 새로고침·중복 메시지 시 동작을 확인하고, 유실 가능성을 문서화한다.

## 완료 기준

- Playnite의 즉시 플레이 화면에 실제 GameLift Streams 영상이 보인다.
- 실제 게임의 모닥불 진입 한 번으로 Playnite가 `rest_site_reached`를 수신한다.
- 이 신호만으로 로컬 전환이나 스트림 종료가 일어나지 않는다.
- 세션 생성 방법, 웹페이지 배포 주소, AWS 설정과 검증 로그 확인법이 문서화된다.

## 이 작업에 포함하지 않는 것

Save 전송·복원, `handoff.ready` 판정, 로컬 게임 실행, 클라우드 세션 종료. 이 작업의 결과는 **모닥불 도달 알림을 Playnite에 보여 주는 것**이다.

## 참고

- [Amazon GameLift Streams Web SDK와 웹 클라이언트](https://docs.aws.amazon.com/gameliftstreams/latest/developerguide/sdk.html)
- [GameLift Streams 데이터 채널](https://docs.aws.amazon.com/gameliftstreams/latest/developerguide/data-channels.html)
- [GameLift Streams 샘플 백엔드·클라이언트 설정](https://docs.aws.amazon.com/gameliftstreams/latest/developerguide/setting-up-web-sdk.html)
- [이 저장소의 모드 사용법](README.md)
- [Playnite 수신 계약](../PlaynitePorting/source/Playnite/GameLink/README.md)
