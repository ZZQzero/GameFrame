#!/usr/bin/env python3
"""Explicit PostgreSQL maintenance; the game server never starts/stops PostgreSQL."""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--pg-bin", type=Path, help="Directory containing PostgreSQL tools; defaults to PATH.")
    commands = result.add_subparsers(dest="action", required=True)
    for name in ("start", "stop", "status"):
        command = commands.add_parser(name)
        command.add_argument("--data-dir", type=Path, required=True, help="Existing PostgreSQL cluster directory.")
        if name == "start":
            command.add_argument("--log-file", type=Path, help="Defaults to DATA_DIR/server.log.")
    for name in ("create-db", "init-schema"):
        command = commands.add_parser(name)
        command.add_argument("--host", default="127.0.0.1")
        command.add_argument("--port", type=int, default=5432)
        command.add_argument("--user", required=True, help="Database role; passwords use .pgpass/PGPASSFILE/PGPASSWORD.")
        command.add_argument("--database", required=True)
        if name == "create-db":
            command.add_argument("--owner", required=True, help="Existing role that will own the new database.")
    return result


def build_command(args):
    if args.action in ("start", "stop", "status"):
        command = ["pg_ctl", "-D", str(args.data_dir.resolve()), args.action]
        if args.action == "start":
            log = args.log_file or args.data_dir / "server.log"
            command.extend(["--wait", "--timeout=30", "--log", str(log.resolve())])
        elif args.action == "stop":
            command.extend(["--wait", "--timeout=30", "--mode=fast"])
        return command

    if not 1 <= args.port <= 65535:
        raise ValueError("Port must be between 1 and 65535.")
    connection = ["--host", args.host, "--port", str(args.port), "--username", args.user, "--no-password"]
    if args.action == "create-db":
        return ["createdb", *connection, "--owner", args.owner, "--", args.database]

    sql = Path(__file__).resolve().parents[1] / "DotNet" / "Game.Server" / "Database" / "001_accounts.sql"
    return ["psql", *connection, "--no-psqlrc", "--dbname", args.database,
            "--set=ON_ERROR_STOP=1", "--file", str(sql)]


def main():
    args = parser().parse_args()
    try:
        command = build_command(args)
        name = command[0] + (".exe" if os.name == "nt" else "")
        binary = str((args.pg_bin / name).resolve()) if args.pg_bin else shutil.which(name)
        if not binary or not Path(binary).is_file():
            raise FileNotFoundError(f"PostgreSQL tool {name} not found; configure --pg-bin or PATH.")
        command[0] = binary
        environment = os.environ.copy()
        environment["PGCONNECT_TIMEOUT"] = "5"
        return subprocess.run(command, env=environment, check=False).returncode
    except (OSError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
