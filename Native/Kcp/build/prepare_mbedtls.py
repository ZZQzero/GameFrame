#!/usr/bin/env python3
"""Explicitly download and verify the locked release outside Unity."""
import argparse
import hashlib
import json
import posixpath
from pathlib import Path
import shutil
import tarfile
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "third_party/mbedtls.lock.json"
UNITY = ROOT.parents[1] / "Unity"


def tree_hash(source):
    digest = hashlib.sha256()
    for path in sorted(Path(source).rglob("*")):
        if path.is_symlink():
            raise ValueError("Prepared dependency must contain materialized files, not links.")
        if not path.is_file() or path.name == "gfnet-source-lock.json":
            continue
        digest.update(path.relative_to(source).as_posix().encode() + b"\0")
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def prepare(output, archive=None):
    output = Path(output).resolve()
    if output.is_relative_to(UNITY) or output.is_relative_to(ROOT):
        raise ValueError("Dependency preparation must stay outside Unity and the maintained native module.")
    if output.exists():
        raise FileExistsError(output)
    lock = json.loads(LOCK.read_text())
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="gfnet-mbedtls-", dir=output.parent) as tmp:
        bundle = Path(tmp) / "release.tar.bz2"
        if archive is None:
            with urllib.request.urlopen(lock["archive_url"], timeout=60) as source:
                bundle.write_bytes(source.read(32 * 1024 * 1024 + 1))
        else:
            bundle.write_bytes(Path(archive).read_bytes())
        digest = hashlib.sha256(bundle.read_bytes()).hexdigest()
        if digest != lock["archive_sha256"]:
            raise ValueError("Locked mbedTLS archive SHA-256 mismatch.")
        extracted = Path(tmp) / "source"
        extracted.mkdir()
        with tarfile.open(bundle, "r:bz2") as tar:
            entries = tar.getmembers()
            for entry in entries:
                dest = (extracted / entry.name).resolve()
                if "\\" in entry.name or extracted not in dest.parents or not (entry.isdir() or entry.isfile() or entry.issym()):
                    raise ValueError("Archive contains unsafe path or unsupported link.")
            # Materialize verified relative links on every host. Windows must not
            # require Developer Mode/admin privileges to prepare the same tree.
            members = {entry.name.rstrip("/"): entry for entry in entries}
            for name in list(members):
                parent = posixpath.dirname(name)
                while parent:
                    if parent not in members:
                        directory = tarfile.TarInfo(parent)
                        directory.type = tarfile.DIRTYPE
                        members[parent] = directory
                    parent = posixpath.dirname(parent)
            children = {}
            for name in members:
                children.setdefault(posixpath.dirname(name), []).append(name)
            for entry in entries:
                if entry.issym():
                    target = posixpath.normpath(posixpath.join(posixpath.dirname(entry.name), entry.linkname))
                    if "\\" in entry.linkname or Path(entry.linkname).is_absolute() or target == ".." or target.startswith("../"):
                        raise ValueError("Archive link escapes the source root.")
            tar.extractall(extracted, members=[entry for entry in entries if not entry.issym()])

            def resolve(name, seen):
                parts = name.split("/")
                for index in range(1, len(parts) + 1):
                    prefix = "/".join(parts[:index])
                    member = members.get(prefix)
                    if member and member.issym():
                        if prefix in seen:
                            raise ValueError("Archive contains a link cycle.")
                        target = posixpath.normpath(posixpath.join(posixpath.dirname(prefix), member.linkname))
                        suffix = "/".join(parts[index:])
                        return resolve(posixpath.join(target, suffix) if suffix else target, seen | {prefix})
                if name not in members:
                    raise ValueError("Archive link has no target: " + name)
                return name

            def materialize(name, dest, seen):
                target = resolve(name, set())
                if target in seen:
                    raise ValueError("Archive contains a directory link cycle.")
                member = members[target]
                if member.isdir():
                    dest.mkdir(parents=True, exist_ok=True)
                    for child in children.get(target, []):
                        materialize(child, dest / posixpath.basename(child), seen | {target})
                else:
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(extracted / target, dest)

            for entry in entries:
                if entry.issym():
                    materialize(entry.name, extracted / entry.name, set())
        roots = list(extracted.iterdir())
        if len(roots) != 1 or not (roots[0] / "include/mbedtls/ssl.h").is_file() or not (roots[0] / "tf-psa-crypto/include/psa/crypto.h").is_file():
            raise ValueError("Unexpected mbedTLS release layout.")
        lock["source_tree_sha256"] = tree_hash(roots[0])
        (roots[0] / "gfnet-source-lock.json").write_text(json.dumps(lock, indent=2) + "\n")
        roots[0].rename(output)
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--archive", help="Use an existing archive; no network request.")
    args = parser.parse_args()
    print(prepare(args.output, args.archive))


if __name__ == "__main__":
    main()
