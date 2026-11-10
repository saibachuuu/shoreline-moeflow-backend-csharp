#!/usr/bin/env python3
"""Offline parity audit between Python and C# HTTP tests.

This tool is intentionally static and read-only:

* It parses Python test source with ``ast``; it never imports ``app`` and never
  reads dotenv/config files, credentials, databases, or network services.
* It scans C# test source lexically; it never runs ``dotnet``.
* Route registration comes from ``tools/Audit-LegacyRoutes.py`` so the same
  normalization and static route discovery is reused.
* Dynamic/unresolved HTTP calls are reported explicitly instead of being
  silently dropped.
"""

from __future__ import annotations

import argparse
import ast
import importlib.util
import json
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any

HTTP_METHODS = {"GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"}
PY_CALL_NAMES = {"get", "post", "put", "patch", "delete", "options", "open"}
CS_METHOD_NAMES = {"GetAsync", "PostAsync", "PutAsync", "PatchAsync", "DeleteAsync",
                   "OptionsAsync", "SendAsync", "SendJsonAsync"}

PY_TEST_ROOT_NAMES = ("tests",)
CS_TEST_ROOT_NAMES = ("tests",)

MODULE_RULES = [
    ("archive_import", lambda p: "import-from-archive" in p or "import-task" in p),
    ("partner_search", lambda p: "partner-search" in p),
    ("storage_pool", lambda p: "storage-pool" in p),
    ("ziteng_partner", lambda p: "ziteng" in p),
    ("auth", lambda p: p in {"/v1/user/token"} or "/v1/users" in p and "token" in p),
    ("admin", lambda p: p.startswith("/v1/admin")),
    ("users", lambda p: p.startswith("/v1/users") or p.startswith("/v1/user")),
    ("team-insight", lambda p: "/insight" in p),
    ("presence", lambda p: "/presence" in p),
    ("file", lambda p: p.startswith("/v1/files")),
    ("source", lambda p: p.startswith("/v1/sources")),
    ("tip", lambda p: p.startswith("/v1/tips")),
    ("translation", lambda p: p.startswith("/v1/translations")),
    ("notifications", lambda p: "notification" in p),
]


def classify_module(path: str) -> str:
    normalized = path.lower()
    for name, rule in MODULE_RULES:
        try:
            if rule(normalized):
                return name
        except Exception:
            continue
    if normalized.startswith("/v1/teams"):
        return "team"
    if normalized.startswith("/v1/projects"):
        return "project"
    if normalized.startswith("/v1/term"):
        return "term"
    if normalized == "/ping" or normalized.startswith("/health"):
        return "index"
    first = normalized.strip("/").split("/", 2)
    return first[1] if len(first) > 1 else (first[0] or "root")


def normalize_path(path: str) -> str:
    value = str(path).strip()
    value = re.sub(r"<[^<>]+>", "{}", value)
    value = re.sub(r"\{[^{}]*\}", "{}", value)
    value = re.sub(r"/{2,}", "/", value)
    if len(value) > 1:
        value = value.rstrip("/")
    return value


def load_route_audit(csharp_root: Path):
    tool_path = csharp_root / "tools" / "Audit-LegacyRoutes.py"
    if not tool_path.is_file():
        raise SystemExit(f"route audit tool not found: {tool_path}")
    spec = importlib.util.spec_from_file_location("audit_legacy_routes", tool_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def analyze_routes(csharp_root: Path, python_root: Path):
    mod = load_route_audit(csharp_root)
    legacy = mod.LegacyAnalyzer(python_root)
    legacy.analyze()
    csharp = mod.CSharpAnalyzer(csharp_root)
    csharp.analyze()
    coverage = mod.build_coverage(legacy, csharp)
    return legacy, csharp, coverage


def python_path_from_expr(expr: ast.AST) -> tuple[str | None, bool]:
    if isinstance(expr, ast.Constant) and isinstance(expr.value, str):
        return expr.value, True
    if isinstance(expr, ast.JoinedStr):
        parts = []
        for value in expr.values:
            if isinstance(value, ast.Constant) and isinstance(value.value, str):
                parts.append(value.value)
            elif isinstance(value, ast.FormattedValue):
                parts.append("{}")
            else:
                return None, False
        return "".join(parts), True
    if isinstance(expr, ast.BinOp) and isinstance(expr.op, ast.Add):
        left, lok = python_path_from_expr(expr.left)
        right, rok = python_path_from_expr(expr.right)
        if left is not None and right is not None and lok and rok:
            return left + right, True
        return None, False
    return None, False


class PythonHttpCollector(ast.NodeVisitor):
    def __init__(self, filepath: Path):
        self.filepath = filepath
        self.current_function = None
        self.calls = []  # list of dicts
        self.functions = []  # list of dicts with assertions
        self._function_stack = []

    def visit_FunctionDef(self, node):
        self._function_stack.append(node.name)
        prev = self.current_function
        self.current_function = node.name
        assertions = []
        http_calls = []
        # We'll collect during traversal by tagging; simpler: first collect assertions here.
        for child in ast.walk(node):
            if isinstance(child, ast.Call) and isinstance(child.func, ast.Attribute):
                if "assert" in child.func.attr.lower():
                    assertions.append(child.func.attr)
            if isinstance(child, ast.Call) and isinstance(child.func, ast.Attribute) and child.func.attr in PY_CALL_NAMES:
                if child.args:
                    route, resolved = python_path_from_expr(child.args[0])
                    if route is not None and "/" in route:
                        http_calls.append((child.lineno, child.func.attr, route, resolved))
        self.functions.append({
            "name": node.name,
            "line": node.lineno,
            "assertions": sorted(set(assertions)),
            "assertion_count": len(assertions),
            "http_calls": [{"line": ln, "method_attr": ma, "route": r, "resolved": res} for ln, ma, r, res in http_calls],
        })
        self.generic_visit(node)
        self.current_function = prev
        self._function_stack.pop()

    def visit_Call(self, node):
        if isinstance(node.func, ast.Attribute) and node.func.attr in PY_CALL_NAMES and node.args:
            method = None
            attr = node.func.attr
            if attr in {"get", "post", "put", "patch", "delete", "options"}:
                method = attr.upper()
            elif attr == "open":
                method = "GET"
                for kw in node.keywords:
                    if kw.arg == "method" and isinstance(kw.value, ast.Constant) and isinstance(kw.value.value, str):
                        method = kw.value.value.upper()
            route, resolved = python_path_from_expr(node.args[0])
            if method in HTTP_METHODS and route is not None and route.startswith("/") and "/" in route:
                self.calls.append({
                    "file": str(self.filepath),
                    "line": node.lineno,
                    "function": self.current_function,
                    "method": method,
                    "route_raw": route,
                    "route_normalized": normalize_path(route),
                    "resolved": bool(resolved),
                    "call": ast.unparse(node) if hasattr(ast, "unparse") else None,
                })
            elif method in HTTP_METHODS:
                receiver = ast.unparse(node.func.value) if hasattr(ast, "unparse") else ""
                expr_text = ast.unparse(node.args[0]) if hasattr(ast, "unparse") else ""
                if (receiver.lower().find("self") >= 0 or receiver.lower().find("client") >= 0
                        or receiver.lower().find("http") >= 0 or "/" in expr_text or "v1" in expr_text):
                    self.calls.append({
                        "file": str(self.filepath),
                        "line": node.lineno,
                        "function": self.current_function,
                        "method": method,
                        "route_raw": ast.unparse(node.args[0]) if hasattr(ast, "unparse") else None,
                        "route_normalized": None,
                        "resolved": False,
                        "unresolved": True,
                        "call": ast.unparse(node) if hasattr(ast, "unparse") else None,
                    })
        self.generic_visit(node)


def collect_python_tests(python_root: Path):
    calls = []
    functions = []
    files = []
    unresolved = []
    test_root = python_root / "tests"
    if not test_root.is_dir():
        return calls, functions, files, unresolved
    for path in sorted(test_root.rglob("*.py")):
        files.append(str(path))
        try:
            tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        except SyntaxError as exc:
            unresolved.append({"file": str(path), "line": exc.lineno, "reason": f"syntax error: {exc.msg}"})
            continue
        collector = PythonHttpCollector(path)
        collector.visit(tree)
        for call in collector.calls:
            if call.get("unresolved"):
                unresolved.append({
                    "side": "python",
                    "file": call["file"],
                    "line": call["line"],
                    "function": call["function"],
                    "method": call["method"],
                    "expression": call["route_raw"],
                    "reason": "dynamic or non-literal route expression",
                })
            else:
                calls.append(call)
        for fn in collector.functions:
            if fn["http_calls"]:
                functions.append({
                    "side": "python",
                    "file": str(path),
                    "function": fn["name"],
                    "line": fn["line"],
                    "assertions": fn["assertions"],
                    "assertion_count": fn["assertion_count"],
                    "http_call_count": len(fn["http_calls"]),
                })
    return calls, functions, files, unresolved


def extract_cs_strings_and_methods(text: str):
    # Returns list of (method, route, line, pattern)
    results = []
    seen = set()
    patterns = [
        (r'\.(Get|Post|Put|Patch|Delete|Options)Async\s*\(\s*(?:\$?)"((?:[^"\\]|\\.)*)"', "method_async"),
        (r'new\s+HttpRequestMessage\s*\(\s*HttpMethod\.(Get|Post|Put|Patch|Delete|Options)\s*,\s*(?:\$?)"((?:[^"\\]|\\.)*)"', "http_request_message"),
        (r'HttpMethod\.(Get|Post|Put|Patch|Delete|Options)\s*,\s*(?:\$?)"((?:[^"\\]|\\.)*)"', "httpmethod_comma"),
        (r'\bRequest\s*\(\s*HttpMethod\.(Get|Post|Put|Patch|Delete|Options)\s*,\s*(?:\$?)"((?:[^"\\]|\\.)*)"', "request_helper"),
        (r'\bSendJsonAsync\s*\(\s*HttpMethod\.(Get|Post|Put|Patch|Delete|Options)\s*,\s*(?:\$?)"((?:[^"\\]|\\.)*)"', "sendjson_helper"),
    ]
    for pattern, kind in patterns:
        for match in re.finditer(pattern, text, re.IGNORECASE):
            method = match.group(1).upper()
            route = match.group(2)
            line = text.count("\n", 0, match.start()) + 1
            key = (line, method, route, kind)
            if key in seen:
                continue
            seen.add(key)
            results.append({"line": line, "method": method, "route_raw": route, "pattern": kind})
    return results, seen


def collect_csharp_tests(csharp_root: Path):
    calls = []
    functions = []
    files = []
    unresolved = []
    test_root = csharp_root / "tests"
    if not test_root.is_dir():
        return calls, functions, files, unresolved
    for path in sorted(test_root.rglob("*.cs")):
        files.append(str(path))
        text = path.read_text(encoding="utf-8")
        extracted, seen_lines = extract_cs_strings_and_methods(text)
        for item in extracted:
            route = item["route_raw"]
            # unescape simple \"
            route = route.replace('\\"', '"')
            if not route.strip().startswith("/"):
                continue
            normalized = normalize_path(re.sub(r"\{[^{}]*\}", "{}", route))
            calls.append({
                "file": str(path),
                "line": item["line"],
                "method": item["method"],
                "route_raw": route,
                "route_normalized": normalized,
                "resolved": True,
                "pattern": item["pattern"],
            })
        # unresolved candidates: lines mentioning HTTP call tokens but no extracted route on that line
        extracted_lines = {c["line"] for c in extracted} if False else set()
        for idx, line in enumerate(text.splitlines(), 1):
            if re.search(r'(GetAsync|PostAsync|PutAsync|PatchAsync|DeleteAsync|OptionsAsync|SendAsync|HttpRequestMessage|HttpMethod\.)', line):
                if idx not in {x["line"] for x in extracted}:
                    if ("HttpMethod." in line or "HttpRequestMessage" in line or "SendAsync" in line
                            or re.search(r'"/', line)):
                        unresolved.append({
                            "side": "csharp",
                            "file": str(path),
                            "line": idx,
                            "expression": line.strip(),
                            "reason": "HTTP call or HttpMethod usage without extracted literal route",
                        })
        # function-level assertion evidence
        # Find method declarations and slice
        for m in re.finditer(r'(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?(?:Task(?:<[^>]*>)?|void|IActionResult)\s+([A-Za-z0-9_]+)\s*\(', text):
            name = m.group(1)
            start = m.start()
            # find next method or class end by next method declaration
            next_m = None
            for m2 in re.finditer(r'(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?(?:Task(?:<[^>]*>)?|void|IActionResult)\s+[A-Za-z0-9_]+\s*\(', text[m.end():]):
                next_m = m2
                break
            end = (m.end() + next_m.start()) if next_m else len(text)
            body = text[start:end]
            http_in_body = [c for c in extracted if start <= text[:c["line"]-1].count("\n")*0+0 or True]
            # count http calls by line range approximate:
            lines_start = text[:start].count("\n") + 1
            lines_end = text[:end].count("\n") + 1
            body_calls = [c for c in extracted if lines_start <= c["line"] <= lines_end]
            if not body_calls:
                continue
            assertions = re.findall(r'Assert\.\w+', body)
            status_codes = re.findall(r'HttpStatusCode\.\w+', body)
            functions.append({
                "side": "csharp",
                "file": str(path),
                "function": name,
                "line": lines_start,
                "assertions": sorted(set(assertions)),
                "assertion_count": len(assertions),
                "status_code_assertions": sorted(set(status_codes)),
                "http_call_count": len(body_calls),
            })
    return calls, functions, files, unresolved


def main():
    parser = argparse.ArgumentParser(description="Offline Python/C# HTTP test parity audit")
    parser.add_argument("--python-root", required=True)
    parser.add_argument("--csharp-root", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    python_root = Path(args.python_root).resolve()
    csharp_root = Path(args.csharp_root).resolve()
    output = Path(args.output).resolve()

    legacy, csharp, coverage = analyze_routes(csharp_root, python_root)

    py_calls, py_functions, py_files, py_unresolved = collect_python_tests(python_root)
    cs_calls, cs_functions, cs_files, cs_unresolved = collect_csharp_tests(csharp_root)

    # Build registered route sets
    legacy_routes = []
    for r in coverage.get("routes", []):
        legacy_routes.append((r["method"], r["normalized_path"], r.get("module"), r.get("category"), r.get("handler_status")))
    csharp_routes = []
    for r in coverage.get("csharp_routes", []):
        csharp_routes.append((r["method"], r["normalized_path"], r.get("source"), r.get("line")))
    for r in coverage.get("csharp_extra_routes", []):
        csharp_routes.append((r["method"], r["normalized_path"], r.get("source"), r.get("line")))

    legacy_route_set = {(m, p) for m, p, *_ in legacy_routes}
    csharp_route_set = {(m, p) for m, p, *_ in csharp_routes}

    # Map test calls to registered routes
    def enrich(call, route_set):
        key = (call["method"], call["route_normalized"])
        call["registered"] = key in route_set
        return call

    for c in py_calls:
        enrich(c, legacy_route_set)
    for c in cs_calls:
        enrich(c, csharp_route_set)

    # Route-level parity
    route_keys = sorted(legacy_route_set | csharp_route_set)
    route_parity = []
    for method, path in route_keys:
        py_hits = [c for c in py_calls if c["method"] == method and c["route_normalized"] == path]
        cs_hits = [c for c in cs_calls if c["method"] == method and c["route_normalized"] == path]
        module = classify_module(path)
        # try legacy module
        for m, p, mod, cat, status in legacy_routes:
            if (m, p) == (method, path) and mod:
                module = mod if mod else module
                break
        route_parity.append({
            "method": method,
            "path": path,
            "module": module,
            "in_python_routes": (method, path) in legacy_route_set,
            "in_csharp_routes": (method, path) in csharp_route_set,
            "python_test_hits": len(py_hits),
            "csharp_test_hits": len(cs_hits),
            "python_test_locations": [{"file": c["file"], "line": c["line"], "function": c["function"]} for c in py_hits],
            "csharp_test_locations": [{"file": c["file"], "line": c["line"], "pattern": c["pattern"]} for c in cs_hits],
            "uncovered_in_python": len(py_hits) == 0 and (method, path) in legacy_route_set,
            "uncovered_in_csharp": len(cs_hits) == 0 and (method, path) in csharp_route_set,
        })

    # Module summary
    modules = defaultdict(lambda: {
        "python_test_files": set(),
        "csharp_test_files": set(),
        "python_http_calls": 0,
        "csharp_http_calls": 0,
        "python_registered_routes": 0,
        "csharp_registered_routes": 0,
        "python_uncovered_routes": 0,
        "csharp_uncovered_routes": 0,
        "python_assertion_calls": 0,
        "csharp_assertion_calls": 0,
        "python_test_functions_with_http": 0,
        "csharp_test_functions_with_http": 0,
    })
    for rp in route_parity:
        mod = rp["module"]
        modules[mod]["python_registered_routes"] += 1 if rp["in_python_routes"] else 0
        modules[mod]["csharp_registered_routes"] += 1 if rp["in_csharp_routes"] else 0
        modules[mod]["python_uncovered_routes"] += 1 if rp["uncovered_in_python"] else 0
        modules[mod]["csharp_uncovered_routes"] += 1 if rp["uncovered_in_csharp"] else 0
    for c in py_calls:
        modules[classify_module(c["route_normalized"])]["python_http_calls"] += 1
    for c in cs_calls:
        modules[classify_module(c["route_normalized"])]["csharp_http_calls"] += 1
    for f in py_functions:
        # classify by file/module? Use route? We'll just count global in "python" bucket? Instead attach to modules by file name heuristic.
        pass
    for f in py_functions:
        modules["__python_functions__"]["python_test_functions_with_http"] += 1
        modules["__python_functions__"]["python_assertion_calls"] += f["assertion_count"]
    for f in cs_functions:
        modules["__csharp_functions__"]["csharp_test_functions_with_http"] += 1
        modules["__csharp_functions__"]["csharp_assertion_calls"] += f["assertion_count"]

    # file sets
    for c in py_calls:
        modules[classify_module(c["route_normalized"])]["python_test_files"].add(c["file"])
    for c in cs_calls:
        modules[classify_module(c["route_normalized"])]["csharp_test_files"].add(c["file"])

    # Convert sets to counts/lists
    module_summary = {}
    for mod, data in sorted(modules.items()):
        if mod.startswith("__"):
            continue
        module_summary[mod] = {
            "python_test_files": sorted(data["python_test_files"]),
            "csharp_test_files": sorted(data["csharp_test_files"]),
            "python_http_calls": data["python_http_calls"],
            "csharp_http_calls": data["csharp_http_calls"],
            "python_registered_routes": data["python_registered_routes"],
            "csharp_registered_routes": data["csharp_registered_routes"],
            "python_uncovered_routes": data["python_uncovered_routes"],
            "csharp_uncovered_routes": data["csharp_uncovered_routes"],
        }

    unresolved_all = py_unresolved + cs_unresolved

    report = {
        "schema": "shoreline.test-parity/v1",
        "tool": "tools/Audit-TestParity.py",
        "inputs": {
            "python_root": str(python_root),
            "csharp_root": str(csharp_root),
        },
        "summary": {
            "python_test_files_scanned": len(py_files),
            "csharp_test_files_scanned": len(cs_files),
            "python_http_calls_resolved": len(py_calls),
            "csharp_http_calls_resolved": len(cs_calls),
            "python_http_calls_unresolved": len(py_unresolved),
            "csharp_http_calls_unresolved": len(cs_unresolved),
            "registered_routes_union": len(route_keys),
            "routes_with_python_tests": sum(1 for rp in route_parity if rp["python_test_hits"] > 0),
            "routes_with_csharp_tests": sum(1 for rp in route_parity if rp["csharp_test_hits"] > 0),
            "python_uncovered_registered_routes": sum(1 for rp in route_parity if rp["uncovered_in_python"]),
            "csharp_uncovered_registered_routes": sum(1 for rp in route_parity if rp["uncovered_in_csharp"]),
            "python_assertion_calls_total": sum(f["assertion_count"] for f in py_functions),
            "csharp_assertion_calls_total": sum(f["assertion_count"] for f in cs_functions),
            "python_test_functions_with_http": len(py_functions),
            "csharp_test_functions_with_http": len(cs_functions),
        },
        "module_summary": module_summary,
        "python_functions_with_http": py_functions,
        "csharp_functions_with_http": cs_functions,
        "route_parity": route_parity,
        "python_http_calls": py_calls,
        "csharp_http_calls": cs_calls,
        "unresolved": unresolved_all,
        "notes": [
            "Static audit only. Python tests were parsed, not imported or executed.",
            "C# tests were scanned lexically, not compiled or executed by this tool.",
            "Route registration comes from tools/Audit-LegacyRoutes.py.",
            "Unresolved entries are dynamic or non-literal HTTP calls and are intentionally retained.",
        ],
    }

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    md_path = output.with_suffix(".md")
    md_path.write_text(render_markdown(report), encoding="utf-8")
    print(f"wrote {output}")
    print(f"wrote {md_path}")


def render_markdown(report: dict) -> str:
    lines = []
    lines.append("# Python vs C# HTTP test parity")
    lines.append("")
    lines.append("Static, offline audit. No Python app import, no dotenv, no database, no network.")
    lines.append("")
    lines.append("## Summary")
    lines.append("")
    s = report["summary"]
    for key in [
        "python_test_files_scanned", "csharp_test_files_scanned",
        "python_http_calls_resolved", "csharp_http_calls_resolved",
        "python_http_calls_unresolved", "csharp_http_calls_unresolved",
        "registered_routes_union", "routes_with_python_tests", "routes_with_csharp_tests",
        "python_uncovered_registered_routes", "csharp_uncovered_registered_routes",
        "python_assertion_calls_total", "csharp_assertion_calls_total",
        "python_test_functions_with_http", "csharp_test_functions_with_http",
    ]:
        lines.append(f"- {key}: {s[key]}")
    lines.append("")
    lines.append("## Module summary")
    lines.append("")
    lines.append("| module | py files | cs files | py calls | cs calls | py routes | cs routes | py uncovered | cs uncovered |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for mod, data in report["module_summary"].items():
        lines.append(
            f"| {mod} | {len(data['python_test_files'])} | {len(data['csharp_test_files'])} | "
            f"{data['python_http_calls']} | {data['csharp_http_calls']} | "
            f"{data['python_registered_routes']} | {data['csharp_registered_routes']} | "
            f"{data['python_uncovered_routes']} | {data['csharp_uncovered_routes']} |"
        )
    lines.append("")
    lines.append("## Route parity (uncovered first)")
    lines.append("")
    lines.append("| method | path | module | py routes | cs routes | py tests | cs tests |")
    lines.append("|---|---|---|---:|---:|---:|---:|")
    for rp in sorted(report["route_parity"], key=lambda x: (x["uncovered_in_csharp"] is False, x["uncovered_in_python"] is False, x["module"], x["path"])):
        lines.append(
            f"| {rp['method']} | `{rp['path']}` | {rp['module']} | "
            f"{'yes' if rp['in_python_routes'] else 'no'} | {'yes' if rp['in_csharp_routes'] else 'no'} | "
            f"{rp['python_test_hits']} | {rp['csharp_test_hits']} |"
        )
    lines.append("")
    lines.append("## Unresolved dynamic/non-literal calls")
    lines.append("")
    if not report["unresolved"]:
        lines.append("None.")
    else:
        for u in report["unresolved"]:
            lines.append(f"- `{u['side']}` {u['file']}:{u['line']} `{u.get('method','')}` {u.get('expression','')}")
    lines.append("")
    return "\n".join(lines)


if __name__ == "__main__":
    main()
