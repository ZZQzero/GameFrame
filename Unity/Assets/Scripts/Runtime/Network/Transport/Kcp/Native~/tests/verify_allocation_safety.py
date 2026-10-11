"""Verify exact KCP patch application and inject failure at every allocation call."""

import argparse
import importlib.util
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


def run(command, cwd):
    subprocess.run(command, cwd=cwd, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cc", default="cc")
    parser.add_argument("--cxx", default="c++")
    parser.add_argument("--no-sanitizers", action="store_true", help="Skip ASan/UBSan when unavailable")
    args = parser.parse_args()
    for executable in (args.cc, args.cxx):
        if shutil.which(executable) is None:
            parser.error(f"required executable not found: {executable}")

    native = Path(__file__).resolve().parents[1]
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("prepare_kcp", native / "build/prepare_kcp.py")
    prepare = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(prepare)
    source = native.parents[5] / "Plugins/GameFrame/Kcp"
    originals = {name: (source / name).read_bytes() for name in prepare.SOURCE_SHA256}
    patch = (native / "patches/kcp-allocation-safety.patch").read_bytes().split(b"--- a/ikcp.h\n")[0]
    # A context change, line offset or corrupt hunk must fail instead of searching nearby lines.
    invalid = (
        patch.replace(b"int capacity =", b"int different =", 1),
        patch.replace(b"@@ -489,7", b"@@ -490,7", 1),
        patch.replace(b"@@ -489,7", b"@@ -489,8", 1),
    )
    for changed in invalid:
        try:
            prepare.apply_exact_patch(originals["ikcp.c"], changed)
        except ValueError:
            pass
        else:
            raise RuntimeError("Malformed/offset patch was accepted")

    modes = [("assertions", []), ("ndebug", ["-DNDEBUG"])]
    if not args.no_sanitizers:
        modes.append(("asan-ubsan", ["-fsanitize=address,undefined", "-fno-sanitize-recover=all",
                                    "-fno-omit-frame-pointer"]))
    with tempfile.TemporaryDirectory(prefix="gameframe-kcp-allocation-") as temporary:
        work = Path(temporary) / "prepared"
        prepare.prepare(work)
        try:
            prepare.prepare(work)
        except FileExistsError:
            pass
        else:
            raise RuntimeError("Preparation overwrote an existing source tree")
        for mode, flags in modes:
            print(f"\n{mode}", flush=True)
            obj = work / "ikcp.o"
            executable = work / "allocation_safety"
            run([args.cc, "-std=c11", "-O1", "-g", *flags, "-c", "ikcp.c", "-o", str(obj)], work)
            run([args.cc, "-std=c11", "-O1", "-g", *flags, "-I", str(work),
                 str(native / "tests/allocation_safety.c"), str(obj), "-o", str(executable)], work)
            run([str(executable)], work)
            # Safety changes must retain the real-endpoint fast-ACK and wraparound behavior.
            fastack = work / "fastack_regression"
            run([args.cxx, "-std=c++98", "-O1", "-g", *flags, "-I", str(work),
                 str(native / "tests/fastack_regression.cpp"), str(obj), "-o", str(fastack)], work)
            run([str(fastack), "--patched"], work)
    for name, original in originals.items():
        if (source / name).read_bytes() != original:
            raise RuntimeError(f"Original {name} changed during verification")
    print(f"\nPASS: exact patch guards; {25 * len(modes)} allocation cases; ACK/wraparound regressions.")
    print("Original Unity assets unchanged; all temporary sources/build files removed.")


if __name__ == "__main__":
    main()
