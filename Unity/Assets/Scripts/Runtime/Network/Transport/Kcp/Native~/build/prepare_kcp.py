"""Prepare hash-locked, allocation-safe KCP sources without changing Unity assets."""

import argparse
import hashlib
import json
import re
from pathlib import Path


BASELINE_COMMIT = "b1a7a2101dcbb96017681a500d6b82bbe5a88766"
SOURCE_SHA256 = {
    "ikcp.c": "3837dc313c120a62b37d1a5458dc0b2939bb917a1411e922f41a5a3caeddc558",
    "ikcp.h": "d67a0e67ba5701f1e882894b4415c4d3ad3d7d9f7231dd2ca68c6037c5217933",
}
PATCH_SHA256 = "ee5f2c3fbce4af7a75ea8150051b33eba7b0461ecda468d2ae341f925917f89d"
PATCHED_SHA256 = {
    "ikcp.c": "cc3dae4e8b215f281c0900fd19137c495d80bb779d65f3d430fb821d09f14d29",
    "ikcp.h": "51bb50e747cac6ffd8b29ffd489cf6d300eff231a0a149d6065af353ef38490c",
}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def apply_exact_patch(source, patch, name="ikcp.c"):
    """Use exact hunk offsets/content; neither offset search nor fuzzy matching is allowed."""
    if b"\n" in source.replace(b"\r\n", b""):
        raise ValueError("Locked KCP source must retain CRLF line endings")
    lines = source.decode("utf-8").replace("\r\n", "\n").splitlines(keepends=True)
    diff = patch.decode("utf-8").splitlines(keepends=True)
    if name not in SOURCE_SHA256 or diff[:2] != [f"--- a/{name}\n", f"+++ b/{name}\n"]:
        raise ValueError("Unexpected patch target")
    output = []
    cursor = 0
    index = 2
    hunks = 0
    while index < len(diff):
        match = re.fullmatch(r"@@ -(\d+),(\d+) \+(\d+),(\d+) @@\n", diff[index])
        if match is None:
            raise ValueError("Invalid patch hunk header")
        old_start, old_count, new_start, new_count = map(int, match.groups())
        old_start -= 1
        if old_start < cursor or old_start > len(lines):
            raise ValueError("Invalid or overlapping patch offset")
        output.extend(lines[cursor:old_start])
        cursor = old_start
        if new_start != len(output) + 1:
            raise ValueError("Patch target offset mismatch")
        index += 1
        removed = added = 0
        while index < len(diff) and not diff[index].startswith("@@ "):
            operation, content = diff[index][0], diff[index][1:]
            if operation not in " +-":
                raise ValueError("Unsupported patch operation")
            if operation in " -":
                if cursor >= len(lines) or lines[cursor] != content:
                    raise ValueError(f"Exact patch context mismatch at source line {cursor + 1}")
                cursor += 1
                removed += 1
            if operation in " +":
                output.append(content)
                added += 1
            index += 1
        if (removed, added) != (old_count, new_count):
            raise ValueError("Patch hunk length mismatch")
        hunks += 1
    if hunks == 0:
        raise ValueError("Empty patch")
    output.extend(lines[cursor:])
    return "".join(output).replace("\n", "\r\n").encode("utf-8")


def prepare(output):
    native = Path(__file__).resolve().parents[1]
    source = native.parents[5] / "Plugins/GameFrame/Kcp"
    originals = {name: (source / name).read_bytes() for name in SOURCE_SHA256}
    for name, expected in SOURCE_SHA256.items():
        if digest(originals[name]) != expected:
            raise ValueError(f"{name}: source SHA-256 mismatch")
    patch = (native / "patches/kcp-allocation-safety.patch").read_bytes()
    if digest(patch) != PATCH_SHA256:
        raise ValueError("Allocation safety patch SHA-256 mismatch")
    parts = patch.split(b"--- a/ikcp.h\n")
    if len(parts) != 2:
        raise ValueError("Expected exactly one C and header patch")
    patched = {
        "ikcp.c": apply_exact_patch(originals["ikcp.c"], parts[0]),
        "ikcp.h": apply_exact_patch(originals["ikcp.h"], b"--- a/ikcp.h\n" + parts[1], "ikcp.h"),
    }
    for name, data in patched.items():
        if digest(data) != PATCHED_SHA256[name]:
            raise ValueError(f"{name}: patched source SHA-256 mismatch")
    # Do not replace an existing build tree or allow outputs to touch project sources.
    output = Path(output).resolve()
    if output.is_relative_to(native.parents[6]):
        raise ValueError("Prepared sources must be outside the Unity project")
    output.mkdir(parents=True, exist_ok=False)
    for name, data in patched.items():
        (output / name).write_bytes(data)
    (output / "LICENSE").write_bytes((source / "LICENSE").read_bytes())
    manifest = {
        "upstream_commit": BASELINE_COMMIT,
        "integrated_pr": 467,
        "source_sha256": SOURCE_SHA256,
        "allocation_safety_patch_sha256": PATCH_SHA256,
        "prepared_source_sha256": PATCHED_SHA256,
    }
    (output / "kcp-source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="New source directory outside Unity")
    args = parser.parse_args()
    print(json.dumps(prepare(args.output), indent=2))


if __name__ == "__main__":
    main()
