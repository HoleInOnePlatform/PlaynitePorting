# Backend-hosted stream page

`public/` is the single source for the backend's static video/audio/input page. The backend serves `index.html`, `app.js`, `style.css`, `vendor/gameliftstreams.mjs` and its license. Playnite does not copy these files or start a Node server on the PC.

The native app creates/resumes the authenticated session and obtains a single-use, short-lived signaling ticket. The URL fragment is consumed and removed immediately, then `POST /v2/stream/connect` exchanges the ticket with a WebRTC signal request. User bearer tokens never enter HTML. The SDK processes the response and attaches keyboard/mouse/gamepad input. Page unload closes its connection. Native reconnect/F5 obtains a fresh ticket; stale tickets cannot reconnect by themselves.

There is no application-message command callback, HTML command bridge, mock entry or local AWS SDK. The retained vendor SDK internally supports data channels; that library is unchanged and its SDK/license files must be deployed together. GameLink command transport is native-to-backend and C# subscribes independently.

`node --test static.test.js` checks asset/contract structure only. This development check does not prove H.264 decoding, WebRTC signaling, live video/audio/input or AWS capacity. Actual provider resources and paid deployment are configured separately in HoleInOneBackend.

Superseded PC-server bundles under `dist/` are generated historical outputs and ignored; they are not included in new packages. Existing experimental builds were retained rather than deleting possible user data. Build a fresh output directory for release verification.
