# HoleInOne Playnite handoff status (2026-10-01)

Current requirements are [TASK.md](TASK.md), not earlier prototype handoff notes. Implementation and restrictions are in [native GameLink README](source/Playnite/GameLink/README.md) and [backend contract](../HoleInOneBackend/contracts/CONTRACT.md).

Implemented: authenticated native backend session/replay/ACK, durable session identity and snapshot recovery, candidate/ready/freeze/restore/load/commit separation, guarded single-file save restoration with backup/journal/ownership, attempt-specific GameLoader named pipe and native stream reconnect. The installation-ready notification now comes from installation completion, and the campfire+installation bypass is removed. Installed games keep their ordinary Playnite launch path.

Removed from the product: PC Node/AWS server path, HTML command bridge, fixed V2 TCP/token signal, mock/hosted URL selection, duplicate WebClient sources and broad copies/server packaging. Remote static media assets, WebView2, other CefSharp features, Steam/Epic, licenses and GameLoader remain.

Desktop/Fullscreen x86 compilation, native fake HTTP/IPC fault tests and static media structure checks are recorded in the native README/TASK. Actual cloud/game E2E, real media/input regression, UI exercise and Steam Cloud safety are pending. Default local restoration fails closed until game-globalized paths and Steam policy can be proven. The explicit manual development adapter does not meet production safety proof. Deployment/paid resources were not created.

Generated historical bundles/check builds are ignored and retained. Possible user data, original saves and unresolved journals are not deleted. Recreate fresh release output rather than using old experimental folders.
