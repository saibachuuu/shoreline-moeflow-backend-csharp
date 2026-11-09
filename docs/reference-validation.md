# Python reference validation

Status: **stateless golden fixtures complete; full Flask differential runtime is
assessed but not implemented.**

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
| No real database | Stateless fixtures use no Mongo connection |
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

Only `pymongo` was installed from public PyPI for this step. The production
`requirements.txt` was not installed. It is compiled for Python 3.12 and its
requirement files contain no private URLs, credentials, or `--index-url`
tokens (checked with a read-only pattern scan). A Python 3.12 interpreter is
available at
`C:\Users\88888888\AppData\Local\Programs\Python\Python312\python.exe` for a
later full bootstrap.

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
& $py -m unittest discover -s tools\legacy-reference\tests -v
```

Measured result:

- generation: `wrote ... (38794 bytes), 7 source files, 16 filename cases, 4 valid BBCode cases, 7 snapshot chunks`
- `--check`: `OK ... (38794 bytes)`
- tests: `Ran 10 tests ... OK`

The tests verify whitelist rejection, no `app` import, deterministic
regeneration, checked-in fixture equality, provenance/hash agreement, BSON
types/order/hashes, Python JSON spacing, chunk hashes, Unicode/HTML coverage,
and the BBCode/access cases. Two consecutive builds produce identical JSON.

## Full Flask reference runtime assessment

### Conclusion

A complete reference Flask runtime is **feasible in a disposable copy**, but
was not attempted in this step. The stateless fixtures are complete; the full
runtime is a later differential step and has explicit blockers below.

### Evidence for feasibility

- `app/models/__init__.py:13-30` `connect_db(config)` supports a `mongomock`
  scheme and injects `mongomock.MongoClient`; an in-memory database is
  possible without a real Mongo.
- `app/factory.py:33-44` creates the Flask app once, applies config mapping,
  and connects the DB; `init_flask_app` at `:50-96` registers APIs/modules and
  initializes storage.
- `app/services/oss.py:141-188` initializes storage from config; a local
  storage configuration can avoid S3/OSS bucket clients. `OSS.__init__` and
  `GoogleStorage.__init__` at import time are inert constructors; network
  clients are only created by `init`.
- `requirements.txt` is public and Python-3.12-based; no private package URL
  was found.

### Safe recipe for the later runtime

1. Copy only public application code to a task-owned temp directory. Exclude
   `app/config.py`, `app/config/`, `.env*`, deploy configs, storage/userdata,
   credentials, tests, and the original conftest.
2. Inject a synthetic `app/config.py` with local dummy values only:
   `DB_URI=mongomock://...` (or a random owned DB on localhost `:27128`,
   never a non-owned database), local storage, `CELERY_BROKER_URL=memory://`,
   `CELERY_BACKEND_URL=cache+memory://`, `CELERY_TASK_ALWAYS_EAGER=True`,
   mail sink, synthetic `SITE_ORIGIN`, and no third-party endpoints.
3. Create a private Python 3.12 venv in the approved temp area. Install only
   public requirements after repeating the private-URL/token check. Do not
   install system services or run a Celery worker/beat.
4. Run with a default-deny network guard that permits only the owned local
   disposable DB/socket. Import the temp copy, never the original `app`.
5. Call Flask test-client/API handlers in-process and compare old responses,
   status codes, and raw BSON against C# fixtures. This is the eventual
   differential harness; it is not present yet.

### Remaining blockers / unresolved items

- Full production dependency installation and import of the copied Flask app
  have not been run.
- The synthetic config key surface is not validated against every import-time
  consumer (APIs, modules, Babel, OSS, Celery, mail).
- `app/__init__.py` module-level app/Celery creation must be isolated by
  importing the temp copy with synthetic config; this has not been proven end
  to end.
- No reference HTTP response/BSON differential has been generated yet.
- The original `tests/__init__.py` and `conftest.py` remain forbidden because
  of `*_test` database drops and in-source `.mo` compilation.
- The full runtime may still require a real Mongo only if `mongomock` cannot
  satisfy a module; if used, it must be a random owned local database.

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
- No original Flask route, response shape, status code, or BSON document was
  executed. The current fixtures are stateless function-level oracles.
- No shared C# build/test was run for this Python-only step.
