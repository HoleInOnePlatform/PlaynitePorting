# Stream client scope (2026-10-01)

The old data-channel → HTML → CefSharp command plan is retired. This document is a scope note, not an additional current task list. [PlaynitePorting/TASK.md](../TASK.md) and [backend shared contract](../../HoleInOneBackend/contracts/CONTRACT.md) are authoritative.

The browser retains video, audio, keyboard/mouse/gamepad and provider signaling. The native backend service independently handles commands, artifact restoration and actual GameLoader load proof. Static structure tests pass; WebRTC/cloud playback and real same-run continuation require an external provider/game E2E run.
