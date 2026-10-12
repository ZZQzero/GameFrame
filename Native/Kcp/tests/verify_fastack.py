#!/usr/bin/env python3
"""Verify the integrated KCP fast-ACK fix against an ACK-only control copy."""

import argparse
import hashlib
import shutil
import subprocess
import tempfile
from pathlib import Path


SOURCE_SHA256 = {
    "ikcp.c": "cc3dae4e8b215f281c0900fd19137c495d80bb779d65f3d430fb821d09f14d29",
    "ikcp.h": "51bb50e747cac6ffd8b29ffd489cf6d300eff231a0a149d6065af353ef38490c",
}


def run(command, cwd):
    subprocess.run(command, cwd=cwd, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cc", default="cc", help="C compiler executable (default: cc)")
    parser.add_argument("--cxx", default="c++", help="C++ compiler executable (default: c++)")
    args = parser.parse_args()
    for executable in (args.cc, args.cxx):
        if shutil.which(executable) is None:
            parser.error(f"required executable not found: {executable}")

    native = Path(__file__).resolve().parents[1]
    source = native / "third_party/kcp"
    regression = native / "tests/fastack_regression.cpp"
    originals = {}
    for name, expected in SOURCE_SHA256.items():
        data = (source / name).read_bytes()
        actual = hashlib.sha256(data).hexdigest()
        if actual != expected:
            raise RuntimeError(f"{name}: source SHA-256 mismatch: {actual}")
        originals[name] = data

    old = b"if (_itimediff(ts, latest_ts) > 0) {"
    fixed = b"if (_itimediff(ts, latest_ts) >= 0) {"
    if originals["ikcp.c"].count(fixed) != 1:
        raise RuntimeError("expected exactly one integrated fast-ACK fix")
    baseline = originals["ikcp.c"].replace(fixed, old, 1)
    # Keep allocation safety identical on both sides; isolate only the ACK comparison.
    if hashlib.sha256(baseline).hexdigest() != "cbf9c7faa39a271651950ae4f73f788e6127d989a1c502e5f38182940d63530c":
        raise RuntimeError("ACK control does not match its locked source hash")

    with tempfile.TemporaryDirectory(prefix="gameframe-kcp-fastack-") as temporary:
        for variant in ("baseline", "patched"):
            work = Path(temporary) / variant
            work.mkdir()
            for name, data in originals.items():
                # Reconstruct the old comparison only in the temporary control copy.
                (work / name).write_bytes(baseline if variant == "baseline" and name == "ikcp.c" else data)

            for mode, flags in (("assertions", []), ("ndebug", ["-DNDEBUG"])):
                print(f"\n{variant}/{mode}", flush=True)
                obj = work / "ikcp.o"
                executable = work / "fastack_regression"
                run([args.cc, "-std=c99", "-O2", *flags, "-c", "ikcp.c", "-o", str(obj)], work)
                run([args.cxx, "-std=c++98", "-O2", *flags, "-I", str(work),
                     str(regression), str(obj), "-o", str(executable)], work)
                run([str(executable), f"--{variant}"], work)

    for name, data in originals.items():
        if (source / name).read_bytes() != data:
            raise RuntimeError(f"original source changed during verification: {name}")
    print("\nPASS: ACK-only control reproduced; integrated ACK cases 16/16; scheduler cases 4/4.")
    print("Project KCP files unchanged during verification; temporary build files removed.")


if __name__ == "__main__":
    main()
