#!/usr/bin/env python3
"""Install a source/hash verified release; sanitizer builds cannot be installed."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import uuid
from build import ROOT, UNITY, digest, sources
from prepare_kcp import read_sources, SOURCE_SHA256

PLUGIN = UNITY / 'Assets/Plugins/GameFrame/Native/Kcp'

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('build', type=Path)
    parser.add_argument('--server-output', type=Path, help='Publish to this .NET native-library directory instead of Unity')
    args = parser.parse_args()
    build = args.build.resolve()
    manifest = json.loads((build / 'build-manifest.json').read_text())
    unsigned = dict(manifest)
    recorded_id = unsigned.pop('build_id')
    if hashlib.sha256(json.dumps(unsigned, sort_keys=True).encode()).hexdigest() != recorded_id:
        raise ValueError('Build manifest changed after publication.')
    if manifest['sanitized'] or manifest['abi'] != 1:
        raise ValueError('Only non-sanitized ABI 1 builds are installable.')
    if manifest['sources'] != sources():
        raise ValueError('Maintained sources changed; rebuild before installation.')
    read_sources(UNITY / 'Assets/Plugins/GameFrame/Kcp')
    if manifest['kcp']['source_sha256'] != SOURCE_SHA256:
        raise ValueError('KCP source lock changed; rebuild before installation.')
    for name, checksum in manifest['artifacts'].items():
        if Path(name).name != name or digest(build / name) != checksum:
            raise ValueError('Artifact hash mismatch: ' + name)
    target = manifest['platform']
    arch = manifest['arch']
    names = list(manifest['artifacts'])
    if len(names) != 1:
        raise ValueError('Exactly one plugin artifact is required.')
    original = names[0]
    if target == 'windows':
        from verify_windows import inspect
        if inspect(build / original, ROOT / 'include/gfnet.h') != manifest.get('windows_binary'):
            raise ValueError('Windows ABI or dependency inspection does not match the manifest.')
    directory = {'macos': 'macOS', 'windows': 'Windows/x86_64', 'android': 'Android/arm64-v8a',
                 'ios': 'iOS', 'ios-simulator': 'iOSSimulator/' + arch, 'linux': 'Linux/' + arch}[target]
    name = 'libgameframe_kcp.a' if target.startswith('ios') else original
    destination = args.server_output.resolve() if args.server_output else PLUGIN / directory
    destination.mkdir(parents=True, exist_ok=True)
    if not args.server_output:
        cpu = {'arm64': 'ARM64', 'x86_64': 'x86_64', 'universal': 'AnyCPU'}[arch]
        settings = {
            'macos': f'    Editor:\n      enabled: 1\n      settings: {{CPU: {cpu}, OS: OSX, DefaultValueInitialized: true}}\n    OSXUniversal:\n      enabled: 1\n      settings: {{CPU: {cpu}}}\n',
            'windows': '    Editor:\n      enabled: 1\n      settings: {CPU: x86_64, OS: Windows, DefaultValueInitialized: true}\n    Win64:\n      enabled: 1\n      settings: {CPU: x86_64}\n',
            'android': '    Android:\n      enabled: 1\n      settings: {CPU: ARM64}\n',
            'ios': '    iOS:\n      enabled: 1\n      settings: {CPU: ARM64, AddToEmbeddedBinaries: false, CompileFlags: "", FrameworkDependencies: ""}\n',
            # Simulator is an explicit alternate artifact: disabled to prevent linking two same-symbol archives.
            'ios-simulator': '',
            'linux': f'    Editor:\n      enabled: 1\n      settings: {{CPU: {cpu}, OS: Linux, DefaultValueInitialized: true}}\n    Linux64:\n      enabled: 1\n      settings: {{CPU: {cpu}}}\n'
        }[target]
        meta = destination / (name + '.meta')
        guid = uuid.uuid4().hex
        if meta.exists():
            import re
            match = re.search(r'^guid: ([0-9a-f]{32})$', meta.read_text(), re.M)
            if not match:
                raise ValueError('Existing plugin metadata has no valid GUID.')
            guid = match.group(1)
        metadata = 'fileFormatVersion: 2\nguid: ' + guid + '\nPluginImporter:\n  externalObjects: {}\n  serializedVersion: 3\n  iconMap: {}\n  executionOrder: {}\n  defineConstraints: []\n  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n  validateReferences: 1\n  platformData:\n    Any:\n      enabled: 0\n      settings: {}\n' + settings + '  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    # Validate metadata before replacing a library that may already be in use.
    temporary = destination / (name + '.installing')
    shutil.copyfile(build / original, temporary)
    os.replace(temporary, destination / name)
    if not args.server_output:
        meta.write_text(metadata)
    (destination / 'build-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    (destination / 'artifact.json').write_text(json.dumps({'abi': 1, 'binary': name, 'sha256': digest(destination / name),
                                                          'build_id': manifest['build_id'], 'platform': target, 'arch': arch}, indent=2) + '\n')
    shutil.copytree(build / 'licenses', destination / 'licenses', dirs_exist_ok=True)
    print('Installed verified artifact: ' + str(destination / name))
    if not args.server_output:
        print('Restart Unity if a previous version was loaded. Simulator archives require explicit importer selection.')

if __name__ == '__main__':
    main()
