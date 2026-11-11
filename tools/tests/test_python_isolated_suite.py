"""Unit tests for tools/Python-IsolatedSuite.py and its shared helpers.

Everything here works on synthetic text and temporary directories.  The tests
never import the legacy Python app, never read a dotenv file from the legacy
workspace, never open a network connection and never run pytest against the
legacy suite.
"""

from __future__ import annotations

import argparse
import collections
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
ROOT = TOOLS.parent
COMMON_PATH = TOOLS / "tests" / "python_isolation_common.py"
TOOL_PATH = TOOLS / "Python-IsolatedSuite.py"


def _load(path: Path, name: str):
    if name in sys.modules:
        return sys.modules[name]
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


common = _load(COMMON_PATH, "python_isolation_common_under_test")
tool = _load(TOOL_PATH, "python_isolated_suite_under_test")

DB = "moeflow_pyiso_0a1b2c3d_test"
STORAGE = Path("/tmp/pyiso-storage")

TEMPLATE = """# template
TESTING=YES
SITE_NAME=demo
SECRET_KEY=CHANGE_ME
ADMIN_INITIAL_PASSWORD=change_me
MONGODB_URI="mongomock://moeflow:CHANGE_ME@127.0.0.1:27017/moeflow_test?authSource=admin"
CELERY_BROKER_URL="amqp://user:pass@127.0.0.1:5672/vhost"
MONGODB_USER=moeflow
MONGODB_PASS=CHANGE_ME
RABBITMQ_USER=moeflow
RABBITMQ_PASS=CHANGE_ME
RABBITMQ_VHOST_NAME=moeflow
STORAGE_TYPE=LOCAL_STORAGE
STORAGE_DOMAIN=http://127.0.0.1:5000/storage/
OSS_VIA_CDN=True
"""


def _config():
    return argparse.Namespace(
        port_mongo=27321,
        port_s3=29321,
        port_smtp=21321,
        port_mailpit=28321,
        port_api=25321,
        port_rabbit=26321,
    )


def _settings():
    return common.isolated_settings(
        _config(), STORAGE, DB, "a" * 32, ("b" * 32, "c" * 64, "d" * 24, "")
    )


def _rendered():
    return common.render_fixture(TEMPLATE, _settings(), drop=common.LEGACY_CRED_KEYS)


class FakeCollection:
    """Minimal in-memory stand-in for the pymongo marker collection."""

    def __init__(self, documents=None):
        self.documents = list(documents or [])

    def find_one(self, query):
        for document in self.documents:
            if all(document.get(key) == value for key, value in query.items()):
                return dict(document)
        return None

    def insert_one(self, document):
        self.documents.append(dict(document))

    def update_one(self, query, update):
        for document in self.documents:
            if all(document.get(key) == value for key, value in query.items()):
                document.update(update.get("$set", {}))
                return


class EnvironmentHygieneTests(unittest.TestCase):
    def test_dotenv_and_pytest_switch_are_dropped(self):
        env = common.normalise_env(
            [
                ("DOTENV", "1"),
                ("DOTENV_PATH", "/x/.env"),
                ("PYTEST_ADDOPTS", "-p dotenv"),
                ("PYTEST_PLUGINS", "dotenv"),
                ("VIRTUAL_ENV", "/opt/foreign"),
                ("CONDA_PREFIX", "/opt/conda"),
                ("PYTHONPATH", "/legacy/workspace"),
                ("PATH", "/usr/bin"),
                ("HOME", "/home/x"),
            ]
        )
        for key in ("DOTENV", "DOTENV_PATH", "PYTEST_ADDOPTS", "PYTEST_PLUGINS"):
            self.assertNotIn(key, env)
        for key in ("VIRTUAL_ENV", "CONDA_PREFIX"):
            self.assertNotIn(key, env)
        self.assertEqual(env["PYTHONPATH"], "")
        self.assertEqual(env["PATH"], "/usr/bin")
        self.assertTrue(common.dotenv_autoload_disabled(env))

    def test_dotenv_autoload_detects_flag(self):
        self.assertFalse(common.dotenv_autoload_disabled({"DOTENV": "1"}))
        self.assertFalse(common.dotenv_autoload_disabled({"DOTENV_PATH": "/x"}))
        self.assertTrue(common.dotenv_autoload_disabled({}))


class SnapshotScanTests(unittest.TestCase):
    def test_dotenv_file_blocks_and_sample_is_reported(self):
        self.assertTrue(common.classify_entry(".env").blocking)
        self.assertTrue(common.classify_entry("app/.env.test").blocking)
        sample = common.classify_entry(".env.test.sample")
        self.assertIsNotNone(sample)
        self.assertFalse(sample.blocking)
        self.assertEqual(sample.rule, "dotenv-sample")

    def test_config_local_blocks(self):
        finding = common.classify_entry("app/modules/x/config_local.py")
        self.assertTrue(finding.blocking)
        self.assertEqual(finding.rule, "config-local")
        self.assertIsNone(
            common.classify_entry("app/modules/x/config_local.py.example")
        )

    def test_environment_placeholders_are_not_secrets(self):
        text = "\n".join(
            [
                "SECRET_KEY=CHANGE_ME",
                "ADMIN_INITIAL_PASSWORD=change_me",
                "PASSWORD=",
                'EMAIL_PASSWORD=""',
                "OSS_ACCESS_KEY_ID=",
                "MONGODB_URI='mongodb://moeflow:PLEASE_CHANGE_THIS@db:27017/x'",
                "TOKEN=<your-token-here>",
            ]
        )
        self.assertEqual(common.scan_environment_text(".env.sample", text), [])

    def test_environment_literal_secret_blocks(self):
        text = "SECRET_KEY=9f8e7d6c5b4a39281706f5e4d3c2b1a0\n"
        findings = common.scan_environment_text(".env.sample", text)
        self.assertEqual([f.rule for f in findings], ["assigned-secret"])

    def test_code_references_are_not_secrets(self):
        text = "\n".join(
            [
                'SECRET_KEY = env["SECRET_KEY"]',
                'user.password = data["new_password"]',
                "token = user.generate_token()",
                "token = request.headers.get('Authorization')",
                "password=initial_password,",
                "claim_token = claim_token",
                "password = fields.Str(required=True, load_only=True)  # comment here",
            ]
        )
        self.assertEqual(common.scan_code_text("app/x.py", text), [])

    def test_code_enum_members_are_not_secrets(self):
        text = '            VCodeType.RESET_PASSWORD: gettext("重置您的密码"),\n'
        self.assertEqual(common.scan_code_text("app/models/v_code.py", text), [])

    def test_code_literal_secret_blocks(self):
        text = 'password = "sup3r-s3cret-value"\n'
        findings = common.scan_code_text("app/x.py", text)
        self.assertEqual([f.rule for f in findings], ["assigned-secret"])
        self.assertTrue(findings[0].blocking)

    def test_test_tree_literal_is_evidence_not_blocker(self):
        text = '        new_password = "new_password111111"\n'
        findings = common.scan_code_text("tests/api/test_auth_api.py", text)
        self.assertEqual([f.rule for f in findings], ["test-fixture-literal"])
        self.assertFalse(findings[0].blocking)
        self.assertTrue(common.is_test_path("tests/x/y.py"))
        self.assertTrue(common.is_test_path("test_auth.py"))
        self.assertFalse(common.is_test_path("app/models/user.py"))

    def test_test_tree_still_blocks_real_shapes(self):
        text = "AKIAABCDEFGHIJKLMNOP\n"
        findings = common.scan_text("tests/data/x.txt", text)
        self.assertTrue(findings[0].blocking)

    def test_token_shapes_block_everywhere(self):
        self.assertTrue(common.scan_text("a.md", "AKIAABCDEFGHIJKLMNOP"))
        self.assertTrue(common.scan_text("b.pem", "-----BEGIN RSA PRIVATE KEY-----"))
        self.assertTrue(
            common.scan_text("c.yaml", 'url: "mongodb://user:Sup3rSecret@db:27017/x"')
        )
        self.assertTrue(common.scan_text("d.txt", "ghp_" + "a" * 30))

    def test_short_and_numeric_values_are_not_secrets(self):
        self.assertEqual(common.scan_text("x.env.sample", "TOKEN=1234"), [])
        self.assertEqual(common.scan_text("x.env.sample", "PASSWORD=abc"), [])
        self.assertEqual(common.scan_text("x.py", "token_expires = 2026-01-01"), [])

    def test_directory_scan_is_recursive(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "a").mkdir()
            (root / ".env.sample").write_text(
                "SECRET_KEY=CHANGE_ME\n", encoding="utf-8"
            )
            (root / "a" / "leak.env").write_text("X=1\n", encoding="utf-8")
            (root / "a" / ".env").write_text(
                "SECRET_KEY=abcdefghijk\n", encoding="utf-8"
            )
            findings = common.scan_directory(root)
            rules = sorted(f.rule for f in findings)
            self.assertIn("dotenv-file", rules)
            self.assertIn("dotenv-sample", rules)
            self.assertEqual(len(common.blocking_findings(findings)), 1)


class FixtureTests(unittest.TestCase):
    def test_rendered_fixture_is_valid_and_drops_legacy_credentials(self):
        text = _rendered()
        values = common.parse_dotenv_text(text)
        self.assertEqual(common.fixture_violations(values, DB, STORAGE), [])
        for key in ("MONGODB_USER", "MONGODB_PASS", "RABBITMQ_PASS"):
            self.assertNotIn(key, values)
        self.assertEqual(values["CELERY_BROKER_URL"], "memory://")
        self.assertNotIn("amqp", text)
        self.assertNotIn("CHANGE_ME@127.0.0.1:27017/moeflow_test", text)
        self.assertEqual(values["OSS_VIA_CDN"], "False")
        self.assertEqual(values["EMAIL_SMTP_PORT"], "21321")

    def test_template_comments_survive(self):
        self.assertIn("# template", _rendered())

    def test_non_mongomock_uri_is_rejected(self):
        values = common.parse_dotenv_text(_rendered())
        broken = dict(values, MONGODB_URI="mongodb://127.0.0.1:27017/moeflow_test")
        problems = common.fixture_violations(broken, DB, STORAGE)
        self.assertTrue(any("mongomock" in problem for problem in problems))

    def test_foreign_database_name_is_rejected(self):
        values = common.parse_dotenv_text(_rendered())
        self.assertTrue(common.fixture_violations(values, "moeflow_test", STORAGE))
        self.assertFalse(common.DB_NAME_RE.match("moeflow_test"))
        self.assertFalse(common.DB_NAME_RE.match("moeflow_pyiso_test"))
        self.assertTrue(common.DB_NAME_RE.match(common.new_db_name()))

    def test_remote_storage_and_provider_keys_are_rejected(self):
        values = common.parse_dotenv_text(_rendered())
        problems = common.fixture_violations(
            dict(values, OSS_ENDPOINT="https://oss.example.com", STORAGE_TYPE="OSS"),
            DB,
            STORAGE,
        )
        self.assertTrue(any("OSS_ENDPOINT" in problem for problem in problems))
        self.assertTrue(any("STORAGE_TYPE" in problem for problem in problems))

    def test_storage_domain_must_stay_inside_isolated_storage(self):
        values = common.parse_dotenv_text(_rendered())
        problems = common.fixture_violations(
            dict(values, STORAGE_DOMAIN="http://127.0.0.1:5000/storage/"), DB, STORAGE
        )
        self.assertTrue(any("STORAGE_DOMAIN" in problem for problem in problems))

    def test_database_names_are_unique(self):
        names = {common.new_db_name() for _ in range(50)}
        self.assertEqual(len(names), 50)


class CommandLineTests(unittest.TestCase):
    def test_pytest_flags_are_fixed(self):
        self.assertIn("no:dotenv", common.PYTEST_BASE_ARGS)
        self.assertIn("env_files=", common.PYTEST_BASE_ARGS)
        self.assertIn("addopts=", common.PYTEST_BASE_ARGS)

    def test_cli_has_no_pytest_passthrough(self):
        parser = tool.build_parser()
        choices = parser._subparsers._group_actions[0].choices  # argparse internals
        self.assertEqual(sorted(choices), ["endpoints", "plan", "run", "verify-endpoint"])
        for name in ("plan", "run"):
            option_strings = {
                option
                for action in choices[name]._actions
                for option in action.option_strings
            }
            for forbidden in ("--pytest-arg", "--args", "--extra-arg", "--keyword"):
                self.assertNotIn(forbidden, option_strings)

    def test_endpoint_names_are_scoped_to_the_harness(self):
        for name in ("mongo", "moeflow", "shoreline-moeflow-backend", "./mongo"):
            with tempfile.TemporaryDirectory() as tmp:
                args = tool.build_parser().parse_args(
                    [
                        "endpoints",
                        "--action",
                        "stop",
                        "--name",
                        name,
                        "--port",
                        "27321",
                        "--database",
                        "moeflow_pyiso_abcdef12_test",
                        "--manifest",
                        str(Path(tmp) / "manifest.json"),
                    ]
                )
                with self.assertRaises(tool.GateFailure):
                    args.handler(args)

    def test_endpoint_stop_is_a_no_op_when_absent(self):
        calls = []

        def fake_docker(argv, check=True):
            calls.append(argv)
            if argv[0] == "ps":
                return collections.namedtuple("R", "returncode stdout stderr")(0, "", "")
            raise AssertionError(f"unexpected docker call: {argv}")

        original = tool.docker
        tool.docker = fake_docker
        try:
            args = tool.build_parser().parse_args(
                [
                    "endpoints",
                    "--action",
                    "stop",
                    "--name",
                    "shoreline-pyiso-test-mongo",
                    "--port",
                    "27321",
                    "--database",
                    "moeflow_pyiso_abcdef12_test",
                    "--manifest",
                    str(Path(tempfile.gettempdir()) / "pyiso-manifest.json"),
                ]
            )
            self.assertEqual(args.handler(args), 0)
        finally:
            tool.docker = original
        self.assertEqual([call[0] for call in calls], ["ps"])

    def test_manifest_requires_evidence_and_rejects_duplicates(self):
        with tempfile.TemporaryDirectory() as tmp:
            manifest = Path(tmp) / "manifest.json"
            tool.write_json(
                manifest,
                {
                    "tool": "Python-IsolatedSuite",
                    "endpoints": [{"port": 27321, "container": "x", "evidence": ""}],
                },
            )
            with self.assertRaises(tool.GateFailure):
                tool.load_endpoint_manifest(str(manifest))
            tool.write_json(
                manifest,
                {
                    "tool": "Python-IsolatedSuite",
                    "endpoints": [
                        {"port": 27321, "evidence": "a"},
                        {"port": 27321, "evidence": "b"},
                    ],
                },
            )
            with self.assertRaises(tool.GateFailure):
                tool.load_endpoint_manifest(str(manifest))
            tool.write_json(
                manifest,
                {"tool": "SomeOtherTool", "endpoints": [{"port": 1, "evidence": "a"}]},
            )
            with self.assertRaises(tool.GateFailure):
                tool.load_endpoint_manifest(str(manifest))
            self.assertEqual(tool.load_endpoint_manifest(None), {})

    def test_foreign_listener_blocks_and_owned_listener_passes(self):
        owned = {27321: {"port": 27321, "evidence": "docker inspect x"}}
        ports = {"port_mongo": 27321, "port_smtp": 21321}
        real_listening, real_run = tool.port_listening, tool.subprocess.run
        tool.port_listening = lambda port: port == 27321
        try:
            tool.subprocess.run = lambda *a, **k: collections.namedtuple(
                "R", "returncode stdout stderr"
            )(0, "", "")
            self.assertEqual(tool.check_ports(ports, {}), {
                "port_mongo": {
                    "port": 27321,
                    "issue": "port held by a foreign process",
                }
            })
            self.assertEqual(tool.check_ports(ports, owned), {})
        finally:
            tool.port_listening = real_listening
            tool.subprocess.run = real_run

    def test_execution_copy_is_writable_and_matches_the_snapshot(self):
        from pathlib import Path as _Path

        with tempfile.TemporaryDirectory() as tmp:
            source = _Path(tmp) / "src"
            (source / "app" / "translations").mkdir(parents=True)
            (source / "a.py").write_text("print('a')\n", encoding="utf-8")
            (source / "app" / "translations" / "x.po").write_text("msgid ''\n", encoding="utf-8")
            (source / "run.sh").symlink_to("a.py")
            run_root = _Path(tmp) / "runs" / "1"
            run_root.mkdir(parents=True)
            exec_root, digest = tool.materialise_execution_copy(source, run_root)
            if source.joinpath("run.sh").is_symlink():
                self.assertTrue((exec_root / "run.sh").is_symlink())
            self.assertEqual(
                digest, tool.snapshot_manifest_digest(source)
            )
            self.assertNotEqual(exec_root, source)
            # The copy is what gets written by translation compilation.
            (exec_root / "app" / "translations" / "x.mo").write_bytes(b"\x95\x04")
            self.assertFalse((source / "app" / "translations" / "x.mo").exists())
            self.assertNotEqual(digest, tool.snapshot_manifest_digest(exec_root))

    def test_source_paths_are_rewritten_for_the_copy(self):
        # Path objects render with whichever separator the host uses, so the
        # test uses the rendered form instead of a literal POSIX string.
        old, new = Path("/snap/src"), Path("/work/runs/1/exec")
        argv = ["-m", "pytest", "-c", str(old / "pytest.ini"), str(old / "tests")]
        self.assertEqual(
            tool.replace_source(argv, old, new),
            ["-m", "pytest", "-c", str(new / "pytest.ini"), str(new / "tests")],
        )
        env = tool.replace_source_env(
            {"STORAGE_DOMAIN": f"file://{old}/storage/", "X": "1"}, old, new
        )
        self.assertEqual(env["STORAGE_DOMAIN"], f"file://{new}/storage/")
        self.assertEqual(env["X"], "1")

    def test_junit_counts_report_real_numbers_only(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "junit.xml"
            self.assertEqual(tool.junit_counts(path), {})
            path.write_text(
                '<?xml version="1.0"?><testsuites>'
                '<testsuite name="a" tests="10" failures="1" errors="2" skipped="3"/>'
                '<testsuite name="b" tests="5" failures="0" errors="0" skipped="0"/>'
                "</testsuites>",
                encoding="utf-8",
            )
            self.assertEqual(
                tool.junit_counts(path),
                {"tests": 15, "failures": 1, "errors": 2, "skipped": 3, "passed": 9},
            )
            path.write_text("<not-xml", encoding="utf-8")
            self.assertEqual(tool.junit_counts(path), {})

    def test_owned_endpoint_that_died_is_reported(self):
        owned = {27321: {"port": 27321, "evidence": "docker inspect x"}}
        real_listening = tool.port_listening
        tool.port_listening = lambda port: False
        try:
            self.assertEqual(
                tool.check_ports({"port_mongo": 27321}, owned),
                {"port_mongo": {"port": 27321, "issue": "owned endpoint is not running"}},
            )
        finally:
            tool.port_listening = real_listening

    def test_identity_check_failure_blocks_an_owned_port(self):
        owned = {
            27321: {
                "port": 27321,
                "evidence": "docker inspect x",
                "verify": ["--kind", "mongodb", "--port", "27321"],
            }
        }
        real_listening, real_run = tool.port_listening, tool.subprocess.run
        seen = []

        def fake_run(argv, **kwargs):
            seen.append(argv)
            return collections.namedtuple("R", "returncode stdout stderr")(
                1, "databases=['moeflow']", ""
            )

        tool.port_listening = lambda port: True
        try:
            tool.subprocess.run = fake_run
            problems = tool.check_ports(
                {"port_mongo": 27321}, owned, "moeflow_pyiso_abcdef12_test"
            )
        finally:
            tool.port_listening = real_listening
            tool.subprocess.run = real_run
        self.assertEqual(
            problems["port_mongo"]["issue"], "owned endpoint identity check failed"
        )
        self.assertIn("--database", seen[0])
        self.assertIn("moeflow_pyiso_abcdef12_test", seen[0])

    def test_claim_owner_creates_a_record_on_a_fresh_database(self):
        marker = FakeCollection()
        result = tool.claim_owner(marker, "moeflow_pyiso_abcdef12_test", "tok-fresh")
        self.assertEqual(result, {"reclaimed": False, "previous_token": ""})
        stored = marker.documents[0]
        self.assertEqual(stored["tool"], "Python-IsolatedSuite")
        self.assertEqual(stored["token"], "tok-fresh")
        self.assertEqual(stored["database"], "moeflow_pyiso_abcdef12_test")

    def test_claim_owner_reclaims_its_own_leftover_database(self):
        marker = FakeCollection(
            [
                {
                    "marker": "moeflow_pyiso_owner",
                    "tool": "Python-IsolatedSuite",
                    "token": "tok-old",
                    "database": "moeflow_pyiso_abcdef12_test",
                    "claimed_utc": "2026-01-01T00:00:00Z",
                }
            ]
        )
        result = tool.claim_owner(marker, "moeflow_pyiso_abcdef12_test", "tok-new")
        self.assertEqual(result, {"reclaimed": True, "previous_token": "tok-old"})
        self.assertEqual(marker.documents[0]["token"], "tok-new")
        self.assertEqual(
            marker.documents[0]["previous_claimed_utc"], "2026-01-01T00:00:00Z"
        )
        self.assertIn("reclaimed_utc", marker.documents[0])

    def test_claim_owner_refuses_foreign_or_malformed_records(self):
        cases = [
            {"token": "tok-old", "database": "moeflow_pyiso_abcdef12_test"},
            {
                "tool": "some-other-tool",
                "token": "tok-old",
                "database": "moeflow_pyiso_abcdef12_test",
            },
            {
                "tool": "Python-IsolatedSuite",
                "token": "tok-old",
                "database": "moeflow_production",
            },
        ]
        for record in cases:
            marker = FakeCollection([{**record, "marker": "moeflow_pyiso_owner"}])
            with self.assertRaises(tool.GateFailure):
                tool.claim_owner(marker, "moeflow_pyiso_abcdef12_test", "tok-new")

    def test_plan_defaults_are_the_reserved_ports(self):
        args = tool.build_parser().parse_args(
            ["plan", "--legacy-directory", "legacy", "--target", "snap"]
        )
        self.assertEqual(
            (
                args.port_mongo,
                args.port_s3,
                args.port_smtp,
                args.port_mailpit,
                args.port_api,
                args.port_rabbit,
            ),
            (27321, 29321, 21321, 28321, 25321, 26321),
        )

    def test_plan_refuses_snapshot_inside_legacy_workspace(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            legacy = root / "legacy"
            (legacy / ".git").mkdir(parents=True)
            with self.assertRaises(tool.GateFailure):
                tool.compute_plan(
                    argparse.Namespace(
                        legacy_directory=legacy,
                        target=legacy / "snapshot",
                        commit=None,
                        expect_commit=None,
                        replace_snapshot=False,
                        db_name=None,
                        workers=1,
                        timeout_seconds=60,
                        **_config_dict(),
                    )
                )

    def test_plan_refuses_non_git_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            legacy = root / "legacy"
            legacy.mkdir()
            with self.assertRaises(tool.GateFailure):
                tool.compute_plan(
                    argparse.Namespace(
                        legacy_directory=legacy,
                        target=root / "snap",
                        commit=None,
                        expect_commit=None,
                        replace_snapshot=False,
                        db_name=None,
                        workers=1,
                        timeout_seconds=60,
                        **_config_dict(),
                    )
                )

    def test_cli_requires_a_command(self):
        with self.assertRaises(SystemExit) as raised:
            tool.main(["run", "--source", "/x"])
        self.assertEqual(raised.exception.code, 2)

    def test_missing_snapshot_marker_blocks_the_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "source"
            work = Path(tmp) / "work"
            source.mkdir()
            args = argparse.Namespace(
                source=source,
                root=work,
                db_name=DB,
                workers=1,
                timeout_seconds=60,
                dry_run=True,
                json_out=str(work / "summary.json"),
                **_config_dict(),
            )
            self.assertEqual(tool.cmd_run(args), 3)
            summary = json.loads((work / "summary.json").read_text(encoding="utf-8"))
            self.assertEqual(summary["status"], "blocked")
            self.assertIn("plan marker", summary["failure"])


def _config_dict():
    return {
        "port_mongo": 27321,
        "port_s3": 29321,
        "port_smtp": 21321,
        "port_mailpit": 28321,
        "port_api": 25321,
        "port_rabbit": 26321,
    }


class GuardTests(unittest.TestCase):
    def test_guard_conftest_checks_marker_stub_and_database(self):
        text = tool.render_guard_conftest()
        self.assertIn("MOEFLOW_PYISO_GATES", text)
        self.assertIn("__moeflow_pyiso_stub__", text)
        self.assertIn("MOEFLOW_PYISO_DATABASE", text)
        self.assertNotIn("skip", text)
        compile(text, "<guard>", "exec")

    def test_dotenv_stub_only_accepts_the_fixture(self):
        text = tool.dotenv_stub_source(Path("/run/fixture/isolated.env"))
        compile(text, "<sitecustomize>", "exec")
        self.assertIn("isolated.env", text)
        self.assertIn("__moeflow_pyiso_stub__ = True", text)
        self.assertIn("PermissionError", text)
        self.assertIn("dotenv_values", text)
        self.assertNotIn("DOTENV_PATH", text)
        self.assertNotIn("find_dotenv()", text)

    def test_import_guard_detects_preloaded_app(self):
        sys.modules["app"] = type(sys)("app")
        try:
            with self.assertRaises(tool.GateFailure):
                tool.assert_app_not_imported("test")
        finally:
            del sys.modules["app"]
        self.assertEqual(tool.assert_app_not_imported("test"), 0)

    def test_status_is_never_reported_as_pass_without_pytest(self):
        source = TOOL_PATH.read_text(encoding="utf-8")
        self.assertIn('summary["status"] = "pass"', source)
        self.assertIn('summary["status"] = "fail"', source)
        self.assertIn('"status"] = "blocked"', source)


class SnapshotManifestTests(unittest.TestCase):
    def test_manifest_is_order_independent_and_relative(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "a.txt").write_text("a", encoding="utf-8")
            (root / "b").mkdir()
            (root / "b" / "c.txt").write_text("c", encoding="utf-8")
            first = tool.snapshot_manifest_digest(root)
            self.assertEqual(first, tool.snapshot_manifest_digest(root))
            (root / "b" / "c.txt").write_text("changed", encoding="utf-8")
            self.assertNotEqual(first, tool.snapshot_manifest_digest(root))

    def test_manifest_excludes_its_own_marker(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "a.txt").write_text("a", encoding="utf-8")
            first = tool.snapshot_manifest_digest(root)
            (root / ".pyiso-snapshot.json").write_text("{}", encoding="utf-8")
            self.assertEqual(first, tool.snapshot_manifest_digest(root))

    def test_write_json_roundtrip(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "out" / "summary.json"
            tool.write_json(path, {"status": "blocked"})
            self.assertEqual(
                json.loads(path.read_text(encoding="utf-8")), {"status": "blocked"}
            )


class RepositoryHygieneTests(unittest.TestCase):
    def test_repository_has_no_dotenv_or_local_config(self):
        offenders = []
        for current, dirnames, filenames in os.walk(ROOT):
            dirnames[:] = [
                d
                for d in dirnames
                if d not in {".git", "bin", "obj", "node_modules", "__pycache__"}
            ]
            for filename in filenames:
                relative = (Path(current) / filename).relative_to(ROOT).as_posix()
                if common.classify_entry(relative) is not None:
                    offenders.append(relative)
        self.assertEqual(offenders, [])


class HardeningRegressionTests(unittest.TestCase):
    """Regressions for destructive edge cases found in review (2026-11-11)."""

    def test_plan_tar_uses_a_unique_name_and_never_overwrites_neighbours(self):
        # A predictable ``<target>.tar`` could overwrite an unrelated
        # neighbouring file; the plan must use a fresh exclusive temp name and
        # clean it up afterwards.
        legacy_root = Path(tempfile.mkdtemp())
        try:
            legacy = legacy_root / "legacy"
            legacy.mkdir()
            for git_dir in (".git",):
                (legacy / git_dir).mkdir()
            # Minimal workable git repo: one commit on HEAD.
            subprocess.run(
                ["git", "init", "--initial-branch=main", str(legacy)],
                capture_output=True,
                check=True,
            )
            subprocess.run(["git", "-C", str(legacy), "config", "user.email", "t@t"],
                           capture_output=True, check=True)
            subprocess.run(["git", "-C", str(legacy), "config", "user.name", "t"],
                           capture_output=True, check=True)
            (legacy / "a.txt").write_text("hello\n", encoding="utf-8")
            subprocess.run(["git", "-C", str(legacy), "add", "a.txt"],
                           capture_output=True, check=True)
            subprocess.run(["git", "-C", str(legacy), "commit", "-m", "one"],
                           capture_output=True, check=True)

            neighbour = legacy_root / "snap.tar"
            neighbour.write_text("PRECIOUS-NEIGHBOUR", encoding="utf-8")
            tars_before = {p.name for p in legacy_root.glob("*.tar")}

            plan = tool.compute_plan(
                argparse.Namespace(
                    legacy_directory=legacy,
                    target=legacy_root / "snap",
                    commit=None,
                    expect_commit=None,
                    replace_snapshot=False,
                    db_name=DB,
                    workers=1,
                    timeout_seconds=60,
                    **_config_dict(),
                )
            )
            self.assertEqual(plan["status"], "planned")
            self.assertEqual(
                neighbour.read_text(encoding="utf-8"), "PRECIOUS-NEIGHBOUR"
            )
            leftovers = {p.name for p in legacy_root.glob("*.tar")}
            self.assertEqual(leftovers, tars_before)
            self.assertTrue((legacy_root / "snap" / "a.txt").is_file())
        finally:
            shutil.rmtree(legacy_root, ignore_errors=True)

    def _endpoint_args(self, manifest: Path) -> argparse.Namespace:
        return argparse.Namespace(
            action="stop",
            name="shoreline-pyiso-repl-20261111-mongo",
            port=27271,
            image="mongo:8.0",
            database=DB,
            manifest=str(manifest),
            ready_timeout_seconds=10,
        )

    def _stop(self, manifest_payload, live_label="20261110", live_id="id-live"):
        """Drive cmd_endpoints stop with fake docker calls; returns (removed, error)."""
        calls = []

        def fake_docker(argv, check=True):
            calls.append(list(argv))
            if argv[:2] == ["inspect", "-f"]:
                return collections.namedtuple("R", "returncode stdout stderr")(
                    0, f"{live_id}\t{live_label}\n", ""
                )
            return collections.namedtuple("R", "returncode stdout stderr")(0, "", "")

        real_docker, real_exists = tool.docker, tool.docker_container_exists
        tool.docker = fake_docker
        tool.docker_container_exists = lambda name: True
        try:
            with tempfile.TemporaryDirectory() as tmp:
                manifest = Path(tmp) / "endpoints.json"
                tool.write_json(manifest, manifest_payload)
                try:
                    tool.cmd_endpoints(self._endpoint_args(manifest))
                    error = None
                except tool.GateFailure as failure:
                    error = str(failure)
        finally:
            tool.docker, tool.docker_container_exists = real_docker, real_exists
        removed = any(argv[:2] == ["rm", "-f"] for argv in calls)
        return removed, error

    def test_stop_removes_only_the_manifest_recorded_container(self):
        payload = {
            "tool": "Python-IsolatedSuite",
            "endpoints": [
                {
                    "port": 27271,
                    "container": "shoreline-pyiso-repl-20261111-mongo",
                    "container_id": "id-live",
                    "evidence": "docker inspect",
                }
            ],
        }
        removed, error = self._stop(payload)
        self.assertIsNone(error)
        self.assertTrue(removed)

    def test_stop_refuses_a_foreign_container_with_the_same_name(self):
        payload = {
            "tool": "Python-IsolatedSuite",
            "endpoints": [
                {
                    "port": 27271,
                    "container": "shoreline-pyiso-repl-20261111-mongo",
                    "container_id": "id-live",
                    "evidence": "docker inspect",
                }
            ],
        }
        # No harness label: this is somebody else's container that reused the name.
        removed, error = self._stop(payload, live_label="")
        self.assertIn("refusing to remove", error or "")
        self.assertFalse(removed)

    def test_stop_refuses_when_the_manifest_does_not_record_the_container(self):
        payload = {"tool": "Python-IsolatedSuite", "endpoints": []}
        removed, error = self._stop(payload)
        self.assertIn("refusing to remove", error or "")
        self.assertFalse(removed)

    def test_stop_refuses_a_container_id_mismatch(self):
        payload = {
            "tool": "Python-IsolatedSuite",
            "endpoints": [
                {
                    "port": 27271,
                    "container": "shoreline-pyiso-repl-20261111-mongo",
                    "container_id": "id-OTHER",
                    "evidence": "docker inspect",
                }
            ],
        }
        removed, error = self._stop(payload, live_id="id-live")
        self.assertIn("container id mismatch", error or "")
        self.assertFalse(removed)

    def test_second_run_in_the_same_second_gets_a_fresh_directory(self):
        # A colliding run root must never be reused: the existing run's
        # fixture/junit evidence would be silently deleted.
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "source"
            work = Path(tmp) / "work"
            source.mkdir()
            (source / "conftest.py").write_text("", encoding="utf-8")
            (source / "pytest.ini").write_text("", encoding="utf-8")
            (source / ".env.test.sample").write_text(TEMPLATE, encoding="utf-8")
            digest = tool.snapshot_manifest_digest(source)
            tool.write_json(
                source / tool.PLAN_MARKER,
                {"snapshot_sha256_manifest": digest},
            )
            args = argparse.Namespace(
                source=source,
                root=work,
                db_name=DB,
                workers=1,
                timeout_seconds=60,
                **_config_dict(),
            )
            first = tool.prepare_workdir(args, source, work)
            stamp = first["run_root"].name
            # Simulate another run that started within the same second.
            colliding = work / "runs" / stamp
            colliding.mkdir(exist_ok=True)
            (colliding / "junit.xml").write_text("keep me", encoding="utf-8")
            second = tool.prepare_workdir(args, source, work)
            self.assertNotEqual(second["run_root"], colliding)
            self.assertTrue((colliding / "junit.xml").is_file())
            self.assertTrue(second["run_root"].name.startswith(stamp + "-"))


if __name__ == "__main__":
    unittest.main()
