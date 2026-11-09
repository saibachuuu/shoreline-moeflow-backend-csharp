#!/usr/bin/env python3
"""Offline route audit: Flask legacy routes vs C# minimal-API routes.

This tool is intentionally static and read-only:

* It parses Python source with ``ast``.  It never imports the legacy app and
  never reads deployment configuration, dotenv files or credentials.
* It parses C# source with a small lexical scanner.  It never runs
  ``dotnet build`` and never executes the API.
* Anything that cannot be resolved statically is reported explicitly in the
  ``unresolved`` sections.  This tool does NOT claim 100% automation.

Examples::

    python tools/Audit-LegacyRoutes.py \
        --legacy-directory ../shoreline-moeflow-backend \
        --csharp-directory . \
        --output docs/route-coverage.json

    python tools/Audit-LegacyRoutes.py \
        --legacy-directory ../shoreline-moeflow-backend \
        --csharp-directory . \
        --output docs/route-coverage.md

When ``--output`` ends in ``.json`` a sibling ``.md`` is also written (and
vice versa).  Use ``--no-companion`` to write only the requested file.
"""

from __future__ import annotations

import argparse
import ast
import json
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path

BUSINESS_METHODS = {"GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"}
ALL_METHODS = BUSINESS_METHODS | {"OPTIONS"}
KNOWN_EXTERNAL_BASES = {"MethodView", "View", "object"}

# Route source categories.
CORE = "core"
CONDITIONAL = "conditional"
OPTIONAL = "optional"
DISABLED = "disabled_comment"

# Handler status.
HANDLER = "handler"
REGISTERED_ONLY_405 = "registered_only_405"
HANDLER_UNKNOWN = "unknown"
OPTIONS_STATUS = "options_not_business"


# ---------------------------------------------------------------------------
# Path normalization
# ---------------------------------------------------------------------------

_FLASK_PARAM_RE = re.compile(r"<[^<>]+>")
_CS_PARAM_RE = re.compile(r"\{[^{}]*\}")


def normalize_path(path: str) -> str:
    """Normalize route placeholders without changing the path structure.

    ``/v1/<group_type>/<group_id>/x`` and ``/v1/{groupType}/{groupId}/x`` both
    become ``/v1/{}/{}/x``.  Flask converters (``<path:path>``) and C#
    constraints/catch-alls (``{id:int}``, ``{*path}``) are one placeholder
    token each.
    """
    if path is None:
        return None
    value = str(path).strip()
    value = _FLASK_PARAM_RE.sub("{}", value)
    value = _CS_PARAM_RE.sub("{}", value)
    value = re.sub(r"/{2,}", "/", value)
    if len(value) > 1:
        value = value.rstrip("/")
    if not value:
        value = "/"
    if not value.startswith("/"):
        value = "/" + value
    return value


def join_paths(prefix, rule) -> str:
    prefix = prefix or ""
    rule = rule or ""
    if not prefix:
        return rule if rule.startswith("/") else "/" + rule
    if not rule:
        return prefix
    return prefix.rstrip("/") + (rule if rule.startswith("/") else "/" + rule)


# ---------------------------------------------------------------------------
# Legacy Python (Flask) analysis
# ---------------------------------------------------------------------------


class Unresolved(Exception):
    def __init__(self, reason: str, node=None):
        super().__init__(reason)
        self.reason = reason
        self.node = node


class ClassRef:
    __slots__ = ("module", "name", "endpoint")

    def __init__(self, module, name, endpoint=None):
        self.module = module
        self.name = name
        self.endpoint = endpoint


class BlueprintRef:
    __slots__ = ("name", "prefix", "line", "module", "condition")

    def __init__(self, name, prefix, line, module, condition=None):
        self.name = name
        self.prefix = prefix
        self.line = line
        self.module = module
        self.condition = condition


class ClassInfo:
    __slots__ = ("module", "name", "file", "line", "bases", "methods")

    def __init__(self, module, name, file, line, bases, methods):
        self.module = module
        self.name = name
        self.file = file
        self.line = line
        self.bases = bases
        self.methods = methods


def _module_name_for_path(rel_path: Path) -> str:
    parts = list(rel_path.with_suffix("").parts)
    if parts and parts[-1] == "__init__":
        parts = parts[:-1]
    return ".".join(parts)


def _is_package_path(rel_path: Path) -> bool:
    return rel_path.name == "__init__.py"


class LegacyAnalyzer:
    def __init__(self, root):
        self.root = Path(root)
        self.classes = {}
        self.classes_by_name = defaultdict(list)
        self.imports = {}
        self.package_flags = {}
        self.routes = []
        self.blueprints = []
        self.unresolved = []
        self.module_specs = []
        self._route_seen = set()

    # -- source indexing -------------------------------------------------

    def analyze(self):
        if not self.root.is_dir():
            raise SystemExit(f"legacy directory not found: {self.root}")

        index_roots = [self.root / "app" / "apis",
                       self.root / "app" / "modules",
                       self.root / "app" / "core"]
        for base in index_roots:
            if not base.is_dir():
                continue
            for path in sorted(base.rglob("*.py")):
                rel = path.relative_to(self.root)
                if "__pycache__" in rel.parts:
                    continue
                self._index_classes(path, rel)

        route_files = []
        urls = self.root / "app" / "apis" / "urls.py"
        notification = self.root / "app" / "apis" / "notification.py"
        if urls.is_file():
            route_files.append((urls, CORE, None, None))
        if notification.is_file():
            route_files.append((notification, CORE, None, "notification"))
        modules_root = self.root / "app" / "modules"
        if modules_root.is_dir():
            for module_dir in sorted(p for p in modules_root.iterdir() if p.is_dir()):
                if module_dir.name == "__pycache__":
                    continue
                init_file = module_dir / "__init__.py"
                if not init_file.is_file():
                    continue
                condition = (
                    "optional module: directory app/modules/"
                    f"{module_dir.name} exists and __init__.py exports MODULE "
                    "(factory.discover()); no environment-variable switch"
                )
                doc = ""
                try:
                    tree = ast.parse(init_file.read_text(encoding="utf-8"))
                    doc = ast.get_docstring(tree) or ""
                    doc = doc.strip().splitlines()[0] if doc.strip() else ""
                except Exception:
                    pass
                self.module_specs.append({
                    "name": module_dir.name,
                    "directory": str(init_file.parent.relative_to(self.root)).replace("\\", "/"),
                    "enable_condition": condition,
                    "docstring_first_line": doc,
                })
                route_files.append((init_file, OPTIONAL, condition, module_dir.name))
                api_file = module_dir / "api.py"
                if api_file.is_file():
                    route_files.append((api_file, OPTIONAL, condition, module_dir.name))

        for path, category, condition, module_hint in route_files:
            self._analyze_route_file(path, category, condition, module_hint)

        self._extract_disabled_comment_routes(self.root / "app" / "apis" / "urls.py")
        self._dedupe_routes()
        self._dedupe_blueprints()
        return self

    def _index_classes(self, path: Path, rel: Path):
        module = _module_name_for_path(rel)
        is_pkg = _is_package_path(rel)
        try:
            tree = ast.parse(path.read_text(encoding="utf-8"))
        except Exception as exc:  # pragma: no cover - defensive
            self.unresolved.append({
                "file": str(rel).replace("\\", "/"),
                "line": 0,
                "reason": f"could not parse Python file: {exc}",
            })
            return
        self.package_flags[module] = is_pkg
        imports = {}
        for node in ast.walk(tree):
            if isinstance(node, ast.ImportFrom):
                target_module = self._resolve_import_module(
                    module, is_pkg, node.module, node.level
                )
                for alias in node.names:
                    imports[alias.asname or alias.name] = (target_module, alias.name)
            elif isinstance(node, ast.Import):
                for alias in node.names:
                    imports[alias.asname or alias.name] = (alias.name, None)
        self.imports[module] = imports
        for node in tree.body:
            if isinstance(node, ast.ClassDef):
                methods = set()
                for item in node.body:
                    if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
                        methods.add(item.name)
                bases = []
                for base in node.bases:
                    bases.append(ast.unparse(base))
                info = ClassInfo(
                    module=module,
                    name=node.name,
                    file=str(rel).replace("\\", "/"),
                    line=node.lineno,
                    bases=bases,
                    methods=methods,
                )
                self.classes[(module, node.name)] = info
                self.classes_by_name[node.name].append(info)

    @staticmethod
    def _resolve_import_module(current_module, is_pkg, name, level):
        if not level:
            return name or ""
        current_package = current_module if is_pkg else current_module.rsplit(".", 1)[0]
        parts = current_package.split(".") if current_package else []
        if level > 1:
            parts = parts[: -(level - 1)] if level - 1 <= len(parts) else []
        base = ".".join(parts)
        if name:
            return base + "." + name if base else name
        return base

    def lookup_class(self, module, name, seen=None):
        """Return (ClassInfo or None, status). status: found/external/unknown."""
        if not name:
            return None, "unknown"
        key = (module, name)
        seen = seen or set()
        if key in seen:
            return None, "unknown"
        seen.add(key)
        if key in self.classes:
            return self.classes[key], "found"
        target = self.imports.get(module, {}).get(name)
        if target:
            target_module, target_name = target
            if target_name is None:
                return None, "unknown"
            info, status = self.lookup_class(target_module, target_name, seen)
            if info or status == "found":
                return info, status
            if target_name in KNOWN_EXTERNAL_BASES:
                return None, "external"
            return None, "unknown"
        if name in KNOWN_EXTERNAL_BASES or name.split(".")[-1] in KNOWN_EXTERNAL_BASES:
            return None, "external"
        return None, "unknown"

    def class_handlers(self, module, name, seen=None):
        """Return (methods, definitive) for a view class, following local bases."""
        seen = seen or set()
        info, status = self.lookup_class(module, name, seen)
        if info is None:
            return set(), False
        methods = set(info.methods)
        definitive = True
        for base_text in info.bases:
            base_name = base_text.split(".")[-1]
            base_info, base_status = self.lookup_class(info.module, base_name, seen)
            if base_info is not None:
                sub_methods, sub_definitive = self.class_handlers(
                    base_info.module, base_info.name, seen
                )
                methods |= sub_methods
                definitive = definitive and sub_definitive
            elif base_status == "external":
                continue
            else:
                definitive = False
        return methods, definitive

    def find_class_anywhere(self, name):
        matches = self.classes_by_name.get(name, [])
        if len(matches) == 1:
            return matches[0]
        if matches:
            for info in matches:
                if info.module.startswith("app.apis."):
                    return info
        return matches[0] if matches else None

    # -- expression evaluation ------------------------------------------

    def eval_expr(self, node, env, module):
        if isinstance(node, ast.Constant):
            return node.value
        if isinstance(node, ast.Name):
            if node.id in env:
                return env[node.id]
            info, status = self.lookup_class(module, node.id)
            if info is not None:
                return ClassRef(info.module, info.name)
            if node.id in KNOWN_EXTERNAL_BASES:
                return ClassRef("__external__", node.id)
            raise Unresolved(f"unresolved name '{node.id}'", node)
        if isinstance(node, ast.Attribute):
            value = self.eval_expr(node.value, env, module)
            if isinstance(value, ClassRef) and node.attr == "__name__":
                return value.name
            raise Unresolved(f"unresolved attribute '{ast.unparse(node)}'", node)
        if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Add):
            left = self.eval_expr(node.left, env, module)
            right = self.eval_expr(node.right, env, module)
            if isinstance(left, str) and isinstance(right, str):
                return left + right
            if isinstance(left, list) and isinstance(right, list):
                return left + right
            if isinstance(left, tuple) and isinstance(right, tuple):
                return left + right
            raise Unresolved(f"unsupported '+' operands in '{ast.unparse(node)}'", node)
        if isinstance(node, (ast.List, ast.Tuple, ast.Set)):
            return [self.eval_expr(item, env, module) for item in node.elts]
        if isinstance(node, ast.Call):
            func = node.func
            if isinstance(func, ast.Attribute) and func.attr == "as_view":
                view = self.eval_expr(func.value, env, module)
                if isinstance(view, ClassRef):
                    endpoint = None
                    if node.args:
                        try:
                            endpoint = self.eval_expr(node.args[0], env, module)
                        except Unresolved:
                            endpoint = None
                    return ClassRef(view.module, view.name, endpoint)
                raise Unresolved("as_view receiver is not a class", node)
            raise Unresolved(f"unresolved call '{ast.unparse(node)}'", node)
        if isinstance(node, ast.JoinedStr):
            parts = []
            for value in node.values:
                if isinstance(value, ast.Constant):
                    parts.append(str(value.value))
                else:
                    raise Unresolved("dynamic f-string", node)
            return "".join(parts)
        if isinstance(node, ast.Subscript):
            raise Unresolved(
                f"cannot read runtime configuration/expression '{ast.unparse(node)}'",
                node,
            )
        raise Unresolved(f"unsupported expression '{ast.unparse(node)}'", node)

    # -- statement walking ----------------------------------------------

    def _analyze_route_file(self, path, category, condition, module_hint):
        rel = path.relative_to(self.root)
        # The source module is always derived from the file path so imports and
        # class references resolve.  ``module_hint`` only documents which
        # optional module owns the file; route grouping derives the same name
        # from ``app.modules.<name>`` in ``_add_rule``.
        module = _module_name_for_path(rel)
        try:
            tree = ast.parse(path.read_text(encoding="utf-8"))
        except Exception as exc:  # pragma: no cover - defensive
            self.unresolved.append({
                "file": str(rel).replace("\\", "/"),
                "line": 0,
                "reason": f"could not parse Python file: {exc}",
            })
            return
        env = {}
        # Seed module-level constants/blueprints first so functions defined later
        # can resolve names such as ``blueprint``.
        self._walk(tree.body, env, category, condition, module, rel, seed_only=True)
        self._walk(tree.body, env, category, condition, module, rel, seed_only=False)

    def _walk(self, statements, env, category, condition, module, rel, seed_only):
        for stmt in statements:
            if isinstance(stmt, ast.Assign):
                try:
                    value = self._eval_assignment(stmt.value, env, module, condition)
                except Unresolved as exc:
                    # Only Blueprint definitions are route-relevant; ignoring
                    # unrelated module-level expressions (Celery config,
                    # ModuleSpec, ORM setup) keeps the report focused.
                    if _is_blueprint_call(stmt.value):
                        self.unresolved.append({
                            "file": str(rel).replace("\\", "/"),
                            "line": getattr(stmt, "lineno", 0),
                            "reason": exc.reason,
                        })
                    value = None
                if seed_only:
                    if value is not None:
                        for target in stmt.targets:
                            if isinstance(target, ast.Name):
                                env[target.id] = value
                    continue
                if value is not None:
                    for target in stmt.targets:
                        if isinstance(target, ast.Name):
                            env[target.id] = value
                continue
            if isinstance(stmt, ast.AnnAssign) and stmt.value is not None:
                if seed_only:
                    try:
                        value = self._eval_assignment(stmt.value, env, module, condition)
                    except Unresolved:
                        value = None
                    if value is not None and isinstance(stmt.target, ast.Name):
                        env[stmt.target.id] = value
                    continue
                continue
            if seed_only:
                continue
            if isinstance(stmt, ast.For):
                self._walk_for(stmt, env, category, condition, module, rel)
            elif isinstance(stmt, ast.If):
                self._walk_if(stmt, env, category, condition, module, rel)
            elif isinstance(stmt, (ast.FunctionDef, ast.AsyncFunctionDef)):
                # Walk only helpers that actually register routes; handler
                # bodies contain arbitrary business logic whose unresolved
                # expressions are irrelevant to route inventory.  The local
                # ``route()`` wrapper is handled when its call sites are seen.
                if stmt.name == "route" or not _contains_registration(stmt.body):
                    continue
                local_env = dict(env)
                # Functions commonly take the module-level blueprint as a
                # parameter; seed matching names so add_url_rule resolves.
                for arg in list(stmt.args.args) + list(stmt.args.kwonlyargs):
                    if arg.arg in env:
                        local_env[arg.arg] = env[arg.arg]
                self._walk(stmt.body, local_env, category, condition, module, rel, False)
            elif isinstance(stmt, ast.Expr) and isinstance(stmt.value, ast.Call):
                self._handle_call(stmt.value, env, category, condition, module, rel)
            elif isinstance(stmt, (ast.With, ast.AsyncWith)):
                self._walk(stmt.body, env, category, condition, module, rel, False)
            elif isinstance(stmt, ast.Try):
                self._walk(stmt.body, env, category, condition, module, rel, False)
                for handler in stmt.handlers:
                    self._walk(handler.body, env, category, condition, module, rel, False)
                self._walk(stmt.orelse, env, category, condition, module, rel, False)
                self._walk(stmt.finalbody, env, category, condition, module, rel, False)
            elif isinstance(stmt, ast.ClassDef):
                # View class bodies do not register routes.
                continue
            else:
                # Nested blocks such as ``while`` are uncommon here; still
                # recurse into any statement body we understand.
                body = getattr(stmt, "body", None)
                if isinstance(body, list):
                    self._walk(body, env, category, condition, module, rel, False)

    def _eval_assignment(self, node, env, module, condition):
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) \
                and node.func.id == "Blueprint":
            name = self.eval_expr(node.args[0], env, module) if node.args else None
            prefix = None
            for kw in node.keywords:
                if kw.arg == "url_prefix":
                    prefix = self.eval_expr(kw.value, env, module)
            if not isinstance(name, str):
                raise Unresolved("Blueprint name is not a literal", node)
            bp = BlueprintRef(name, prefix, node.lineno, module, condition)
            self.blueprints.append(bp)
            return bp
        return self.eval_expr(node, env, module)

    def _walk_for(self, stmt, env, category, condition, module, rel):
        route_relevant = _contains_registration(stmt.body)
        try:
            iterable = self.eval_expr(stmt.iter, env, module)
        except Unresolved as exc:
            if route_relevant:
                self.unresolved.append({
                    "file": str(rel).replace("\\", "/"),
                    "line": stmt.lineno,
                    "reason": f"for-loop iterable not statically resolvable: {exc.reason}",
                })
            return
        if not isinstance(iterable, (list, tuple)):
            if route_relevant:
                self.unresolved.append({
                    "file": str(rel).replace("\\", "/"),
                    "line": stmt.lineno,
                    "reason": "for-loop iterable is not a static sequence",
                })
            return
        for item in iterable:
            local_env = dict(env)
            if isinstance(stmt.target, ast.Tuple):
                if not isinstance(item, (list, tuple)) or len(stmt.target.elts) != len(item):
                    if route_relevant:
                        self.unresolved.append({
                            "file": str(rel).replace("\\", "/"),
                            "line": stmt.lineno,
                            "reason": "for-loop tuple unpacking shape mismatch",
                        })
                    continue
                for target, value in zip(stmt.target.elts, item):
                    if isinstance(target, ast.Name):
                        local_env[target.id] = value
            elif isinstance(stmt.target, ast.Name):
                local_env[stmt.target.id] = item
            self._walk(stmt.body, local_env, category, condition, module, rel, False)

    def _walk_if(self, stmt, env, category, condition, module, rel):
        try:
            test_value = self.eval_expr(stmt.test, env, module)
            if not isinstance(test_value, bool):
                raise Unresolved("test is not a static boolean", stmt.test)
            chosen = stmt.body if test_value else stmt.orelse
            self._walk(chosen, env, category, condition, module, rel, False)
            return
        except Unresolved:
            pass
        test_text = ast.unparse(stmt.test)
        nested_condition = test_text
        if condition:
            nested_condition = f"{condition} and ({test_text})"
        nested_category = CONDITIONAL if category == CORE else category
        self._walk(stmt.body, env, nested_category, nested_condition, module, rel, False)
        if stmt.orelse:
            else_condition = f"not ({test_text})"
            if condition:
                else_condition = f"{condition} and {else_condition}"
            self._walk(stmt.orelse, env, nested_category, else_condition, module, rel, False)

    def _handle_call(self, call, env, category, condition, module, rel):
        func = call.func
        # notification.py uses a local ``route(path, cls, methods, name, admin=)``
        # helper that wraps ``notification.add_url_rule``.
        if isinstance(func, ast.Name) and func.id == "route":
            self._handle_route_helper(call, env, category, condition, module, rel)
            return
        if not isinstance(func, ast.Attribute) or func.attr != "add_url_rule":
            return
        try:
            receiver = self.eval_expr(func.value, env, module)
        except Unresolved as exc:
            self.unresolved.append({
                "file": str(rel).replace("\\", "/"),
                "line": call.lineno,
                "reason": f"add_url_rule receiver unresolved: {exc.reason}",
            })
            return
        if not isinstance(receiver, BlueprintRef):
            self.unresolved.append({
                "file": str(rel).replace("\\", "/"),
                "line": call.lineno,
                "reason": "add_url_rule receiver is not a Blueprint",
            })
            return
        self._add_rule(call, receiver, env, category, condition, module, rel)

    def _handle_route_helper(self, call, env, category, condition, module, rel):
        if len(call.args) < 3:
            self.unresolved.append({
                "file": str(rel).replace("\\", "/"),
                "line": call.lineno,
                "reason": "route() helper call has fewer than 3 arguments",
            })
            return
        blueprints = [bp for bp in self.blueprints if bp.name == "notification"]
        blueprint = blueprints[0] if blueprints else None
        if blueprint is None:
            self.unresolved.append({
                "file": str(rel).replace("\\", "/"),
                "line": call.lineno,
                "reason": "route() helper used before notification Blueprint is defined",
            })
            return
        self._add_rule(call, blueprint, env, category, condition, module, rel,
                       positional=(0, 1, 2))

    def _add_rule(self, call, blueprint, env, category, condition, module, rel,
                  positional=None):
        try:
            if positional:
                path_node = call.args[positional[0]]
                view_node = call.args[positional[1]]
                methods_node = call.args[positional[2]]
            else:
                path_node = call.args[0] if call.args else None
                view_node = None
                methods_node = None
                for kw in call.keywords:
                    if kw.arg == "view_func":
                        view_node = kw.value
                    elif kw.arg == "methods":
                        methods_node = kw.value
                if path_node is None:
                    raise Unresolved("add_url_rule has no rule argument", call)
            path = self.eval_expr(path_node, env, module)
            if not isinstance(path, str):
                raise Unresolved("route path is not a string literal", path_node)
            if methods_node is None:
                methods = ["GET"]
            else:
                methods = self.eval_expr(methods_node, env, module)
                if not isinstance(methods, (list, tuple)):
                    raise Unresolved("methods argument is not a static list", methods_node)
                methods = [str(m).upper() for m in methods]

            handler = None
            handler_status_default = HANDLER_UNKNOWN
            handler_module = None
            if view_node is not None:
                view = self.eval_expr(view_node, env, module)
                if isinstance(view, ClassRef):
                    handler = view.name
                    handler_module = view.module
                    defined, definitive = self.class_handlers(view.module, view.name)
                    if not definitive:
                        self.unresolved.append({
                            "file": str(rel).replace("\\", "/"),
                            "line": call.lineno,
                            "reason": (
                                f"handler inheritance for {view.name} contains an "
                                "unknown base class; method support is not definitive"
                            ),
                        })
            else:
                raise Unresolved("add_url_rule has no view_func", call)

            full_path = join_paths(blueprint.prefix, path)
            for method in methods:
                if method not in ALL_METHODS:
                    self.unresolved.append({
                        "file": str(rel).replace("\\", "/"),
                        "line": call.lineno,
                        "reason": f"unknown HTTP method '{method}'",
                    })
                    continue
                if method == "OPTIONS":
                    status = OPTIONS_STATUS
                elif handler is None:
                    status = HANDLER_UNKNOWN
                elif handler_module is not None:
                    defined, definitive = self.class_handlers(handler_module, handler)
                    if method.lower() in defined:
                        status = HANDLER
                    elif definitive:
                        status = REGISTERED_ONLY_405
                    else:
                        status = HANDLER_UNKNOWN
                else:
                    status = HANDLER_UNKNOWN
                self._record_route({
                    "method": method,
                    "path": full_path,
                    "normalized_path": normalize_path(full_path),
                    "handler": handler or "<unresolved>",
                    "handler_module": handler_module,
                    "source": str(rel).replace("\\", "/"),
                    "line": call.lineno,
                    "category": category,
                    "condition": condition,
                    "module": self._route_module(module, category, blueprint),
                    "blueprint": blueprint.name,
                    "handler_status": status,
                    "is_options": method == "OPTIONS",
                })
        except Unresolved as exc:
            self.unresolved.append({
                "file": str(rel).replace("\\", "/"),
                "line": getattr(exc.node, "lineno", call.lineno),
                "reason": exc.reason,
            })

    @staticmethod
    def _route_module(module, category, blueprint):
        if category == OPTIONAL and module.startswith("app.modules."):
            parts = module.split(".")
            if len(parts) >= 3:
                return parts[2]
        return blueprint.name

    def _dedupe_blueprints(self):
        seen = set()
        unique = []
        for blueprint in self.blueprints:
            key = (blueprint.module, blueprint.line, blueprint.name)
            if key in seen:
                continue
            seen.add(key)
            unique.append(blueprint)
        self.blueprints = unique

    def _record_route(self, route):
        key = (route["method"], route["path"], route["source"], route["line"],
               route["category"], route["handler"])
        if key in self._route_seen:
            return
        self._route_seen.add(key)
        self.routes.append(route)

    # -- commented-out registrations ------------------------------------

    def _extract_disabled_comment_routes(self, path):
        if not path.is_file():
            return
        text = path.read_text(encoding="utf-8")
        raw_lines = text.splitlines()
        start_re = re.compile(r"^\s*#\s*[\w.]*\.?add_url_rule\s*\(")
        i = 0
        while i < len(raw_lines):
            match = start_re.match(raw_lines[i])
            if not match:
                i += 1
                continue
            block = []
            depth = 0
            started = False
            j = i
            while j < len(raw_lines):
                line = raw_lines[j]
                stripped = line.lstrip()
                if not stripped.startswith("#"):
                    break
                code = re.sub(r"^\s*#\s?", "", line)
                block.append(code)
                for ch in code:
                    if ch == "(":
                        depth += 1
                        started = True
                    elif ch == ")":
                        depth -= 1
                j += 1
                if started and depth <= 0:
                    break
            if block:
                self._parse_disabled_block(block, i + 1, path)
            i = max(j, i + 1)

    def _parse_disabled_block(self, block, start_line, path):
        code = "\n".join(line if line.strip() else "" for line in block)
        try:
            tree = ast.parse(code)
        except Exception as exc:
            self.unresolved.append({
                "file": str(path.relative_to(self.root)).replace("\\", "/"),
                "line": start_line,
                "reason": f"commented add_url_rule block could not be parsed: {exc}",
            })
            return
        rel = str(path.relative_to(self.root)).replace("\\", "/")
        for node in ast.walk(tree):
            if not isinstance(node, ast.Call):
                continue
            func = node.func
            if not isinstance(func, ast.Attribute) or func.attr != "add_url_rule":
                continue
            receiver = ast.unparse(func.value) if func.value is not None else "?"
            path_value = None
            methods = []
            handler = None
            if node.args and isinstance(node.args[0], ast.Constant):
                path_value = node.args[0].value
            for kw in node.keywords:
                if kw.arg == "methods" and isinstance(kw.value, (ast.List, ast.Tuple)):
                    methods = [elt.value for elt in kw.value.elts
                               if isinstance(elt, ast.Constant)]
                if kw.arg == "view_func":
                    text = ast.unparse(kw.value)
                    handler = text.split(".")[0]
            if path_value is None:
                continue
            methods = [str(m).upper() for m in methods]
            if "OPTIONS" not in methods:
                methods.append("OPTIONS")
            for method in methods:
                status = OPTIONS_STATUS if method == "OPTIONS" else HANDLER_UNKNOWN
                if method != "OPTIONS" and handler:
                    info = self.find_class_anywhere(handler)
                    if info is not None:
                        defined, definitive = self.class_handlers(info.module, info.name)
                        if method.lower() in defined:
                            status = HANDLER
                        elif definitive:
                            status = REGISTERED_ONLY_405
                self._record_route({
                    "method": method,
                    "path": path_value,
                    "normalized_path": normalize_path(path_value),
                    "handler": handler or "<commented>",
                    "handler_module": None,
                    "source": rel,
                    "line": start_line + node.lineno - 1,
                    "category": DISABLED,
                    "condition": "commented out in source",
                    "module": receiver,
                    "blueprint": receiver,
                    "handler_status": status,
                    "is_options": method == "OPTIONS",
                })

    def _dedupe_routes(self):
        seen = set()
        unique = []
        for route in self.routes:
            key = (route["method"], route["path"], route["category"],
                   route["source"], route["line"])
            if key in seen:
                continue
            seen.add(key)
            unique.append(route)
        self.routes = unique


# ---------------------------------------------------------------------------
# C# minimal API analysis
# ---------------------------------------------------------------------------


CALL_RE = re.compile(
    r"\b([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*\.\s*"
    r"(MapGet|MapPost|MapPut|MapPatch|MapDelete|MapMethods|MapGroup)\s*\("
)
GROUP_DECL_RE = re.compile(
    r"\b(?:var|string|RouteGroupBuilder)\s+(\w+)\s*=\s*"
    r"([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*\.\s*MapGroup\s*\("
)
CONST_HEAD_RE = re.compile(
    r"\b(?:const\s+string|static\s+readonly\s+string)\s+(\w+)\s*="
)


def _iter_const_declarations(cleaned, masked):
    """Yield ``(name, expression, index)`` for C# string constants.

    The declaration head is located on ``masked`` (so ``const string`` inside a
    string literal cannot match).  The expression is then sliced from
    ``cleaned`` with string-literal aware semicolon detection, because the
    masked copy has blanked the literal body and therefore cannot provide a
    usable span.
    """
    n = len(cleaned)
    for match in CONST_HEAD_RE.finditer(masked):
        start = match.end()
        i = start
        quote = False
        verbatim = False
        while i < n:
            ch = cleaned[i]
            if quote:
                if verbatim:
                    if ch == '"':
                        if i + 1 < n and cleaned[i + 1] == '"':
                            i += 2
                            continue
                        quote = False
                else:
                    if ch == "\\":
                        i += 2
                        continue
                    if ch == '"':
                        quote = False
                i += 1
                continue
            if ch == '"':
                quote = True
                verbatim = i > 0 and cleaned[i - 1] == "@"
            elif ch == ";":
                break
            i += 1
        if i < n and cleaned[i] == ";":
            yield match.group(1), cleaned[start:i], match.start()

METHOD_NAMES = {
    "MapGet": "GET",
    "MapPost": "POST",
    "MapPut": "PUT",
    "MapPatch": "PATCH",
    "MapDelete": "DELETE",
}


def _is_blueprint_call(node) -> bool:
    return (
        isinstance(node, ast.Call)
        and isinstance(node.func, ast.Name)
        and node.func.id == "Blueprint"
    )


def _contains_registration(statements) -> bool:
    """True when the block contains an add_url_rule or route() call."""
    for stmt in statements:
        for node in ast.walk(stmt):
            if not isinstance(node, ast.Call):
                continue
            func = node.func
            if isinstance(func, ast.Name) and func.id == "route":
                return True
            if isinstance(func, ast.Attribute) and func.attr == "add_url_rule":
                return True
    return False


def _mask_cs(text: str) -> str:
    """Return a same-length string with comments and string contents blanked."""
    out = list(text)
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                out[i] = " "
                i += 1
            continue
        if ch == "/" and i + 1 < n and text[i + 1] == "*":
            out[i] = out[i + 1] = " "
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                if text[i] != "\n":
                    out[i] = " "
                i += 1
            if i + 1 < n:
                out[i] = out[i + 1] = " "
                i += 2
            continue
        if ch == '"':
            verbatim = i > 0 and text[i - 1] == "@"
            out[i] = " "
            i += 1
            while i < n:
                if verbatim and text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    out[i] = out[i + 1] = " "
                    i += 2
                    continue
                if not verbatim and text[i] == "\\":
                    out[i] = " "
                    if i + 1 < n and text[i + 1] != "\n":
                        out[i + 1] = " "
                    i += 2
                    continue
                if text[i] == '"':
                    out[i] = " "
                    i += 1
                    break
                if text[i] != "\n":
                    out[i] = " "
                i += 1
            continue
        if ch == "'":
            out[i] = " "
            i += 1
            while i < n:
                if text[i] == "\\":
                    out[i] = " "
                    if i + 1 < n:
                        out[i + 1] = " "
                    i += 2
                    continue
                if text[i] == "'":
                    out[i] = " "
                    i += 1
                    break
                if text[i] != "\n":
                    out[i] = " "
                i += 1
            continue
        i += 1
    return "".join(out)


def _strip_cs_comments(text: str) -> str:
    """Remove comments but keep string literals and line numbers intact."""
    out = list(text)
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                out[i] = " "
                i += 1
            continue
        if ch == "/" and i + 1 < n and text[i + 1] == "*":
            out[i] = out[i + 1] = " "
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                if text[i] != "\n":
                    out[i] = " "
                i += 1
            if i + 1 < n:
                out[i] = out[i + 1] = " "
                i += 2
            continue
        if ch == '"':
            verbatim = i > 0 and text[i - 1] == "@"
            i += 1
            while i < n:
                if verbatim and text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    i += 2
                    continue
                if not verbatim and text[i] == "\\":
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if ch == "'":
            i += 1
            while i < n:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        i += 1
    return "".join(out)


def _find_matching_paren(text: str, open_index: int) -> int:
    depth = 0
    i = open_index
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == '"':
            verbatim = i > 0 and text[i - 1] == "@"
            i += 1
            while i < n:
                if verbatim and text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    i += 2
                    continue
                if not verbatim and text[i] == "\\":
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if ch == "'":
            i += 1
            while i < n:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def _split_top_level(text: str, delimiter: str = ","):
    parts = []
    current = []
    depth = 0
    angle = 0
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == '"':
            verbatim = i > 0 and text[i - 1] == "@"
            current.append(ch)
            i += 1
            while i < n:
                current.append(text[i])
                if verbatim and text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    current.append(text[i + 1])
                    i += 2
                    continue
                if not verbatim and text[i] == "\\":
                    if i + 1 < n:
                        current.append(text[i + 1])
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if ch == "'":
            current.append(ch)
            i += 1
            while i < n:
                current.append(text[i])
                if text[i] == "\\":
                    if i + 1 < n:
                        current.append(text[i + 1])
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        elif ch == "<":
            angle += 1
        elif ch == ">":
            angle = max(0, angle - 1)
        if ch == delimiter and depth == 0 and angle == 0:
            parts.append("".join(current).strip())
            current = []
            i += 1
            continue
        current.append(ch)
        i += 1
    parts.append("".join(current).strip())
    return parts


def _unescape_cs_string(body: str, verbatim=False) -> str:
    if verbatim:
        return body.replace('""', '"')
    return (body.replace("\\\\", "\\")
                .replace('\\"', '"')
                .replace("\\n", "\n")
                .replace("\\r", "\r")
                .replace("\\t", "\t"))


class CSharpAnalyzer:
    def __init__(self, root):
        self.root = Path(root)
        self.routes = []
        self.unresolved = []
        self.constants = {}
        self._duplicate_constants = set()

    def analyze(self):
        if not self.root.is_dir():
            raise SystemExit(f"C# directory not found: {self.root}")
        files = []
        for path in sorted(self.root.rglob("*.cs")):
            rel = path.relative_to(self.root)
            if any(part in {"bin", "obj", ".git"} for part in rel.parts):
                continue
            files.append((path, rel))
        # Global constants first.
        for path, rel in files:
            text = path.read_text(encoding="utf-8")
            cleaned = _strip_cs_comments(text)
            masked = _mask_cs(cleaned)
            for name, expr, _index in _iter_const_declarations(cleaned, masked):
                value = self.resolve_path(expr, self.constants)
                if value is None:
                    continue
                if name in self.constants and self.constants[name] != value:
                    self._duplicate_constants.add(name)
                else:
                    self.constants[name] = value
        for name in self._duplicate_constants:
            self.constants.pop(name, None)
        # Per-file analysis.
        for path, rel in files:
            self._analyze_file(path, rel)
        self.routes.sort(key=lambda r: (r["source"], r["line"], r["method"],
                                        r["normalized_path"]))
        return self

    def resolve_path(self, expr, local_consts):
        expr = expr.strip()
        if not expr:
            return None
        terms = _split_top_level(expr, "+")
        if len(terms) > 1:
            values = [self._resolve_term(term, local_consts) for term in terms]
            if any(value is None for value in values):
                return None
            return "".join(values)
        return self._resolve_term(expr, local_consts)

    def _resolve_term(self, term, local_consts):
        term = term.strip()
        while term.startswith("(") and term.endswith(")"):
            inner = term[1:-1].strip()
            if _find_matching_paren(term, 0) == len(term) - 1:
                term = inner
            else:
                break
        if term.startswith("@$\"") or term.startswith("$@\""):
            return self._resolve_interpolated(term[2:-1], local_consts, verbatim=True)
        if term.startswith("$\""):
            return self._resolve_interpolated(term[2:-1], local_consts, verbatim=False)
        if term.startswith("$@"):
            return self._resolve_interpolated(term[3:-1], local_consts, verbatim=True)
        if term.startswith("@\""):
            return _unescape_cs_string(term[2:-1], verbatim=True)
        if term.startswith("\""):
            return _unescape_cs_string(term[1:-1], verbatim=False)
        name_match = re.fullmatch(r"[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*", term)
        if name_match:
            name = term.replace(" ", "")
            simple = name.split(".")[-1]
            if name in local_consts:
                return local_consts[name]
            if name in self.constants:
                return self.constants[name]
            if simple in local_consts:
                return local_consts[simple]
            if simple in self.constants:
                return self.constants[simple]
            return None
        nameof_match = re.fullmatch(r"nameof\s*\(\s*([^)]+)\s*\)", term)
        if nameof_match:
            inner = nameof_match.group(1).strip()
            return inner.split(".")[-1]
        return None

    def _resolve_interpolated(self, body, local_consts, verbatim):
        result = []
        i = 0
        n = len(body)
        while i < n:
            if body[i] == "{" and i + 1 < n and body[i + 1] == "{":
                result.append("{")
                i += 2
                continue
            if body[i] == "}" and i + 1 < n and body[i + 1] == "}":
                result.append("}")
                i += 2
                continue
            if body[i] == "{":
                depth = 0
                j = i
                while j < n:
                    if body[j] == "{":
                        depth += 1
                    elif body[j] == "}":
                        depth -= 1
                        if depth == 0:
                            break
                    j += 1
                if j >= n:
                    return None
                expr = body[i + 1:j]
                if ":" in expr:
                    expr = expr.split(":", 1)[0]
                value = self.resolve_path(expr, local_consts)
                if value is None:
                    return None
                result.append(value)
                i = j + 1
                continue
            result.append(body[i])
            i += 1
        return "".join(result)

    def _analyze_file(self, path, rel):
        text = path.read_text(encoding="utf-8")
        cleaned = _strip_cs_comments(text)
        masked = _mask_cs(cleaned)
        rel_text = str(rel).replace("\\", "/")

        local_consts = {}
        for name, expr, _index in _iter_const_declarations(cleaned, masked):
            value = self.resolve_path(expr, local_consts)
            if value is not None:
                local_consts[name] = value

        group_prefix = {}

        def line_of(index):
            return cleaned.count("\n", 0, index) + 1

        # Group declarations (in source order, so nested groups resolve).
        for match in GROUP_DECL_RE.finditer(masked):
            var_name = match.group(1)
            receiver = re.sub(r"\s+", "", match.group(2))
            open_index = cleaned.find("(", match.end() - 1)
            close_index = _find_matching_paren(cleaned, open_index)
            if close_index < 0:
                self.unresolved.append({
                    "file": rel_text,
                    "line": line_of(match.start()),
                    "call": "MapGroup",
                    "reason": "unterminated MapGroup call",
                })
                continue
            args = _split_top_level(cleaned[open_index + 1:close_index])
            prefix = self.resolve_path(args[0], local_consts) if args else None
            base = "" if receiver == "app" else group_prefix.get(receiver)
            if prefix is None:
                self.unresolved.append({
                    "file": rel_text,
                    "line": line_of(match.start()),
                    "call": "MapGroup",
                    "reason": f"cannot resolve group prefix expression: {args[0] if args else '<missing>'}",
                })
                group_prefix[var_name] = None
                continue
            if receiver != "app" and base is None:
                self.unresolved.append({
                    "file": rel_text,
                    "line": line_of(match.start()),
                    "call": "MapGroup",
                    "reason": f"unknown receiver '{receiver}' for MapGroup",
                })
                group_prefix[var_name] = None
                continue
            combined = join_paths(base, prefix) if base else prefix
            group_prefix[var_name] = combined

        # Route calls.
        for match in CALL_RE.finditer(masked):
            method_name = match.group(2)
            if method_name == "MapGroup":
                continue
            receiver = re.sub(r"\s+", "", match.group(1))
            open_index = cleaned.find("(", match.end() - 1)
            close_index = _find_matching_paren(cleaned, open_index)
            if close_index < 0:
                self.unresolved.append({
                    "file": rel_text,
                    "line": line_of(match.start()),
                    "call": method_name,
                    "reason": "unterminated route call",
                })
                continue
            args = _split_top_level(cleaned[open_index + 1:close_index])
            if not args:
                self.unresolved.append({
                    "file": rel_text,
                    "line": line_of(match.start()),
                    "call": method_name,
                    "reason": "route call has no pattern argument",
                })
                continue
            path_expr = args[0]
            path = self.resolve_path(path_expr, local_consts)
            if path is None:
                self.unresolved.append({
                    "file": rel_text,
                    "line": line_of(match.start()),
                    "call": method_name,
                    "reason": f"cannot resolve route pattern expression: {path_expr}",
                })
                continue
            base = ""
            if receiver != "app":
                if receiver not in group_prefix:
                    self.unresolved.append({
                        "file": rel_text,
                        "line": line_of(match.start()),
                        "call": method_name,
                        "reason": f"unknown route-group receiver '{receiver}'",
                    })
                    continue
                base = group_prefix[receiver]
                if base is None:
                    self.unresolved.append({
                        "file": rel_text,
                        "line": line_of(match.start()),
                        "call": method_name,
                        "reason": f"route-group prefix for '{receiver}' is unresolved",
                    })
                    continue
            full_path = join_paths(base, path) if base else path
            if method_name == "MapMethods":
                if len(args) < 2:
                    self.unresolved.append({
                        "file": rel_text,
                        "line": line_of(match.start()),
                        "call": method_name,
                        "reason": "MapMethods has no HTTP methods argument",
                    })
                    continue
                methods = re.findall(r'"([^"]+)"', args[1])
                if not methods:
                    self.unresolved.append({
                        "file": rel_text,
                        "line": line_of(match.start()),
                        "call": method_name,
                        "reason": f"MapMethods HTTP methods are not a literal array: {args[1]}",
                    })
                    continue
                methods = [m.upper() for m in methods]
            else:
                methods = [METHOD_NAMES[method_name]]
            for method in methods:
                if method not in ALL_METHODS:
                    self.unresolved.append({
                        "file": rel_text,
                        "line": line_of(match.start()),
                        "call": method_name,
                        "reason": f"unknown HTTP method '{method}'",
                    })
                    continue
                self.routes.append({
                    "method": method,
                    "path": full_path,
                    "normalized_path": normalize_path(full_path),
                    "source": rel_text,
                    "line": line_of(match.start()),
                    "receiver": receiver,
                })


# ---------------------------------------------------------------------------
# Coverage assembly
# ---------------------------------------------------------------------------


def build_coverage(legacy: LegacyAnalyzer, csharp: CSharpAnalyzer) -> dict:
    csharp_keys = {(route["method"], route["normalized_path"]): route
                   for route in csharp.routes}
    legacy_keys = {(route["method"], route["normalized_path"])
                   for route in legacy.routes}

    for route in legacy.routes:
        if route["is_options"]:
            route["in_csharp"] = None
            route["csharp_source"] = None
        else:
            match = csharp_keys.get((route["method"], route["normalized_path"]))
            route["in_csharp"] = match is not None
            route["csharp_source"] = (
                f"{match['source']}:{match['line']}" if match else None
            )

    def business(route):
        return not route["is_options"]

    core_effective = [r for r in legacy.routes
                      if r["category"] == CORE and business(r)
                      and r["handler_status"] == HANDLER]
    core_registered_only = [r for r in legacy.routes
                            if r["category"] == CORE and business(r)
                            and r["handler_status"] == REGISTERED_ONLY_405]
    core_handler_unknown = [r for r in legacy.routes
                            if r["category"] == CORE and business(r)
                            and r["handler_status"] == HANDLER_UNKNOWN]
    conditional_effective = [r for r in legacy.routes
                             if r["category"] == CONDITIONAL and business(r)
                             and r["handler_status"] == HANDLER]
    optional_effective = [r for r in legacy.routes
                          if r["category"] == OPTIONAL and business(r)
                          and r["handler_status"] == HANDLER]

    core_missing = [r for r in core_effective if not r["in_csharp"]]
    conditional_missing = [r for r in conditional_effective if not r["in_csharp"]]
    optional_missing = [r for r in optional_effective if not r["in_csharp"]]

    def by_module(routes):
        grouped = defaultdict(list)
        for route in routes:
            grouped[route["module"]].append(route)
        return grouped

    core_missing_by_module = {
        module: [
            {
                "method": route["method"],
                "path": route["path"],
                "normalized_path": route["normalized_path"],
                "handler": route["handler"],
                "source": f"{route['source']}:{route['line']}",
            }
            for route in sorted(routes, key=lambda r: (r["normalized_path"], r["method"]))
        ]
        for module, routes in sorted(by_module(core_missing).items())
    }
    optional_missing_by_module = {
        module: [
            {
                "method": route["method"],
                "path": route["path"],
                "normalized_path": route["normalized_path"],
                "handler": route["handler"],
                "source": f"{route['source']}:{route['line']}",
            }
            for route in sorted(routes, key=lambda r: (r["normalized_path"], r["method"]))
        ]
        for module, routes in sorted(by_module(optional_missing).items())
    }

    module_summary = {}
    for route in legacy.routes:
        if not business(route) or route["category"] == DISABLED:
            continue
        module = route["module"]
        entry = module_summary.setdefault(module, {
            "module": module,
            "category": route["category"],
            "condition": route["condition"],
            "business_registrations": 0,
            "effective": 0,
            "registered_only_405": 0,
            "handler_unknown": 0,
            "matched_in_csharp": 0,
            "missing": 0,
        })
        entry["business_registrations"] += 1
        if route["handler_status"] == HANDLER:
            entry["effective"] += 1
            if route["in_csharp"]:
                entry["matched_in_csharp"] += 1
            else:
                entry["missing"] += 1
        elif route["handler_status"] == REGISTERED_ONLY_405:
            entry["registered_only_405"] += 1
        else:
            entry["handler_unknown"] += 1

    optional_modules = []
    for spec in legacy.module_specs:
        module_routes = [r for r in legacy.routes
                         if r["category"] == OPTIONAL and r["module"] == spec["name"]]
        effective = [r for r in module_routes if business(r)
                     and r["handler_status"] == HANDLER]
        optional_modules.append({
            **spec,
            "business_registrations": sum(1 for r in module_routes if business(r)),
            "effective": len(effective),
            "matched_in_csharp": sum(1 for r in effective if r["in_csharp"]),
            "missing": sum(1 for r in effective if not r["in_csharp"]),
        })

    csharp_extra = [
        route for route in csharp.routes
        if (route["method"], route["normalized_path"]) not in legacy_keys
    ]

    registered_only_payload = [
        {
            "method": route["method"],
            "path": route["path"],
            "normalized_path": route["normalized_path"],
            "handler": route["handler"],
            "source": f"{route['source']}:{route['line']}",
            "note": "registered by Flask but the view class has no handler; runtime 405",
        }
        for route in sorted(core_registered_only,
                            key=lambda r: (r["normalized_path"], r["method"]))
    ]

    legacy_unresolved_payload = sorted(
        legacy.unresolved,
        key=lambda item: (item["file"], item["line"], item["reason"]),
    )
    csharp_unresolved_payload = sorted(
        csharp.unresolved,
        key=lambda item: (item["file"], item["line"], item["reason"]),
    )

    return {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "tool": {
            "name": "Audit-LegacyRoutes.py",
            "mode": "offline static analysis",
            "executes_legacy_app": False,
            "reads_runtime_config": False,
            "guarantee": "not 100% automated; see unresolved sections",
        },
        "inputs": {
            "legacy_directory": str(legacy.root),
            "csharp_directory": str(csharp.root),
        },
        "normalization": {
            "placeholder": "{}",
            "rule": (
                "Flask <name>/<converter:name> and C# {name}/{name:constraint}/"
                "{*name} are collapsed to one '{}' token; path structure is unchanged"
            ),
            "options": "OPTIONS is recorded but excluded from business coverage",
        },
        "summary": {
            "legacy_route_method_entries_including_options": len(legacy.routes),
            "legacy_business_registrations": sum(
                1 for r in legacy.routes
                if business(r) and r["category"] != DISABLED
            ),
            "legacy_blueprints": len(legacy.blueprints),
            "core_business_registrations": sum(
                1 for r in legacy.routes if r["category"] == CORE and business(r)
            ),
            "core_effective_methods": len(core_effective),
            "core_matched_in_csharp": sum(1 for r in core_effective if r["in_csharp"]),
            "core_missing_in_csharp": len(core_missing),
            "core_registered_only_405": len(core_registered_only),
            "core_handler_unknown": len(core_handler_unknown),
            "conditional_business_registrations": sum(
                1 for r in legacy.routes
                if r["category"] == CONDITIONAL and business(r)
            ),
            "conditional_effective_methods": len(conditional_effective),
            "conditional_matched_in_csharp": sum(
                1 for r in conditional_effective if r["in_csharp"]
            ),
            "conditional_missing_in_csharp": len(conditional_missing),
            "optional_business_registrations": sum(
                1 for r in legacy.routes
                if r["category"] == OPTIONAL and business(r)
            ),
            "optional_effective_methods": len(optional_effective),
            "optional_matched_in_csharp": sum(
                1 for r in optional_effective if r["in_csharp"]
            ),
            "optional_missing_in_csharp": len(optional_missing),
            "disabled_comment_method_entries": sum(
                1 for r in legacy.routes if r["category"] == DISABLED
            ),
            "csharp_route_method_entries": len(csharp.routes),
            "csharp_extra_route_entries_not_in_legacy": len(csharp_extra),
            "legacy_unresolved_count": len(legacy_unresolved_payload),
            "csharp_unresolved_count": len(csharp_unresolved_payload),
        },
        "conditions": {
            "mit_routes": sorted({
                r["condition"] for r in legacy.routes
                if r["category"] == CONDITIONAL and r["condition"]
            }),
            "optional_modules": optional_modules,
        },
        "registered_only_405": registered_only_payload,
        "missing_by_module": {
            "core": core_missing_by_module,
            "optional": optional_missing_by_module,
        },
        "module_summary": sorted(module_summary.values(),
                                 key=lambda item: item["module"]),
        "routes": sorted(
            legacy.routes,
            key=lambda r: (r["category"], r["module"], r["normalized_path"],
                           r["method"], r["line"]),
        ),
        "csharp_routes": csharp.routes,
        "csharp_extra_routes": sorted(
            csharp_extra,
            key=lambda r: (r["normalized_path"], r["method"]),
        ),
        "legacy_unresolved": legacy_unresolved_payload,
        "csharp_unresolved": csharp_unresolved_payload,
        "limitations": [
            "Static analysis only. Neither the Flask app nor the C# app is run.",
            "Flask auto-added OPTIONS/HEAD are not synthesized; only explicit "
            "registrations in source are reported. OPTIONS is not business coverage.",
            "Handler support is resolved from local class definitions and their "
            "local bases. Unknown bases are reported as unresolved, never as 405.",
            "Optional modules are enabled by directory presence in this repository; "
            "deployment-specific configuration values are never read.",
            "C# route matching proves route registration only, not business compatibility.",
            "Dynamic route strings or unresolved receivers in C# are listed under "
            "csharp_unresolved and are not counted as matches.",
        ],
    }


# ---------------------------------------------------------------------------
# Markdown rendering
# ---------------------------------------------------------------------------


def _escape_cell(value) -> str:
    if value is None:
        return ""
    return str(value).replace("|", "\\|").replace("\n", " ")


def render_markdown(report: dict) -> str:
    summary = report["summary"]
    lines = []
    add = lines.append
    add("# Legacy route vs C# route coverage")
    add("")
    add(f"Generated: `{report['generated_at']}` (UTC) by `{report['tool']['name']}`.")
    add("")
    add("This is an **offline static audit**. It does not import or run the legacy "
        "Python app, does not run `dotnet build`, and does not read deployment "
        "configuration values. It reports route registration presence only, not "
        "business compatibility.")
    add("")
    add("## Inputs and normalization")
    add("")
    add(f"- Legacy directory: `{report['inputs']['legacy_directory']}`")
    add(f"- C# directory: `{report['inputs']['csharp_directory']}`")
    add(f"- Placeholder normalization: {report['normalization']['rule']}.")
    add(f"- {report['normalization']['options']}.")
    add("")
    add("## Summary")
    add("")
    add("| Metric | Count |")
    add("| --- | ---: |")
    for key, label in [
        ("legacy_route_method_entries_including_options", "Legacy method entries (incl. OPTIONS)"),
        ("legacy_business_registrations", "Legacy business registrations (non-OPTIONS)"),
        ("core_business_registrations", "Core business registrations"),
        ("core_effective_methods", "Core effective methods (handler exists)"),
        ("core_matched_in_csharp", "Core effective methods present in C#"),
        ("core_missing_in_csharp", "Core effective methods missing in C#"),
        ("core_registered_only_405", "Core registered-only methods (405)"),
        ("core_handler_unknown", "Core methods with unresolved handler status"),
        ("conditional_effective_methods", "Conditional (MIT) effective methods"),
        ("conditional_matched_in_csharp", "Conditional (MIT) present in C#"),
        ("optional_effective_methods", "Optional-module effective methods"),
        ("optional_matched_in_csharp", "Optional-module methods present in C#"),
        ("disabled_comment_method_entries", "Commented-out method entries"),
        ("csharp_route_method_entries", "C# route method entries"),
        ("csharp_extra_route_entries_not_in_legacy", "C# entries not in legacy"),
        ("legacy_unresolved_count", "Legacy unresolved warnings"),
        ("csharp_unresolved_count", "C# unresolved warnings"),
    ]:
        add(f"| {label} | {summary.get(key, 0)} |")
    add("")
    add("> `core_effective_methods` intentionally excludes OPTIONS and methods that "
        "Flask registered without a real view handler. Those are reported below as "
        "registered-only/405 and are **not** counted as missing business.")
    add("")

    add("## Registered-only routes (runtime 405)")
    add("")
    if report["registered_only_405"]:
        add("| Method | Path | Handler | Source |")
        add("| --- | --- | --- | --- |")
        for route in report["registered_only_405"]:
            add(f"| {route['method']} | `{_escape_cell(route['path'])}` | "
                f"`{_escape_cell(route['handler'])}` | `{route['source']}` |")
    else:
        add("None detected.")
    add("")

    add("## Missing by module (core effective routes)")
    add("")
    core_missing = report["missing_by_module"]["core"]
    if core_missing:
        add("| Module | Missing | Methods and paths |")
        add("| --- | ---: | --- |")
        for module, routes in core_missing.items():
            items = "<br>".join(
                f"`{route['method']} {_escape_cell(route['path'])}` "
                f"({_escape_cell(route['handler'])} @ {route['source']})"
                for route in routes
            )
            add(f"| {module} | {len(routes)} | {items} |")
    else:
        add("No core effective routes are missing.")
    add("")

    add("## Optional modules and enable conditions")
    add("")
    if report["conditions"]["optional_modules"]:
        add("| Module | Enable condition | Routes | Effective | In C# | Missing |")
        add("| --- | --- | ---: | ---: | ---: | ---: |")
        for module in report["conditions"]["optional_modules"]:
            add(f"| {module['name']} | {_escape_cell(module['enable_condition'])} | "
                f"{module['business_registrations']} | {module['effective']} | "
                f"{module['matched_in_csharp']} | {module['missing']} |")
    else:
        add("No optional modules discovered.")
    add("")
    add("### Optional-module missing routes")
    add("")
    optional_missing = report["missing_by_module"]["optional"]
    if optional_missing:
        for module, routes in optional_missing.items():
            add(f"- **{module}**: " + ", ".join(
                f"`{route['method']} {route['path']}`" for route in routes
            ))
    else:
        add("None.")
    add("")

    add("## Conditional MIT routes")
    add("")
    mit_routes = [r for r in report["routes"]
                  if r["category"] == CONDITIONAL]
    if mit_routes:
        add("| Method | Path | Handler | Condition | In C# |")
        add("| --- | --- | --- | --- | --- |")
        for route in mit_routes:
            add(f"| {route['method']} | `{_escape_cell(route['path'])}` | "
                f"`{_escape_cell(route['handler'])}` | "
                f"`{_escape_cell(route['condition'])}` | "
                f"{'yes' if route['in_csharp'] else 'no'} |")
    else:
        add("None.")
    add("")

    add("## Commented-out (disabled) routes")
    add("")
    disabled = [r for r in report["routes"] if r["category"] == DISABLED]
    if disabled:
        add("| Method | Path | Handler | Source |")
        add("| --- | --- | --- | --- |")
        for route in disabled:
            add(f"| {route['method']} | `{_escape_cell(route['path'])}` | "
                f"`{_escape_cell(route['handler'])}` | "
                f"`{route['source']}:{route['line']}` |")
    else:
        add("None.")
    add("")

    add("## Unresolved")
    add("")
    add("### Legacy Python")
    add("")
    if report["legacy_unresolved"]:
        add("| File | Line | Reason |")
        add("| --- | ---: | --- |")
        for item in report["legacy_unresolved"]:
            add(f"| `{item['file']}` | {item['line']} | {_escape_cell(item['reason'])} |")
    else:
        add("None.")
    add("")
    add("### C#")
    add("")
    if report["csharp_unresolved"]:
        add("| File | Line | Call | Reason |")
        add("| --- | ---: | --- | --- |")
        for item in report["csharp_unresolved"]:
            add(f"| `{item['file']}` | {item['line']} | {item['call']} | "
                f"{_escape_cell(item['reason'])} |")
    else:
        add("None.")
    add("")

    add("## Full legacy route inventory")
    add("")
    add("| Category | Module | Method | Path | Handler | Handler status | C# | Source |")
    add("| --- | --- | --- | --- | --- | --- | --- | --- |")
    for route in report["routes"]:
        presence = "-"
        if not route["is_options"]:
            presence = "yes" if route["in_csharp"] else "no"
        add(f"| {route['category']} | {route['module']} | {route['method']} | "
            f"`{_escape_cell(route['path'])}` | `{_escape_cell(route['handler'])}` | "
            f"{route['handler_status']} | {presence} | "
            f"`{route['source']}:{route['line']}` |")
    add("")

    add("## C# routes not present in legacy")
    add("")
    if report["csharp_extra_routes"]:
        add("| Method | Path | Source |")
        add("| --- | --- | --- |")
        for route in report["csharp_extra_routes"]:
            add(f"| {route['method']} | `{_escape_cell(route['path'])}` | "
                f"`{route['source']}:{route['line']}` |")
    else:
        add("None.")
    add("")

    add("## Usage")
    add("")
    add("```powershell")
    add("# from the C# repository root")
    add("python tools/Audit-LegacyRoutes.py `")
    add("  --legacy-directory ../shoreline-moeflow-backend `")
    add("  --csharp-directory . `")
    add("  --output docs/route-coverage.json")
    add("```")
    add("")
    add("Writing `.json` also writes the companion `.md` (and the reverse). "
        "The tool has no third-party dependencies and performs no network, "
        "database, or application startup work.")
    add("")
    add("```powershell")
    add("python -m unittest discover -s tools -p \"test_audit_legacy_routes.py\" -v")
    add("```")
    add("")
    add("## Limitations")
    add("")
    for item in report["limitations"]:
        add(f"- {item}")
    add("")
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def _resolve_input(path_value, csharp_dir):
    path = Path(path_value)
    if path.is_absolute() or path.exists():
        return path.resolve()
    alternative = Path(csharp_dir) / path
    if alternative.exists():
        return alternative.resolve()
    return path.resolve()


def write_outputs(report: dict, output: Path, companion: bool = True):
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    suffix = output.suffix.lower()
    if suffix == ".json":
        json_path = output
        md_path = output.with_suffix(".md")
        json_text = json.dumps(report, ensure_ascii=False, indent=2)
        json_path.write_text(json_text + "\n", encoding="utf-8")
        written = [json_path]
        if companion:
            md_path.write_text(render_markdown(report), encoding="utf-8")
            written.append(md_path)
    elif suffix == ".md":
        md_path = output
        json_path = output.with_suffix(".json")
        md_path.write_text(render_markdown(report), encoding="utf-8")
        written = [md_path]
        if companion:
            json_path.write_text(
                json.dumps(report, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            written.append(json_path)
    else:
        raise SystemExit("--output must end in .json or .md")
    return written


def build_arg_parser():
    parser = argparse.ArgumentParser(
        description="Offline Flask vs C# route inventory and coverage report."
    )
    parser.add_argument(
        "--legacy-directory",
        default="../shoreline-moeflow-backend",
        help="legacy Python backend root (default: ../shoreline-moeflow-backend)",
    )
    parser.add_argument(
        "--csharp-directory",
        default=".",
        help="C# backend root (default: .)",
    )
    parser.add_argument(
        "--output",
        default="docs/route-coverage.json",
        help="output file ending in .json or .md (default: docs/route-coverage.json)",
    )
    parser.add_argument(
        "--no-companion",
        action="store_true",
        help="write only --output instead of both .json and .md",
    )
    return parser


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    legacy_dir = _resolve_input(args.legacy_directory, args.csharp_directory)
    csharp_dir = Path(args.csharp_directory).resolve()
    legacy = LegacyAnalyzer(legacy_dir).analyze()
    csharp = CSharpAnalyzer(csharp_dir).analyze()
    report = build_coverage(legacy, csharp)
    written = write_outputs(report, Path(args.output), not args.no_companion)
    summary = report["summary"]
    print("Route audit complete (static, offline).")
    print(f"  core effective methods      : {summary['core_effective_methods']}")
    print(f"  core matched in C#          : {summary['core_matched_in_csharp']}")
    print(f"  core missing in C#          : {summary['core_missing_in_csharp']}")
    print(f"  core registered-only (405)  : {summary['core_registered_only_405']}")
    print(f"  optional effective methods  : {summary['optional_effective_methods']}")
    print(f"  optional matched in C#      : {summary['optional_matched_in_csharp']}")
    print(f"  legacy unresolved           : {summary['legacy_unresolved_count']}")
    print(f"  C# unresolved               : {summary['csharp_unresolved_count']}")
    print("  written: " + ", ".join(str(path) for path in written))
    return 0


if __name__ == "__main__":
    sys.exit(main())
