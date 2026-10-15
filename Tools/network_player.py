#!/usr/bin/env python3
"""Run the dedicated macOS IL2CPP/HybridCLR networking Player against an isolated .NET companion."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import shutil
import signal
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET

from network_load import verified_kcp_library


def wait_file(path, process, timeout):
    deadline = time.monotonic() + timeout
    while not path.is_file():
        if process.poll() is not None:
            raise RuntimeError(f"process exited {process.returncode} before {path.name}")
        if time.monotonic() >= deadline:
            raise TimeoutError(f"timed out waiting for {path.name}")
        time.sleep(0.05)


def stop_process(process):
    if process is not None:
        # Include testhost descendants even if the dotnet front end already exited.
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--player", type=Path, required=True, help="NetworkValidation.app from the Unity validation build menu")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"), help="dotnet executable (SDK 10.0.401)")
    parser.add_argument("--json", type=Path, help="optional evidence JSON output; parent must exist")
    args = parser.parse_args()
    if platform.system() != "Darwin":
        parser.error("this validation entry currently targets macOS only")
    if not args.dotnet or not Path(args.dotnet).is_file():
        parser.error("configure --dotnet with the dotnet executable")
    app = args.player.resolve()
    if not (app.parent / "build-succeeded.txt").is_file():
        parser.error("use an intact dedicated validation build output directory")
    if args.json is not None and not args.json.parent.is_dir():
        parser.error("--json parent directory must exist")
    root = Path(__file__).resolve().parent.parent
    native, identity = verified_kcp_library(
        root / "Unity/Assets/Plugins/GameFrame/Native/Kcp/macOS/libgameframe_kcp.dylib", root)
    bundled = list(app.rglob("libgameframe_kcp.dylib"))
    if len(bundled) != 1 or hashlib.sha256(bundled[0].read_bytes()).hexdigest() != identity["Sha256"]:
        parser.error("Player must contain exactly one matching verified KCP native library")
    subprocess.run(["/usr/bin/codesign", "--verify", "--deep", str(app)], check=True)
    with (app / "Contents/Info.plist").open("rb") as stream:
        executable = app / "Contents/MacOS" / plistlib.load(stream)["CFBundleExecutable"]
    subprocess.run([args.dotnet, "build", "NetworkServer.Tests/NetworkServer.Tests.csproj", "-c", "Release",
                    "-p:RestoreLockedMode=true", "--nologo", "-v:q"], cwd=root / "Tools", check=True)
    host = player = None
    with tempfile.TemporaryDirectory(prefix="gameframe-network-player-") as temporary:
        directory = Path(temporary)
        os.chmod(directory, 0o700)
        settings = ET.Element("RunSettings")
        parameters = ET.SubElement(settings, "TestRunParameters")
        ET.SubElement(parameters, "Parameter", name="HostDirectory", value=str(directory))
        settings_path = directory / "host.runsettings"
        ET.ElementTree(settings).write(settings_path, encoding="utf-8", xml_declaration=True)
        with (directory / "host.log").open("w") as host_log, (directory / "stdout.log").open("w") as player_log:
            try:
                host_env = os.environ.copy()
                host_env["GF_KCP_NATIVE_LIBRARY"] = str(native)
                host = subprocess.Popen([
                    args.dotnet, "test", "NetworkServer.Tests/NetworkServer.Tests.csproj", "-c", "Release",
                    "--no-build", "--no-restore", "--filter",
                    "FullyQualifiedName=GameFrame.Tests.KcpUnityFaultHostTests.ServeUnityFaults",
                    "--settings", str(settings_path), "--results-directory", str(directory)
                ], cwd=root / "Tools", env=host_env, stdout=host_log, stderr=subprocess.STDOUT, start_new_session=True)
                wait_file(directory / "ready.json", host, 30)
                player_env = {key: value for key, value in os.environ.items()
                              if not key.startswith("DYLD_") and key != "GF_KCP_NATIVE_LIBRARY"}
                architecture = "arm64" if platform.machine().lower() == "arm64" else "x86_64"
                player = subprocess.Popen([
                    "/usr/bin/arch", "-" + architecture, str(executable), "-batchmode", "-nographics", "-logFile", str(directory / "player.log"),
                    "--gf-network-host", str(directory)
                ], cwd=directory, env=player_env, stdout=player_log, stderr=subprocess.STDOUT, start_new_session=True)
                code = player.wait(timeout=100)
                if code != 0 or not (directory / "player-result.json").is_file():
                    failure = directory / "failure.txt"
                    diagnostic = "\n".join(failure.read_text().splitlines()[:18]) if failure.is_file() else "no managed failure record"
                    raise RuntimeError(f"Player failed (exit {code}): {diagnostic}")
                result = json.loads((directory / "player-result.json").read_text())
                if result.get("Passed") is not True or len(result.get("Checks", [])) != 7:
                    raise RuntimeError("Player did not complete all seven verification groups")
                sequence = int((directory / "sequence.txt").read_text()) + 1
                stem = directory / f"{sequence:06d}"
                pending = stem.with_suffix(".tmp")
                pending.write_text(json.dumps({"Operation": "stop", "Index": 0}))
                pending.rename(stem.with_suffix(".command.json"))
                response = stem.with_suffix(".result.json")
                wait_file(response, host, 20)
                stopped = json.loads(response.read_text())
                if not all(stopped.get(key) is True for key in ("Ok", "ResourcesReleased", "NormalSessionsClosed", "AccountingValid")):
                    raise RuntimeError("Companion resource release failed")
                if host.wait(timeout=15) != 0:
                    raise RuntimeError("Companion test process failed")
                result.update(NativeArtifact=identity, CompanionReleased=True,
                              ExecutionArchitecture=architecture,
                              Build=(app.parent / "build-succeeded.txt").read_text())
                rendered = json.dumps(result, ensure_ascii=False, indent=2)
                if args.json is not None:
                    args.json.write_text(rendered + "\n")
                print(rendered)
            finally:
                stop_process(player)
                stop_process(host)


if __name__ == "__main__":
    main()
