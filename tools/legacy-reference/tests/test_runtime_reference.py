"""Offline validation for the generated Flask runtime reference fixture."""

from __future__ import annotations

import base64
import hashlib
import json
import re
import sys
import unittest
import uuid
from pathlib import Path

TOOLS_ROOT = Path(__file__).resolve().parents[1]
if str(TOOLS_ROOT) not in sys.path:
    sys.path.insert(0, str(TOOLS_ROOT))

import runtime_reference as runtime  # noqa: E402

FIXTURE = TOOLS_ROOT / "fixtures" / "runtime_flask_reference.json"


class RuntimeReferenceFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.raw = FIXTURE.read_text(encoding="utf-8")
        cls.fixture = json.loads(cls.raw)

    def test_schema_and_minimum_cases(self) -> None:
        self.assertEqual(
            self.fixture["schema"], "shoreline.python-runtime-reference/v1"
        )
        cases = self.fixture["cases"]
        self.assertGreaterEqual(len(cases), 5)
        self.assertEqual(
            [case["response"]["status"] for case in cases],
            [200, 401, 200, 400, 200, 200, 200, 405],
        )
        self.assertEqual(
            [case["name"] for case in cases],
            [
                "ping",
                "missing_token_team_list",
                "user_info",
                "team_list_missing_word",
                "team_list",
                "term_bank_list",
                "application_list",
                "method_not_allowed_ping_post",
            ],
        )

    def test_ping_and_405_are_real_http_responses(self) -> None:
        ping = self.fixture["cases"][0]["response"]
        self.assertEqual(ping["body"], {"kind": "text", "value": "pong"})
        self.assertEqual(ping["headers"]["Content-Type"], "text/html; charset=utf-8")
        method_not_allowed = self.fixture["cases"][7]["response"]
        self.assertEqual(method_not_allowed["status"], 405)
        self.assertIn(
            "Method Not Allowed", method_not_allowed["body"]["value"]
        )

    def test_legacy_error_codes(self) -> None:
        missing = self.fixture["cases"][1]["response"]["body"]["value"]
        self.assertEqual(missing["error"], "NeedTokenError")
        self.assertEqual(missing["code"], 1003)
        self.assertEqual(missing["message"], "需要令牌")

        empty = self.fixture["cases"][3]["response"]["body"]["value"]
        self.assertEqual(empty["error"], "RequestDataEmptyError")
        self.assertEqual(empty["code"], 110)

    def test_user_team_term_and_application_shapes(self) -> None:
        user = self.fixture["cases"][2]["response"]["body"]["value"]
        self.assertEqual(user["id"], self.fixture["fixed_inputs"]["ids"]["owner"])
        self.assertEqual(user["name"], "Reference Owner")
        self.assertEqual(user["avatar"], "")
        self.assertEqual(user["default_display_name"], "Reference Owner")

        team_payload = self.fixture["cases"][4]["response"]["body"]["value"]
        self.assertIsInstance(team_payload, list)
        self.assertEqual(len(team_payload), 1)
        team = team_payload[0]
        self.assertEqual(team["id"], self.fixture["fixed_inputs"]["ids"]["team"])
        self.assertEqual(team["name"], "Reference Team")
        self.assertTrue(team["joined"])
        self.assertEqual(team["user_count"], 2)
        self.assertEqual(team["default_role"], self.fixture["fixed_inputs"]["ids"]["beginner_role"])

        term_bank_payload = self.fixture["cases"][5]["response"]["body"]["value"]
        self.assertIsInstance(term_bank_payload, list)
        self.assertEqual(len(term_bank_payload), 1)
        term_bank = term_bank_payload[0]
        self.assertEqual(term_bank["name"], "Reference Glossary")
        self.assertEqual(term_bank["source_language"]["code"], "ja")
        self.assertEqual(term_bank["target_language"]["code"], "en")

        application_payload = self.fixture["cases"][6]["response"]["body"]["value"]
        self.assertIsInstance(application_payload, list)
        self.assertEqual(len(application_payload), 1)
        application = application_payload[0]
        self.assertEqual(application["status"], 2)
        self.assertEqual(application["group_type"], "team")
        self.assertEqual(application["user_role"]["system_code"], "member")
        self.assertEqual(
            application["id"], self.fixture["fixed_inputs"]["ids"]["application"]
        )
        self.assertTrue(application["create_time"].startswith("2026-01-01T00:00:00"))

    def test_seed_bson_hashes_and_read_only_snapshots(self) -> None:
        for name, collection in self.fixture["seed_collections"].items():
            self.assertEqual(collection["count"], len(collection["documents"]))
            for document in collection["documents"]:
                raw = base64.b64decode(document["bson_base64"])
                self.assertEqual(raw.hex(), document["bson_hex"])
                self.assertEqual(
                    hashlib.sha256(raw).hexdigest(), document["sha256"]
                )
                self.assertEqual(len(raw), document["bytes"])
        counts = {
            name: collection["count"]
            for name, collection in self.fixture["seed_collections"].items()
        }
        self.assertEqual(
            counts,
            {
                "application": 1,
                "language": 2,
                "team": 1,
                "team_member": 2,
                "team_role": 5,
                "term_bank": 1,
                "user": 2,
            },
        )
        for case in self.fixture["cases"]:
            self.assertEqual(
                case["database_before"], case["database_after"], case["name"]
            )

    def test_no_secrets_and_owned_database_guard(self) -> None:
        self.assertRegex(
            self.fixture["database"]["name"],
            re.compile(r"^shoreline_reference_[0-9a-f]{32}$"),
        )
        self.assertTrue(self.fixture["database"]["owned"])
        self.assertEqual(
            self.fixture["database"]["cleanup"],
            "dropped and verified before fixture write",
        )
        self.assertNotIn("Bearer ", self.raw)
        self.assertNotIn("reference-only-synthetic-secret", self.raw)
        self.assertNotIn("__pycache__", self.raw)
        self.assertNotIn('"path": "app/config.py"', self.raw)
        self.assertNotIn('"path": "app/migrations/', self.raw)
        self.assertNotIn("app/static/", self.raw)
        self.assertNotIn("app/templates/", self.raw)
        provenance = self.fixture["source_provenance"]
        self.assertTrue(provenance)
        for entry in provenance:
            self.assertTrue(entry["path"].startswith("app/"))
            self.assertRegex(entry["sha256"], re.compile(r"^[0-9a-f]{64}$"))
            self.assertGreaterEqual(entry["bytes"], 0)

    def test_bootstrap_and_coverage_are_explicit(self) -> None:
        bootstrap = self.fixture["bootstrap"]
        self.assertEqual(bootstrap["storage"], "LOCAL_STORAGE")
        self.assertEqual(bootstrap["celery"], "memory")
        self.assertEqual(bootstrap["mail"], "disabled")
        self.assertEqual(bootstrap["optional_modules"], "disabled")
        self.assertIn("127.0.0.1:27128", bootstrap["network_guard"])
        self.assertIn("no production config read", bootstrap["synthetic_config"])
        self.assertGreaterEqual(self.fixture["coverage"]["cases"], 5)
        self.assertTrue(self.fixture["coverage"]["not_covered"])

    def test_request_authorization_is_redacted(self) -> None:
        for case in self.fixture["cases"]:
            self.assertIn(
                case["request"]["authorization"], {"absent", "redacted"}
            )
        self.assertEqual(
            self.fixture["cases"][2]["request"]["authorization"], "redacted"
        )
        self.assertEqual(
            self.fixture["cases"][0]["request"]["authorization"], "absent"
        )


class RuntimeGuardTests(unittest.TestCase):
    def test_mongo_port_allows_default_and_owned_range_only(self) -> None:
        self.assertEqual(runtime.validate_mongo_port(27128), 27128)
        self.assertEqual(runtime.validate_mongo_port(27281), 27281)
        self.assertEqual(runtime.validate_mongo_port(27289), 27289)
        for port in (0, 27017, 27280, 27290, 29281, 65536):
            with self.subTest(port=port), self.assertRaises(ValueError):
                runtime.validate_mongo_port(port)

    def test_database_guard_rejects_mismatch_without_query(self) -> None:
        class Database:
            name = "shoreline_reference_" + "0" * 32

        with self.assertRaises(RuntimeError):
            runtime.assert_owned_database(Database(), "shoreline_reference_" + "1" * 32)

        class NoName:
            pass

        with self.assertRaises(RuntimeError):
            runtime.assert_owned_database(NoName(), "shoreline_reference_" + "1" * 32)

    def test_drop_guard_verifies_absence_before_success(self) -> None:
        db_name = "shoreline_reference_" + uuid.uuid4().hex

        class Client:
            def __init__(self) -> None:
                self.names = {db_name}

            def drop_database(self, name: str) -> None:
                self.names.discard(name)

            def list_database_names(self) -> list[str]:
                return sorted(self.names)

        client = Client()
        runtime.drop_owned_database(client, db_name)
        self.assertNotIn(db_name, client.names)
        with self.assertRaises(RuntimeError):
            runtime.drop_owned_database(client, "shoreline_reference_short")

    def test_temp_parent_must_stay_beneath_approved_root(self) -> None:
        owned = runtime.APPROVED_TEMP_ROOT / f"guard-{uuid.uuid4().hex}"
        created = runtime.resolve_temp_parent(owned)
        try:
            self.assertEqual(created, owned.resolve())
        finally:
            created.rmdir()
        with self.assertRaises(RuntimeError):
            runtime.resolve_temp_parent(runtime.APPROVED_TEMP_ROOT.parent)

    def test_output_is_absolute_before_chdir(self) -> None:
        resolved = runtime.resolve_output_path(Path("relative-fixture.json"))
        self.assertTrue(resolved.is_absolute())
        self.assertEqual(resolved, (Path.cwd() / "relative-fixture.json").resolve())


if __name__ == "__main__":
    unittest.main()
