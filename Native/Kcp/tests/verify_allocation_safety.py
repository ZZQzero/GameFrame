"""Verify integrated KCP source integrity and failure at every allocation call."""

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
    source = native / "third_party/kcp"
    originals = prepare.read_sources(source)
    metadata = {name: (source / name).read_bytes() for name in ("ikcp.c.meta", "ikcp.h.meta")}

    modes = [("assertions", []), ("ndebug", ["-DNDEBUG"])]
    if not args.no_sanitizers:
        modes.append(("asan-ubsan", ["-fsanitize=address,undefined", "-fno-sanitize-recover=all",
                                    "-fno-omit-frame-pointer"]))
    with tempfile.TemporaryDirectory(prefix="gameframe-kcp-allocation-") as temporary:
        drift = Path(temporary) / "drift"
        drift.mkdir()
        for name, data in originals.items():
            (drift / name).write_bytes(data)
        for name, data in originals.items():
            (drift / name).write_bytes(data + b"\r\n")
            try:
                prepare.read_sources(drift)
            except ValueError:
                pass
            else:
                raise RuntimeError(f"Source drift was accepted: {name}")
            (drift / name).write_bytes(data)

        for protected in (source, native, native.parents[1] / "Unity"):
            try:
                prepare.prepare(protected)
            except ValueError:
                pass
            else:
                raise RuntimeError(f"Preparation allowed an output in protected sources: {protected}")

        work = Path(temporary) / "prepared"
        prepare.prepare(work)
        for name, data in originals.items():
            if (work / name).read_bytes() != data:
                raise RuntimeError(f"Preparation changed integrated source: {name}")
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
            # Exercise the same integrated source used by future plugin builds.
            fastack = work / "fastack_regression"
            run([args.cxx, "-std=c++98", "-O1", "-g", *flags, "-I", str(work),
                 str(native / "tests/fastack_regression.cpp"), str(obj), "-o", str(fastack)], work)
            run([str(fastack), "--patched"], work)
    for name, original in originals.items():
        if (source / name).read_bytes() != original:
            raise RuntimeError(f"Original {name} changed during verification")
    for name, original in metadata.items():
        if (source / name).read_bytes() != original:
            raise RuntimeError(f"Unity metadata changed during verification: {name}")
    print(f"\nPASS: source hash/copy guards; {25 * len(modes)} allocation cases; ACK/wraparound regressions.")
    print("Shared KCP sources and retained metadata unchanged during verification; temporary files removed.")


if __name__ == "__main__":
    main()
