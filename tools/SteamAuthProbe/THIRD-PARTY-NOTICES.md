# Dependencies

This diagnostic package uses unmodified SteamKit2 3.4.0 (LGPL-2.1), QRCoder 1.8.0 (MIT),
and their transitive dependencies. SteamKit2 remains a separate replaceable DLL.
License texts and the .NET runtime notices are in `Licenses/`.

- Corresponding SteamKit2 source: https://github.com/SteamRE/SteamKit/tree/3.4.0
- QRCoder source: https://github.com/Shane32/QRCoder/tree/v1.8.0
- protobuf-net / protobuf-net.Core 3.2.56 (Apache-2.0): https://github.com/protobuf-net/protobuf-net/tree/dfdfce61a739cfd76f05fcdacf8a4b3b9e94e684
- ZstdSharp.Port 0.8.7 (MIT): https://github.com/oleg-st/ZstdSharp/tree/0ee6121aaa173b42e68d3c6c8816a68e910e0557
- Microsoft .NET source: https://github.com/dotnet

The package includes the probe's complete source in `Source/`. Keep the sibling Backend and
PlaynitePorting directory names when rebuilding; both use .NET SDK 10. Source is included for
review and rebuilding the linked diagnostic application. No Steam account data is included.
