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


def performance_run(args, root, app, executable, native, identity):
    build = (app.parent / "build-succeeded.txt").read_text()
    expected_build = "Development profiling" if args.mode == "profiling" else "Release performance"
    if expected_build not in build:
        raise ValueError(f"{args.mode} mode requires the dedicated {expected_build} build")
    if args.transport == "wss" and (args.certificate is None or not args.certificate.is_file()):
        raise ValueError("WSS requires --certificate pointing to an already trusted localhost PFX; trust is never changed")
    configuration = dict(
        Transport=args.transport, Scenario=args.scenario, Connections=args.connections,
        DurationSeconds=args.duration, WarmupSeconds=args.warmup,
        RequestsPerSecond=0 if args.scenario == "idle" else args.rate,
        MaxInFlight=args.concurrency, PendingLimit=args.pending_limit,
        TextLength=args.text_length, Unicode=args.unicode, Push=args.push, PushMoves=args.push_moves,
        Profiling=args.mode == "profiling", AllocationTrace=args.allocation_trace is not None)
    host = player = None
    with tempfile.TemporaryDirectory(prefix="gameframe-network-performance-") as temporary:
        directory = Path(temporary)
        os.chmod(directory, 0o700)
        (directory / "performance.json").write_text(json.dumps(configuration))
        settings = ET.Element("RunSettings")
        parameters = ET.SubElement(settings, "TestRunParameters")
        ET.SubElement(parameters, "Parameter", name="HostDirectory", value=str(directory))
        path = directory / "host.runsettings"
        ET.ElementTree(settings).write(path, encoding="utf-8", xml_declaration=True)
        with (directory / "host.log").open("w") as host_log, (directory / "stdout.log").open("w") as player_log:
            try:
                environment = os.environ.copy()
                environment["GF_KCP_NATIVE_LIBRARY"] = str(native)
                if args.certificate is not None:
                    environment["GF_PERFORMANCE_CERTIFICATE"] = str(args.certificate.resolve())
                host = subprocess.Popen([
                    args.dotnet, "test", "NetworkServer.Tests/NetworkServer.Tests.csproj", "-c", "Release",
                    "--no-build", "--no-restore", "--filter",
                    "FullyQualifiedName=GameFrame.Tests.NetworkPlayerPerformanceHostTests.ServePerformance",
                    "--settings", str(path), "--results-directory", str(directory)
                ], cwd=root / "Tools", env=environment, stdout=host_log, stderr=subprocess.STDOUT, start_new_session=True)
                wait_file(directory / "ready.json", host, 30)
                player_env = {key: value for key, value in os.environ.items()
                              if not key.startswith("DYLD_") and key not in
                              ("GF_KCP_NATIVE_LIBRARY", "GF_PERFORMANCE_CERTIFICATE", "GF_PERFORMANCE_CERTIFICATE_PASSWORD")}
                architecture = "arm64" if platform.machine().lower() == "arm64" else "x86_64"
                player = subprocess.Popen([
                    "/usr/bin/arch", "-" + architecture, str(executable), "-batchmode", "-nographics",
                    "-logFile", str(directory / "player.log"), "--gf-network-host", str(directory)
                ], cwd=directory, env=player_env, stdout=player_log, stderr=subprocess.STDOUT, start_new_session=True)
                monitor = []
                started = time.monotonic()
                deadline = started + args.duration + args.warmup + 80
                while player.poll() is None:
                    if time.monotonic() > deadline:
                        raise TimeoutError("Performance Player lifetime exceeded")
                    sample = subprocess.run(["/bin/ps", "-p", str(player.pid), "-o", "rss=", "-o", "%cpu="],
                                            capture_output=True, text=True, check=False).stdout.split()
                    if len(sample) == 2:
                        monitor.append(dict(ElapsedSeconds=time.monotonic() - started,
                                            RssBytes=int(sample[0]) * 1024, PsCpuPercent=float(sample[1]),
                                            MeasurementWindow=(directory / "measuring").is_file() and
                                            not (directory / "measured").is_file()))
                    time.sleep(1)
                code = player.returncode
                result_path = directory / "player-result.json"
                if code != 0 or not result_path.is_file():
                    failure = directory / "failure.txt"
                    diagnostic = failure.read_text() if failure.is_file() else "no managed failure record"
                    server_failures = [json.loads(path.read_text()) for path in sorted(directory.glob("server-failure-*.json"))]
                    raise RuntimeError(f"Performance Player failed (exit {code}); server failures: {server_failures}: {diagnostic}")
                result = json.loads(result_path.read_text())
                if result.get("Passed") is not True:
                    raise RuntimeError("Performance Player did not pass resource/workload checks")
                (directory / "stop-performance").touch()
                wait_file(directory / "performance-host-result.json", host, 30)
                host_code = host.wait(timeout=15)
                companion = json.loads((directory / "performance-host-result.json").read_text())
                if host_code != 0 or companion.get("Passed") is not True:
                    raise RuntimeError("Performance companion did not release all sessions and buffers")
                result.update(Companion=companion, PlayerExit=code, CompanionExit=host.returncode, ProcessSamples=monitor,
                              NativeArtifact=identity, ExecutionArchitecture=architecture, Build=build)
                if args.allocation_trace is not None:
                    trace = directory / "allocations.raw"
                    if not trace.is_file() or trace.stat().st_size == 0:
                        raise RuntimeError("Player produced no allocation trace")
                    # Exclusive creation protects existing profiler evidence from accidental replacement.
                    with args.allocation_trace.open("xb") as target, trace.open("rb") as source:
                        shutil.copyfileobj(source, target)
                    result["AllocationTrace"] = str(args.allocation_trace.resolve())
                rendered = json.dumps(result, ensure_ascii=False, indent=2)
                if args.json is not None:
                    args.json.write_text(rendered + "\n")
                print(rendered)
            finally:
                stop_process(player)
                stop_process(host)


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--player", type=Path, required=True, help="NetworkValidation.app from the Unity validation build menu")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"), help="dotnet executable (SDK 10.0.401)")
    parser.add_argument("--json", type=Path, help="optional evidence JSON output; parent must exist")
    parser.add_argument("--mode", choices=("correctness", "performance", "profiling"), default="correctness")
    parser.add_argument("--allocation-trace", type=Path, help="optional new raw Profiler file; profiling mode only, duration <= 10s")
    parser.add_argument("--transport", choices=("kcp", "wss"), default="kcp")
    parser.add_argument("--scenario", choices=("idle", "echo", "timeout", "cancel", "backpressure"), default="echo")
    parser.add_argument("--connections", type=int, choices=range(1, 5), default=1)
    parser.add_argument("--duration", type=int, default=15, help="measurement seconds (1..120)")
    parser.add_argument("--warmup", type=int, default=3, help="warmup seconds (1..30)")
    parser.add_argument("--rate", type=int, default=60, help="total offered requests/second across all clients")
    parser.add_argument("--concurrency", type=int, default=64)
    parser.add_argument("--pending-limit", type=int, default=512)
    parser.add_argument("--text-length", type=int, default=32)
    parser.add_argument("--unicode", action="store_true")
    parser.add_argument("--push", action="store_true", help="synthetic existing GomokuSnapshot Push per Echo, not gameplay")
    parser.add_argument("--push-moves", type=int, default=0)
    parser.add_argument("--certificate", type=Path, help="already trusted localhost PFX for WSS; password in GF_PERFORMANCE_CERTIFICATE_PASSWORD")
    args = parser.parse_args()
    if args.allocation_trace is not None:
        if args.mode != "profiling" or args.duration > 10:
            parser.error("--allocation-trace requires --mode profiling and --duration <= 10")
        if not args.allocation_trace.parent.is_dir() or args.allocation_trace.exists():
            parser.error("--allocation-trace requires an existing parent and a new output file")
    if not (1 <= args.duration <= 120 and 1 <= args.warmup <= 30
            and 0 <= args.rate <= 10000 and 1 <= args.concurrency <= 512 and 1 <= args.pending_limit <= 512
            and 0 <= args.text_length <= 128 and 0 <= args.push_moves <= 225):
        parser.error("duration/warmup/rate/concurrency/pending/text/push configuration exceeds bounded workload limits")
    if args.mode != "correctness" and args.scenario != "idle" and args.rate * args.duration > 100000:
        parser.error("rate * duration must not exceed the 100000 RPC sample capacity")
    if args.mode != "correctness" and args.scenario != "idle" and args.rate == 0:
        parser.error("non-idle workload requires --rate > 0")
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
    if args.mode != "correctness":
        performance_run(args, root, app, executable, native, identity)
        return
    if "Development correctness" not in (app.parent / "build-succeeded.txt").read_text():
        parser.error("correctness mode requires the Development validation build")
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
