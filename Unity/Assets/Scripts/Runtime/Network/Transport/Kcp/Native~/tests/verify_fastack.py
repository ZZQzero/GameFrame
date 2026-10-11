#!/usr/bin/env python3
"""Verify the integrated KCP fast-ACK fix against a temporary pre-fix baseline."""

import argparse
import hashlib
import shutil
import subprocess
import tempfile
from pathlib import Path


SOURCE_SHA256 = {
    "ikcp.c": "3837dc313c120a62b37d1a5458dc0b2939bb917a1411e922f41a5a3caeddc558",
    "ikcp.h": "d67a0e67ba5701f1e882894b4415c4d3ad3d7d9f7231dd2ca68c6037c5217933",
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
    source = native.parents[5] / "Plugins/GameFrame/Kcp"
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
    if hashlib.sha256(baseline).hexdigest() != "8654038a70d1bbe605aa94f066554ffb30bf28580a58769970221de3ba5027f0":
        raise RuntimeError("pre-fix baseline does not match the locked upstream source")

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
    print("\nPASS: baseline reproduced; patched ACK cases 16/16; scheduler cases 4/4.")
    print("Project KCP files unchanged during verification; temporary build files removed.")


if __name__ == "__main__":
    main()
