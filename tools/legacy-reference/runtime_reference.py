#!/usr/bin/env python3
"""Run a disposable, offline Python Flask reference runtime.

This tool copies only explicitly allowed public source files from the legacy
Python backend into a temporary tree, writes a synthetic configuration, opens
one owned local MongoDB database, imports the real application package, and
executes a small matrix of requests through Flask's in-process test client.

It never imports the source-tree ``app`` package, never reads dotenv/config
files, never starts a listening server, and never contacts a non-local network
service.  The result is a concrete HTTP/BSON fixture for comparing the C#
port against real legacy behaviour.

Usage (from the C# repository root, with the private runtime venv):

    C:\\...\\flask-reference-runtime-20261109\\venv\\Scripts\\python.exe \\
        tools/legacy-reference/runtime_reference.py

The MongoDB database name is always ``shoreline_reference_<uuid>`` and is
dropped in a guarded ``finally`` block.  No other database is touched.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import json
import os
import platform
import re
import shutil
import socket
import sys
import tempfile
import traceback
import uuid
from importlib import metadata
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PYTHON_ROOT = REPO_ROOT.parent / "shoreline-moeflow-backend"
DEFAULT_OUTPUT = Path(__file__).resolve().parent / "fixtures" / "runtime_flask_reference.json"
APPROVED_TEMP_ROOT = Path(
    os.environ.get(
        "SHORELINE_REFERENCE_TEMP_ROOT",
        r"C:\Users\88888888\AppData\Local\Temp\opencode",
    )
).resolve()

MONGO_HOST = "127.0.0.1"
DEFAULT_MONGO_PORT = 27128
TEST_MONGO_PORTS = frozenset(range(27281, 27290))
DB_PREFIX = "shoreline_reference_"
DB_RE = re.compile(r"^shoreline_reference_[0-9a-f]{32}$")

FIXED_TIME = dt.datetime(2026, 1, 1, 0, 0, 0)
FIXED_IDS = {
    "creator_role": "650000000000000000000001",
    "beginner_role": "650000000000000000000002",
    "admin_role": "650000000000000000000003",
    "senior_role": "650000000000000000000004",
    "member_role": "650000000000000000000005",
    "owner": "650000000000000000000101",
    "applicant": "650000000000000000000102",
    "team": "650000000000000000000201",
    "ja": "650000000000000000000301",
    "en": "650000000000000000000302",
    "term_bank": "650000000000000000000401",
    # ObjectId timestamp 2026-01-01T00:00:00Z; Application.create_time is
    # derived from this id, so keep the clock and the id aligned.
    "application": "6955b9000000000000000501",
}

# Only public application source directories are copied.  config.py is
# intentionally absent: a generated synthetic config is supplied instead.
ALLOWED_TOP_FILES = {"__init__.py", "factory.py"}
ALLOWED_APP_DIRS = {
    "apis",
    "constants",
    "core",
    "decorators",
    "exceptions",
    "models",
    "regexs",
    "services",
    "tasks",
    "utils",
    "validators",
}
# Optional modules and translation catalogs are deliberately not copied.
ALLOWED_SUBPACKAGE_INIT = {"modules", "translations"}
FORBIDDEN_DIRS = {"migrations", "scripts", "static", "templates"}


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_text(text: str) -> str:
    return sha256_bytes(text.encode("utf-8"))


def assert_owned_database(database: Any, db_name: str) -> str:
    """Reject any database that is not the exact generated owned name.

    This is intentionally a metadata-only check: it runs before seeding and
    before any collection query, so a mismatch cannot touch foreign data.
    """

    if DB_RE.fullmatch(db_name) is None:
        raise RuntimeError("refusing to use a database name outside the owned namespace")
    actual_name = getattr(database, "name", None)
    if not isinstance(actual_name, str):
        raise RuntimeError(f"database object has no string name: {actual_name!r}")
    if actual_name != db_name or DB_RE.fullmatch(actual_name) is None:
        raise RuntimeError(
            "refusing to use unexpected database "
            f"{actual_name!r}; expected {db_name!r}"
        )
    return actual_name


def resolve_output_path(value: Path) -> Path:
    """Make the fixture destination absolute before the process chdirs."""

    return value.expanduser().resolve()


def resolve_temp_parent(value: Path) -> Path:
    """Require the disposable tree to live under the approved temp root."""

    parent = value.expanduser().resolve()
    if parent != APPROVED_TEMP_ROOT and APPROVED_TEMP_ROOT not in parent.parents:
        raise RuntimeError(
            f"refusing temp parent outside approved root {APPROVED_TEMP_ROOT}: {parent}"
        )
    parent.mkdir(parents=True, exist_ok=True)
    return parent


def drop_owned_database(client: Any, db_name: str) -> None:
    """Drop one exact owned database and verify it is gone before success."""

    if DB_RE.fullmatch(db_name) is None:
        raise RuntimeError("refusing to drop a database outside the owned namespace")
    client.drop_database(db_name)
    if db_name in set(client.list_database_names()):
        raise RuntimeError(f"owned database still exists after drop: {db_name}")


def source_allowed(relative: Path) -> bool:
    parts = relative.parts
    if not parts or parts[0] != "app":
        return False
    if "__pycache__" in parts or "config.py" in parts:
        return False
    if len(parts) == 2:
        return parts[1] in ALLOWED_TOP_FILES
    if parts[1] in FORBIDDEN_DIRS:
        return False
    if parts[1] in ALLOWED_SUBPACKAGE_INIT:
        return len(parts) == 3 and parts[2] == "__init__.py"
    return parts[1] in ALLOWED_APP_DIRS and relative.suffix == ".py"


def copy_public_sources(python_root: Path, temp_root: Path) -> list[dict[str, Any]]:
    app_root = python_root / "app"
    if not app_root.is_dir():
        raise RuntimeError(f"Python app directory not found: {app_root}")
    provenance: list[dict[str, Any]] = []
    for source in sorted(app_root.rglob("*.py")):
        relative = source.relative_to(python_root)
        if not source_allowed(relative):
            continue
        destination = temp_root / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
        raw = source.read_bytes()
        provenance.append(
            {
                "path": relative.as_posix(),
                "sha256": sha256_bytes(raw),
                "bytes": len(raw),
            }
        )
    if not provenance:
        raise RuntimeError("no public source files matched the allowlist")
    # Production images compile gettext catalogs before start (Dockerfile runs
    # `make babel-update-mo`). Copy the public .mo build artifacts so runtime
    # responses use the same default-locale strings as a deployed backend.
    for catalog in sorted((app_root / "translations").glob("*/LC_MESSAGES/messages.mo")):
        relative = catalog.relative_to(python_root)
        destination = temp_root / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(catalog, destination)
        raw = catalog.read_bytes()
        provenance.append(
            {
                "path": relative.as_posix(),
                "sha256": sha256_bytes(raw),
                "bytes": len(raw),
            }
        )
    return provenance


def write_synthetic_config(temp_root: Path, db_name: str, mongo_port: int = DEFAULT_MONGO_PORT) -> None:
    values: dict[str, Any] = {
        "DB_URI": f"mongodb://{MONGO_HOST}:{mongo_port}/{db_name}",
        "SECRET_KEY": "reference-only-synthetic-secret-change-me",
        "TESTING": True,
        "DEBUG": False,
        "BUILD_ID": "reference-runtime",
        "BABEL_DEFAULT_LOCALE": "en",
        "BABEL_DEFAULT_TIMEZONE": "UTC",
        "SITE_NAME": "Shoreline Reference",
        "SITE_ORIGIN": "https://reference.invalid",
        "STORAGE_TYPE": "LOCAL_STORAGE",
        "STORAGE_DOMAIN": "https://reference.invalid/storage",
        "DEFAULT_USER_AVATAR": "",
        "DEFAULT_TEAM_AVATAR": "",
        "OSS_ENDPOINT": "",
        "OSS_BUCKET_NAME": "",
        "OSS_BUCKET_STYLE": "S3",
        "OSS_KEY_PREFIX": "",
        "OSS_ACCESS_KEY_ID": "",
        "OSS_ACCESS_KEY_SECRET": "",
        "OSS_VIA_CDN": False,
        "OSS_FILE_PREFIX": "",
        "OSS_OUTPUT_PREFIX": "",
        "OSS_USER_AVATAR_PREFIX": "",
        "OSS_TEAM_AVATAR_PREFIX": "",
        "OSS_PROCESS_COVER_NAME": "",
        "OSS_PROCESS_RESAMPLE_NAME": "",
        "OSS_S3_ADDRESSING_STYLE": "",
        "OSS_S3_REGION": "",
        "OSS_S3_VERIFY_SSL": False,
        "MIT_STORAGE_ROOT": "",
        "BUCKET_NAME": "",
        "CDN_URL_KEY_A": "",
        "APIKIT_PAGINATION_DEFAULT_LIMIT": 10,
        "APIKIT_PAGINATION_MAX_LIMIT": 100,
        "APIKIT_PAGINATION_PAGE_KEY": "page",
        "APIKIT_PAGINATION_LIMIT_KEY": "limit",
        "APIKIT_PAGINATION_HEADER_PAGE_KEY": "X-Pagination-Page",
        "APIKIT_PAGINATION_HEADER_LIMIT_KEY": "X-Pagination-Limit",
        "APIKIT_PAGINATION_HEADER_COUNT_KEY": "X-Pagination-Count",
        "APIKIT_PAGINATION_HEADER_PAGE_COUNT_KEY": "X-Pagination-Page-Count",
        "APIKIT_ACCESS_CONTROL_ALLOW_ORIGIN": "*",
        "APIKIT_ACCESS_CONTROL_ALLOW_HEADERS": "Content-Type,Authorization",
        "APIKIT_ACCESS_CONTROL_EXPOSE_HEADERS": "X-Pagination-Page,X-Pagination-Limit,X-Pagination-Count,X-Pagination-Page-Count",
        "APIKIT_ACCESS_CONTROL_ALLOW_CREDENTIALS": False,
        "APIKIT_ACCESS_CONTROL_MAX_AGE": 3600,
        "CELERY_BROKER_URL": "memory://",
        "CELERY_BACKEND_URL": "cache+memory://",
        "CELERY_BACKEND_SETTINGS": {},
        "EMAIL_ADDRESS": "",
        "EMAIL_ERROR_ADDRESS": "",
        "EMAIL_PASSWORD": "",
        "EMAIL_REPLY_ADDRESS": "",
        "EMAIL_SMTP_HOST": "127.0.0.1",
        "EMAIL_SMTP_PORT": 25,
        "EMAIL_USERNAME": "",
        "EMAIL_USE_SSL": False,
        "ENABLE_LOG_EMAIL": False,
        "ENABLE_NOTIFICATIONS": False,
        "ENABLE_USER_EMAIL": False,
        "NOTIFICATION_EMAIL_ALLOWLIST": [],
        "NOTIFICATION_EMAIL_UNRESTRICTED": False,
        "NOTIFICATION_MAX_AUDIENCE": 100,
        "NOTIFICATION_TRUST_EXISTING_EMAILS": False,
        "GOOGLE_HTTP_PROXY": "",
        "GOOGLE_OCR_API_URL": "",
        "GOOGLE_REVERSE_PROXY_AUTH": "",
        "GOOGLE_STORAGE_MOEFLOW_VISION_TMP": {},
        "GS_URL": "",
        "JSON": {},
        "LOG_PATH": "",
        "MAX_TEAM_ALIASES": 10,
        "MAX_USER_ALIASES": 10,
        "max_team_aliases": 10,
        "max_user_aliases": 10,
        "OUTPUT_WAIT_SECONDS": 1,
        "RESET_EMAIL_WAIT_SECONDS": 3600,
        "RESET_PASSWORD_WAIT_SECONDS": 3600,
        "CONFIRM_EMAIL_WAIT_SECONDS": 3600,
        "ARCHIVE_API_KEY_ENCRYPTION_KEY": "reference-only-synthetic-key",
        "ARCHIVE_PROVIDER_API_ALLOWED_HOSTS": [],
        "THUMBNAIL_SYNC_FALLBACK": False,
        "ADMIN_EMAIL": "",
        "ADMIN_INITIAL_PASSWORD": "",
    }
    lines = [
        '"""Generated synthetic configuration for the reference runtime.',
        "",
        "No production values, credentials, or dotenv files are read or copied.",
        '"""',
        "",
    ]
    for key in sorted(values):
        lines.append(f"{key} = {values[key]!r}")
    lines.append("")
    (temp_root / "app" / "config.py").write_text("\n".join(lines), encoding="utf-8")


def validate_mongo_port(port: int) -> int:
    if port != DEFAULT_MONGO_PORT and port not in TEST_MONGO_PORTS:
        raise ValueError("runtime Mongo port must be historical default 27128 or an owned test port 27281-27289")
    return port


def install_local_socket_guard(mongo_port: int = DEFAULT_MONGO_PORT) -> None:
    """Allow only the owned local MongoDB endpoint.

    The guard is installed before the application package is imported.  It is
    intentionally process-local and does not modify global network settings.
    """

    original_getaddrinfo = socket.getaddrinfo
    original_connect = socket.socket.connect
    original_connect_ex = socket.socket.connect_ex

    def allowed_host_port(host: Any, port: Any) -> bool:
        try:
            port_number = int(port)
        except (TypeError, ValueError):
            return False
        return host == MONGO_HOST and port_number == mongo_port

    def guarded_getaddrinfo(host: Any, port: Any, *args: Any, **kwargs: Any):
        if not allowed_host_port(host, port):
            raise OSError(f"reference runtime blocked DNS/network lookup: {host!r}:{port!r}")
        return original_getaddrinfo(host, port, *args, **kwargs)

    def guarded_connect(self: socket.socket, address: Any):
        if not (
            isinstance(address, tuple)
            and len(address) >= 2
            and allowed_host_port(address[0], address[1])
        ):
            raise OSError(f"reference runtime blocked socket connect: {address!r}")
        return original_connect(self, address)

    def guarded_connect_ex(self: socket.socket, address: Any):
        if not (
            isinstance(address, tuple)
            and len(address) >= 2
            and allowed_host_port(address[0], address[1])
        ):
            raise OSError(f"reference runtime blocked socket connect_ex: {address!r}")
        return original_connect_ex(self, address)

    socket.getaddrinfo = guarded_getaddrinfo  # type: ignore[assignment]
    socket.socket.connect = guarded_connect  # type: ignore[assignment]
    socket.socket.connect_ex = guarded_connect_ex  # type: ignore[assignment]


def package_versions() -> dict[str, str]:
    names = [
        "Flask",
        "mongoengine",
        "pymongo",
        "celery",
        "boto3",
        "oss2",
        "google-cloud-storage",
        "PyJWT",
        "cryptography",
        "Pillow",
        "flask-babel",
        "marshmallow",
        "itsdangerous",
    ]
    result: dict[str, str] = {}
    for name in names:
        try:
            result[name] = metadata.version(name)
        except metadata.PackageNotFoundError:
            result[name] = "not-installed"
    return result


def bson_document(doc: dict[str, Any]) -> dict[str, Any]:
    from bson import BSON

    raw = BSON.encode(doc)
    return {
        "bson_base64": base64.b64encode(raw).decode("ascii"),
        "bson_hex": raw.hex(),
        "sha256": sha256_bytes(raw),
        "bytes": len(raw),
    }


def collection_digest(database: Any, name: str) -> dict[str, Any]:
    from bson import BSON

    collection = database[name]
    docs = list(collection.find({}).sort("_id", 1))
    encoded = [BSON.encode(doc) for doc in docs]
    joined = b"".join(encoded)
    return {
        "count": len(docs),
        "sha256": sha256_bytes(joined),
        "bytes": len(joined),
    }


def database_digest(database: Any) -> dict[str, Any]:
    names = sorted(
        name
        for name in database.list_collection_names()
        if not name.startswith("system.")
    )
    result: dict[str, Any] = {}
    for name in names:
        digest = collection_digest(database, name)
        # Index creation can materialize an empty collection.  Empty
        # collections carry no reference rows, so do not let that incidental
        # side effect look like a data mutation.
        if digest["count"]:
            result[name] = digest
    return result


def seed_reference_data() -> dict[str, Any]:
    from bson import ObjectId
    from app.models.application import Application, ApplicationStatus
    from app.models.language import Language
    from app.models.team import Team, TeamRole
    from app.models.team_member import TeamMember
    from app.models.term import TermBank
    from app.models.user import User

    roles: dict[str, Any] = {}
    role_id_fields = {
        "creator": "creator_role",
        "admin": "admin_role",
        "senior": "senior_role",
        "member": "member_role",
        "beginner": "beginner_role",
    }
    for data in TeamRole.system_role_data:
        code = data["system_code"]
        role = TeamRole(
            id=ObjectId(FIXED_IDS[role_id_fields[code]]),
            _name=data["name"],
            level=data["level"],
            intro="",
            permissions=list(data["permissions"]),
            system=True,
            system_code=code,
            create_time=FIXED_TIME,
        ).save()
        roles[code] = role

    ja = Language(
        id=ObjectId(FIXED_IDS["ja"]),
        en_name="Japanese",
        lo_name="Japanese",
        code="ja",
        g_tra_code="ja",
        g_ocr_code="ja",
        no_space=True,
        sort=1,
    ).save()
    en = Language(
        id=ObjectId(FIXED_IDS["en"]),
        en_name="English",
        lo_name="English",
        code="en",
        g_tra_code="en",
        g_ocr_code="en",
        no_space=False,
        sort=2,
    ).save()

    owner = User(
        id=ObjectId(FIXED_IDS["owner"]),
        email="owner@reference.invalid",
        name="Reference Owner",
        aliases=[],
        default_display_name="Reference Owner",
        signature="reference owner",
        locale="en",
        timezone="UTC",
        _avatar="",
        banned=False,
        password_hash="pbkdf2:sha256:1$reference$deadbeef",
        admin=False,
        create_time=FIXED_TIME,
    ).save()
    applicant = User(
        id=ObjectId(FIXED_IDS["applicant"]),
        email="applicant@reference.invalid",
        name="Reference Applicant",
        aliases=[],
        default_display_name="Reference Applicant",
        signature="reference applicant",
        locale="en",
        timezone="UTC",
        _avatar="",
        banned=False,
        password_hash="pbkdf2:sha256:1$reference$cafebabe",
        admin=False,
        create_time=FIXED_TIME,
    ).save()

    team = Team(
        id=ObjectId(FIXED_IDS["team"]),
        name="Reference Team",
        _avatar="",
        intro="A deterministic reference team",
        default_role=roles["beginner"],
        max_user=100,
        user_count=2,
        worker_qualification_mode="open",
        create_time=FIXED_TIME,
        edit_time=FIXED_TIME,
    ).save()

    TeamMember(
        team=team,
        user=owner,
        base_tag="creator",
        tags=[],
        worker_qualifications=[],
        aliases=[],
        default_display_name="",
        version=0,
        status="active",
        create_time=FIXED_TIME,
        edit_time=FIXED_TIME,
    ).save()
    TeamMember(
        team=team,
        user=applicant,
        base_tag="member",
        tags=[],
        worker_qualifications=[],
        aliases=[],
        default_display_name="",
        version=0,
        status="active",
        create_time=FIXED_TIME,
        edit_time=FIXED_TIME,
    ).save()

    term_bank = TermBank(
        id=ObjectId(FIXED_IDS["term_bank"]),
        team=team,
        name="Reference Glossary",
        source_language=ja,
        target_language=en,
        tip="reference terms only",
        user=owner,
        create_time=FIXED_TIME,
        edit_time=FIXED_TIME,
    ).save()

    application = Application(
        id=ObjectId(FIXED_IDS["application"]),
        user=applicant,
        operator=owner,
        group=team,
        status=ApplicationStatus.ALLOW,
        message="Deterministic approved application",
        user_ids_can_check_user=[owner.id],
    ).save()

    return {
        "team": team,
        "owner": owner,
        "applicant": applicant,
        "term_bank": term_bank,
        "application": application,
        "token": owner.generate_token(),
        "ids": {key: value for key, value in FIXED_IDS.items()},
    }


def response_body(response: Any) -> dict[str, Any]:
    parsed = response.get_json(silent=True)
    if parsed is not None:
        return {"kind": "json", "value": parsed}
    return {"kind": "text", "value": response.get_data(as_text=True)}


def stable_headers(response: Any) -> dict[str, str]:
    names = sorted(
        name
        for name in response.headers.keys()
        if name.lower()
        in {
            "content-type",
            "content-length",
            "x-pagination-page",
            "x-pagination-limit",
            "x-pagination-count",
            "x-pagination-page-count",
            "location",
        }
    )
    return {name: response.headers[name] for name in names}


def run_cases(flask_app: Any, database: Any, token: str) -> list[dict[str, Any]]:
    client = flask_app.test_client()
    auth = {"Authorization": f"Bearer {token}"}
    cases: list[dict[str, Any]] = [
        {
            "name": "ping",
            "method": "GET",
            "path": "/ping",
            "headers": {},
        },
        {
            "name": "missing_token_team_list",
            "method": "GET",
            "path": "/v1/teams",
            "query": {"word": "Reference"},
            "headers": {},
        },
        {
            "name": "user_info",
            "method": "GET",
            "path": "/v1/user/info",
            "headers": auth,
        },
        {
            "name": "team_list_missing_word",
            "method": "GET",
            "path": "/v1/teams",
            "headers": auth,
        },
        {
            "name": "team_list",
            "method": "GET",
            "path": "/v1/teams",
            "query": {"word": "Reference"},
            "headers": auth,
        },
        {
            "name": "term_bank_list",
            "method": "GET",
            "path": f"/v1/teams/{FIXED_IDS['team']}/term-banks",
            "headers": auth,
        },
        {
            "name": "application_list",
            "method": "GET",
            "path": f"/v1/teams/{FIXED_IDS['team']}/applications",
            "headers": auth,
        },
        {
            "name": "user_by_name",
            "method": "GET",
            "path": "/v1/users/Reference%20Owner",
            "headers": auth,
        },
        {
            "name": "my_teams",
            "method": "GET",
            "path": "/v1/user/teams",
            "headers": auth,
        },
        {
            "name": "my_projects",
            "method": "GET",
            "path": "/v1/user/projects",
            "headers": auth,
        },
        {
            "name": "team_detail",
            "method": "GET",
            "path": f"/v1/teams/{FIXED_IDS['team']}",
            "headers": auth,
        },
        {
            "name": "team_members",
            "method": "GET",
            "path": f"/v1/teams/{FIXED_IDS['team']}/members",
            "headers": auth,
        },
        {
            "name": "team_project_sets",
            "method": "GET",
            "path": f"/v1/teams/{FIXED_IDS['team']}/project-sets",
            "headers": auth,
        },
        {
            "name": "term_bank_terms",
            "method": "GET",
            "path": f"/v1/term-banks/{FIXED_IDS['term_bank']}/terms",
            "headers": auth,
        },
        {
            "name": "team_invitations",
            "method": "GET",
            "path": f"/v1/teams/{FIXED_IDS['team']}/invitations",
            "headers": auth,
        },
        {
            "name": "languages",
            "method": "GET",
            "path": "/v1/languages",
            "headers": auth,
        },
        {
            "name": "team_insight_users",
            "method": "GET",
            "path": f"/v1/teams/{FIXED_IDS['team']}/insight/users",
            "headers": auth,
        },
        {
            "name": "team_insight_projects",
            "method": "GET",
            "path": f"/v1/teams/{FIXED_IDS['team']}/insight/projects",
            "headers": auth,
        },
        {
            "name": "method_not_allowed_ping_post",
            "method": "POST",
            "path": "/ping",
            "headers": {},
        },
        {
            "name": "term_bank_create",
            "method": "POST",
            "path": f"/v1/teams/{FIXED_IDS['team']}/term-banks",
            "headers": auth,
            "json": {
                "name": "Differential Glossary",
                "source_language_id": FIXED_IDS["ja"],
                "target_language_id": FIXED_IDS["en"],
                "tip": "created by runtime differential",
            },
        },
    ]

    records: list[dict[str, Any]] = []
    from flask import g

    for index, case in enumerate(cases, start=1):
        # The generator holds one application context for the whole run, so
        # Flask's `g` would otherwise leak `current_user` between requests and
        # make public endpoints look authenticated. Production starts every
        # request with an empty `g`.
        g.pop("current_user", None)
        before = database_digest(database)
        response = client.open(
            case["path"],
            method=case["method"],
            query_string=case.get("query"),
            json=case.get("json"),
            headers={"Accept-Language": "zh", **case["headers"]},
        )
        after = database_digest(database)
        records.append(
            {
                "index": index,
                "name": case["name"],
                "request": {
                    "method": case["method"],
                    "path": case["path"],
                    "query": case.get("query", {}),
                    **({"json": case["json"]} if "json" in case else {}),
                    "accept_language": "zh",
                    "authorization": "redacted" if case["headers"] else "absent",
                },
                "response": {
                    "status": response.status_code,
                    "headers": stable_headers(response),
                    "body": response_body(response),
                },
                "database_before": before,
                "database_after": after,
            }
        )
    return records


def make_fixture(
    provenance: list[dict[str, Any]],
    flask_app: Any,
    database: Any,
    seeded: dict[str, Any],
    db_name: str,
    mongo_port: int = DEFAULT_MONGO_PORT,
) -> dict[str, Any]:
    from bson import BSON

    collections: dict[str, Any] = {}
    for name in sorted(database.list_collection_names()):
        if name.startswith("system."):
            continue
        docs = list(database[name].find({}).sort("_id", 1))
        collections[name] = {
            "count": len(docs),
            "documents": [bson_document(doc) for doc in docs],
        }

    server_version = database.client.server_info().get("version", "unknown")
    cases = run_cases(flask_app, database, seeded["token"])
    return {
        "schema": "shoreline.python-runtime-reference/v1",
        "bootstrap": {
            "python": platform.python_version(),
            "packages": package_versions(),
            "mongodb": server_version,
            "storage": "LOCAL_STORAGE",
            "celery": "memory",
            "mail": "disabled",
            "optional_modules": "disabled",
            "translations": "not copied; passthrough/noop",
            "network_guard": f"only 127.0.0.1:{mongo_port}",
            "synthetic_config": "generated app/config.py; no production config read",
        },
        "database": {
            "name": db_name,
            "owned": True,
        },
        "fixed_inputs": {
            "utc_datetime": FIXED_TIME.isoformat() + "Z",
            "ids": FIXED_IDS,
        },
        "source_provenance": provenance,
        "seed_collections": collections,
        "cases": cases,
        "coverage": {
            "cases": len(cases),
            "http": [
                "ping text response",
                "missing token error 1003",
                "authenticated user info",
                "validation error for missing word",
                "team list with pagination metadata",
                "term bank list",
                "approved application with applicant role",
                "user by name",
                "my teams and my projects",
                "team detail, members, project sets, invitations",
                "term bank term list",
                "application detail",
                "language catalog",
                "team insight users and projects",
                "405 method not allowed",
            ],
            "not_covered": [
                "actual mail delivery or registration",
                "real OSS/R2/Google storage",
                "optional modules",
                "translation catalogs",
                "write requests and mutation semantics",
                "C# differential comparison",
            ],
        },
    }


def write_fixture_atomic(path: Path, fixture: dict[str, Any]) -> None:
    """Write the final fixture only after cleanup has been verified."""

    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        temporary.write_text(
            json.dumps(fixture, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--python-root", type=Path, default=DEFAULT_PYTHON_ROOT)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--temp-parent",
        type=Path,
        default=APPROVED_TEMP_ROOT,
        help="a directory under the approved disposable temp root",
    )
    parser.add_argument("--keep-temp", action="store_true")
    parser.add_argument("--mongo-port", type=int, default=DEFAULT_MONGO_PORT)
    args = parser.parse_args()
    mongo_port = validate_mongo_port(args.mongo_port)

    python_root = args.python_root.expanduser().resolve()
    output = resolve_output_path(args.output)
    temp_parent = resolve_temp_parent(args.temp_parent)
    db_name = f"{DB_PREFIX}{uuid.uuid4().hex}"
    if not DB_RE.fullmatch(db_name):
        raise RuntimeError("refusing to use a database name outside the owned namespace")
    temp_root = Path(
        tempfile.mkdtemp(prefix="shoreline-flask-reference-", dir=temp_parent)
    )
    (temp_root / "storage").mkdir(parents=True, exist_ok=True)
    (temp_root / "files" / "tmp").mkdir(parents=True, exist_ok=True)

    drop_client = None
    db_owned = False
    try:
        provenance = copy_public_sources(python_root, temp_root)
        write_synthetic_config(temp_root, db_name, mongo_port)
        install_local_socket_guard(mongo_port)
        os.environ["APP_ROLE"] = "web"
        os.environ.pop("LOG_LEVEL", None)
        sys.path.insert(0, str(temp_root))
        os.chdir(temp_root)

        from pymongo import MongoClient

        drop_client = MongoClient(
            f"mongodb://{MONGO_HOST}:{mongo_port}/",
            serverSelectionTimeoutMS=3000,
        )
        drop_client.admin.command("ping")

        import app as reference_app

        flask_app = reference_app.create_app()
        from mongoengine.connection import get_db

        with flask_app.app_context():
            database = get_db()
            assert_owned_database(database, db_name)
            db_owned = True
            seeded = seed_reference_data()
            fixture = make_fixture(
                provenance, flask_app, database, seeded, db_name, mongo_port
            )

        # Write the fixture only after the exact owned database has actually
        # been dropped and the drop has been verified.
        drop_owned_database(drop_client, db_name)
        fixture["database"]["cleanup"] = "dropped and verified before fixture write"
        write_fixture_atomic(output, fixture)
        print(f"dropped and verified owned database: {db_name}")
        print(f"fixture: {output}")
        print(f"temp tree: {temp_root}")
        print(f"owned database: {db_name}")
        print(f"cases: {len(fixture['cases'])}")
        for case in fixture["cases"]:
            print(
                f"  {case['index']:02d} {case['request']['method']} "
                f"{case['request']['path']} -> {case['response']['status']}"
            )
        return 0
    finally:
        if drop_client is not None and db_owned and DB_RE.fullmatch(db_name):
            try:
                if db_name in set(drop_client.list_database_names()):
                    drop_client.drop_database(db_name)
                    if db_name in set(drop_client.list_database_names()):
                        raise RuntimeError(
                            f"owned database still exists after cleanup: {db_name}"
                        )
                    print(f"cleanup dropped owned database: {db_name}", file=sys.stderr)
            except Exception as cleanup_error:
                print(
                    f"cleanup failed for owned database {db_name}: {cleanup_error}",
                    file=sys.stderr,
                )
        if not args.keep_temp:
            shutil.rmtree(temp_root, ignore_errors=True)
        else:
            print(f"kept temp tree: {temp_root}")


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception:
        traceback.print_exc()
        raise SystemExit(1)
