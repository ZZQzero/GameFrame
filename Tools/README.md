# Native plugin layout

Native source and build scripts live in:

- `Unity/Assets/Scripts/Runtime/Sqlite/Native~`
- `Unity/Assets/Scripts/Runtime/MediaBackup/Native~`

Both installers publish to `Unity/Assets/Plugins/GameFrame/Native/<module>/<platform>`.
Build and install Sqlite first, then build MediaBackup against that same core build
and install it. Keep binaries together with their `.meta`, `artifact.json` and
`build-manifest.json` files. Restart Unity after replacing a loaded native library.

KCP source-fix notes and native regression tests live in
`Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~`.
Its [README](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/README.md)
documents the integrated fast-ACK fix, exact allocation-safety patch preparation
and native regression commands. The gfnet build/install scripts and UDP/DTLS
bridge are still planned; these tests do not install a plugin.

On macOS, Backup searches `@loader_path` for a built Player's adjacent SQLite
library, and `@loader_path/../../Sqlite/macOS` for the Editor directory layout.
Newly generated macOS importer metadata enables preloading; existing metadata
and GUIDs are preserved by installation.

The initial directory migration repackages existing artifacts; it is not a
four-platform rebuild. Historical `build-manifest.json` files remain unchanged.
Backup's `artifact.json` includes `layout_migration` with the original manifest
and binary hashes, exact packaging-source changes, and macOS RPATH/signing
operations. Windows, Android and iOS binaries are byte-for-byte unchanged.
A normal new build/install writes a fresh artifact manifest without this field.

Verify these migrated artifacts from the repository root:

```sh
python3 Tools/migrate_native_layout.py
```

`--apply` performs the initial migration only after verifying the original hashes
and the exact allowed source transformations. It replaces the macOS binary
atomically rather than modifying an inode that Unity may have loaded.


Network generation/check is in `NetworkProtocol`; the real Kestrel/WSS and pure client-core integration tests are in `NetworkServer.Tests`. Run `(cd Tools && dotnet test NetworkServer.Tests)` from the repository root. This project references the existing DotNet server and links the same connection-core sources without a Unity/UniTask shim; runtime usage and remaining platform gates are documented in [Network.md](../Docs/Network.md).

`.github/workflows/network.yml` checks locked dependency restoration, generated protocol drift, .NET protocol/WSS tests and native KCP regressions on network changes. Native safety verification runs both assertions/NDEBUG and ASan/UBSan with sanitizer errors treated as failures. Unity Editor, Player/IL2CPP and device gates remain separate.
