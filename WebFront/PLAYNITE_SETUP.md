# Native stream setup

Use [GameLink native integration documentation](../source/Playnite/GameLink/README.md) and [the backend contract](../../HoleInOneBackend/contracts/CONTRACT.md).

The user PC needs WebView2 Runtime and the built Desktop/Fullscreen package with GameLoader. Instant play opens a native connection dialog when configuration is missing; issued tokens are current-user DPAPI encrypted. A complete `HIO_BACKEND_URL`/`HIO_USER_TOKEN` pair remains an optional developer override. Production IdP login/refresh is not implemented. Node and AWS credentials belong to the backend only. The backend hosts `WebFront/public` and provides a session-bound stream ticket to the native app.

For this project's Windows development backend, run `powershell -NoProfile -ExecutionPolicy Bypass -File D:\Project\HoleInOne\HoleInOneBackend\tools\start-local.ps1 -ConfigureClient`. It generates development authentication, starts the loopback server and registers this Windows user's encrypted connection without printing tokens. Close the old app and use the updated build. The app detects fake capabilities before session creation and explains that the running development server has no video. Repeat the script without `-ConfigureClient` to reuse state; pass `-Stop` to stop its recorded server. It is never automatically launched by the user PC app.

Run the backend fake locally for development. The fake provider cannot produce a real WebRTC stream. Static page tests, WebView initialization/H.264 checks, fake native restore/IPC tests and real cloud/game E2E are distinct evidence categories. Production restore refuses to guess Steam/Godot account/save paths or assume Steam Cloud is disabled. See the explicit development-attestation limitations in the native README.
