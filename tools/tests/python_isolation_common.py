"""Shared pieces for the isolated Python suite harness.

Nothing here imports the legacy ``app`` package, reads a dotenv file or opens
a network connection; it is pure stdlib and operates on synthetic text or on
trees the caller hands in.

Key distinction (learned from the real snapshot):
``key=value`` inside a ``*.sample`` template is an *environment assignment* and
``key=value`` inside code is a *code reference*; only the first can carry a
credential literal.  Treating both as assignments floods the scan with 450+
false positives and hides the one finding that matters.
"""

from __future__ import annotations

import hashlib
import os
import re
import secrets
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

TOOL_VERSION = "2"

# ---------------------------------------------------------------------------
# Environment hygiene
# ---------------------------------------------------------------------------

# Variables removed from every child environment: they would (re)enable
# implicit dotenv handling, inject pytest plugins/flags, or point the
# interpreter at a foreign tree.
FORBIDDEN_ENV_EXACT = {
    "PYTEST_PLUGINS",
    "PYTEST_ADDOPTS",
    "PYTEST_DEBUG",
    "PYTEST_CURRENT_TEST",
    "PYTHONSTARTUP",
    "PYTHONHOME",
    "PYTHONINSPECT",
    "PYTHONBREAKPOINT",
    "PYTEST_DOTENV_PATH",
    "DOTENV",
    "APP_ROLE",
    "FLASK_APP",
}
FORBIDDEN_ENV_PREFIXES = ("PYTEST_", "DOTENV_", "CONDA_", "VIRTUAL_ENV")
# ``PYTHONPATH`` and ``VIRTUAL_ENV`` are never inherited; the harness pins its
# own value so a foreign interpreter tree cannot win.
ENV_REPLACED = {"PYTHONPATH": ""}

# Fixed pytest invocation.  No caller-supplied flags reach pytest, so
# ``-p dotenv`` / ``-c other.ini`` / ``-k`` style bypasses are impossible.
PYTEST_BASE_ARGS = (
    "-p",
    "no:dotenv",
    "-o",
    "env_files=",
    "-o",
    "addopts=",
    "-o",
    "cache_dir=.pyiso-cache",
    "--color=no",
    "-q",
)


def normalise_env(
    source: Iterable[tuple[str, str]], extra_drop: Iterable[str] = ()
) -> dict[str, str]:
    drop = set(extra_drop)
    env: dict[str, str] = {}
    for key, value in source:
        if key in drop or key in FORBIDDEN_ENV_EXACT:
            continue
        if key.upper().startswith(FORBIDDEN_ENV_PREFIXES):
            continue
        env[key] = value
    env.update(ENV_REPLACED)
    return env


def dotenv_autoload_disabled(env: dict[str, str]) -> bool:
    if "DOTENV" in env:
        return False
    return not any(key.startswith("DOTENV_") for key in env)


# ---------------------------------------------------------------------------
# Snapshot scanning (pure functions, no git)
# ---------------------------------------------------------------------------

DOTENV_NAME_RE = re.compile(r"^\.env(\..*)?$")
CONFIG_LOCAL_RE = re.compile(r"^config_local\.py$")
URL_WITH_CREDENTIALS_RE = re.compile(
    r"(?i)\b(?:mongodb(?:\+srv)?|amqp|rediss?|smtp)://"
    r"[^/\s:@]+:(?P<secret>[^@/\s\"']{6,})@"
)
CREDENTIAL_KEY_RE = re.compile(
    r"(?i)^[A-Za-z0-9_.]*?"
    r"(SECRET_KEY|SECRET|PASSWORD|PASSWD|PASSPHRASE|TOKEN|API_KEY|"
    r"APIKEY|ACCESS_KEY_ID|ACCESS_KEY_SECRET|SECRET_ACCESS_KEY|"
    r"ACCESS_KEY|PRIVATE_KEY|CLIENT_SECRET|CLIENT_ID|CREDENTIALS?|"
    r"AUTH_KEY|ENCRYPTION_KEY)$"
)
# Shapes that are placeholders in this repository and in well-known sample
# files: ``CHANGE_ME``, ``PLEASE_CHANGE_THIS``, ``<your-key>``, ``${VAR}``.
PLACEHOLDER_RE = re.compile(
    r"(?i)^(?:(?:change|please[_-]?change|replace|insert|your|my|some)[_-]?"
    r"(?:me|this|here|it|value|token|key|password|credentials?)[-_a-z0-9]*"
    r"|change[_-]?me|placeholder|example|dummy|fake|sample|test|testing|local"
    r"[-_a-z0-9]*|none|null|nil|empty|unset|to[_-]be[_-]set|x{3,}|0+"
    r"|<.*>|\$\{.*\}|\$\(.*\)|\{\{.*\}\})$"
)
SCANNABLE_SUFFIXES = {
    ".py",
    ".md",
    ".txt",
    ".yml",
    ".yaml",
    ".ini",
    ".cfg",
    ".toml",
    ".json",
    ".sh",
    ".js",
    ".ts",
    ".jsx",
    ".tsx",
    ".rst",
    ".html",
    ".jinja",
    ".j2",
    ".mako",
    ".sample",
    ".example",
    ".pot",
}
SCAN_SKIP_DIRS = {"files", "node_modules", "vendor", "artifacts", "storage"}
MAX_SCAN_BYTES = 2 * 1024 * 1024


@dataclass(frozen=True)
class ScanFinding:
    rule: str
    path: str
    line: int
    detail: str
    blocking: bool

    def as_dict(self) -> dict[str, Any]:
        return {
            "rule": self.rule,
            "path": self.path,
            "line": self.line,
            "detail": self.detail,
            "blocking": self.blocking,
        }


def classify_entry(relative_path: str) -> ScanFinding | None:
    """Directory-level rule for one snapshot path (relative, POSIX style)."""
    name = relative_path.rsplit("/", 1)[-1]
    if CONFIG_LOCAL_RE.match(name):
        return ScanFinding(
            "config-local",
            relative_path,
            0,
            "optional-module local config (may hold keys)",
            True,
        )
    if DOTENV_NAME_RE.match(name):
        if name.endswith(".sample"):
            return ScanFinding(
                "dotenv-sample",
                relative_path,
                0,
                "committed dotenv template; values are scanned",
                False,
            )
        return ScanFinding(
            "dotenv-file",
            relative_path,
            0,
            "dotenv file inside the commit",
            True,
        )
    return None


def is_placeholder(value: str) -> bool:
    candidate = value.strip().strip("'\"")
    if not candidate:
        return True
    if PLACEHOLDER_RE.match(candidate):
        return True
    return bool(re.match(r"(?i)^(?:change|replace|please)[_-]?me", candidate))


def is_secret_candidate(candidate: str) -> bool:
    value = candidate.strip().strip("'\"")
    if len(value) < 8:
        return False
    if is_placeholder(value):
        return False
    if value.startswith(("http://", "https://", "mongodb://", "amqp://", "wss://")):
        return False
    if re.fullmatch(r"(?:[A-Z_]+|\d+|True|False|None)", value):
        return False
    return True


def is_literal_secret(value: str) -> bool:
    """True when an assigned value is a complete literal credential.

    The key already matched `CREDENTIAL_KEY_RE`, so only the value shape is
    decided here.  Quoted literals are inspected directly; an unquoted value
    that is not obviously literal data (`42`, `2026-01-01`) is code and is
    ignored.
    """
    assigned = value.strip()
    if not assigned:
        return False
    quoted = re.match(r"^(['\"])(?P<inner>.+?)\1\s*(?:[#;].*)?$", assigned)
    if quoted:
        return is_secret_candidate(quoted.group("inner"))
    if re.match(r"^[0-9][0-9A-Za-z_.:/+=~-]*$", assigned):
        return is_secret_candidate(assigned)
    return False


def credential_key(key: str) -> bool:
    return bool(CREDENTIAL_KEY_RE.match(key.strip().strip("'\"")))


ENV_LINE_RE = re.compile(
    r"(?i)^(?:export\s+)?(?P<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?P<value>.*)$"
)


def scan_environment_text(relative_path: str, text: str) -> list[ScanFinding]:
    """Scan a dotenv-style file: every `KEY=value` is an assignment."""
    findings: list[ScanFinding] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        match = ENV_LINE_RE.match(stripped)
        if not match:
            continue
        key, value = match.group("key"), match.group("value")
        if not credential_key(key):
            continue
        if is_literal_secret(value):
            findings.append(
                ScanFinding(
                    "assigned-secret",
                    relative_path,
                    line_number,
                    f"{key} holds a literal value",
                    True,
                )
            )
    return findings


PY_ASSIGN_RE = re.compile(
    r"^(?P<key>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*=\s*(?P<value>.+)$"
)
YAML_KV_RE = re.compile(r"^(?P<key>[A-Za-z_][A-Za-z0-9_.-]*)\s*:\s*(?P<value>.+)$")


def scan_code_text(relative_path: str, text: str) -> list[ScanFinding]:
    """Scan source/config files: only a real assignment with a literal.

    `SECRET_KEY = env['SECRET_KEY']` is a reference and is ignored;
    `SECRET_KEY = 'abcdef...'` (quoted literal >= 8 chars, not a placeholder)
    is reported.  Keyword arguments (`token=str(token)`) and comparisons are
    code, not assignments, and are not reported.
    """
    findings: list[ScanFinding] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        stripped = line.strip()
        if not stripped or stripped.startswith(("#", "//")):
            continue
        match = PY_ASSIGN_RE.match(stripped) or YAML_KV_RE.match(stripped)
        if not match:
            continue
        key, value = match.group("key"), match.group("value")
        if not credential_key(key.split(".")[-1]):
            continue
        if not is_literal_secret(value):
            continue
        # A literal inside the test tree is disposable fixture data (for
        # example ``new_password111111``).  It is recorded as evidence, but it
        # cannot leak a deployment credential, so it does not block the run.
        # Anywhere else a literal credential is a real finding.
        fixture = is_test_path(relative_path)
        findings.append(
            ScanFinding(
                "test-fixture-literal" if fixture else "assigned-secret",
                relative_path,
                line_number,
                f"{key} holds a literal value",
                not fixture,
            )
        )
    return findings


TOKEN_PATTERNS = (
    ("private-key-block", re.compile(r"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----")),
    ("aws-access-key-id", re.compile(r"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")),
    ("provider-token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{20,}\b")),
    ("provider-token", re.compile(r"\bsk-(?:live|prod|ant)-[A-Za-z0-9_-]{16,}\b")),
    ("url-credentials", URL_WITH_CREDENTIALS_RE),
)


def scan_tokens(relative_path: str, text: str) -> list[ScanFinding]:
    """Shape-based rules that apply to every scannable file."""
    findings: list[ScanFinding] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        for rule, pattern in TOKEN_PATTERNS:
            match = pattern.search(line)
            if not match:
                continue
            if rule == "url-credentials" and not is_secret_candidate(
                match.group("secret")
            ):
                continue
            findings.append(
                ScanFinding(
                    rule,
                    relative_path,
                    line_number,
                    "credential-looking literal",
                    True,
                )
            )
    return findings


def is_test_path(relative_path: str) -> bool:
    """True for files inside a test tree (``tests/`` or ``test_*.py``)."""
    parts = relative_path.split("/")
    if "tests" in parts or "test" in parts:
        return True
    return parts[-1].startswith("test_") or parts[-1].endswith("_test.py")


def is_environment_file(relative_path: str) -> bool:
    name = relative_path.rsplit("/", 1)[-1]
    return name.endswith((".sample", ".example", ".env")) or bool(
        DOTENV_NAME_RE.match(name)
    )


def scan_text(relative_path: str, text: str) -> list[ScanFinding]:
    findings = list(scan_tokens(relative_path, text))
    findings.extend(scan_code_text(relative_path, text))
    return findings


def scan_directory(root: Path) -> list[ScanFinding]:
    findings: list[ScanFinding] = []
    root = root.resolve()
    for current, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in SCAN_SKIP_DIRS)
        current_path = Path(current)
        for filename in sorted(filenames):
            path = current_path / filename
            relative = path.relative_to(root).as_posix()
            entry = classify_entry(relative)
            if entry is not None:
                findings.append(entry)
                if entry.blocking:
                    continue
            suffix = path.suffix.lower()
            if suffix not in SCANNABLE_SUFFIXES and not DOTENV_NAME_RE.match(filename):
                continue
            try:
                if path.stat().st_size > MAX_SCAN_BYTES:
                    continue
                text = path.read_text(encoding="utf-8", errors="replace")
            except OSError as error:  # pragma: no cover - unreadable entry
                findings.append(
                    ScanFinding("unreadable", relative, 0, str(error), False)
                )
                continue
            findings.extend(scan_text(relative, text))
    return findings


def blocking_findings(findings: Iterable[ScanFinding]) -> list[ScanFinding]:
    return [finding for finding in findings if finding.blocking]


# ---------------------------------------------------------------------------
# Fixture rendering/validation (pure functions)
# ---------------------------------------------------------------------------


class GateFailure(RuntimeError):
    """A safety gate refused the run.  Never treated as a test failure."""


def render_fixture(template: str, values: dict[str, str], drop: Iterable[str]) -> str:
    drop_set = set(drop)
    lines_out: list[str] = []
    written: set[str] = set()
    for line in template.splitlines():
        match = re.match(r"^([A-Za-z_][A-Za-z0-9_]*)=(.*)$", line)
        if not match:
            lines_out.append(line)
            continue
        key = match.group(1)
        if key in drop_set:
            continue
        if key in values:
            lines_out.append(f"{key}={values[key]}")
            written.add(key)
        else:
            lines_out.append(line)
    missing = [key for key in values if key not in written]
    if missing:
        lines_out.append("# ---- isolated harness overrides (generated) ----")
        lines_out.extend(f"{key}={values[key]}" for key in missing)
    return "\n".join(lines_out) + "\n"


def parse_dotenv_text(text: str) -> dict[str, str]:
    values: dict[str, str] = {}
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        match = re.match(r"^([A-Za-z_][A-Za-z0-9_]*)=(.*)$", line)
        if not match:
            continue
        key, value = match.group(1), match.group(2).strip()
        if value.startswith("#"):
            value = ""
        elif len(value) >= 2 and value[0] in {"'", '"'} and value[-1] == value[0]:
            value = value[1:-1]
        else:
            value = re.split(r"\s+#", value, maxsplit=1)[0].strip()
        values[key] = value
    return values


DB_NAME_RE = re.compile(r"^moeflow_pyiso_[0-9a-f]{8,32}_test$")
REQUIRED_SETTINGS = {
    "TESTING": "YES",
    "CELERY_BROKER_URL": "memory://",
    "STORAGE_TYPE": "LOCAL_STORAGE",
    "ENABLE_USER_EMAIL": "False",
    "ENABLE_LOG_EMAIL": "False",
}
MUST_BE_EMPTY = (
    "OSS_ENDPOINT",
    "OSS_ACCESS_KEY_ID",
    "OSS_ACCESS_KEY_SECRET",
    "GOOGLE_OCR_API_KEY",
    "EMAIL_PASSWORD",
    "EMAIL_SMTP_HOST",
    "EMAIL_USERNAME",
    "MONGODB_USER",
    "MONGODB_PASS",
    "RABBITMQ_USER",
    "RABBITMQ_PASS",
)
LEGACY_CRED_KEYS = (
    "MONGODB_USER",
    "MONGODB_PASS",
    "RABBITMQ_USER",
    "RABBITMQ_PASS",
    "RABBITMQ_VHOST_NAME",
)


def new_db_name() -> str:
    return f"moeflow_pyiso_{secrets.token_hex(4)}_test"


def fixture_violations(
    values: dict[str, str], db_name: str, storage_dir: Path
) -> list[str]:
    problems: list[str] = []
    for key, expected in REQUIRED_SETTINGS.items():
        if values.get(key) != expected:
            problems.append(f"{key} must be {expected!r}")
    uri = values.get("MONGODB_URI", "")
    if not uri.startswith("mongomock://"):
        problems.append("MONGODB_URI must use the mongomock:// scheme")
    elif not uri.endswith(f"/{db_name}?authSource=admin"):
        problems.append(f"MONGODB_URI must target database {db_name!r}")
    if not DB_NAME_RE.match(db_name):
        problems.append("database name is not an owned isolated name")
    if values.get("CELERY_BACKEND_URL", ""):
        problems.append("CELERY_BACKEND_URL must be empty")
    for key in MUST_BE_EMPTY:
        if values.get(key, ""):
            problems.append(f"{key} must be empty")
    storage_domain = values.get("STORAGE_DOMAIN", "")
    expected_prefix = f"file://{storage_dir.as_posix()}"
    if not storage_domain.startswith(expected_prefix):
        problems.append("STORAGE_DOMAIN must point into the isolated storage directory")
    if values.get("OSS_VIA_CDN") == "True":
        problems.append("OSS_VIA_CDN must be disabled")
    if values.get("EMAIL_USE_SSL") == "True":
        problems.append("EMAIL_USE_SSL must be disabled in tests")
    return problems


def isolated_settings(
    config: Any,
    storage_dir: Path,
    db_name: str,
    token: str,
    secrets_: tuple[str, str, str, str],
) -> dict[str, str]:
    """Every legacy setting the suite may see.  Nothing is inherited.

    ``secrets_`` is ``(mongo_password, secret_key, admin_password, dummy)``.
    """
    mongo_password, secret_key, admin_password, _ = secrets_
    api = f"http://127.0.0.1:{config.port_api}"
    return {
        "TESTING": "YES",
        "LOG_LEVEL": "WARNING",
        "SITE_NAME": "shoreline-pyiso",
        "SITE_ORIGIN": api,
        "SECRET_KEY": secret_key,
        "ADMIN_EMAIL": "admin@pyiso.invalid",
        "ADMIN_INITIAL_PASSWORD": admin_password,
        "MONGODB_URI": (
            f"mongomock://pyiso:{mongo_password}"
            f"@127.0.0.1:{config.port_mongo}/{db_name}?authSource=admin"
        ),
        "CELERY_BROKER_URL": "memory://",
        "STORAGE_TYPE": "LOCAL_STORAGE",
        "STORAGE_DOMAIN": f"file://{storage_dir.as_posix()}/",
        "MAX_CONTENT_LENGTH_MB": "64",
        "OSS_BUCKET_STYLE": "S3",
        "OSS_KEY_PREFIX": "storage/",
        "OSS_S3_REGION": "auto",
        "OSS_S3_ADDRESSING_STYLE": "path",
        "OSS_S3_VERIFY_SSL": "True",
        "OSS_VIA_CDN": "False",
        "OSS_PROCESS_COVER_NAME": "cover",
        "ENABLE_USER_EMAIL": "False",
        "ENABLE_LOG_EMAIL": "False",
        "EMAIL_USE_SSL": "False",
        # A loopback port nothing listens on: real SMTP attempts fail fast
        # instead of reaching an inherited host.
        "EMAIL_SMTP_PORT": str(config.port_smtp),
        "EMAIL_ADDRESS": "noreply@pyiso.invalid",
        "EMAIL_REPLY_ADDRESS": "noreply@pyiso.invalid",
        "EMAIL_ERROR_ADDRESS": "noreply@pyiso.invalid",
        "GOOGLE_STORAGE_JSON_PATH": "",
        "GOOGLE_STORAGE_BUCKET_NAME": "",
        "GOOGLE_STORAGE_GS_URL": "",
        "ARCHIVE_PROVIDER_API_URL": "",
        "ARCHIVE_PROVIDER_API_ALLOWED_HOSTS": "team-api.invalid",
        "ARCHIVE_MAX_ZIP_UNCOMPRESSED_MB": "64",
        "ARCHIVE_API_KEY_ENCRYPTION_KEY": "",
        "ZITENG_PARTNER_API_KEY": "test-ziteng-key",
        "ZITENG_PARTNER_API_URL": f"{api}/ziteng-never-called",
        "ZITENG_PARTNER_TIMEOUT": "1",
        "ZITENG_PARTNER_MIN_INTERVAL": "0",
        "ENABLE_NOTIFICATIONS": "False",
        "NOTIFICATION_EMAIL_ALLOWLIST": "",
        "THUMBNAIL_SYNC_FALLBACK": "True",
        "CONFIRM_EMAIL_WAIT_SECONDS": "1",
        "RESET_EMAIL_WAIT_SECONDS": "1",
        "RESET_PASSWORD_WAIT_SECONDS": "1",
        "MIT_STORAGE_ROOT": "",
        "MOEFLOW_PYISO_TOKEN": token,
    }


# ---------------------------------------------------------------------------
# Process helpers
# ---------------------------------------------------------------------------


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_within(path: Path, root: Path) -> bool:
    try:
        path.resolve().relative_to(root.resolve())
        return True
    except (ValueError, OSError):
        return False


def utc_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def load_module(path: Path, name: str):
    import importlib.util

    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:  # pragma: no cover
        raise RuntimeError(f"cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module
