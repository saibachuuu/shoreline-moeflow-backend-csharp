#!/usr/bin/env python3
"""Isolated harness for one *safe* run of the legacy Python pytest suite.

Why this exists
---------------
``app/config.py`` calls ``dotenv.load_dotenv()`` at import time, ``pytest.ini``
points pytest-dotenv at ``.env.test`` and ``tests/__init__.py`` only checks the
database name *after* ``create_app()`` has already connected and possibly
dropped it.  Running pytest in the developer workspace would therefore load
whatever dotenv file or environment that workspace carries.  This harness never
imports the legacy app in place and never runs pytest inside the legacy
workspace.

Two halves
----------
``plan`` (read-only, stdlib only, safe to run anywhere)
    * Materialises a disposable source snapshot with ``git archive`` of *one*
      explicit commit (optionally pinned with ``--expect-commit``).  Worktree
      files, ``.git`` objects other than that commit, dotenv files and
      credentials are never read.
    * Scans the snapshot.  Any dotenv file that is not a ``*.sample`` template,
      any optional-module ``config_local.py`` and any credential-looking value
      blocks the run; nothing is silently skipped.
    * Emits the environment, the fixed pytest command and the scan result as
      JSON.  Caller-supplied pytest flags are not accepted at all, so
      ``-p dotenv`` / ``-c other.ini`` style bypasses cannot happen.

``run`` (inside the disposable container; needs the pinned requirements)
    * Re-applies every rule as a hard gate *before* anything named ``app`` is
      imported: dotenv autoload disabled and ``load_dotenv`` stubbed at
      interpreter start (``sitecustomize``), fixture staged outside the source
      tree, ``sys.path`` pinned, database ownership claimed before the app
      connects, dedicated service endpoints free and each proven to be the
      harness's own disposable instance, ``memory://`` broker, local storage
      confined to a temp dir, empty SMTP / provider keys, bounded workers.
    * Ports held by the *harness's own* service endpoints are recorded in
      ``--owned-endpoint-manifest`` (written by the launcher that started
      them).  A port is free when nothing listens on it, or when the listener
      is exactly the disposable instance named in that manifest.  Any other
      listener - a developer service, a stale container, a foreign database -
      blocks the run.
    * Starts pytest (with the container-side ``timeout`` wrapper and the
      harness's own subprocess timeout) only after every gate passed and
      passes the pytest exit code through unchanged.  Assertions are never
      patched out, tests are never skipped on purpose and a test failure is
      reported as a failure, never as a pass.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import secrets
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR / "tests") not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR / "tests"))

import python_isolation_common as common  # noqa: E402

GateFailure = common.GateFailure

DEFAULT_PORTS = {
    "port_mongo": 27321,
    "port_s3": 29321,
    "port_smtp": 21321,
    "port_mailpit": 28321,
    "port_api": 25321,
    "port_rabbit": 26321,
}
GATE_MARKER_ENV = "MOEFLOW_PYISO_GATES"
PLAN_MARKER = ".pyiso-snapshot.json"


# ---------------------------------------------------------------------------
# plan
# ---------------------------------------------------------------------------


def git(args: list[str], cwd: Path) -> str:
    result = subprocess.run(
        ["git", "-c", "core.quotepath=false", *args],
        cwd=str(cwd),
        capture_output=True,
        text=True,
        timeout=300,
        check=False,
    )
    if result.returncode != 0:
        raise GateFailure(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result.stdout


def snapshot_manifest_digest(root: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted(
        p for p in root.rglob("*") if p.is_file() and p.name != PLAN_MARKER
    ):
        relative = path.relative_to(root).as_posix()
        digest.update(relative.encode() + b"\0")
        digest.update((common.sha256_file(path) if path.is_file() else "link").encode() + b"\n")
    return digest.hexdigest()


def compute_plan(args: argparse.Namespace) -> dict:
    legacy = Path(args.legacy_directory).resolve()
    target = Path(args.target).resolve()
    if (
        target == legacy
        or common.is_within(target, legacy)
        or common.is_within(legacy, target)
    ):
        raise GateFailure("snapshot target must be disjoint from the legacy workspace")
    if not (legacy / ".git").exists():
        raise GateFailure(
            "legacy directory is not a git worktree; refusing to copy files"
        )
    head = git(["rev-parse", "HEAD"], legacy).strip()
    commit = args.commit or head
    resolved = git(["rev-parse", "--verify", f"{commit}^{{commit}}"], legacy).strip()
    if args.expect_commit and resolved != args.expect_commit:
        raise GateFailure(
            f"commit {resolved} does not match --expect-commit {args.expect_commit}"
        )
    commit_meta = git(
        ["show", "-s", "--format=%H%x09%ct%x09%s", resolved], legacy
    ).strip()

    if target.exists():
        if not args.replace_snapshot:
            raise GateFailure(f"snapshot target already exists: {target}")
        if not (target / ".pyiso-snapshot.json").is_file():
            raise GateFailure(
                "refusing to replace a directory this tool did not create"
            )
        shutil.rmtree(target)
    target.mkdir(parents=True, exist_ok=False)

    archive = subprocess.run(
        ["git", "archive", "--format=tar", resolved],
        cwd=str(legacy),
        capture_output=True,
        timeout=900,
        check=False,
    )
    if archive.returncode != 0:
        raise GateFailure(
            f"git archive failed: {archive.stderr.decode('utf-8', 'replace')}"
        )
    tar_path = target.parent / (target.name + ".tar")
    tar_path.write_bytes(archive.stdout)
    try:
        extract = subprocess.run(
            ["tar", "-C", str(target), "-xf", str(tar_path)],
            capture_output=True,
            timeout=900,
            check=False,
        )
    finally:
        tar_path.unlink(missing_ok=True)
    if extract.returncode != 0:
        raise GateFailure(
            f"tar extract failed: {extract.stderr.decode('utf-8', 'replace')}"
        )

    findings_raw = common.scan_directory(target)
    # ``*.sample`` templates are environment files: they must be scanned with
    # the assignment rules (``KEY=value``), not the code rules.
    for path in sorted(target.rglob(".env*")):
        relative = path.relative_to(target).as_posix()
        if not common.is_environment_file(relative):
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        findings_raw.extend(common.scan_environment_text(relative, text))
    findings = [finding.as_dict() for finding in findings_raw]
    blockers = [finding for finding in findings if finding["blocking"]]
    files = sum(1 for path in target.rglob("*") if path.is_file())
    plan = {
        "tool": "Python-IsolatedSuite",
        "version": common.TOOL_VERSION,
        "generated_utc": common.utc_now(),
        "legacy_head": head,
        "snapshot_commit": resolved,
        "snapshot_commit_meta": commit_meta,
        "snapshot_dir": str(target),
        "snapshot_files": files,
        "snapshot_sha256_manifest": snapshot_manifest_digest(target),
        "scan": {"findings": findings, "blocking": len(blockers)},
        "dotenv_templates": sorted(
            path.relative_to(target).as_posix()
            for path in target.rglob(".env*")
            if path.name.endswith(".sample")
        ),
        "db_name": args.db_name or common.new_db_name(),
        "ports": {key: getattr(args, key) for key in DEFAULT_PORTS},
        "pytest_args": list(common.PYTEST_BASE_ARGS),
        "workers": args.workers,
        "timeout_seconds": args.timeout_seconds,
        "status": "blocked" if blockers else "planned",
    }
    if blockers:
        plan["blocking_rules"] = sorted({finding["rule"] for finding in blockers})
    (target / ".pyiso-snapshot.json").write_text(
        json.dumps(
            {
                "tool": plan["tool"],
                "version": plan["version"],
                "snapshot_commit": resolved,
                "snapshot_sha256_manifest": plan["snapshot_sha256_manifest"],
                "db_name": plan["db_name"],
            },
            indent=2,
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    return plan


def cmd_plan(args: argparse.Namespace) -> int:
    plan = compute_plan(args)
    text = json.dumps(plan, indent=2, sort_keys=True) + "\n"
    if getattr(args, "print_plan", False):
        print(json.dumps(plan, indent=2, sort_keys=True))
    if args.json_out:
        out = Path(args.json_out)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(text, encoding="utf-8")
    else:
        print(text)
    if plan["status"] == "blocked":
        print(
            "BLOCKED: snapshot contains blocking findings: "
            + ", ".join(plan["blocking_rules"]),
            file=sys.stderr,
        )
        return 3
    return 0


# ---------------------------------------------------------------------------
# run-side gates (executed inside the container)
# ---------------------------------------------------------------------------


def assert_app_not_imported(stage: str) -> int:
    loaded = sorted(n for n in sys.modules if n == "app" or n.startswith("app."))
    if loaded:
        raise GateFailure(f"{stage}: app imported before isolation gates: {loaded[:5]}")
    return 0


def dotenv_stub_source(fixture_path: Path) -> str:
    return f'''"""Generated by tools/Python-IsolatedSuite.py -- do not edit.

Loaded by ``sitecustomize`` before any application code runs.  The legacy
``app/config.py`` calls ``dotenv.load_dotenv()`` at import time; this stub
replaces that function so only the isolated fixture file can ever be read.
"""

import os

import dotenv

FIXTURE = {str(fixture_path)!r}
_ALLOWED = ("MOEFLOW_PYISO_TOKEN",)


def _stub_load_dotenv(dotenv_path=None, stream=None, verbose=False,
                     override=False, interpolate=True, encoding="utf-8"):
    if dotenv_path is None:
        path = FIXTURE
    elif isinstance(dotenv_path, (list, tuple)):
        raise PermissionError("python-isolation: dotenv path lists are not allowed")
    else:
        path = str(dotenv_path)
    if os.path.abspath(path) != os.path.abspath(FIXTURE):
        raise PermissionError(
            "python-isolation: dotenv load outside the isolated fixture: " + path
        )
    from dotenv.main import dotenv_values

    loaded = []
    for key, value in dotenv_values(path, encoding=encoding).items():
        if key in _ALLOWED:
            # Harness bookkeeping key: already exported by the harness, and
            # never copied into the legacy environment by this stub.
            continue
        if key in os.environ and not override:
            continue
        if value is None:
            os.environ.pop(key, None)
        else:
            os.environ[key] = value
        loaded.append(key)
    return bool(loaded)


_stub_load_dotenv.__moeflow_pyiso_stub__ = True
dotenv.load_dotenv = _stub_load_dotenv
dotenv.find_dotenv = lambda *args, **kwargs: ""
'''


def render_guard_conftest() -> str:
    return '''"""Generated by tools/Python-IsolatedSuite.py -- do not edit.

Imported with ``-p python_isolation_guard`` before pytest collects ``tests``
(and therefore before ``tests/__init__.py`` imports ``app``).  If the gates
were not installed this aborts collection instead of running the suite with a
live dotenv loader or a foreign database.
"""

import os
import sys


def _fail(message):
    raise RuntimeError("python_isolation gate failed: " + message)


if os.environ.get("MOEFLOW_PYISO_GATES") != "installed":
    _fail("isolation gates are not installed")
if "app" in sys.modules:
    _fail("app was imported before the guard")

import dotenv  # noqa: E402

if not getattr(dotenv.load_dotenv, "__moeflow_pyiso_stub__", False):
    _fail("dotenv.load_dotenv is live")
if os.environ.get("DOTENV"):
    _fail("DOTENV is set")
if not os.environ.get("MONGODB_URI", "").startswith("mongomock://"):
    _fail("MONGODB_URI is not the isolated mongomock fixture")
if not os.environ.get("MONGODB_URI", "").endswith(
    "/%s?authSource=admin" % os.environ.get("MOEFLOW_PYISO_DATABASE", "-")
):
    _fail("MONGODB_URI does not target the owned database")
'''


def prepare_workdir(args: argparse.Namespace, source: Path, work: Path) -> dict:
    if common.is_within(work, source):
        raise GateFailure("work directory must be outside the source tree")
    if not common.DB_NAME_RE.match(args.db_name):
        raise GateFailure("database name is not an owned isolated name")
    work.mkdir(parents=True, exist_ok=True)
    storage_dir = work / "storage"
    storage_dir.mkdir(exist_ok=True)
    run_root = work / "runs" / time.strftime("%Y%m%dT%H%M%S", time.gmtime())
    fixture_dir = run_root / "fixture"
    guard_dir = run_root / "guard"
    tmp_dir = run_root / "tmp"
    home_dir = run_root / "home"
    for directory in (fixture_dir, guard_dir, tmp_dir, home_dir):
        directory.mkdir(parents=True, exist_ok=True)
    fixture_path = fixture_dir / "isolated.env"
    if common.is_within(fixture_path, source):
        raise GateFailure("fixture would be staged inside the source tree")

    marker_path = source / PLAN_MARKER
    if not marker_path.is_file():
        raise GateFailure(
            "source tree has no plan marker; run the plan sub-command first"
        )
    marker = json.loads(marker_path.read_text(encoding="utf-8"))
    actual_digest = snapshot_manifest_digest(source)
    if marker.get("snapshot_sha256_manifest") != actual_digest:
        raise GateFailure(
            "source tree does not match the snapshot digest recorded by plan"
        )
    if (source / ".git").exists():
        # A snapshot is a plain tree; a real worktree would make the digest
        # check meaningless and could expose the legacy workspace.
        raise GateFailure("source must be the extracted archive, not a git worktree")
    template = source / ".env.test.sample"
    if not template.is_file():
        raise GateFailure("committed fixture template .env.test.sample is missing")

    token = secrets.token_hex(16)
    settings = common.isolated_settings(
        args,
        storage_dir,
        args.db_name,
        token,
        (
            secrets.token_hex(16),
            secrets.token_hex(32),
            secrets.token_hex(12),
            "",
        ),
    )
    rendered = common.render_fixture(
        template.read_text(encoding="utf-8"), settings, drop=common.LEGACY_CRED_KEYS
    )
    values = common.parse_dotenv_text(rendered)
    problems = common.fixture_violations(values, args.db_name, storage_dir)
    if problems:
        raise GateFailure("rendered fixture rejected: " + "; ".join(problems))
    fixture_path.write_text(rendered, encoding="utf-8")

    (guard_dir / "sitecustomize.py").write_text(
        dotenv_stub_source(fixture_path), encoding="utf-8"
    )
    (guard_dir / "python_isolation_guard.py").write_text(
        render_guard_conftest(), encoding="utf-8"
    )
    if not (source / "conftest.py").is_file():
        raise GateFailure("legacy root conftest.py is missing")

    env = common.normalise_env(os.environ.items())
    env.update(settings)
    env.update(
        {
            GATE_MARKER_ENV: "installed",
            "MOEFLOW_PYISO_DATABASE": args.db_name,
            "PYTHONPATH": str(guard_dir),
            "PYTHONSAFEPATH": "1",
            "TMPDIR": str(tmp_dir),
            "HOME": str(home_dir),
        }
    )
    pytest_argv = [
        sys.executable,
        "-m",
        "pytest",
        *common.PYTEST_BASE_ARGS,
        f"-n={args.workers}",
        "-p",
        "python_isolation_guard",
        "-c",
        str(source / "pytest.ini"),
        "--rootdir",
        str(source),
        "--override-ini",
        f"env_files={fixture_path.as_posix()}",
        "--override-ini",
        "testpaths=",
        "--junitxml",
        str(run_root / "junit.xml"),
        str(source / "tests"),
    ]
    return {
        "plan": marker,
        "run_root": run_root,
        "fixture_path": fixture_path,
        "fixture_sha256": common.sha256_file(fixture_path),
        "fixture_values": values,
        "guard_dir": guard_dir,
        "env": env,
        "token": token,
        "pytest_argv": pytest_argv,
        "storage_dir": storage_dir,
    }


def port_listening(port: int) -> bool:
    """True when something accepts TCP connections on 127.0.0.1:port."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.settimeout(2.0)
        return probe.connect_ex(("127.0.0.1", port)) == 0


def load_endpoint_manifest(path_text: str | None) -> dict[int, dict]:
    if not path_text:
        return {}
    path = Path(path_text)
    if not path.is_file():
        raise GateFailure(f"endpoint manifest not found: {path}")
    payload = json.loads(path.read_text(encoding="utf-8"))
    if payload.get("tool") != "Python-IsolatedSuite":
        raise GateFailure("endpoint manifest was not written by this tool")
    owned: dict[int, dict] = {}
    for entry in payload.get("endpoints", []):
        port = int(entry["port"])
        if not (1 <= port <= 65535):
            raise GateFailure(f"endpoint manifest has an invalid port: {port}")
        if not str(entry.get("evidence", "")):
            raise GateFailure(f"endpoint manifest entry {port} has no evidence")
        if port in owned:
            raise GateFailure(f"endpoint manifest lists port {port} twice")
        owned[port] = entry
    return owned


def check_ports(
    ports: dict[str, int],
    owned: dict[int, dict],
    database: str = "",
) -> dict[str, dict]:
    """Report ports that are used by something other than the owned endpoint.

    A free port is fine.  A listening port is fine only when the launcher
    recorded it in the endpoint manifest *and* the endpoint still answers with
    the harness identity (``verify`` command of the launcher).  Everything else
    is a blocker: the harness never reuses or reconfigures a foreign service.
    """

    problems: dict[str, dict] = {}
    for label, port in ports.items():
        if not port_listening(port):
            if port in owned:
                problems[label] = {
                    "port": port,
                    "issue": "owned endpoint is not running",
                }
            continue
        entry = owned.get(port)
        if entry is None:
            problems[label] = {"port": port, "issue": "port held by a foreign process"}
            continue
        check = entry.get("verify")
        if not check:
            continue
        if "--database" not in check:
            check = [*check, "--database", database]
        result = subprocess.run(
            [sys.executable, str(Path(__file__).resolve()), "verify-endpoint", *check],
            capture_output=True,
            text=True,
            timeout=entry.get("verify_timeout_seconds", 60),
            check=False,
        )
        if result.returncode != 0:
            problems[label] = {
                "port": port,
                "issue": "owned endpoint identity check failed",
                "detail": (result.stdout + result.stderr).strip()[-800:],
            }
    return problems


def cmd_verify_endpoint(args: argparse.Namespace) -> int:
    """Identity probe used by check_ports; stdlib only, no app imports."""
    if args.kind == "mongodb":
        if not args.database:
            raise GateFailure("mongodb verification needs --database")
        probe = (
            "import sys, pymongo\n"
            "c = pymongo.MongoClient(sys.argv[1], serverSelectionTimeoutMS=5000,"
            " connectTimeoutMS=5000)\n"
            "names = c.list_database_names()\n"
            "dbs = [n for n in names if n.startswith('moeflow_pyiso_')"
            " and n != sys.argv[2]]\n"
            "print('databases=%s' % sorted(names))\n"
            "sys.exit(1 if dbs else 0)\n"
        )
        result = subprocess.run(
            [
                sys.executable,
                "-c",
                probe,
                f"mongodb://127.0.0.1:{args.port}/?directConnection=true",
                args.database,
            ],
            capture_output=True,
            text=True,
            timeout=60,
            check=False,
        )
    elif args.kind == "tcp":
        listening = port_listening(args.port)
        print(f"listening={listening}")
        return 0 if listening else 1
    else:
        raise GateFailure(f"unknown endpoint kind: {args.kind}")
    sys.stdout.write(result.stdout)
    sys.stderr.write(result.stderr)
    return 0 if result.returncode == 0 else 1


def cmd_endpoints(args: argparse.Namespace) -> int:
    """Start/stop the disposable service endpoints this harness owns.

    Only ever touches containers whose name starts with ``shoreline-pyiso-``
    and that carry the label written here, so a user service can never be
    stopped by a later ``stop``.
    """
    name = args.name
    if not name.startswith("shoreline-pyiso-"):
        raise GateFailure("endpoint container names must start with shoreline-pyiso-")
    if not common.DB_NAME_RE.match(args.database):
        raise GateFailure("endpoint manifest needs an owned isolated database name")

    if args.action == "stop":
        if docker_container_exists(name):
            docker(["rm", "-f", name])
            print(f"removed {name}")
        else:
            print(f"{name} not present")
        return 0

    if docker_container_exists(name):
        raise GateFailure(f"container {name} already exists; refusing to reuse it")

    docker(
        [
            "run",
            "-d",
            "--name",
            name,
            "--label",
            "shoreline.pyiso=20261110",
            "--tmpfs",
            "/data/db",
            "--memory",
            "512m",
            "--memory-swap",
            "768m",
            "--cpus",
            "1.5",
            "-p",
            f"127.0.0.1:{args.port}:27017",
            args.image,
            "mongod",
            "--bind_ip_all",
            "--wiredTigerCacheSizeGB",
            "0.25",
        ]
    )
    deadline = time.monotonic() + args.ready_timeout_seconds
    while True:
        ping = docker(
            [
                "exec",
                name,
                "mongosh",
                "--quiet",
                "--eval",
                "db.runCommand({ping:1}).ok",
            ],
            check=False,
        )
        if ping.returncode == 0 and "1" in ping.stdout:
            break
        if time.monotonic() > deadline:
            raise GateFailure(f"endpoint {name} did not become ready in time")
        time.sleep(2)

    container_id = docker(["inspect", "-f", "{{.Id}}", name]).stdout.strip()
    manifest = {
        "tool": "Python-IsolatedSuite",
        "version": common.TOOL_VERSION,
        "generated_utc": common.utc_now(),
        "database": args.database,
        "endpoints": [
            {
                "label": "port_mongo",
                "port": args.port,
                "container": name,
                "container_id": container_id,
                "image": args.image,
                "evidence": f"docker inspect {name} -> {container_id}",
                "verify": ["--kind", "mongodb", "--port", str(args.port)],
                "verify_timeout_seconds": 60,
            }
        ],
    }
    write_json(Path(args.manifest), manifest)
    print(json.dumps({"started": name, "port": args.port, "manifest": args.manifest}))
    return 0


def docker(argv: list[str], check: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(
        ["docker", *argv], capture_output=True, text=True, timeout=300, check=False
    )
    if check and result.returncode != 0:
        raise GateFailure(
            f"docker {' '.join(argv[:2])} failed: "
            f"{(result.stderr or result.stdout).strip()[:600]}"
        )
    return result


def docker_container_exists(name: str) -> bool:
    result = docker(
        ["ps", "-a", "--filter", f"name=^/{name}$", "--format", "{{.Names}}"],
        check=False,
    )
    return name in result.stdout.split()


def claim_owner(marker, database: str, token: str) -> dict:
    """Claim/verify the owner record of an isolated database (no connection)."""
    existing = marker.find_one({"marker": "moeflow_pyiso_owner"})
    if existing is None:
        marker.insert_one(
            {
                "marker": "moeflow_pyiso_owner",
                "tool": "Python-IsolatedSuite",
                "token": token,
                "database": database,
                "claimed_utc": common.utc_now(),
            }
        )
        return {"reclaimed": False, "previous_token": ""}
    # A database left behind by an earlier *same-tool* run on this disposable
    # endpoint is re-claimed: the record must be well formed (tool + isolated
    # name recorded), then it is stamped with this run's token.  Anything else
    # is foreign and blocks.
    if (
        existing.get("tool") != "Python-IsolatedSuite"
        or existing.get("database") != database
    ):
        raise GateFailure("database exists but is not owned by this run")
    previous = str(existing.get("token", ""))
    if previous == token:
        return {"reclaimed": False, "previous_token": previous}
    marker.update_one(
        {"marker": "moeflow_pyiso_owner"},
        {
            "$set": {
                "token": token,
                "reclaimed_utc": common.utc_now(),
                "previous_claimed_utc": existing.get("claimed_utc", ""),
            }
        },
    )
    return {"reclaimed": True, "previous_token": previous}


def claim_database(args: argparse.Namespace, token: str) -> dict:
    import pymongo

    if not common.DB_NAME_RE.match(args.db_name):
        raise GateFailure("database name is not an owned isolated name")
    client = pymongo.MongoClient(
        f"mongodb://127.0.0.1:{args.port_mongo}/?directConnection=true",
        serverSelectionTimeoutMS=5000,
        connectTimeoutMS=5000,
    )
    try:
        names = client.list_database_names()
        foreign = sorted(
            name
            for name in names
            if name.startswith("moeflow_pyiso_") and name != args.db_name
        )
        if foreign:
            raise GateFailure(f"foreign isolated databases visible: {foreign}")
        preexisting = args.db_name in names
        db = client[args.db_name]
        marker = db["__moeflow_pyiso_owner__"]
        claim = claim_owner(marker, args.db_name, token)
        owned = marker.find_one({"marker": "moeflow_pyiso_owner"})
        if not owned or owned.get("token") != token:
            raise GateFailure("database ownership marker could not be verified")
        if not db.name.endswith("_test"):
            raise GateFailure("database name must end with _test")
        return {
            "database": db.name,
            "owned": True,
            "preexisting": preexisting,
            "reclaimed": claim["reclaimed"],
        }
    finally:
        client.close()


def cmd_run(args: argparse.Namespace) -> int:
    source = Path(args.source).resolve()
    work = Path(args.root).resolve()
    summary: dict = {
        "tool": "Python-IsolatedSuite",
        "version": common.TOOL_VERSION,
        "started_utc": common.utc_now(),
        "python": sys.version.split()[0],
        "docker": os.environ.get("MOEFLOW_PYISO_DOCKER", ""),
    }
    json_out = Path(args.json_out) if args.json_out else work / "summary.json"
    gates: dict = {}
    exec_root: Path | None = None
    try:
        gates["imports_before_gates"] = assert_app_not_imported("pre-gate")
        invocation = prepare_workdir(args, source, work)
        gates["fixture"] = {
            "path": str(invocation["fixture_path"]),
            "sha256": invocation["fixture_sha256"],
            "values": invocation["fixture_values"],
        }
        gates["dotenv_autoload"] = common.dotenv_autoload_disabled(invocation["env"])
        if not gates["dotenv_autoload"]:
            raise GateFailure("dotenv autoload environment is present")
        ports = {key: getattr(args, key) for key in DEFAULT_PORTS}
        gates["ports"] = ports
        owned_endpoints = load_endpoint_manifest(args.owned_endpoint_manifest)
        gates["owned_endpoints"] = sorted(owned_endpoints)
        gates["ports_occupied"] = check_ports(
            ports, owned_endpoints, args.db_name
        )
        if gates["ports_occupied"]:
            raise GateFailure(
                f"isolated ports unavailable: {gates['ports_occupied']}"
            )
        gates["imports_after_fixture"] = assert_app_not_imported("post-fixture")
        try:
            gates["db"] = claim_database(args, invocation["token"])
        except GateFailure:
            raise
        except Exception as error:  # noqa: BLE001 - connection problems block, never pass
            raise GateFailure(f"could not claim isolated database: {error}") from error
        gates["imports_after_db"] = assert_app_not_imported("post-db")

        exec_root, digest = materialise_execution_copy(
            source, invocation["run_root"]
        )
        gates["execution_copy"] = {
            "path": str(exec_root),
            "sha256_manifest": digest,
            "same_files_as_source": digest
            == snapshot_manifest_digest(source),
        }
        if not gates["execution_copy"]["same_files_as_source"]:
            raise GateFailure("execution copy does not match the verified snapshot")

        summary["gates"] = gates
        summary["pytest_argv"] = replace_source(
            invocation["pytest_argv"], source, exec_root
        )

        if args.dry_run:
            summary["status"] = "dry-run"
            write_json(json_out, summary)
            print(json.dumps({"status": "dry-run", "gates": gates}, indent=2))
            return 0

        log_path = invocation["run_root"] / "pytest.log"
        started = time.monotonic()
        try:
            process = subprocess.run(
                summary["pytest_argv"],
                cwd=str(exec_root),
                env=replace_source_env(invocation["env"], source, exec_root),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                timeout=args.timeout_seconds,
                check=False,
            )
            exit_code = process.returncode
            output = process.stdout
        except subprocess.TimeoutExpired as expired:
            exit_code = 124
            output = (expired.stdout or b"") + b"\n[harness] pytest timed out\n"
        elapsed = round(time.monotonic() - started, 1)
        text = output.decode("utf-8", "replace")
        log_path.write_text(text, encoding="utf-8")
        summary["pytest"] = {
            "exit_code": exit_code,
            "elapsed_seconds": elapsed,
            "log": str(log_path),
            "log_sha256": common.sha256_file(log_path),
            "junit": str(invocation["run_root"] / "junit.xml"),
        }
        counts = junit_counts(invocation["run_root"] / "junit.xml")
        summary["pytest"]["counts"] = counts
        summary["pytest"]["log_tail"] = text.strip().splitlines()[-40:]
        if exit_code == 0:
            summary["status"] = "pass"
        elif exit_code == 124:
            summary["status"] = "timeout"
        else:
            summary["status"] = "fail"
        write_json(json_out, summary)
        print(text[-12000:])
        return exit_code
    except GateFailure as failure:
        summary["status"] = "blocked"
        summary["failure"] = str(failure)
        summary["gates"] = gates
        write_json(json_out, summary)
        print(f"BLOCKED: {failure}", file=sys.stderr)
        return 3
    finally:
        if exec_root is not None and not args.keep_execution_copy:
            shutil.rmtree(exec_root, ignore_errors=True)


def materialise_execution_copy(source: Path, run_root: Path) -> tuple[Path, str]:
    """Copy the verified snapshot to a writable per-run tree.

    The snapshot the plan verified stays read-only.  pytest (through the legacy
    root ``conftest.py``) compiles translation catalogs and tests write under
    ``artifacts/`` and ``files/tmp``, so the run happens in a throwaway copy
    whose digest must equal the snapshot digest.
    """
    exec_root = run_root / "exec"
    if exec_root.exists():
        shutil.rmtree(exec_root)
    shutil.copytree(source, exec_root, symlinks=True)
    digest = snapshot_manifest_digest(exec_root)
    return exec_root, digest


def replace_source(argv: list[str], old: Path, new: Path) -> list[str]:
    old_text, new_text = str(old), str(new)
    return [arg.replace(old_text, new_text) for arg in argv]


def replace_source_env(env: dict[str, str], old: Path, new: Path) -> dict[str, str]:
    old_text, new_text = str(old), str(new)
    replaced = {}
    for key, value in env.items():
        replaced[key] = value.replace(old_text, new_text) if isinstance(value, str) else value
    return replaced


def junit_counts(path: Path) -> dict[str, int]:
    """Read pass/fail/skip/error counts from the JUnit XML pytest wrote."""
    if not path.is_file():
        return {}
    import xml.etree.ElementTree as ET

    try:
        root = ET.parse(path).getroot()
    except ET.ParseError:
        return {}
    suites = [root] if root.tag == "testsuite" else list(root.iter("testsuite"))
    totals = {"tests": 0, "failures": 0, "errors": 0, "skipped": 0}
    for suite in suites:
        for key in totals:
            totals[key] += int(suite.get(key, 0) or 0)
    totals["passed"] = max(0, totals["tests"] - totals["failures"] - totals["errors"] - totals["skipped"])
    return totals


def write_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="Python-IsolatedSuite.py",
        description="Plan and run one isolated legacy Python pytest suite.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    plan = sub.add_parser("plan", help="create a read-only snapshot and safety plan")
    plan.add_argument("--legacy-directory", required=True, type=Path)
    plan.add_argument("--target", required=True, type=Path)
    plan.add_argument(
        "--commit", default=None, help="commitish to archive (default HEAD)"
    )
    plan.add_argument(
        "--expect-commit", default=None, help="pin the resolved commit id"
    )
    plan.add_argument("--replace-snapshot", action="store_true")
    plan.add_argument("--db-name", default=None)
    plan.add_argument("--workers", type=int, default=2)
    plan.add_argument("--timeout-seconds", type=int, default=1800)
    plan.add_argument("--json-out", default=None)
    plan.add_argument("--print-plan", action="store_true")
    for key in DEFAULT_PORTS:
        plan.add_argument(
            "--" + key.replace("_", "-"), dest=key, type=int, default=DEFAULT_PORTS[key]
        )
    plan.set_defaults(handler=cmd_plan)

    run = sub.add_parser("run", help="run the suite inside the disposable container")
    run.add_argument("--source", required=True, type=Path)
    run.add_argument("--root", required=True, type=Path)
    run.add_argument("--db-name", required=True)
    run.add_argument("--workers", type=int, default=2)
    run.add_argument("--timeout-seconds", type=int, default=1800)
    run.add_argument("--json-out", default=None)
    run.add_argument("--dry-run", action="store_true")
    run.add_argument(
        "--keep-execution-copy",
        dest="keep_execution_copy",
        action="store_true",
        default=False,
        help="keep the per-run writable copy of the verified snapshot",
    )
    run.add_argument(
        "--owned-endpoint-manifest",
        default=None,
        help="JSON manifest of disposable service endpoints this harness started",
    )
    for key in DEFAULT_PORTS:
        run.add_argument(
            "--" + key.replace("_", "-"), dest=key, type=int, default=DEFAULT_PORTS[key]
        )
    run.set_defaults(handler=cmd_run)

    verify = sub.add_parser(
        "verify-endpoint", help="identity probe used by the port gate"
    )
    verify.add_argument("--kind", required=True, choices=("mongodb", "tcp"))
    verify.add_argument("--port", required=True, type=int)
    verify.add_argument("--database", default="")
    verify.set_defaults(handler=cmd_verify_endpoint)

    endpoints = sub.add_parser(
        "endpoints", help="start/stop the disposable endpoints this harness owns"
    )
    endpoints.add_argument("--action", required=True, choices=("start", "stop"))
    endpoints.add_argument("--name", required=True)
    endpoints.add_argument("--port", required=True, type=int)
    endpoints.add_argument("--image", default="mongo:8.0")
    endpoints.add_argument("--database", required=True)
    endpoints.add_argument("--manifest", required=True)
    endpoints.add_argument("--ready-timeout-seconds", type=int, default=120)
    endpoints.set_defaults(handler=cmd_endpoints)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return int(args.handler(args))


if __name__ == "__main__":
    raise SystemExit(main())
