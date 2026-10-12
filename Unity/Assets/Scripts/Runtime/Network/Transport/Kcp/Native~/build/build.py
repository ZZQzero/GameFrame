#!/usr/bin/env python3
"""Build the locked C KCP/DTLS plugin; dependency downloads are separate."""
import argparse
import hashlib
import json
import platform
import re
from pathlib import Path
import shutil
import subprocess
import sys
from prepare_kcp import prepare, read_sources
from prepare_mbedtls import tree_hash

ROOT = Path(__file__).resolve().parents[1]
UNITY = ROOT.parents[6]
PLATFORMS = ("macos", "windows", "android", "ios", "ios-simulator", "linux")


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def sources():
    paths = [ROOT / "CMakeLists.txt", ROOT / "third_party/mbedtls.lock.json"]
    for directory in ("include", "src", "config", "build", "tests"):
        paths += [p for p in (ROOT / directory).rglob("*") if p.suffix in (".c", ".h", ".py", ".cmake")]
    return {p.relative_to(ROOT).as_posix(): digest(p) for p in sorted(paths)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=PLATFORMS)
    parser.add_argument("--arch", required=True, choices=("arm64", "x86_64", "universal"))
    parser.add_argument("--configuration", required=True, choices=("Debug", "Release"))
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--mbedtls", required=True, type=Path)
    parser.add_argument("--cmake", default="cmake")
    parser.add_argument("--ndk", type=Path)
    parser.add_argument("--llvm-mingw", type=Path, help="Explicit LLVM-MinGW root for Windows cross builds, as used by Sqlite")
    parser.add_argument("--android-api", type=int, default=23)
    parser.add_argument("--deployment-target", default="13.0")
    parser.add_argument("--tests", action="store_true")
    parser.add_argument("--sanitizers", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    if args.llvm_mingw and args.platform != "windows":
        parser.error("--llvm-mingw requires --platform windows.")
    cross_windows = args.platform == "windows" and platform.system() != "Windows"
    if cross_windows and not args.llvm_mingw:
        parser.error("Windows cross builds require an explicit --llvm-mingw.")
    output = args.output.resolve()
    dependency = args.mbedtls.resolve()
    if output == UNITY or UNITY in output.parents:
        parser.error("Build output must be outside Unity.")
    if args.arch == "universal" and args.platform != "macos":
        parser.error("universal is only valid for macOS.")
    if args.platform in ("android", "ios") and args.arch != "arm64":
        parser.error("V1 device builds use arm64.")
    if args.platform == "windows" and args.arch != "x86_64":
        parser.error("V1 Windows builds use x86_64.")
    cmake = shutil.which(args.cmake)
    if not cmake and not args.dry_run:
        parser.error("CMake executable not found: " + args.cmake)
    cmake = cmake or args.cmake
    prepared = output / "kcp-source"
    command = [cmake, "-S", str(ROOT), "-B", str(output),
               "-DGFNET_MBEDTLS_SOURCE=" + str(dependency), "-DGFNET_KCP_SOURCE=" + str(prepared),
               "-DCMAKE_BUILD_TYPE=" + args.configuration, "-DGFNET_TESTS=" + ("ON" if args.tests else "OFF"),
               "-DGFNET_SANITIZERS=" + ("ON" if args.sanitizers else "OFF")]
    if args.platform == "macos":
        command += ["-DCMAKE_OSX_ARCHITECTURES=" + ("arm64;x86_64" if args.arch == "universal" else args.arch)]
    elif args.platform in ("ios", "ios-simulator"):
        sdk = "iphoneos" if args.platform == "ios" else "iphonesimulator"
        command += ["-DCMAKE_SYSTEM_NAME=iOS", "-DCMAKE_OSX_SYSROOT=" + sdk,
                    "-DCMAKE_OSX_ARCHITECTURES=" + args.arch, "-DCMAKE_OSX_DEPLOYMENT_TARGET=" + args.deployment_target]
    elif args.platform == "android":
        if not args.ndk:
            parser.error("Android requires an explicit --ndk.")
        command += ["-DCMAKE_TOOLCHAIN_FILE=" + str(args.ndk.resolve() / "build/cmake/android.toolchain.cmake"),
                    "-DANDROID_ABI=arm64-v8a", "-DANDROID_PLATFORM=android-" + str(args.android_api)]
    elif args.platform == "windows":
        if args.llvm_mingw:
            command += ["-G", "Ninja" if platform.system() == "Windows" else "Unix Makefiles",
                        "-DCMAKE_TOOLCHAIN_FILE=" + str(ROOT / "build/windows-llvm-mingw.cmake"),
                        "-DGFNET_LLVM_MINGW=" + args.llvm_mingw.resolve().as_posix()]
        else:
            command += ["-A", "x64"]
    commands = [command, [cmake, "--build", str(output), "--config", args.configuration, "--parallel", "8"]]
    if args.dry_run:
        print(json.dumps({"platform": args.platform, "arch": args.arch, "commands": commands}, indent=2))
        return
    host = platform.system()
    if args.platform in ("macos", "ios", "ios-simulator") and host != "Darwin":
        parser.error("Apple builds require a macOS/Xcode host.")
    if args.platform == "linux" and host != "Linux":
        parser.error("Linux requires a Linux host; cross-toolchain is not configured.")
    if args.platform == "linux":
        expected_arch = "arm64" if platform.machine().lower() in ("aarch64", "arm64") else "x86_64"
        if args.arch != expected_arch:
            parser.error("Linux cross-architecture builds require a separately configured cross-toolchain.")
    if args.tests and args.platform not in ("macos", "windows", "linux"):
        parser.error("Native regression execution is host-only.")
    if args.sanitizers and not args.tests:
        parser.error("Sanitizers require --tests.")
    if args.sanitizers and args.platform not in ("macos", "linux"):
        parser.error("Sanitizers require a macOS/Linux host target.")
    lock = json.loads((ROOT / "third_party/mbedtls.lock.json").read_text())
    prepared_lock = json.loads((dependency / "gfnet-source-lock.json").read_text())
    if any(prepared_lock.get(k) != v for k, v in lock.items()):
        raise ValueError("Prepared dependency does not match the maintained lock.")
    if tree_hash(dependency) != prepared_lock.get("source_tree_sha256"):
        raise ValueError("Prepared mbedTLS source tree drifted; prepare a fresh verified release.")
    output.mkdir(parents=True, exist_ok=True)
    if prepared.exists():
        read_sources(prepared)
    else:
        prepare(prepared)
    source_hashes = sources()
    for call in commands:
        subprocess.run(call, check=True)
    if args.platform == "windows":
        name = "gameframe_kcp.dll"
    elif args.platform == "macos":
        name = "libgameframe_kcp.dylib"
    elif args.platform in ("ios", "ios-simulator"):
        name = "libgameframe_kcp-combined.a"
        merged = output / name
        libraries = [output / "libgameframe_kcp.a"] + [output / "mbedtls/library" / ("lib" + lib + ".a") for lib in ("mbedtls", "mbedx509", "mbedcrypto")]
        subprocess.run(["xcrun", "libtool", "-static", "-o", str(merged), *map(str, libraries)], check=True)
    else:
        name = "libgameframe_kcp.so"
    binary = output / name
    # Multi-config generators write there on every build; never publish a stale root copy.
    if (output / args.configuration / name).exists():
        shutil.copyfile(output / args.configuration / name, binary)
    if args.platform == "macos":
        subprocess.run(["codesign", "--force", "--sign", "-", str(binary)], check=True)
    test_result = {"built": args.tests, "executed": False}
    if args.tests and not cross_windows:
        ctest = str(Path(cmake).with_name("ctest.exe" if host == "Windows" else "ctest"))
        result = subprocess.run([ctest, "--test-dir", str(output), "-C", args.configuration, "--output-on-failure"], check=True, capture_output=True, text=True)
        print(result.stdout)
        test_result = {"built": True, "executed": True, "passed": True, "runner": "ctest"}
    if source_hashes != sources():
        raise ValueError("Sources changed during build; no installable manifest emitted.")
    cache = (output / "CMakeCache.txt").read_text()
    toolchain = {line.split("=", 1)[0]: line.split("=", 1)[1] for line in cache.splitlines()
                 if line.startswith(("CMAKE_C_COMPILER:", "CMAKE_C_FLAGS", "CMAKE_OSX_SYSROOT:", "CMAKE_OSX_DEPLOYMENT_TARGET:", "CMAKE_TOOLCHAIN_FILE:", "ANDROID_PLATFORM:", "GFNET_LLVM_MINGW:"))}
    compiler_records = list((output / "CMakeFiles").glob("*/CMakeCCompiler.cmake"))
    for record in compiler_records:
        compiler_text = record.read_text()
        for field in ("CMAKE_C_COMPILER_ID", "CMAKE_C_COMPILER_VERSION", "CMAKE_C_COMPILER_TARGET"):
            match = re.search(r'set\(' + field + r' "([^"]*)"\)', compiler_text)
            if match:
                toolchain[field] = match.group(1)
    if args.platform in ("macos", "ios", "ios-simulator"):
        sdk = {"macos": "macosx", "ios": "iphoneos", "ios-simulator": "iphonesimulator"}[args.platform]
        toolchain["sdk_version"] = subprocess.check_output(["xcrun", "--sdk", sdk, "--show-sdk-version"], text=True).strip()
    if args.platform == "android":
        toolchain["ndk_source_properties"] = (args.ndk.resolve() / "source.properties").read_text()
    manifest = {"abi": 1, "platform": args.platform, "arch": args.arch, "configuration": args.configuration,
                "sanitized": args.sanitizers, "sources": source_hashes, "mbedtls": prepared_lock,
                "kcp": json.loads((prepared / "kcp-source-manifest.json").read_text()), "commands": commands,
                "cmake_version": subprocess.check_output([cmake, "--version"], text=True).splitlines()[0],
                "toolchain": toolchain, "native_tests": test_result, "artifacts": {name: digest(binary)}}
    if args.platform == "windows":
        from verify_windows import inspect
        manifest["windows_binary"] = inspect(binary, ROOT / "include/gfnet.h")
    manifest["build_id"] = hashlib.sha256(json.dumps(manifest, sort_keys=True).encode()).hexdigest()
    (output / "build-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    license_output = output / "licenses"
    license_output.mkdir(exist_ok=True)
    shutil.copyfile(prepared / "LICENSE", license_output / "KCP-MIT.txt")
    license_source = dependency / "LICENSE"
    if license_source.is_file():
        shutil.copyfile(license_source, license_output / "mbedTLS-LICENSE.txt")
    else:
        for path in license_source.iterdir():
            if path.is_file():
                shutil.copyfile(path, license_output / ("mbedTLS-" + path.name))
    print(str(binary))


if __name__ == "__main__":
    main()
