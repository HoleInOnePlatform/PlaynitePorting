# GameLink GameLift Streams 웹 클라이언트

Playnite WebView에서 STS2 스트림을 표시하고, GameLift Streams 데이터 채널의 `rest_site_reached` 신호를 수신해 `CefSharp.PostMessage`로 전달하는 페이지입니다. 이 신호로 스트림을 닫거나 로컬 게임을 실행하지 않습니다.

## 준비

1. AWS의 [Web SDK 번들](https://aws.amazon.com/gamelift/streams/getting-started/#Resources)을 받아 `.mjs` 파일을 `public/vendor/gameliftstreams.mjs`로 복사합니다. SDK 자체는 이 저장소에 포함되지 않습니다.
2. Node.js와 `npm install`이 필요합니다. AWS 자격 증명은 서버의 표준 자격 증명 체인(예: AWS profile 또는 IAM role)에 설정합니다.
3. GameLift Streams의 준비된 application ID/ARN과 active stream group ID/ARN을 환경 변수에 지정합니다.

```powershell
$env:AWS_REGION = 'us-west-2'
$env:STREAM_OPTIONS = 'STS2|a-APPLICATION_ID|sg-STREAM_GROUP_ID'
npm install
npm start
```

여러 구성을 제공하려면 쉼표로 구분합니다: `STS2 Dev|a-...|sg-...,STS2 Prod|a-...|sg-...`. 페이지는 `http://localhost:8000/`에서 열립니다. 운영 환경에는 인증 및 접근 제어와 HTTPS 역방향 프록시를 추가해야 합니다. 현재 API는 신뢰된 로컬 개발 환경용입니다. AWS 자격 증명을 브라우저에 전달하지 않습니다.

## 동작

페이지가 `generateSignalRequest()`를 호출하고 `POST /api/streams`에 구성 인덱스와 함께 전달합니다. 서버는 `StartStreamSession`을 호출하고 `SignalResponse`만 돌려줍니다. 페이지는 `processSignalResponse()`와 `attachInput()`을 호출합니다. `clientConnection.applicationMessage`는 최대 256바이트의 유효한 UTF-8 JSON 객체 중 `type` 필드 하나만 있는 `rest_site_reached`만 받습니다. 첫 수신을 페이지에 표시하고 메인 프레임에서 Playnite로 전달합니다. 페이지 새로고침 시 표시 상태는 초기화됩니다.

## 검증

- `npm test`: 메시지 형식과 브리지 전달 테스트.
- 브라우저 개발자 도구에서 `applicationMessage` 콜백을 통해 신호가 들어오는지 확인합니다. Playnite WebView에서는 상태 카드와 CONNECTION ACTIVITY 로그를 확인합니다.
- 실제 AWS 스트림에서 모닥불에 진입한 후 페이지의 상태 카드가 바뀌는지 확인합니다. 현재 환경에는 AWS 리소스와 SDK 번들이 없어 실제 송수신은 미검증입니다.
- 재연결 또는 새로고침 중 application-side 메시지는 유실될 수 있습니다. 이 페이지에는 재생/수신 보장 기능이 없습니다.

Playnite 측의 `IInstantPlayAddressProvider`는 이 페이지의 현재 주소를 열도록 연결해야 하며, 별도 저장소인 `../PlaynitePorting`의 메인 프레임·origin 검사 이후 `rest_site_reached` 수신 경로를 추가해야 합니다. 이 저장소의 수정만으로 Playnite 기록까지 완료되지는 않습니다.
