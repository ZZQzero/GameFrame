#!/usr/bin/env python3
"""Explicitly download and verify the locked release outside Unity."""
import argparse
import hashlib
import json
from pathlib import Path
import tarfile
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "third_party/mbedtls.lock.json"
UNITY = ROOT.parents[6]


def tree_hash(source):
    digest = hashlib.sha256()
    for path in sorted(Path(source).rglob("*")):
        if not path.is_file() or path.name == "gfnet-source-lock.json":
            continue
        digest.update(path.relative_to(source).as_posix().encode() + b"\0")
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def prepare(output, archive=None):
    output = Path(output).resolve()
    if output == UNITY or UNITY in output.parents:
        raise ValueError("Dependency preparation must stay outside Unity.")
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
            for entry in tar.getmembers():
                dest = (extracted / entry.name).resolve()
                if extracted not in dest.parents or not (entry.isdir() or entry.isfile()):
                    raise ValueError("Archive contains unsafe path or unsupported link.")
            tar.extractall(extracted)
        roots = list(extracted.iterdir())
        if len(roots) != 1 or not (roots[0] / "include/mbedtls/ssl.h").is_file():
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
