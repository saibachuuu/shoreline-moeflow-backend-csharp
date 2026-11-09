#!/usr/bin/env python3
"""Independent unittest coverage for tools/Audit-LegacyRoutes.py.

The tests build small synthetic Python/C# trees in a temporary directory and
assert exact parser behaviour.  They do not touch the real repositories and do
not run ``dotnet`` or the legacy Flask app.

Run::

    python -m unittest discover -s tools -p "test_audit_legacy_routes.py" -v
"""

import importlib.util
import tempfile
import unittest
from pathlib import Path

TOOL_PATH = Path(__file__).with_name("Audit-LegacyRoutes.py")
_SPEC = importlib.util.spec_from_file_location("audit_legacy_routes", TOOL_PATH)
audit = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(audit)


def write_file(root: Path, rel: str, text: str) -> None:
    path = root / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def make_legacy(root: Path, urls_source: str, views_source: str) -> None:
    write_file(root, "app/core/views.py", (
        "from flask.views import MethodView\n\n"
        "class MoeAPIView(MethodView):\n"
        "    pass\n"
    ))
    write_file(root, "app/apis/views.py", views_source)
    write_file(root, "app/apis/urls.py", urls_source)


def analyze(legacy_root: Path, csharp_root: Path):
    legacy = audit.LegacyAnalyzer(legacy_root).analyze()
    csharp = audit.CSharpAnalyzer(csharp_root).analyze()
    return audit.build_coverage(legacy, csharp)


def route_by(report, method, normalized_path):
    matches = [
        route for route in report["routes"]
        if route["method"] == method
        and route["normalized_path"] == normalized_path
    ]
    assert len(matches) == 1, (method, normalized_path, matches)
    return matches[0]


class SnapshotLoopTest(unittest.TestCase):
    def test_for_loop_suffixes_are_unrolled_per_iteration(self):
        views = (
            "from app.core.views import MoeAPIView\n\n"
            "class AlphaAPI(MoeAPIView):\n"
            "    def get(self):\n"
            "        return 1\n\n"
            "class BetaAPI(MoeAPIView):\n"
            "    def post(self):\n"
            "        return 2\n"
        )
        urls = (
            "from flask import Blueprint\n"
            "from app.apis.views import AlphaAPI, BetaAPI\n\n"
            "bp = Blueprint('bp', __name__, url_prefix='/v1/p')\n"
            "for suffix, view, methods in (\n"
            "    ('one', AlphaAPI, ['GET']),\n"
            "    ('two/<item_id>', BetaAPI, ['POST']),\n"
            "):\n"
            "    bp.add_url_rule(\n"
            "        '/<pid>/' + suffix,\n"
            "        methods=methods + ['OPTIONS'],\n"
            "        view_func=view.as_view(view.__name__),\n"
            "    )\n"
        )
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            legacy_root = root / "legacy"
            csharp_root = root / "csharp"
            csharp_root.mkdir()
            make_legacy(legacy_root, urls, views)
            report = analyze(legacy_root, csharp_root)

        get_route = route_by(report, "GET", "/v1/p/{}/one")
        post_route = route_by(report, "POST", "/v1/p/{}/two/{}")
        self.assertEqual(get_route["handler"], "AlphaAPI")
        self.assertEqual(post_route["handler"], "BetaAPI")
        self.assertEqual(get_route["handler_status"], audit.HANDLER)
        self.assertEqual(post_route["handler_status"], audit.HANDLER)
        self.assertEqual(report["summary"]["core_effective_methods"], 2)
        self.assertEqual(report["summary"]["core_missing_in_csharp"], 2)
        options = [r for r in report["routes"] if r["is_options"]]
        self.assertEqual(len(options), 2)
        self.assertTrue(all(r["in_csharp"] is None for r in options))


class TemplateNormalizeTest(unittest.TestCase):
    def test_flask_and_csharp_placeholders_match(self):
        legacy = audit.normalize_path("/v1/<group_type>/<group_id>/invitations")
        csharp = audit.normalize_path("/v1/{groupType}/{groupId}/invitations")
        self.assertEqual(legacy, "/v1/{}/{}/invitations")
        self.assertEqual(csharp, legacy)

    def test_converter_and_catch_all_are_one_token(self):
        self.assertEqual(
            audit.normalize_path("/v1/docs/<path:path>"),
            "/v1/docs/{}",
        )
        self.assertEqual(
            audit.normalize_path("/v1/files/{*path}"),
            "/v1/files/{}",
        )
        self.assertEqual(
            audit.normalize_path("/v1/items/{id:int}"),
            "/v1/items/{}",
        )

    def test_structure_is_not_collapsed(self):
        self.assertNotEqual(
            audit.normalize_path("/v1/teams/<team_id>/members"),
            audit.normalize_path("/v1/teams/<team_id>"),
        )
        self.assertEqual(audit.normalize_path("/v1/users/"), "/v1/users")


class ConditionalAndDisabledTest(unittest.TestCase):
    def test_conditional_mit_and_commented_route_are_distinct(self):
        views = (
            "from app.core.views import MoeAPIView\n\n"
            "class ConeAPI(MoeAPIView):\n"
            "    def get(self):\n"
            "        return 1\n"
            "    def post(self):\n"
            "        return 2\n"
        )
        urls = (
            "from flask import Blueprint\n"
            "from app.apis.views import ConeAPI\n\n"
            "v1_prefix = '/v1'\n"
            "core = Blueprint('core', __name__, url_prefix=v1_prefix + '/core')\n"
            "core.add_url_rule(\n"
            "    '/ping', methods=['GET', 'OPTIONS'],\n"
            "    view_func=ConeAPI.as_view('core_ping'),\n"
            ")\n"
            "if app_config['MIT_STORAGE_ROOT']:\n"
            "    mit = Blueprint('mit', __name__, url_prefix=v1_prefix + '/mit')\n"
            "    mit.add_url_rule(\n"
            "        '/images', methods=['POST', 'OPTIONS'],\n"
            "        view_func=ConeAPI.as_view('mit_image'),\n"
            "    )\n"
            "# retired = Blueprint('retired', __name__, url_prefix='/v1')\n"
            "# retired.add_url_rule(\n"
            "#     '/gone',\n"
            "#     methods=['GET', 'OPTIONS'],\n"
            "#     view_func=ConeAPI.as_view('gone'),\n"
            "# )\n"
        )
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            legacy_root = root / "legacy"
            csharp_root = root / "csharp"
            csharp_root.mkdir()
            make_legacy(legacy_root, urls, views)
            report = analyze(legacy_root, csharp_root)

        core_route = route_by(report, "GET", "/v1/core/ping")
        mit_route = route_by(report, "POST", "/v1/mit/images")
        disabled_route = route_by(report, "GET", "/gone")
        self.assertEqual(core_route["category"], audit.CORE)
        self.assertEqual(mit_route["category"], audit.CONDITIONAL)
        self.assertIn("MIT_STORAGE_ROOT", mit_route["condition"])
        self.assertEqual(disabled_route["category"], audit.DISABLED)
        self.assertEqual(report["summary"]["core_effective_methods"], 1)
        self.assertEqual(report["summary"]["conditional_effective_methods"], 1)
        self.assertEqual(report["summary"]["disabled_comment_method_entries"], 2)
        self.assertEqual(report["summary"]["core_missing_in_csharp"], 1)


class Missing405Test(unittest.TestCase):
    def test_registered_put_without_handler_is_405_not_missing_business(self):
        views = (
            "from app.core.views import MoeAPIView\n\n"
            "class ApplicationAPI(MoeAPIView):\n"
            "    def patch(self, application_id):\n"
            "        return 1\n"
            "    def delete(self, application_id):\n"
            "        return 2\n"
        )
        urls = (
            "from flask import Blueprint\n"
            "from app.apis.views import ApplicationAPI\n\n"
            "group = Blueprint('group', __name__, url_prefix='/v1')\n"
            "group.add_url_rule(\n"
            "    '/applications/<application_id>',\n"
            "    methods=['PUT', 'DELETE', 'PATCH', 'OPTIONS'],\n"
            "    view_func=ApplicationAPI.as_view('application'),\n"
            ")\n"
        )
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            legacy_root = root / "legacy"
            csharp_root = root / "csharp"
            csharp_root.mkdir()
            make_legacy(legacy_root, urls, views)
            report = analyze(legacy_root, csharp_root)

        put_route = route_by(report, "PUT", "/v1/applications/{}")
        self.assertEqual(put_route["handler_status"], audit.REGISTERED_ONLY_405)
        self.assertEqual(
            report["summary"]["core_registered_only_405"], 1
        )
        self.assertEqual(report["summary"]["core_effective_methods"], 2)
        self.assertEqual(report["summary"]["core_missing_in_csharp"], 2)
        self.assertEqual(
            [item["method"] for item in report["registered_only_405"]],
            ["PUT"],
        )
        missing_methods = {
            item["method"]
            for items in report["missing_by_module"]["core"].values()
            for item in items
        }
        self.assertEqual(missing_methods, {"DELETE", "PATCH"})
        self.assertNotIn("PUT", missing_methods)


class CSharpParserTest(unittest.TestCase):
    def test_literals_constants_groups_and_unresolved(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            legacy_root = root / "legacy"
            csharp_root = root / "csharp"
            legacy_root.mkdir()
            write_file(csharp_root, "src/Program.cs", (
                "const string V1 = \"/v1\";\n"
                "app.MapGet(\"/v1/ping\", () => \"pong\");\n"
                "app.MapGet(V1 + \"/users/{name}\", () => \"u\");\n"
                "var team = app.MapGroup(\"/v1/teams/{teamId}\");\n"
                "team.MapPost(\"/members\", () => \"m\");\n"
                "var dynamicPath = \"/v1/\" + something;\n"
                "app.MapGet(dynamicPath, () => \"d\");\n"
                "app.MapMethods(\"/v1/multi\", new[] { \"GET\", \"PATCH\" }, () => \"x\");\n"
            ))
            report = analyze(legacy_root, csharp_root)

        csharp = report["csharp_routes"]
        keys = {(route["method"], route["normalized_path"]) for route in csharp}
        self.assertEqual(keys, {
            ("GET", "/v1/ping"),
            ("GET", "/v1/users/{}"),
            ("POST", "/v1/teams/{}/members"),
            ("GET", "/v1/multi"),
            ("PATCH", "/v1/multi"),
        })
        self.assertEqual(len(report["csharp_unresolved"]), 1)
        self.assertIn(
            "dynamicPath",
            report["csharp_unresolved"][0]["reason"],
        )


class GenericGroupTemplateTest(unittest.TestCase):
    def test_old_group_template_matches_csharp_generic_group(self):
        views = (
            "from app.core.views import MoeAPIView\n\n"
            "class InvitesAPI(MoeAPIView):\n"
            "    def get(self, group_type, group_id):\n"
            "        return 1\n"
        )
        urls = (
            "from flask import Blueprint\n"
            "from app.apis.views import InvitesAPI\n\n"
            "group = Blueprint('group', __name__, url_prefix='/v1')\n"
            "group.add_url_rule(\n"
            "    '/<group_type>/<group_id>/invitations',\n"
            "    methods=['GET', 'OPTIONS'],\n"
            "    view_func=InvitesAPI.as_view('invitation_list'),\n"
            ")\n"
        )
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            legacy_root = root / "legacy"
            csharp_root = root / "csharp"
            make_legacy(legacy_root, urls, views)
            write_file(csharp_root, "src/InvitationEndpoints.cs", (
                "var group = app.MapGroup(\"/v1/{groupType}/{groupId}/invitations\");\n"
                "group.MapGet(\"\", () => \"ok\");\n"
            ))
            report = analyze(legacy_root, csharp_root)

        route = route_by(report, "GET", "/v1/{}/{}/invitations")
        self.assertTrue(route["in_csharp"])
        self.assertEqual(route["csharp_source"], "src/InvitationEndpoints.cs:2")
        self.assertEqual(report["summary"]["core_matched_in_csharp"], 1)
        self.assertEqual(report["summary"]["core_missing_in_csharp"], 0)


if __name__ == "__main__":
    unittest.main()
