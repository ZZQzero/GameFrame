# Native plugin layout

Native source and build scripts live in:

- `Unity/Assets/Scripts/Runtime/Sqlite/Native~`
- `Unity/Assets/Scripts/Runtime/MediaBackup/Native~`

Both installers publish to `Unity/Assets/Plugins/GameFrame/Native/<module>/<platform>`.
Build and install Sqlite first, then build MediaBackup against that same core build
and install it. Keep binaries together with their `.meta`, `artifact.json` and
`build-manifest.json` files. Restart Unity after replacing a loaded native library.

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
