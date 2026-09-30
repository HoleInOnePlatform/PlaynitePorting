# Playnite GameLift Streams client

Playnite starts `server.js` on a random `127.0.0.1` port for each instant-play view and opens its bundled page in the internal WebView. Closing the view stops the server. The page passes only `{"type":"rest_site_reached"}` from the GameLift Streams application message callback to `CefSharp.PostMessage`; Playnite records it on the active view after the existing main-frame and origin checks. It does not change `HandoffSession`, close the view, or start a local game.

## Inputs required for an AWS stream

1. Install Node.js on the Playnite machine. In the installed `GameLink/WebClient` directory run `npm install` to install `@aws-sdk/client-gameliftstreams`. The SDK package is a server dependency and is never sent to the browser.
2. Obtain the GameLift Streams Web SDK browser bundle from AWS and place it at `GameLink/WebClient/public/vendor/gameliftstreams.mjs`. This proprietary bundle is not present in this repository.
3. Set `AWS_REGION` and `STREAM_OPTIONS` in the Playnite process environment before starting Playnite. Example: `AWS_REGION=us-west-2`, `STREAM_OPTIONS=STS2|a-APPLICATION_ID|sg-STREAM_GROUP_ID`. Multiple options use commas. Supply AWS credentials through the standard SDK credential chain, such as a profile or short-lived role credentials. The identity needs permission to start a stream session for the configured application and stream group. Do not place credentials in the page or URL.
4. The application must be READY, attached to an ACTIVE stream group with capacity, and contain a runnable STS2 build with GameLinkPoint v0.3.0. Specify the real region and identifiers from your AWS account.

The browser generates a WebRTC signal request. The loopback server sends it to `StartStreamSession` using its own AWS credentials and returns only the signal response. It rejects API calls without the per-view token and stream starts without the matching loopback Origin. The page then calls `processSignalResponse` and `attachInput`.

## Local checks

Run `node --test` for strict UTF-8 JSON filtering, bridge forwarding, loopback page serving, and API token checks. To test the Playnite WebView without AWS, set `GAMELINK_MOCK=1` before launching Playnite, open Instant Play, and click **모닥불 데이터 채널 모의 수신**. The page indicator should change, and the Playnite log should contain `rest site reached` for the current session. `GamesEditor.InstantPlayRestSiteReached` exposes the active view's recorded state. The mock button calls the same application-message handler used by the SDK. It does not simulate WebRTC or AWS.

For an actual stream, leave `GAMELINK_MOCK` unset, start a configured session in the page, enter a rest site in STS2, and check both the page indicator and Playnite log. A data-channel message sent while the page is disconnected or reconnecting may be lost; no replay is implemented.

This repository has no AWS resource identifiers, credentials, or Web SDK bundle. A real GameLift session and end-to-end signal receipt require those inputs.
