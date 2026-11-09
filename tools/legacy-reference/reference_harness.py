"""Safe, stateless extraction of Python reference behavior.

This module never imports the legacy application package. It reads a small
whitelist of source files, extracts selected top-level definitions with AST,
compiles only those definitions in an isolated namespace, and records the
original path, line range, and SHA-256 for every block. The extracted code is
the reference; this file is not a reimplementation of it.
"""

from __future__ import annotations

import ast
import base64
import builtins
import datetime
import hashlib
import http.client
import json
import platform
import re
import socket
import sys
import unicodedata
import urllib.request
from contextlib import contextmanager
from pathlib import Path, PurePosixPath
from typing import Any, Dict, Iterable, List, Optional, Tuple

import pymongo
from bson import BSON, Binary, Decimal128, Int64, ObjectId
from urllib.parse import urlsplit


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PYTHON_ROOT = REPO_ROOT.parent / "shoreline-moeflow-backend"

# Only these files may be read. app/config.py, dotenv files, storage data,
# deploy directories, tests, and user data are intentionally absent.
ALLOWED_SOURCE_PATHS = (
    "app/constants/file.py",
    "app/models/file.py",
    "app/services/file_order.py",
    "app/services/label_snapshot.py",
    "app/services/notification_content.py",
    "app/utils/labelplus.py",
    "app/utils/type.py",
)

EXTRACTION_NOTE = (
    "ast.parse + compile of selected definitions only; no import of the "
    "legacy app package, no conftest, no config, no dotenv, no database."
)


class SourcePathError(RuntimeError):
    """Raised when a caller tries to leave the explicit source whitelist."""


class NetworkDisabled(RuntimeError):
    """Raised if extracted reference code attempts a network operation."""


def resolve_source(root: Path, relative: str) -> Path:
    """Resolve one whitelisted reference source and reject path escapes."""

    if not isinstance(relative, str) or not relative:
        raise SourcePathError("source path must be a non-empty string")
    posix = PurePosixPath(relative)
    if posix.is_absolute() or ".." in posix.parts:
        raise SourcePathError(f"path escape rejected: {relative}")
    if relative not in ALLOWED_SOURCE_PATHS:
        raise SourcePathError(f"source path not in whitelist: {relative}")
    root = root.resolve()
    path = (root / relative).resolve()
    if path == root or root not in path.parents:
        raise SourcePathError(f"path escapes python root: {relative}")
    return path


def read_source(root: Path, relative: str) -> str:
    path = resolve_source(root, relative)
    return path.read_bytes().decode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def find_definition(
    tree: ast.Module,
    name: str,
    container: Optional[str] = None,
) -> ast.AST:
    """Find a named top-level definition, or a method inside one class."""

    body: Iterable[ast.AST] = tree.body
    if container is not None:
        parent = next(
            (
                node
                for node in tree.body
                if isinstance(node, ast.ClassDef) and node.name == container
            ),
            None,
        )
        if parent is None:
            raise LookupError(f"class not found: {container}")
        body = parent.body
    for node in body:
        if isinstance(
            node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)
        ) and node.name == name:
            return node
    label = f"{container}.{name}" if container else name
    raise LookupError(f"definition not found: {label}")


def extract_block(
    root: Path,
    relative: str,
    name: str,
    container: Optional[str] = None,
) -> Dict[str, Any]:
    text = read_source(root, relative)
    tree = ast.parse(text, filename=relative)
    node = find_definition(tree, name, container)
    segment = ast.get_source_segment(text, node)
    if segment is None:
        raise RuntimeError(f"could not recover source segment for {relative}:{name}")
    return {
        "path": relative,
        "name": name,
        "container": container,
        "node": node,
        "source": segment,
        "line": node.lineno,
        "end_line": getattr(node, "end_lineno", node.lineno),
        "source_sha256": sha256_bytes(text.encode("utf-8")),
        "block_sha256": sha256_bytes(segment.encode("utf-8")),
    }


def compile_blocks(
    blocks: List[Dict[str, Any]],
    namespace: Dict[str, Any],
    module_name: str,
) -> Dict[str, Any]:
    module = ast.Module(body=[block["node"] for block in blocks], type_ignores=[])
    ast.fix_missing_locations(module)
    code = compile(module, module_name, "exec")
    exec(code, namespace)
    return namespace


def provenance_for(blocks: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    grouped: Dict[str, Dict[str, Any]] = {}
    seen = set()
    for block in blocks:
        entry = grouped.setdefault(
            block["path"],
            {
                "path": block["path"],
                "source_sha256": block["source_sha256"],
                "blocks": [],
            },
        )
        key = (block["path"], block["name"], block["container"], block["line"])
        if key in seen:
            continue
        seen.add(key)
        entry["blocks"].append(
            {
                "name": block["name"],
                "container": block["container"],
                "line": block["line"],
                "end_line": block["end_line"],
                "block_sha256": block["block_sha256"],
            }
        )
    for entry in grouped.values():
        entry["blocks"].sort(key=lambda item: (item["line"], item["name"]))
    return [grouped[key] for key in sorted(grouped)]


def _sandbox(
    root: Path,
    specs: List[Tuple[str, str, Optional[str]]],
    stubs: Dict[str, Any],
    module_name: str,
) -> Tuple[Dict[str, Any], List[Dict[str, Any]]]:
    blocks = [extract_block(root, *spec) for spec in specs]
    namespace: Dict[str, Any] = {
        "__builtins__": builtins,
        "__name__": module_name,
    }
    namespace.update(stubs)
    compile_blocks(blocks, namespace, module_name)
    return namespace, blocks


@contextmanager
def forbid_app_imports() -> Iterable[None]:
    """Fail if any extracted code or imported dependency imports ``app``."""

    original = builtins.__import__

    def guarded(name: str, *args: Any, **kwargs: Any) -> Any:
        if name == "app" or name.startswith("app."):
            raise RuntimeError(
                f"legacy app import blocked during reference extraction: {name}"
            )
        return original(name, *args, **kwargs)

    builtins.__import__ = guarded
    try:
        yield
    finally:
        builtins.__import__ = original


def assert_no_app_imported() -> None:
    loaded = sorted(
        name
        for name in sys.modules
        if name == "app" or name.startswith("app.")
    )
    if loaded:
        raise RuntimeError(f"legacy app package was imported: {loaded}")


@contextmanager
def network_disabled() -> Iterable[List[str]]:
    """Disable sockets, DNS, urllib and http.client for the whole fixture run."""

    attempts: List[str] = []
    replacements: List[Tuple[Any, str, Any]] = []

    def deny(label: str):
        def blocked(*args: Any, **kwargs: Any) -> Any:
            attempts.append(label)
            raise NetworkDisabled(f"network disabled: {label}")

        return blocked

    targets = [
        (socket.socket, "connect", "socket.connect"),
        (socket, "create_connection", "socket.create_connection"),
        (socket, "getaddrinfo", "socket.getaddrinfo"),
        (urllib.request, "urlopen", "urllib.request.urlopen"),
        (http.client.HTTPConnection, "request", "http.client.request"),
    ]
    for owner, attribute, label in targets:
        replacements.append((owner, attribute, getattr(owner, attribute)))
        setattr(owner, attribute, deny(label))
    try:
        yield attempts
    finally:
        for owner, attribute, original in reversed(replacements):
            setattr(owner, attribute, original)


def _jsonable(value: Any) -> Any:
    if isinstance(value, ObjectId):
        return {"$oid": str(value)}
    if isinstance(value, Decimal128):
        return {"$decimal128": str(value)}
    if isinstance(value, Int64):
        return {"$int64": int(value)}
    if isinstance(value, Binary):
        return {"$binary": base64.b64encode(bytes(value)).decode("ascii")}
    if isinstance(value, datetime.datetime):
        return {"$date": value.isoformat()}
    if isinstance(value, dict):
        return {str(key): _jsonable(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_jsonable(item) for item in value]
    return value


def _bson_type(value: Any) -> str:
    if isinstance(value, bool):
        return "bool"
    if isinstance(value, Int64):
        return "int64"
    if isinstance(value, int):
        return "int32"
    if isinstance(value, float):
        return "double"
    if isinstance(value, Decimal128):
        return "decimal128"
    if isinstance(value, ObjectId):
        return "objectId"
    if value is None:
        return "null"
    if isinstance(value, str):
        return "string"
    if isinstance(value, datetime.datetime):
        return "date"
    if isinstance(value, dict):
        return "object"
    if isinstance(value, list):
        return "array"
    return type(value).__name__


def _encode_case(name: str, document: Dict[str, Any]) -> Dict[str, Any]:
    encoded = BSON.encode(document)
    return {
        "name": name,
        "field_order": list(document.keys()),
        "field_types": {key: _bson_type(value) for key, value in document.items()},
        "b64": base64.b64encode(encoded).decode("ascii"),
        "hex": encoded.hex(),
        "sha256": sha256_bytes(encoded),
        "decoded": _jsonable(BSON.decode(encoded)),
    }


class _FilenameIllegalError(Exception):
    pass


class _IntType:
    pass


class _APIError(Exception):
    def __init__(self, message: str, replace: bool = False) -> None:
        super().__init__(message)
        self.message = message


class _DoesNotExist(Exception):
    pass


def _filename_fixture(root: Path) -> Tuple[Dict[str, Any], List[Dict[str, Any]]]:
    namespace, blocks = _sandbox(
        root,
        [
            ("app/constants/file.py", "FileType", None),
            ("app/utils/type.py", "is_number", None),
            ("app/models/file.py", "Filename", None),
        ],
        {
            "IntType": _IntType,
            "FilenameIllegalError": _FilenameIllegalError,
            "gettext": lambda value: value,
            "re": re,
        },
        "<legacy-reference:filename>",
    )
    Filename = namespace["Filename"]
    cases = [
        {"name": "01 - 封面.png", "folder": False},
        {"name": "10 - page.JPG", "folder": False},
        {"name": "2.png", "folder": False},
        {"name": "book1-002.webp", "folder": False},
        {"name": "阅读笔记.txt", "folder": False},
        {"name": "noext", "folder": False},
        {"name": "ßσς 😀.png", "folder": False},
        {"name": "章节 😀", "folder": True},
        {"name": "  ", "folder": False},
        {"name": "a" * 128 + ".png", "folder": False},
        {"name": "..", "folder": False},
        {"name": "name.", "folder": False},
        {"name": ".png", "folder": False},
        {"name": "a/b.png", "folder": False},
        {"name": "a:b.png", "folder": False},
        {"name": 'a<b>c|d?.png', "folder": False},
    ]
    results = []
    for case in cases:
        entry: Dict[str, Any] = {
            "input": case["name"],
            "folder": case["folder"],
        }
        try:
            filename = Filename(case["name"], folder=case["folder"])
            entry.update(
                {
                    "ok": True,
                    "prefix": filename.prefix,
                    "suffix": filename.suffix,
                    "file_type": int(filename.file_type),
                    "sort_name": filename.sort_name,
                }
            )
        except Exception as error:  # intentionally record reference errors
            entry.update(
                {
                    "ok": False,
                    "error": {
                        "type": type(error).__name__,
                        "message": str(error),
                    },
                }
            )
        results.append(entry)
    return {"cases": results, "sort_width": 6}, blocks


def _labelplus_load_fixture(
    root: Path,
) -> Tuple[Dict[str, Any], List[Dict[str, Any]]]:
    namespace, blocks = _sandbox(
        root,
        [
            ("app/utils/labelplus.py", "LPLabel", None),
            ("app/utils/labelplus.py", "LPFile", None),
            ("app/utils/labelplus.py", "load_from_labelplus", None),
        ],
        {
            "List": List,
            "TypedDict": __import__("typing").TypedDict,
            "re": re,
        },
        "<legacy-reference:labelplus-load>",
    )
    text = "\n".join(
        [
            "project header",
            ">>>>>>>>[第1话/封面 – ßσ 😀 <&>.png]<<<<<<<<",
            "----------------[1]----------------[12.5,34.5,0]",
            "你好，世界 😀 <b>&",
            "第二行",
            "----------------[2]----------------[0,0,1]",
            "",
            ">>>>>>>>[02-текст.txt]<<<<<<<<",
            "----------------[1]----------------[-1.25,2.5,2]",
            "Ünïcode ßσς",
        ]
    )
    loaded = namespace["load_from_labelplus"](text)
    return {"input": text, "expected": loaded}, blocks


def _labelplus_export_fixture(
    root: Path,
) -> Tuple[Dict[str, Any], List[Dict[str, Any]]]:
    namespace, blocks = _sandbox(
        root,
        [("app/models/file.py", "to_labelplus", "File")],
        {},
        "<legacy-reference:labelplus-export>",
    )

    class Translation:
        def __init__(self, content: str, proofread: str = "") -> None:
            self.content = content
            self.proofread_content = proofread

    class Source:
        def __init__(
            self,
            x: float,
            y: float,
            position_type: int,
            translation: Optional[Translation],
        ) -> None:
            self.x = x
            self.y = y
            self.position_type = position_type
            self._translation = translation

        def best_translation(self, target: str) -> Optional[Translation]:
            return self._translation

    class Ancestor:
        def __init__(self, name: str) -> None:
            self.name = name

    class FileStub:
        name = "封面 – ßσ 😀 <&>.png"
        ancestors = [Ancestor("第1章"), Ancestor("scene")]

        def __init__(self) -> None:
            self._sources = [
                Source(
                    12.5,
                    34.25,
                    0,
                    Translation("原文 😀\n第二行", "校对 😀\n第二行 <&>"),
                ),
                Source(0, 0, 1, Translation("普通\n换行")),
                Source(-1.5, 2, 2, None),
            ]

        def sources(self) -> List[Source]:
            return self._sources

    fn = namespace["to_labelplus"]
    file_stub = FileStub()
    plain_output = fn(file_stub, target="zh")
    staff_block = "[staff]\r\nname: 欶澜 😀\r\n"
    staff_first = fn(
        file_stub,
        target="zh",
        staff_block=staff_block,
        staff_block_first=True,
    )
    staff_last = fn(
        file_stub,
        target="zh",
        staff_block=staff_block,
        staff_block_first=False,
    )
    export_name = fn(file_stub, target="zh", export_name="custom/名字 😀.png")
    return {
        "file": {
            "name": file_stub.name,
            "ancestors": [ancestor.name for ancestor in file_stub.ancestors],
        },
        "plain": plain_output,
        "staff_block": staff_block,
        "staff_first": staff_first,
        "staff_last": staff_last,
        "export_name": export_name,
    }, blocks


def _notification_fixture(
    root: Path,
) -> Tuple[Dict[str, Any], List[Dict[str, Any]]]:
    namespace, blocks = _sandbox(
        root,
        [
            ("app/services/notification_content.py", "NotificationError", None),
            ("app/services/notification_content.py", "fail", None),
            ("app/services/notification_content.py", "safe_url", None),
            ("app/services/notification_content.py", "parse_body", None),
            ("app/services/notification_content.py", "normalize", None),
            ("app/services/notification_content.py", "search_tokens", None),
            ("app/services/notification_content.py", "project_accessible", None),
        ],
        {
            "APIError": _APIError,
            "DoesNotExist": _DoesNotExist,
            "html": __import__("html"),
            "re": re,
            "unicodedata": unicodedata,
            "urlsplit": urlsplit,
            "ObjectId": ObjectId,
        },
        "<legacy-reference:notification>",
    )
    parse_body = namespace["parse_body"]
    safe_url = namespace["safe_url"]
    normalize = namespace["normalize"]
    search_tokens = namespace["search_tokens"]
    project_accessible = namespace["project_accessible"]

    valid_inputs = [
        "你好 [b]粗体 😀[/b] [url=https://example.com/a?x=1&y=2]链接[/url] [list][*]一[*]二[/list]",
        "[quote]外层 [i]斜体[/i] [u]下划线[/u][/quote]",
        "HTML <script>&\"' 特殊字符 😀 ßσς",
        "[project]64b000000000000000000001[/project]",
    ]
    parsed_valid = []
    for text in valid_inputs:
        parsed_valid.append({"input": text, "ok": True, "nodes": parse_body(text)})

    parsed_invalid = []
    invalid_inputs = [
        "[b]未闭合",
        "[*]孤立",
        "[url=javascript:alert(1)]x[/url]",
        "[url=https://user:pass@example.com]x[/url]",
        "[b=red]x[/b]",
    ]
    for text in invalid_inputs:
        try:
            parse_body(text)
        except Exception as error:
            parsed_invalid.append(
                {
                    "input": text,
                    "ok": False,
                    "error": {
                        "type": type(error).__name__,
                        "message": str(error),
                        "status_code": getattr(error, "status_code", None),
                    },
                }
            )
        else:
            parsed_invalid.append({"input": text, "ok": True, "nodes": None})

    urls = [
        "https://example.com/a?x=1&y=2",
        "http://example.com",
        "javascript:alert(1)",
        "https://user:pass@example.com",
        "https://example.com/\x01",
        "x" * 2049,
    ]
    safe_urls = [{"input": value, "expected": safe_url(value)} for value in urls]

    normalize_input = "  Ｆｕｌｌ　Width  ßΣς😀  "
    normalized = normalize(normalize_input)
    tokens = search_tokens(normalize_input)

    class Project:
        pass

    class User:
        def __init__(self, banned: bool, allowed: bool, raises: bool = False) -> None:
            self.banned = banned
            self.allowed = allowed
            self.raises = raises

        def can(self, project: Any, permission: str) -> bool:
            if self.raises:
                raise _DoesNotExist()
            return self.allowed

    access_cases = [
        ("allowed", Project(), User(False, True), True),
        ("denied", Project(), User(False, False), False),
        ("banned", Project(), User(True, True), False),
        ("missing-project", None, User(False, True), False),
        ("missing-user", Project(), None, False),
        ("does-not-exist", Project(), User(False, True, raises=True), False),
    ]
    access = []
    for case, project, user, expected in access_cases:
        actual = project_accessible(project, user)
        if actual != expected:
            raise RuntimeError(
                f"project_accessible reference mismatch for {case}: "
                f"{actual!r} != {expected!r}"
            )
        access.append({"case": case, "expected": actual})

    return {
        "valid": parsed_valid,
        "invalid": parsed_invalid,
        "safe_url": safe_urls,
        "normalize": {
            "input": normalize_input,
            "expected": normalized,
            "tokens": tokens,
        },
        "project_access": access,
    }, blocks


def _file_order_fixture(root: Path) -> Tuple[Dict[str, Any], List[Dict[str, Any]]]:
    namespace, blocks = _sandbox(
        root,
        [
            ("app/constants/file.py", "FileType", None),
            ("app/services/file_order.py", "order_snapshot", None),
            ("app/services/file_order.py", "export_file_names", None),
        ],
        {
            "IntType": _IntType,
            "hashlib": hashlib,
            "json": json,
        },
        "<legacy-reference:file-order>",
    )

    class FileStub:
        def __init__(self, oid: ObjectId, name: str, manual_order: Optional[int]):
            self.id = oid
            self.name = name
            self.manual_order = manual_order

    files = [
        FileStub(ObjectId("64b000000000000000000011"), "封面 😀.png", 1),
        FileStub(ObjectId("64b000000000000000000012"), "第2页 ßσ.png", 2),
        FileStub(ObjectId("64b000000000000000000013"), "legacy <&>.png", None),
        FileStub(ObjectId("64b000000000000000000014"), "zzz.png", None),
    ]

    class ProjectStub:
        def __init__(self) -> None:
            self.calls: List[Dict[str, Any]] = []

        def files(self, parent: Any = None, type_only: Any = None) -> List[FileStub]:
            self.calls.append({"parent": parent, "type_only": int(type_only)})
            return files

    project = ProjectStub()
    returned, version = namespace["order_snapshot"](project)
    canonical_json = json.dumps(
        [[str(file.id), file.name, file.manual_order] for file in returned],
        ensure_ascii=False,
    )
    expected_version = hashlib.sha256(canonical_json.encode("utf-8")).hexdigest()
    if version != expected_version:
        raise RuntimeError(
            f"order_snapshot version mismatch: {version} != {expected_version}"
        )
    all_unordered = [
        FileStub(ObjectId("64b000000000000000000021"), "一.png", None),
        FileStub(ObjectId("64b000000000000000000022"), "二 😀.png", None),
    ]
    with_manual = namespace["export_file_names"](files)
    without_manual = namespace["export_file_names"](all_unordered)
    return {
        "project_call": project.calls[0],
        "files": [
            {
                "id": str(file.id),
                "name": file.name,
                "manual_order": file.manual_order,
            }
            for file in returned
        ],
        "canonical_json": canonical_json,
        # The reference serializes a list of lists, so only the comma-space
        # default is observable; there are no object key colons in this value.
        "python_json_default_separators": ", " in canonical_json,
        "version": version,
        "json_sha256": expected_version,
        "export_with_manual": with_manual,
        "export_without_manual": without_manual,
    }, blocks


def _label_snapshot_fixture(
    root: Path,
) -> Tuple[Dict[str, Any], List[Dict[str, Any]]]:
    class FakeSnapshots:
        def __init__(self) -> None:
            self.inserted: List[Dict[str, Any]] = []
            self.updated: List[Dict[str, Any]] = []

        def insert_one(self, document: Dict[str, Any]) -> None:
            self.inserted.append(dict(document))

        def update_one(self, selector: Dict[str, Any], update: Dict[str, Any]) -> None:
            self.updated.append({"selector": selector, "update": update})
            if self.inserted:
                self.inserted[0].update(update.get("$set", {}))

        def delete_one(self, selector: Dict[str, Any]) -> None:
            self.updated.append({"delete_one": selector})

    class FakeChunks:
        def __init__(self) -> None:
            self.inserted: List[Dict[str, Any]] = []

        def insert_one(self, document: Dict[str, Any]) -> None:
            self.inserted.append(dict(document))

        def delete_many(self, selector: Dict[str, Any]) -> None:
            self.inserted.clear()

    snapshots = FakeSnapshots()
    chunks = FakeChunks()

    def fake_collections() -> Tuple[FakeSnapshots, FakeChunks]:
        return snapshots, chunks

    def fake_label_data(project: Any) -> Dict[str, Any]:
        oid_file = ObjectId("64b000000000000000000031")
        oid_file2 = ObjectId("64b000000000000000000037")
        oid_file3 = ObjectId("64b000000000000000000038")
        oid_target = ObjectId("64b000000000000000000032")
        oid_target2 = ObjectId("64b000000000000000000039")
        oid_source = ObjectId("64b000000000000000000033")
        oid_source2 = ObjectId("64b00000000000000000003a")
        oid_source3 = ObjectId("64b00000000000000000003b")
        oid_translation = ObjectId("64b000000000000000000034")
        oid_translation2 = ObjectId("64b00000000000000000003c")
        oid_tip = ObjectId("64b000000000000000000035")
        oid_tip2 = ObjectId("64b00000000000000000003d")
        return {
            "files": [
                {
                    "_id": oid_file,
                    "sa": "封面 😀.png",
                    "md": Int64(1700000000123),
                    "t": 2,
                    "v": 3,
                },
                {
                    "_id": oid_file2,
                    "sa": "第2页 ßσ.png",
                    "md": Int64(1700000000124),
                    "t": 2,
                    "v": 3,
                },
                {
                    "_id": oid_file3,
                    "sa": "legacy <&>.png",
                    "md": None,
                    "t": 2,
                    "v": 3,
                },
            ],
            "targets": [
                {"_id": oid_target, "l": "简体中文"},
                {"_id": oid_target2, "l": "日本語"},
            ],
            "sources": [
                {
                    "_id": oid_source,
                    "f": oid_file,
                    "x": 12.5,
                    "y": Decimal128("34.250"),
                    "n": None,
                    "big": Int64(2**40),
                    "u": "ßσς 😀 <&>",
                },
                {
                    "_id": oid_source2,
                    "f": oid_file2,
                    "x": 0,
                    "y": Decimal128("-1.500"),
                    "n": None,
                    "big": Int64(2**41),
                    "u": "第二页",
                },
                {
                    "_id": oid_source3,
                    "f": oid_file3,
                    "x": -1.5,
                    "y": Decimal128("2.000"),
                    "n": None,
                    "big": Int64(2**42),
                    "u": "legacy",
                },
            ],
            "translations": [
                {
                    "_id": oid_translation,
                    "o": oid_source,
                    "c": "翻译\n第二行 😀",
                },
                {
                    "_id": oid_translation2,
                    "o": oid_source2,
                    "c": "翻訳\n二行目",
                },
            ],
            "tips": [
                {"_id": oid_tip, "o": oid_source, "n": None},
                {"_id": oid_tip2, "o": oid_source2, "n": "注"},
            ],
        }

    @contextmanager
    def fake_label_lock(pk: Any) -> Iterable[None]:
        yield

    class ProjectStub:
        pk = ObjectId("64b000000000000000000030")
        label_recovery = False
        label_revision = 7

        def reload(self) -> None:
            return None

    class ActorStub:
        pk = ObjectId("64b000000000000000000036")
        name = "fixture actor 😀"

    class LabelOperationError(Exception):
        pass

    namespace, blocks = _sandbox(
        root,
        [
            ("app/services/label_snapshot.py", "data_digest", None),
            ("app/services/label_snapshot.py", "create_snapshot", None),
        ],
        {
            "datetime": datetime,
            "hashlib": hashlib,
            "BSON": BSON,
            "Binary": Binary,
            "ObjectId": ObjectId,
            "label_lock": fake_label_lock,
            "label_data": fake_label_data,
            "collections": fake_collections,
            "VERSION": 1,
            "CHUNK_BYTES": 200,
            "LabelOperationError": LabelOperationError,
        },
        "<legacy-reference:label-snapshot>",
    )
    project = ProjectStub()
    actor = ActorStub()
    namespace["create_snapshot"](project, actor, "金标快照 😀", reason="fixture")
    metadata = snapshots.inserted[0]
    chunk_rows = []
    for chunk in chunks.inserted:
        encoded = bytes(chunk["data"])
        decoded = BSON.decode(encoded)
        chunk_rows.append(
            {
                "index": chunk["index"],
                "kind": chunk["kind"],
                "byte_length": len(encoded),
                "sha256": sha256_bytes(encoded),
                "stored_hash": chunk["hash"],
                "b64": base64.b64encode(encoded).decode("ascii"),
                "rows": _jsonable(decoded["rows"]),
            }
        )
    row_bson = {}
    for kind, rows in fake_label_data(project).items():
        if not isinstance(rows, list):
            continue
        entries = []
        for row in rows:
            encoded = BSON.encode(row)
            entries.append(
                {
                    "b64": base64.b64encode(encoded).decode("ascii"),
                    "sha256": sha256_bytes(encoded),
                    "byte_length": len(encoded),
                    "field_order": list(row.keys()),
                    "field_types": {key: _bson_type(value) for key, value in row.items()},
                }
            )
        row_bson[kind] = entries
    return {
        "chunk_bytes_override": 200,
        "metadata": {
            "name": metadata.get("name"),
            "version": metadata.get("version"),
            "reason": metadata.get("reason"),
            "ready": metadata.get("ready"),
            "digest": metadata.get("digest"),
            "counts": metadata.get("counts"),
            "revision": metadata.get("revision"),
        },
        "chunk_count": len(chunks.inserted),
        "chunks": chunk_rows,
        "row_bson": row_bson,
    }, blocks


def _bson_typed_fixture() -> Dict[str, Any]:
    fixed_oid = ObjectId("64b000000000000000000041")
    documents = [
        (
            "ordered_mixed",
            {
                "z": 1,
                "a": Int64(2**40),
                "m": Decimal128("123.456"),
                "n": None,
                "o": fixed_oid,
                "u": "ßσς 😀 <&>",
                "arr": [1, "x"],
            },
        ),
        (
            "scalar_types",
            {
                "int32": 42,
                "int64": Int64(42),
                "double": 1.5,
                "bool": True,
                "date": datetime.datetime(2020, 1, 2, 3, 4, 5, 678000),
                "nested": {"b": 2, "a": 1},
            },
        ),
    ]
    return {"cases": [_encode_case(name, doc) for name, doc in documents]}


def build_goldens(python_root: Optional[Path] = None) -> Dict[str, Any]:
    """Build all fixtures from extracted reference definitions only."""

    root = Path(python_root) if python_root is not None else DEFAULT_PYTHON_ROOT
    root = root.resolve()
    assert_no_app_imported()
    with forbid_app_imports():
        with network_disabled() as attempts:
            filename, filename_blocks = _filename_fixture(root)
            labelplus_load, load_blocks = _labelplus_load_fixture(root)
            labelplus_export, export_blocks = _labelplus_export_fixture(root)
            notification, notification_blocks = _notification_fixture(root)
            file_order, order_blocks = _file_order_fixture(root)
            label_snapshot, snapshot_blocks = _label_snapshot_fixture(root)
            bson_typed = _bson_typed_fixture()
    assert_no_app_imported()
    if attempts:
        raise RuntimeError(f"reference generation attempted network: {attempts}")
    blocks = (
        filename_blocks
        + load_blocks
        + export_blocks
        + notification_blocks
        + order_blocks
        + snapshot_blocks
    )
    return {
        "schema": 1,
        "generated_by": "tools/legacy-reference/generate_goldens.py",
        "reference": {
            "root": "shoreline-moeflow-backend (sibling of the C# repository)",
            "extraction": EXTRACTION_NOTE,
            "network_guard": "socket, DNS, urllib and http.client disabled; zero attempts",
        },
        "runtime": {
            "python": platform.python_version(),
            "pymongo": pymongo.version,
            "bson_module": "bson (pymongo distribution)",
        },
        "provenance": provenance_for(blocks),
        "fixtures": {
            "filename": filename,
            "labelplus_load": labelplus_load,
            "labelplus_export": labelplus_export,
            "notification": notification,
            "file_order": file_order,
            "label_snapshot": label_snapshot,
            "bson_typed": bson_typed,
        },
    }
