# Python reference validation

Status: **stateless golden fixtures complete; disposable Flask runtime is
implemented and verified with eight real HTTP/BSON cases. The runtime addition
has not been committed yet; commit `738b617a2729` contains only the stateless
harness.**

This document describes the Python reference oracle under
`tools/legacy-reference/`. It exists so the C# replacement can eventually be
checked against original Python behavior and old BSON/response shapes, rather
than only against C# tests that both define and satisfy their own expectations.

## Why this is separate from the Python test suite

The legacy repository is not safe to import or test naively:

- `app/__init__.py:32-45` creates the Flask app, Celery object, and calls
  `init_flask_app` at module import time.
- `app/factory.py:7` imports `app.config`, and `app/factory.py:33-38` calls
  `connect_db(app.config)` while creating the app.
- `app/factory.py:23-24` constructs `OSS()` and `GoogleStorage()` at module
  import time.
- The legacy `tests/__init__.py` has a `create_test_app` that can drop
  `*_test` databases; its conftest also compiles `.mo` files inside the source
  tree.

Therefore this harness does **not** import the original `app` package, does
not run the original conftest, and does not use the original test bootstrap.

## Safety invariants

| Control | Implementation |
| --- | --- |
| Source whitelist | `reference_harness.ALLOWED_SOURCE_PATHS`, exact relative paths only |
| Path escape rejection | `resolve_source()` rejects absolute paths, `..`, and non-whitelisted paths |
| No app bootstrap | `ast.parse` + compile selected definitions only; never `import app` |
| Import guard | `forbid_app_imports()` raises on `app` / `app.*` during generation |
| No original test run | No `conftest.py`, no `tests/__init__.py`, no pytest collection |
| No config/dotenv | No `app/config.py`, config directory, `.env*`, or deploy file is read |
| No user/storage data | No `storage/`, uploaded files, database dumps, or credentials |
| Network default-deny | `network_disabled()` blocks socket connect, DNS, `urllib`, and `http.client` |
| No real database (stateless) | Stateless fixtures use no Mongo connection |
| Owned local DB only (runtime) | Local `127.0.0.1:27128`; exact `shoreline_reference_<uuid>`; guarded drop |
| Provenance | Original path, line range, source SHA-256, and block SHA-256 in JSON |
| No hand-written oracle | Expected values come from extracted original functions/classes |

The generator asserts `sys.modules` contains no `app`/`app.*` module before and
after generation, and raises if the network guard records an attempt.

## Environment used

The checked-in fixture was generated on 2026-11-09 with:

- Python 3.10.11 (Windows)
- pymongo 4.18.3
- dnspython 2.8.0
- private disposable venv:
  `C:\Users\88888888\AppData\Local\Temp\opencode\legacy-reference-venv-py310`

Only `pymongo` was installed from public PyPI for the stateless step. The
production `requirements.txt` is compiled for Python 3.12 and contains no
private URLs, credentials, or `--index-url` tokens (checked with a read-only
pattern scan).

The runtime fixture was generated separately on 2026-11-09 with Python 3.12.10
in a second disposable venv:
`C:\Users\88888888\AppData\Local\Temp\opencode\flask-reference-runtime-20261109\venv`.
It installed only public PyPI packages from `requirements.txt`:

| Package | Version |
| --- | --- |
| Flask | 3.1.3 |
| mongoengine | 0.29.3 |
| pymongo | 4.17.0 |
| celery | 5.6.3 |
| boto3 | 1.43.80 |
| oss2 | 2.19.1 |
| google-cloud-storage | 3.13.1 |
| PyJWT | 2.13.0 |
| cryptography | 50.0.1 |
| Pillow | 12.3.0 |
| flask-babel | 4.0.0 |
| marshmallow | 3.26.2 |
| itsdangerous | 2.2.0 |

The runtime used local MongoDB 8.0.34 at `127.0.0.1:27128`. No production
configuration, dotenv file, credential, R2/OSS bucket, SMTP server, Google
client, upload, or user data was read.

## Extracted definitions and provenance

The fixture records the full source-file SHA-256 for each path and a block
SHA-256 plus line range for each definition. The current extraction is:

| Source | Definition | Lines |
| --- | --- | --- |
| `app/constants/file.py` | `FileType` | 5-27 |
| `app/models/file.py` | `Filename` | 115-190 |
| `app/models/file.py` | `File.to_labelplus` | 1323-1376 |
| `app/services/file_order.py` | `order_snapshot` | 39-47 |
| `app/services/file_order.py` | `export_file_names` | 103-115 |
| `app/services/label_snapshot.py` | `data_digest` | 68-74 |
| `app/services/label_snapshot.py` | `create_snapshot` | 77-141 |
| `app/services/notification_content.py` | `NotificationError` | 12-17 |
| `app/services/notification_content.py` | `fail` | 20-21 |
| `app/services/notification_content.py` | `safe_url` | 24-40 |
| `app/services/notification_content.py` | `parse_body` | 43-96 |
| `app/services/notification_content.py` | `normalize` | 112-113 |
| `app/services/notification_content.py` | `search_tokens` | 116-118 |
| `app/services/notification_content.py` | `project_accessible` | 121-127 |
| `app/utils/labelplus.py` | `LPLabel` | 5-9 |
| `app/utils/labelplus.py` | `LPFile` | 12-14 |
| `app/utils/labelplus.py` | `load_from_labelplus` | 17-53 |
| `app/utils/type.py` | `is_number` | 1-8 |

Exact hashes are in `tools/legacy-reference/fixtures/goldens.json` under
`provenance`. They let a later reviewer detect source drift before trusting a
fixture.

## Fixture inventory

`fixtures/goldens.json` is 38,794 bytes in the checked-in run and contains:

- 16 `Filename` cases: numeric sort padding, suffix/file type, folder mode,
  Unicode `ßσς`, emoji, `noext`, empty/space-only, >127 chars, `.`/`..`,
  trailing dot, empty prefix, and every illegal filename character class.
- `load_from_labelplus`: 2 files, 3 labels, multiline translations, empty
  translation, Unicode/emoji/HTML specials.
- `File.to_labelplus`: ancestor path, CRLF output, proofread-over-content,
  empty translation, staff block first/last, and `export_name` override.
- BBCode: 4 valid parse trees and 5 exact invalid errors, 6 `safe_url` cases,
  NFKC/casefold output and sorted 2-gram tokens, and 6 fixed-scope
  `project_accessible` cases.
- File order: 4 files with Unicode names and `manual_order` values including
  `None`; canonical Python-default JSON (`", "` separator), SHA-256, and
  export mappings with and without manual order.
- Label snapshot: 5 kinds with counts `files=3, targets=2, sources=3,
  translations=2, tips=2`; 7 chunks; typed raw BSON rows; per-chunk
  base64/SHA-256; metadata digest/counts/revision. `CHUNK_BYTES` is overridden
  to 200 only to force small-fixture multi-chunk boundaries; the extracted
  chunking code is unchanged and the production constant is 4 MiB.
- Raw BSON: 2 standalone documents with ordered fields and
  int32/int64/Decimal128/null/ObjectId/date/double/bool/nested values, each
  with hex/base64/SHA-256 and field-type metadata.

## Verification commands and results

With the private venv:

```powershell
$py = "C:\Users\88888888\AppData\Local\Temp\opencode\legacy-reference-venv-py310\Scripts\python.exe"
& $py tools\legacy-reference\generate_goldens.py
& $py tools\legacy-reference\generate_goldens.py --check
& $py -m unittest -v tools\legacy-reference\tests\test_reference_tools.py
```

Measured result:

- generation: `wrote ... (38794 bytes), 7 source files, 16 filename cases, 4 valid BBCode cases, 7 snapshot chunks`
- `--check`: `OK ... (38794 bytes)`
- tests: `Ran 10 tests ... OK`

The tests verify whitelist rejection, no `app` import, deterministic
regeneration, checked-in fixture equality, provenance/hash agreement, BSON
types/order/hashes, Python JSON spacing, chunk hashes, Unicode/HTML coverage,
and the BBCode/access cases. Two consecutive builds produce identical JSON.

## Disposable Flask reference runtime

### Implementation

`tools/legacy-reference/runtime_reference.py` performs the real bootstrap in a
task-owned temp directory:

- It copies only an explicit public allowlist from the read-only Python source:
  top-level `app/__init__.py` and `app/factory.py`, `.py` files under the
  public API/model/service/etc. directories, `app/modules/__init__.py`, and
  `app/translations/__init__.py`. It excludes `app/config.py`, config
  directories, `.env*`, deploy files, tests, storage/uploads/userdata,
  migrations, scripts, static/templates, optional module subpackages,
  translation catalogs, and `__pycache__`.
- It writes a synthetic `app/config.py` with typed local defaults, local
  storage, memory Celery, disabled mail, empty MIT storage root, and no
  production values. No production config is read or copied.
- Before importing anything, it installs a socket guard that permits only
  `127.0.0.1:27128`. DNS, external HTTP, R2/OSS, SMTP, and Google clients are
  denied. It sets `APP_ROLE=web`, imports the temp copy (never the original
  `app`), and never starts a listening server or Celery worker.
- It uses an exact owned database name,
  `shoreline_reference_<32 lowercase hex>`, on the local Mongo instance. It
  asserts the opened `database.name` matches that name and the owned namespace
  before any seed or collection query. After the fixture data is collected, it
  drops only that exact database, verifies it is gone, then writes the fixture.
  The `finally` block is only a best-effort fallback and does not set the
  fixture's cleanup status.
- The fixture output path is resolved to an absolute path before the process
  changes into the temporary tree. The disposable tree itself is created with
  `tempfile.mkdtemp` under the approved
  `C:\Users\88888888\AppData\Local\Temp\opencode` root; `--temp-parent` may
  select a directory only beneath that root.
- Seeds are fixed ObjectIds and fixed UTC datetimes: one owner, one applicant,
  two active `TeamMember` rows, all five system `TeamRole` rows, one team, two
  languages, one term bank, and one approved application. The application id
  `6955b9000000000000000501` makes `Application.create_time` deterministic.
- It executes eight requests through `flask_app.test_client()` and records the
  actual status, headers, body, fixed inputs, per-collection raw BSON
  before/after (base64, hex, SHA-256, byte count), source provenance, dependency
  versions, owned DB name, and cleanup outcome.

The runtime fixture is
`tools/legacy-reference/fixtures/runtime_flask_reference.json` (85,297 bytes).
It uses schema `shoreline.python-runtime-reference/v1` and contains only
synthetic fixture data. It contains no `Bearer ` token or synthetic secret in
clear text. Its seed BSON deliberately includes synthetic `password_hash`
placeholders for the fixture users (inside the base64/hex BSON), but no real
credentials, production values, or dotenv contents.

### Runtime cases

| # | Request | Result |
| --- | --- | --- |
| 1 | `GET /ping` | 200, text `pong` |
| 2 | `GET /v1/teams?word=Reference` without token | 401, code 1003 `NeedTokenError` |
| 3 | `GET /v1/user/info` with owned token | 200, owner `to_api()` |
| 4 | `GET /v1/teams` with token, no `word` | 400, code 110 `RequestDataEmptyError` |
| 5 | `GET /v1/teams?word=Reference` | 200, one team + pagination headers |
| 6 | `GET /v1/teams/<team>/term-banks` | 200, one term bank |
| 7 | `GET /v1/teams/<team>/applications` | 200, approved application; applicant `user_role.system_code=member` |
| 8 | `POST /ping` | 405 real Flask HTML response |

Seed counts are `application=1, language=2, team=1, team_member=2,
team_role=5, term_bank=1, user=2`. Because all cases are reads, every
collection's before/after BSON digest is identical; the empty
`identity_tag_policy` side collection is not emitted as a read-only case.

### Verification commands and results

```powershell
$pyRuntime = "C:\Users\88888888\AppData\Local\Temp\opencode\flask-reference-runtime-20261109\venv\Scripts\python.exe"
& $pyRuntime tools\legacy-reference\runtime_reference.py
$env:PYTHONPATH = (Resolve-Path tools\legacy-reference\tests)
& $pyRuntime -m unittest -v test_runtime_reference
```

Measured generation result: app imported successfully; 8/8 cases produced the
statuses above; the owned database was dropped and verified absent before the
fixture was written atomically at 85,297 bytes. The fixture's
`database.cleanup` field is set only after that verified drop.

The runtime fixture tests include four guard checks: database-name mismatch
fails before seeding, drop verification, approved temp-root enforcement, and
absolute output resolution before `chdir`.

The original stateless tests remain a Python 3.10 check:

```powershell
$pyStateless = "C:\Users\88888888\AppData\Local\Temp\opencode\legacy-reference-venv-py310\Scripts\python.exe"
& $pyStateless -m unittest -v tools\legacy-reference\tests\test_reference_tools.py
```

Measured stateless result: `Ran 10 tests ... OK`. Runtime fixture result:
`Ran 12 tests ... OK`. The two fixture suites also pass together under the
Python 3.10 venv (`test_reference_tools test_runtime_reference`:
`Ran 22 tests ... OK`), because the runtime tests only inspect the checked-in
JSON or exercise pure guard helpers; they never import the legacy `app`.
Runtime **generation** still requires Python 3.12 and the runtime venv;
running the stateless determinism check under 3.12 would compare a different
recorded `python`/`pymongo` environment and is not the supported check.

### Runtime limitations

- Authentication is exercised with a seeded local user and a token generated
  from the synthetic `SECRET_KEY`; registration/mail delivery is not run.
- All HTTP cases are reads. No write/mutation semantics, invite flows, file
  upload, real label lock/recovery, or background Celery tasks are covered.
- Optional modules, translation catalogs, real OSS/R2, SMTP, Google Vision,
  and real production data are disabled or absent.
- No C# differential comparison has been run yet; this fixture is the Python
  side of that future comparison.
- The original `tests/__init__.py` and `conftest.py` remain forbidden because
  of `*_test` database drops and in-source `.mo` compilation.
- The runtime addition is pending review and is intentionally not committed;
  commit `738b617a2729` covers the stateless harness only.

## Explicit non-coverage (do not treat as expected)

These areas were intentionally not fabricated:

- `load_from_labelplus`/`Filename` `gettext` strings use identity translation;
  the extracted code and Chinese messages are original, but locale catalogs
  are not loaded.
- `project_accessible` uses a fixed callback scope; full RBAC/team/project
  identity resolution is not covered.
- `create_snapshot` uses fake `label_data` and fake snapshot/chunk collections.
  The BSON encoding, digest, chunk boundary, and chunk hashes are original,
  but Mongo persistence, indexes, locks, and recovery behavior are not.
- `File.to_labelplus` uses synthetic sources/translations; database queries and
  project-level export assembly (`app/models/project.py:1040-1096`) are not
  included.
- Notification `prepare`/`visible_nodes` project resolution and HTTP-facing
  rendering are not extracted; only `parse_body`, `safe_url`, normalization,
  search tokens, and the access primitive are.
- The stateless fixtures are function-level oracles. The separate runtime
  fixture executes the eight read-only Flask cases listed above; it does not
  claim coverage of writes, registration, background tasks, optional modules,
  or real storage.
- No shared C# build/test was run for this Python-only step.
