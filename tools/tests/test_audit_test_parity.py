"""Unit tests for tools/Audit-TestParity.py.

The tests use synthetic source strings only.  They do not import the Python app,
read dotenv files, touch a database, or start a server.
"""

from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "Audit-TestParity.py"


def _load_tool():
    name = "audit_test_parity_under_test"
    if name in sys.modules:
        return sys.modules[name]
    spec = importlib.util.spec_from_file_location(name, MODULE_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {MODULE_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


audit = _load_tool()


def _cs_calls(source: str):
    return audit.analyze_csharp_source(source, "Synthetic.cs")


def _py_calls(source: str):
    return audit.analyze_python_source(source, "Synthetic.py")


def _call_dicts(calls):
    return calls


class CSharpLexerTests(unittest.TestCase):
    def test_all_http_methods_and_asjson_variants(self):
        source = r'''
using System.Net.Http;
class T
{
    [Fact]
    public async Task Calls()
    {
        using var c = new HttpClient();
        await c.GetAsync("/v1/a");
        await c.PostAsJsonAsync("/v1/b", new { });
        await c.PutAsJsonAsync("/v1/c", new { });
        await c.PatchAsJsonAsync("/v1/d", new { });
        await c.DeleteAsync("/v1/e");
        await c.GetFromJsonAsync<JsonElement>("/v1/f");
    }
}
'''
        result = _cs_calls(source)
        self.assertEqual(
            [(call["method"], call["route_raw"]) for call in result["calls"]],
            [
                ("GET", "/v1/a"),
                ("POST", "/v1/b"),
                ("PUT", "/v1/c"),
                ("PATCH", "/v1/d"),
                ("DELETE", "/v1/e"),
                ("GET", "/v1/f"),
            ],
        )

    def test_interpolation_keeps_nested_quotes_and_indexers(self):
        source = r'''
class T
{
    [Fact]
    public async Task Calls()
    {
        using var c = new HttpClient();
        var user = new { _id = "x" };
        await c.PutAsJsonAsync($"/v1/admin/users/{user["_id"]}", new { });
        await c.GetAsync($"/v1/projects/{Project["_id"]}/files?status=normal&page=1");
        await c.GetAsync($$"""/v1/raw/{{Project["_id"]}}/x""");
    }
}
'''
        result = _cs_calls(source)
        routes = [call["route_raw"] for call in result["calls"]]
        self.assertIn("/v1/admin/users/{}", routes)
        self.assertIn("/v1/projects/{}/files?status=normal&page=1", routes)
        self.assertIn("/v1/raw/{}/x", routes)

    def test_comments_and_string_contents_are_not_calls(self):
        source = r'''
class T
{
    [Fact]
    public async Task Calls()
    {
        using var c = new HttpClient();
        // await c.GetAsync("/v1/commented");
        /* await c.PostAsync("/v1/blocked", null); */
        var literal = "await c.GetAsync(\"/v1/string\")";
        await c.GetAsync("/v1/real");
    }
}
'''
        result = _cs_calls(source)
        self.assertEqual([call["route_raw"] for call in result["calls"]], ["/v1/real"])

    def test_nested_request_helper_is_not_double_counted(self):
        source = r'''
class T
{
    private static HttpRequestMessage Request(HttpMethod method, string path) =>
        new HttpRequestMessage(method, path);

    [Fact]
    public async Task Calls()
    {
        using var c = new HttpClient();
        using var response = await c.SendAsync(Request(HttpMethod.Post, "/v1/once"));
    }
}
'''
        result = _cs_calls(source)
        self.assertEqual(
            [(call["method"], call["route_raw"]) for call in result["calls"]],
            [("POST", "/v1/once")],
        )

    def test_dynamic_route_is_unresolved_not_dropped(self):
        source = r'''
class T
{
    [Fact]
    public async Task Calls()
    {
        using var c = new HttpClient();
        var route = BuildRoute();
        await c.GetAsync(route);
    }
}
'''
        result = _cs_calls(source)
        self.assertEqual(result["calls"], [])
        self.assertEqual(len(result["unresolved"]), 1)
        self.assertIn("not statically resolvable", result["unresolved"][0]["reason"])

    def test_local_variable_and_http_request_message(self):
        source = r'''
class T
{
    [Fact]
    public async Task Calls()
    {
        using var c = new HttpClient();
        var url = "/v1/local";
        await c.GetAsync(url);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/v1/request");
        await c.SendAsync(request);
    }
}
'''
        result = _cs_calls(source)
        self.assertEqual(
            [(call["method"], call["route_raw"]) for call in result["calls"]],
            [("GET", "/v1/local"), ("PUT", "/v1/request")],
        )

    def test_expression_bodied_route_helper_binds_literal_parameters(self):
        source = r'''
class T
{
    private static string Path(string scope, object id) => $"/v1/{scope}/{id}/applications";

    [Fact]
    public async Task Calls()
    {
        using var c = new HttpClient();
        await c.GetAsync(Path("projects", 1));
    }
}
'''
        result = _cs_calls(source)
        self.assertEqual(
            [call["route_raw"] for call in result["calls"]],
            ["/v1/projects/{}/applications"],
        )

    def test_expression_bodied_property_route_is_used_as_prefix(self):
        source = r'''
class T
{
    private sealed record Team(string Id)
    {
        public string Path => $"/v1/teams/{Id}";
    }

    [Fact]
    public async Task Calls()
    {
        using var c = new HttpClient();
        var team = new Team("x");
        await c.GetAsync(team.Path + "/members");
    }
}
'''
        result = _cs_calls(source)
        self.assertEqual(
            [call["route_raw"] for call in result["calls"]],
            ["/v1/teams/{}/members"],
        )


class RouteMatchingTests(unittest.TestCase):
    def _catalog(self):
        coverage = {
            "routes": [
                {
                    "method": "GET",
                    "path": "/v1/<group_type>/<group_id>/applications",
                    "normalized_path": "/v1/{}/{}/applications",
                    "module": "group",
                    "category": "core",
                    "handler_status": "handler",
                    "handler": "ApplicationListAPI",
                    "source": "app/apis/urls.py",
                    "line": 554,
                },
                {
                    "method": "GET",
                    "path": "/docs/<path:path>",
                    "normalized_path": "/docs/{}",
                    "module": "index",
                    "category": "core",
                    "handler_status": "handler",
                    "handler": "docs",
                    "source": "app/apis/urls.py",
                    "line": 1,
                },
                {
                    "method": "GET",
                    "path": "/v1/files/<int:file_id>",
                    "normalized_path": "/v1/files/{}",
                    "module": "file",
                    "category": "core",
                    "handler_status": "handler",
                    "handler": "FileAPI",
                    "source": "app/apis/urls.py",
                    "line": 2,
                },
            ],
            "csharp_routes": [],
        }
        return audit.build_route_catalog(coverage, None, Path("."), Path("."))

    def test_generic_group_segment_matches_with_observed_literal(self):
        catalog = self._catalog()
        call = {"method": "GET", "route_raw": "/v1/projects/123/applications"}
        associated = audit.associate_call(call, catalog)
        self.assertIsNotNone(associated)
        entry, info = associated
        self.assertEqual(entry["path"], "/v1/{}/{}/applications")
        self.assertEqual(info["match_kind"], "template_wildcard")
        binding = next(
            item for item in info["parameter_bindings"] if item["placeholder"] == "group_type"
        )
        self.assertEqual(binding["value"], "projects")

    def test_query_and_fragment_are_stripped(self):
        path, suffix = audit.strip_query_fragment("/v1/users?status=normal#frag")
        self.assertEqual(path, "/v1/users")
        self.assertEqual(suffix, "?status=normal#frag")
        self.assertTrue(audit._looks_like_http_route("/v1/users"))
        self.assertFalse(audit._looks_like_http_route("/v1{}"))

    def test_path_converter_is_a_catch_all(self):
        route = audit.compile_route_pattern("/docs/<path:path>", "/docs/{}", "python")
        call = audit.compile_call_pattern("/docs/a/b")
        matched = audit.match_route_pattern(route, call)
        self.assertIsNotNone(matched)
        self.assertIn("catchall", matched[1][0])

    def test_int_converter_rejects_non_numeric_literal(self):
        route = audit.compile_route_pattern("/v1/files/<int:file_id>", "/v1/files/{}", "python")
        good = audit.compile_call_pattern("/v1/files/123")
        bad = audit.compile_call_pattern("/v1/files/abc")
        self.assertIsNotNone(audit.match_route_pattern(route, good))
        self.assertIsNone(audit.match_route_pattern(route, bad))

    def test_route_buckets_are_separated(self):
        self.assertEqual(
            audit.classify_route_bucket(
                {"method": "GET", "category": "core", "handler_status": "handler"}
            ),
            "business",
        )
        self.assertEqual(
            audit.classify_route_bucket(
                {"method": "GET", "category": "core", "handler_status": "registered_only_405"}
            ),
            "registered_only_405",
        )
        self.assertEqual(
            audit.classify_route_bucket(
                {"method": "GET", "category": "disabled_comment", "handler_status": "handler"}
            ),
            "disabled_comment",
        )
        self.assertEqual(
            audit.classify_route_bucket(
                {"method": "OPTIONS", "category": "core", "handler_status": "options_not_business"}
            ),
            "options",
        )

    def test_module_vocabulary_is_unified(self):
        self.assertEqual(audit.canonical_module("/v1/notifications/1"), "notification")
        self.assertEqual(audit.canonical_module("/v1/admin/notifications/1"), "notification")
        self.assertEqual(audit.canonical_module("/v1/me/notifications/1"), "notification")
        self.assertEqual(audit.canonical_module("/v1/term-banks/1"), "term_bank")


class PythonAstTests(unittest.TestCase):
    def test_dict_get_is_not_an_http_call_and_self_get_is(self):
        source = '''
class T(MoeAPITestCase):
    def test_calls(self):
        data = self.get("/v1/projects/1/files")
        code = data.json.get("code")
        self.assertEqual(data.status_code, 200, data.json)
'''
        result = _py_calls(source)
        self.assertEqual(len(result["calls"]), 1)
        self.assertEqual(result["calls"][0]["method"], "GET")
        self.assertEqual(result["calls"][0]["route_raw"], "/v1/projects/1/files")

    def test_fstring_format_and_class_attributes(self):
        source = '''
class T(MoeAPITestCase):
    URL = "/v1/notifications"
    def test_calls(self):
        self.get(f"/v1/files/{123}/ocr")
        self.get("/v1/files/{}/sources".format(123))
        self.get(self.URL)
'''
        result = _py_calls(source)
        routes = [call["route_raw"] for call in result["calls"]]
        self.assertIn("/v1/files/{}/ocr", routes)
        self.assertIn("/v1/files/{}/sources", routes)
        self.assertIn("/v1/notifications", routes)

    def test_parameterised_helper_binds_literal_call_arguments(self):
        source = '''
class T(MoeAPITestCase):
    def call(self, method, path, user=None):
        return self.client.open("/v1" + path, method=method)

    def test_wrapper(self):
        self.call("POST", "/notifications")
'''
        result = _py_calls(source)
        self.assertEqual(
            [(call["method"], call["route_raw"]) for call in result["calls"]],
            [("POST", "/v1/notifications")],
        )

    def test_assertion_evidence_is_attached(self):
        source = '''
class T(MoeAPITestCase):
    def test_evidence(self):
        response = self.get("/v1/notifications")
        self.assertEqual(response.status_code, 200)
        self.assertErrorEqual(response, NotificationError)
        self.assertIn("X-Request-ID", response.headers)
'''
        result = _py_calls(source)
        evidence = result["functions"][0]["evidence"]
        self.assertEqual(evidence["expected_status"], [200])
        self.assertIn("NotificationError", evidence["error_assertions"])
        self.assertTrue(evidence["assertion_count"] >= 3)
        self.assertTrue(evidence["header_assertions"])


class FileScanTests(unittest.TestCase):
    def test_build_output_directories_are_skipped(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "tests"
            (root / "obj").mkdir(parents=True)
            (root / "bin").mkdir(parents=True)
            (root / "Real.cs").write_text("class Real {}", encoding="utf-8")
            (root / "obj" / "Generated.cs").write_text("class Generated {}", encoding="utf-8")
            (root / "bin" / "Generated.cs").write_text("class Generated {}", encoding="utf-8")
            files = audit.iter_source_files(root, ".cs")
            self.assertEqual([path.name for path in files], ["Real.cs"])


if __name__ == "__main__":
    unittest.main()
