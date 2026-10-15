#!/usr/bin/env python3
"""Run explicit WSS or KCP load and fault checks using the existing .NET test infrastructure."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import runpy
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


def bounded_integer(minimum, maximum):
    def parse(value):
        number = int(value)
        if not minimum <= number <= maximum:
            raise argparse.ArgumentTypeError(f"expected {minimum}..{maximum}")
        return number
    return parse


def verified_kcp_library(path, root):
    binary = path.resolve()
    artifact = json.loads((binary.parent / "artifact.json").read_text())
    manifest = json.loads((binary.parent / "build-manifest.json").read_text())
    unsigned = dict(manifest)
    build_id = unsigned.pop("build_id")
    if hashlib.sha256(json.dumps(unsigned, sort_keys=True).encode()).hexdigest() != build_id:
        raise ValueError("native build manifest changed after publication")
    checksum = hashlib.sha256(binary.read_bytes()).hexdigest()
    host = {"Darwin": "macos", "Linux": "linux", "Windows": "windows"}.get(platform.system())
    arch = "arm64" if platform.machine().lower() in ("arm64", "aarch64") else "x86_64"
    if (manifest["abi"] != 2 or manifest["sanitized"] or manifest["configuration"] != "Release"
            or manifest["platform"] != host or manifest["arch"] not in (arch, "universal")
            or (manifest["arch"] == "universal" and host != "macos")
            or artifact["binary"] != binary.name or artifact["sha256"] != checksum
            or manifest["artifacts"].get(binary.name) != checksum
            or any(artifact[key] != manifest[key] for key in ("abi", "build_id", "platform", "arch"))):
        raise ValueError("native artifact must match its manifests and the host: non-sanitized Release ABI 2")
    # Reuse the installer's source inventory without installing or generating bytecode.
    old_bytecode = sys.dont_write_bytecode
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(root / "Native/Kcp/build"))
    try:
        build = runpy.run_path(str(root / "Native/Kcp/build/build.py"))
        if manifest["sources"] != build["sources"]():
            raise ValueError("maintained native sources changed; rebuild the native library")
        build["read_sources"](root / "Native/Kcp/third_party/kcp")
    finally:
        sys.path.pop(0)
        sys.dont_write_bytecode = old_bytecode
    for name, expected in manifest["kcp"]["source_sha256"].items():
        if Path(name).name != name or hashlib.sha256((root / "Native/Kcp/third_party/kcp" / name).read_bytes()).hexdigest() != expected:
            raise ValueError("native KCP source fingerprint mismatch")
    return binary, {"Abi": 2, "BuildId": build_id, "Sha256": checksum,
                    "Platform": manifest["platform"], "Architecture": manifest["arch"], "MaintainedSourcesVerified": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--transport", choices=("wss", "kcp"), default="wss")
    parser.add_argument("--native-library", type=Path,
                        help="KCP only: verified library with adjacent artifact/build manifests; alternatively GF_KCP_NATIVE_LIBRARY")
    parser.add_argument("--network-profile", choices=("direct", "loss", "delay", "jitter", "reorder", "mixed"), default="direct",
                        help="KCP only: deterministic bidirectional UDP impairment after connection setup; default direct")
    parser.add_argument("--network-seed", type=bounded_integer(1, 2147483647),
                        help="KCP impairment seed, default 1; fixed packet rules, not deterministic OS scheduling")
    parser.add_argument("--scenario", choices=("echo", "slow-handler", "idle", "burst", "close-storm", "fault"), default="echo")
    parser.add_argument("--fault-case", choices=("all", "dtls-loss", "dtls-cancel", "dtls-timeout",
                        "hello-cancel", "hello-timeout", "outage-recover", "outage-timeout", "close-client-loss",
                        "close-server-loss", "close-client-blackhole", "close-server-blackhole", "reconnect"),
                        help="KCP fault only: fixed default-deadline scenario; default all")
    parser.add_argument("--close-mode", choices=("client", "server"),
                        help="close-storm only: close from clients (default) or server sessions")
    parser.add_argument("--close-requests", type=bounded_integer(0, 128),
                        help="close-storm only: requests per connection held until close; default 8, 0 for idle close")
    parser.add_argument("--burst-size", type=bounded_integer(1, 1024),
                        help="requests per connection per burst; burst only, default 8")
    parser.add_argument("--burst-interval-ms", type=bounded_integer(1, 60000),
                        help="fixed interval between bursts; burst only, default 1000ms")
    parser.add_argument("--slow-handler-ms", type=bounded_integer(1, 30000),
                        help="async server delay for client 0; slow-handler default 25ms, burst optional")
    parser.add_argument("--connections", type=bounded_integer(1, 128), default=4)
    parser.add_argument("--rate", type=bounded_integer(0, 10000),
                        help="requests per second per connection; default 20, idle/burst require 0")
    parser.add_argument("--warmup", type=bounded_integer(0, 300), default=2, help="seconds")
    parser.add_argument("--duration", type=bounded_integer(1, 3600),
                        help="seconds per measured run; default 10, idle default 30 and minimum 6")
    parser.add_argument("--repeat", type=bounded_integer(1, 10), default=1)
    parser.add_argument("--payload-bytes", type=bounded_integer(16, 128), default=128,
                        help="ASCII Echo text size; excludes serialization and frame header")
    parser.add_argument("--inflight", type=bounded_integer(1, 1024), default=8, help="maximum in-flight requests per connection; total <= 8192")
    parser.add_argument("--request-timeout", type=bounded_integer(1, 30), default=3, help="seconds")
    parser.add_argument("--pump-ms", type=bounded_integer(1, 100),
                        help="requested delay between client pump iterations; default 1ms, idle 20ms")
    parser.add_argument("--json", type=Path, help="optional report file, overwritten; parent directory must exist")
    args = parser.parse_args()
    fault = args.scenario == "fault"
    if args.fault_case is not None and not fault:
        parser.error("--fault-case requires --scenario fault")
    if fault:
        if args.transport != "kcp":
            parser.error("fault requires --transport kcp")
        load_only = {"--connections", "--rate", "--warmup", "--duration", "--payload-bytes", "--inflight",
                     "--request-timeout", "--pump-ms", "--network-profile", "--network-seed", "--close-mode",
                     "--close-requests", "--burst-size", "--burst-interval-ms", "--slow-handler-ms"}
        if any(argument.split("=", 1)[0] in load_only for argument in sys.argv[1:]):
            parser.error("fault has fixed connections/timings/injection; load scenario options do not apply")
    idle = args.scenario == "idle"
    burst = args.scenario == "burst"
    close_storm = args.scenario == "close-storm"
    if args.transport == "kcp" and args.connections > 64:
        parser.error("KCP supports at most 64 connections")
    if args.transport != "kcp" and (args.network_profile != "direct" or args.network_seed is not None):
        parser.error("--network-profile and --network-seed require --transport kcp")
    if args.network_seed is not None and args.network_profile == "direct":
        parser.error("--network-seed requires an impaired --network-profile")
    if args.transport != "kcp" and args.native_library is not None:
        parser.error("--native-library requires --transport kcp")
    if not close_storm and (args.close_mode is not None or args.close_requests is not None):
        parser.error("--close-mode and --close-requests require --scenario close-storm")
    if close_storm and (args.close_requests if args.close_requests is not None else 8) > args.inflight:
        parser.error("--close-requests must be <= --inflight (default close requests: 8)")
    if args.rate is None:
        args.rate = 0 if idle or burst else 20
    if args.duration is None:
        args.duration = 30 if idle else 10
    if args.pump_ms is None:
        args.pump_ms = 20 if idle else 1
    if idle and (args.rate != 0 or args.duration < 6):
        parser.error("idle requires --rate 0 and --duration >= 6 to observe heartbeat traffic")
    if burst and args.rate != 0:
        parser.error("burst requires --rate 0; use --burst-size and --burst-interval-ms")
    if not idle and not burst and args.rate == 0:
        parser.error("echo, slow-handler and close-storm require --rate >= 1")
    if not burst and (args.burst_size is not None or args.burst_interval_ms is not None):
        parser.error("--burst-size and --burst-interval-ms require --scenario burst")
    if args.scenario == "slow-handler" and args.connections < 2:
        parser.error("slow-handler requires at least two connections (one slow, others normal)")
    if args.scenario not in ("slow-handler", "burst") and args.slow_handler_ms is not None:
        parser.error("--slow-handler-ms requires --scenario slow-handler or burst")
    if args.connections * args.inflight > 8192:
        parser.error("connections * inflight must be <= 8192")
    output = args.json.resolve() if args.json else None
    if output and (not output.parent.is_dir() or output.is_dir()):
        parser.error("--json must name a file in an existing directory")

    root = Path(__file__).resolve().parent.parent
    native_proof = None
    child_environment = os.environ.copy()
    if args.transport == "kcp":
        native_path = args.native_library or os.environ.get("GF_KCP_NATIVE_LIBRARY")
        if not native_path:
            parser.error("KCP requires --native-library or GF_KCP_NATIVE_LIBRARY")
        try:
            native_binary, native_proof = verified_kcp_library(Path(native_path), root)
        except (OSError, ValueError, KeyError, TypeError) as error:
            parser.error(f"KCP native verification failed: {error}")
        child_environment["GF_KCP_NATIVE_LIBRARY"] = str(native_binary)
    dotnet = os.environ.get("DOTNET_EXECUTABLE") or shutil.which("dotnet")
    if not dotnet:
        parser.error("dotnet was not found; install SDK 10.0.401 or set DOTNET_EXECUTABLE")
    sdk = subprocess.check_output([dotnet, "--version"], cwd=root / "Tools", text=True).strip()
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True).strip())
    parameters = {
        "Scenario": args.scenario,
        "FaultCase": args.fault_case or "all",
        "Transport": args.transport,
        "NetworkProfile": args.network_profile,
        "NetworkSeed": args.network_seed or 1,
        "NativeProof": json.dumps(native_proof),
        "SlowHandlerMs": (args.slow_handler_ms or 25) if args.scenario == "slow-handler" else (args.slow_handler_ms or 0),
        "BurstSize": (args.burst_size or 8) if burst else 0,
        "BurstIntervalMs": (args.burst_interval_ms or 1000) if burst else 0,
        "CloseMode": (args.close_mode or "client") if close_storm else "",
        "CloseRequests": (args.close_requests if args.close_requests is not None else 8) if close_storm else 0,
        "Connections": args.connections, "Rate": args.rate, "Warmup": args.warmup,
        "Duration": args.duration, "Repeat": args.repeat, "PayloadBytes": args.payload_bytes,
        "Inflight": args.inflight, "RequestTimeout": args.request_timeout, "PumpMs": args.pump_ms,
        "JsonPath": str(output) if output else "", "Sdk": sdk,
        "Revision": revision, "WorktreeDirty": str(dirty).lower(),
    }
    # Structured XML preserves spaces and special characters without shell interpolation.
    with tempfile.TemporaryDirectory(prefix="gameframe-network-load-") as temporary:
        settings = ET.Element("RunSettings")
        test_parameters = ET.SubElement(settings, "TestRunParameters")
        for name, value in parameters.items():
            ET.SubElement(test_parameters, "Parameter", name=name, value=str(value))
        settings_path = Path(temporary) / "load.runsettings"
        ET.ElementTree(settings).write(settings_path, encoding="utf-8", xml_declaration=True)
        selected_test = "KcpFaultMatrix" if fault else "SustainedEcho"
        command = [dotnet, "test", str(root / "Tools/NetworkServer.Tests/NetworkServer.Tests.csproj"),
                   "-c", "Release", "-p:RestoreLockedMode=true", "--settings", str(settings_path),
                   "--filter", f"FullyQualifiedName=GameFrame.Tests.WssLoadTests.{selected_test}",
                   "--results-directory", temporary, "--logger", "console;verbosity=normal"]
        return subprocess.call(command, cwd=root / "Tools", env=child_environment)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
    except (OSError, subprocess.CalledProcessError) as error:
        print(f"Network load runner failed: {error}", file=sys.stderr)
        sys.exit(1)
