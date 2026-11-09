"""Safety and determinism checks for the legacy-reference fixture generator.

Run with the private venv that contains pymongo/bson:

    python -m unittest discover -s tools/legacy-reference/tests -v

These tests never import the legacy ``app`` package and never run its test
suite. They only exercise the AST extractor and the checked-in golden JSON.
"""

from __future__ import annotations

import ast
import base64
import hashlib
import json
import sys
import unittest
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parents[1]
CSHARP_ROOT = TOOL_DIR.parents[1]
PYTHON_ROOT = CSHARP_ROOT.parent / "shoreline-moeflow-backend"
FIXTURE = TOOL_DIR / "fixtures" / "goldens.json"

sys.path.insert(0, str(TOOL_DIR))

import reference_harness as harness  # noqa: E402


def sha256_hex(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


class ReferenceHarnessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.goldens = harness.build_goldens(PYTHON_ROOT)

    def test_source_whitelist_rejects_config_env_storage_and_traversal(self) -> None:
        absolute = str((PYTHON_ROOT / "app" / "models" / "file.py").resolve())
        rejected = [
            "app/config.py",
            ".env",
            ".env.local",
            "storage/anything",
            "app/tests/test_presence.py",
            "../shoreline-moeflow-backend/app/models/file.py",
            absolute,
        ]
        for relative in rejected:
            with self.subTest(relative=relative):
                with self.assertRaises(harness.SourcePathError):
                    harness.resolve_source(PYTHON_ROOT, relative)

        accepted = harness.resolve_source(PYTHON_ROOT, "app/models/file.py")
        self.assertTrue(accepted.is_file())
        self.assertEqual(accepted, (PYTHON_ROOT / "app/models/file.py").resolve())

    def test_generation_does_not_import_legacy_app_or_load_network(self) -> None:
        harness.assert_no_app_imported()
        loaded = [
            name
            for name in sys.modules
            if name == "app" or name.startswith("app.")
        ]
        self.assertEqual(loaded, [])
        self.assertIn("no import of the legacy app package", self.goldens["reference"]["extraction"])
        self.assertIn("zero attempts", self.goldens["reference"]["network_guard"])

    def test_fixture_is_deterministic_and_checked_in_copy_matches(self) -> None:
        again = harness.build_goldens(PYTHON_ROOT)
        left = json.dumps(self.goldens, ensure_ascii=False, sort_keys=True)
        right = json.dumps(again, ensure_ascii=False, sort_keys=True)
        self.assertEqual(left, right)

        checked_in = json.loads(FIXTURE.read_text(encoding="utf-8"))
        self.assertEqual(
            json.dumps(checked_in, ensure_ascii=False, sort_keys=True),
            left,
            "fixtures/goldens.json is stale; regenerate it",
        )

    def test_provenance_paths_lines_and_source_hashes_match(self) -> None:
        provenance = self.goldens["provenance"]
        self.assertGreaterEqual(len(provenance), 7)
        allowed = set(harness.ALLOWED_SOURCE_PATHS)
        for entry in provenance:
            path = entry["path"]
            self.assertIn(path, allowed)
            self.assertNotIn("config", path)
            self.assertNotIn("tests", path)
            source_bytes = (PYTHON_ROOT / path).read_bytes()
            self.assertEqual(entry["source_sha256"], sha256_hex(source_bytes))
            for block in entry["blocks"]:
                with self.subTest(path=path, name=block["name"]):
                    extracted = harness.extract_block(
                        PYTHON_ROOT,
                        path,
                        block["name"],
                        block["container"],
                    )
                    self.assertEqual(block["line"], extracted["line"])
                    self.assertEqual(block["end_line"], extracted["end_line"])
                    self.assertEqual(block["block_sha256"], extracted["block_sha256"])
                    self.assertGreater(block["end_line"], 0)

    def test_bson_cases_keep_types_order_and_hashes(self) -> None:
        cases = self.goldens["fixtures"]["bson_typed"]["cases"]
        self.assertGreaterEqual(len(cases), 2)
        seen_types = set()
        for case in cases:
            raw = base64.b64decode(case["b64"])
            self.assertEqual(case["hex"], raw.hex())
            self.assertEqual(case["sha256"], sha256_hex(raw))
            seen_types.update(case["field_types"].values())
            self.assertEqual(
                case["field_order"],
                list(case["decoded"].keys()),
            )
        self.assertTrue(
            {"int32", "int64", "decimal128", "null", "objectId"}.issubset(seen_types)
        )
        ordered = next(case for case in cases if case["name"] == "ordered_mixed")
        self.assertEqual(
            ordered["field_order"],
            ["z", "a", "m", "n", "o", "u", "arr"],
        )

    def test_file_order_uses_python_json_spacing_and_expected_sha(self) -> None:
        fixture = self.goldens["fixtures"]["file_order"]
        canonical = fixture["canonical_json"]
        self.assertIn(", ", canonical)
        self.assertNotIn(",", canonical.replace(", ", ""))
        self.assertTrue(fixture["python_json_default_separators"])
        self.assertEqual(
            fixture["version"], sha256_hex(canonical.encode("utf-8"))
        )
        self.assertEqual(fixture["version"], fixture["json_sha256"])
        self.assertEqual(
            fixture["export_with_manual"]["64b000000000000000000011"],
            "0001_封面 😀.png",
        )
        self.assertEqual(fixture["export_without_manual"], {})

    def test_label_snapshot_chunks_have_verifiable_raw_bson(self) -> None:
        fixture = self.goldens["fixtures"]["label_snapshot"]
        self.assertGreaterEqual(fixture["chunk_count"], 2)
        self.assertEqual(fixture["chunk_count"], len(fixture["chunks"]))
        self.assertEqual(fixture["metadata"]["ready"], True)
        self.assertEqual(fixture["metadata"]["counts"]["files"], 3)
        self.assertGreaterEqual(
            len([chunk for chunk in fixture["chunks"] if chunk["kind"] == "files"]),
            2,
        )
        self.assertRegex(fixture["metadata"]["digest"], r"^[0-9a-f]{64}$")
        for chunk in fixture["chunks"]:
            raw = base64.b64decode(chunk["b64"])
            self.assertEqual(chunk["byte_length"], len(raw))
            self.assertEqual(chunk["sha256"], sha256_hex(raw))
            self.assertEqual(chunk["stored_hash"], chunk["sha256"])
            self.assertTrue(isinstance(chunk["rows"], list))
        for kind, rows in fixture["row_bson"].items():
            with self.subTest(kind=kind):
                self.assertTrue(rows)
                for row in rows:
                    raw = base64.b64decode(row["b64"])
                    self.assertEqual(row["sha256"], sha256_hex(raw))
                    self.assertEqual(row["byte_length"], len(raw))

    def test_fixture_text_contains_unicode_and_html_specials(self) -> None:
        text = json.dumps(self.goldens["fixtures"], ensure_ascii=False)
        for token in ("ß", "σ", "ς", "😀", "<&>", "简体中文"):
            self.assertIn(token, text)

    def test_notification_and_access_fixtures_are_explicit(self) -> None:
        fixture = self.goldens["fixtures"]["notification"]
        self.assertTrue(all(case["ok"] for case in fixture["valid"]))
        self.assertTrue(all(not case["ok"] for case in fixture["invalid"]))
        self.assertEqual(
            fixture["invalid"][0]["error"]["message"],
            "BBCode 标签未正确闭合",
        )
        self.assertEqual(
            fixture["normalize"]["expected"],
            "full width ssσσ😀",
        )
        self.assertTrue(fixture["safe_url"][0]["expected"])
        self.assertFalse(fixture["safe_url"][2]["expected"])
        self.assertEqual(
            {item["case"]: item["expected"] for item in fixture["project_access"]},
            {
                "allowed": True,
                "denied": False,
                "banned": False,
                "missing-project": False,
                "missing-user": False,
                "does-not-exist": False,
            },
        )

    def test_extracted_ast_contains_only_top_level_requested_nodes(self) -> None:
        block = harness.extract_block(
            PYTHON_ROOT,
            "app/services/notification_content.py",
            "parse_body",
        )
        tree = ast.parse(block["source"])
        self.assertEqual(len(tree.body), 1)
        self.assertIsInstance(tree.body[0], ast.FunctionDef)
        self.assertEqual(tree.body[0].name, "parse_body")
        self.assertNotIn("import app", block["source"])


if __name__ == "__main__":
    unittest.main()
