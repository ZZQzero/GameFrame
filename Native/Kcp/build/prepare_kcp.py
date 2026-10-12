"""Copy hash-locked KCP sources with integrated fixes into a new build tree."""

import argparse
import hashlib
import json
from pathlib import Path


BASELINE_COMMIT = "b1a7a2101dcbb96017681a500d6b82bbe5a88766"
SOURCE_SHA256 = {
    "ikcp.c": "cc3dae4e8b215f281c0900fd19137c495d80bb779d65f3d430fb821d09f14d29",
    "ikcp.h": "51bb50e747cac6ffd8b29ffd489cf6d300eff231a0a149d6065af353ef38490c",
}
LOCAL_FIXES = ["allocation-failure-handling", "standard-offsetof"]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_sources(source):
    sources = {name: (source / name).read_bytes() for name in SOURCE_SHA256}
    for name, expected in SOURCE_SHA256.items():
        if digest(sources[name]) != expected:
            raise ValueError(f"{name}: source SHA-256 mismatch")
    return sources


def prepare(output):
    native = Path(__file__).resolve().parents[1]
    source = native / "third_party/kcp"
    sources = read_sources(source)
    # Do not replace an existing build tree or allow outputs to touch project sources.
    output = Path(output).resolve()
    if output.is_relative_to(native.parents[1] / "Unity") or output.is_relative_to(native):
        raise ValueError("Prepared sources must be outside Unity and the maintained native module")
    output.mkdir(parents=True, exist_ok=False)
    for name, data in sources.items():
        (output / name).write_bytes(data)
    (output / "LICENSE").write_bytes((source / "LICENSE").read_bytes())
    manifest = {
        "upstream_commit": BASELINE_COMMIT,
        "integrated_pr": 467,
        "local_fixes": LOCAL_FIXES,
        "source_sha256": SOURCE_SHA256,
    }
    (output / "kcp-source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="New source directory outside Unity and the maintained native module")
    args = parser.parse_args()
    print(json.dumps(prepare(args.output), indent=2))


if __name__ == "__main__":
    main()
