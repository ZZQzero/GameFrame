"""Repackage existing binaries for the Assets/Plugins layout without claiming a rebuild.

Historical build-manifest.json files stay unchanged. artifact.json records the
exact source-only packaging transformations and, on macOS, the RPATH rewrite.
Run with --apply once, or without arguments to verify the installed layout.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

PROJECT = Path(__file__).resolve().parents[1] / 'Unity'
NATIVE = PROJECT / 'Assets/Scripts/Runtime/MediaBackup/Native~'
PLUGINS = PROJECT / 'Assets/Plugins/GameFrame/Native'
OLD_RPATH = '@loader_path/../../../Sqlite/Plugins/macOS'
NEW_RPATH = '@loader_path/../../Sqlite/macOS'
TRANSFORMS = {
    'CMakeLists.txt': [(OLD_RPATH, NEW_RPATH)],
    'build/install.py': [
        ('from build import ROOT, sources\n',
         "from build import ROOT, sources\n\nPLUGIN_ROOT = ROOT.parents[3] / 'Plugins/GameFrame/Native'\n"),
        ("ROOT.parent.parent/'Sqlite/Plugins'/folder", "PLUGIN_ROOT/'Sqlite'/folder"),
        ("ROOT.parent/'Plugins'/folder/name", "PLUGIN_ROOT/'MediaBackup'/folder/name"),
    ],
}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def verify_sources(manifest):
    changes = []
    for name, expected in manifest['sources'].items():
        current = (NATIVE / name).read_bytes()
        original = current.decode('utf-8')
        for before, after in reversed(TRANSFORMS.get(name, [])):
            if original.count(after) != 1:
                raise ValueError('Unexpected layout source: ' + name)
            original = original.replace(after, before, 1)
        if digest(original.encode('utf-8')) != expected:
            raise ValueError('Non-layout source change: ' + name)
        if name in TRANSFORMS:
            changes.append(dict(path=name, original_sha256=expected,
                                installed_sha256=digest(current)))
    return changes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    for folder in ['macOS', 'Windows/x86_64', 'Android/arm64-v8a', 'iOS']:
        directory = PLUGINS / 'MediaBackup' / folder
        manifest_path = directory / 'build-manifest.json'
        manifest = json.loads(manifest_path.read_text())
        changes = verify_sources(manifest)
        artifact_path = directory / 'artifact.json'
        artifact = json.loads(artifact_path.read_text())
        binary = directory / artifact['binary']
        original_hash = manifest['artifacts'][binary.name]
        if args.apply and 'layout_migration' not in artifact:
            if digest(binary.read_bytes()) != original_hash:
                raise ValueError('Original binary mismatch: ' + str(binary))
            if folder == 'macOS':
                # Replace the inode atomically: Unity may still have the old image loaded.
                with tempfile.TemporaryDirectory(dir=directory) as temporary:
                    candidate = Path(temporary) / binary.name
                    shutil.copy2(binary, candidate)
                    subprocess.run(['install_name_tool', '-rpath', OLD_RPATH,
                                    NEW_RPATH, str(candidate)], check=True)
                    subprocess.run(['codesign', '--force', '--sign', '-', str(candidate)], check=True)
                    os.replace(candidate, binary)
            artifact['sha256'] = digest(binary.read_bytes())
            artifact['sources'] = [dict(path=name, sha256=digest((NATIVE/name).read_bytes()))
                                   for name in manifest['sources']]
            artifact['layout_migration'] = dict(
                version=1, kind='repackaged-not-rebuilt',
                original_manifest_sha256=digest(manifest_path.read_bytes()),
                original_binary_sha256=original_hash, source_changes=changes,
                binary_operations=(['rpath: ' + OLD_RPATH + ' -> ' + NEW_RPATH,
                                    'ad-hoc codesign'] if folder == 'macOS' else []))
            artifact_path.write_text(json.dumps(artifact, indent=2)+'\n')
        migration = artifact['layout_migration']
        assert migration['kind'] == 'repackaged-not-rebuilt'
        assert migration['original_manifest_sha256'] == digest(manifest_path.read_bytes())
        assert migration['original_binary_sha256'] == original_hash
        assert migration['source_changes'] == changes
        assert artifact['sha256'] == digest(binary.read_bytes())
        if folder != 'macOS':
            assert artifact['sha256'] == original_hash
        else:
            commands = subprocess.check_output(['otool', '-l', str(binary)], text=True)
            assert OLD_RPATH not in commands and commands.count(NEW_RPATH) == 2
            subprocess.run(['codesign', '--verify', str(binary)], check=True)
        for source in artifact['sources']:
            assert source['sha256'] == digest((NATIVE/source['path']).read_bytes())
        core_dir = PLUGINS / 'Sqlite' / folder
        core = json.loads((core_dir/'artifact.json').read_text())
        assert artifact['sqlite_build_id'] == core['build_id']
        assert core['sha256'] == digest((core_dir/core['binary']).read_bytes())
        print(folder + ': layout, source provenance and binary hashes verified')


if __name__ == '__main__':
    main()
