# Legacy Python reference harness

This directory produces stateless golden fixtures and a disposable Flask
runtime fixture from the original Python implementation. The stateless path
extracts functions/classes without importing the legacy application package;
the runtime path copies only a public allowlist into a temp tree, injects a
synthetic config, imports that copy, and calls Flask's in-process test client.
Both are reference oracles for later C# differential tests; they are not a
production feature and they do not replace the Python test suite.

## Safety model

The generator is deliberately narrower than a normal Python test run:

- It reads only the seven files listed in `reference_harness.ALLOWED_SOURCE_PATHS`.
  `app/config.py`, config directories, `.env` files, tests, storage data,
  deploy files, and user data are rejected before reading.
- It calls `ast.parse` on those files and compiles only the requested
  definitions. It never imports the legacy `app` package, never runs
  `conftest.py` or `tests/__init__.py`, and never connects to a database or
  object store.
- A `builtins.__import__` guard rejects `app` and `app.*` imports during
  generation. `assert_no_app_imported()` checks `sys.modules` before and after.
- `network_disabled()` blocks `socket.connect`, DNS lookups,
  `socket.create_connection`, `urllib.request.urlopen`, and
  `http.client.HTTPConnection.request` for the whole run. Any attempt fails
  generation rather than silently escaping.
- The only non-stdlib dependency for the stateless generator is
  `pymongo`/`bson`, used for BSON encoding. The runtime generator uses the
  public production requirements in its own temp venv, but still never reads
  production config or contacts a production service.

The runtime path adds its own controls: public allowlist copy into a random
tree under the approved `C:\Users\88888888\AppData\Local\Temp\opencode` root;
synthetic `app/config.py`; local-only storage and memory Celery; disabled mail
and optional modules; a socket guard allowing only the exact local Mongo
endpoint; and an owned `shoreline_reference_<uuid>` database. It checks the
opened database name before seeding, drops only that exact database, verifies
the drop, and only then atomically writes the fixture. The `finally` block is a
best-effort fallback. It imports the temp copy, never the original `app`.

Every extracted block records its original path, line range, source-file
SHA-256, and block SHA-256 in `fixtures/goldens.json` under `provenance`.
Expected values are therefore produced by the original Python code, not by a
hand-written C# or Python equivalent.

## Files

| File | Purpose |
| --- | --- |
| `reference_harness.py` | Whitelist, AST extraction, isolated compile, network/import guards, all fixture cases |
| `generate_goldens.py` | CLI to write or verify `fixtures/goldens.json` |
| `fixtures/goldens.json` | Checked-in typed references, raw BSON bytes, and digests |
| `fixtures/README.md` | Fixture schema and C# consumption notes |
| `runtime_reference.py` | Safe temp copy + synthetic config + real temp Flask import/test client |
| `fixtures/runtime_flask_reference.json` | Eight real HTTP responses plus seed BSON/provenance |
| `tests/test_reference_tools.py` | 10 stdlib `unittest` safety/determinism checks |
| `tests/test_runtime_reference.py` | 12 stdlib `unittest` runtime fixture/security checks |

## Environment used

The checked-in fixture was generated on 2026-11-09 with:

- Python 3.10.11 (Windows)
- pymongo 4.18.3
- dnspython 2.8.0 (pymongo dependency)

A disposable venv was used outside the repository:
`C:\Users\88888888\AppData\Local\Temp\opencode\legacy-reference-venv-py310`.
Do not commit a venv into this directory.

The production Python requirements are compiled for Python 3.12 by `uv`.
The runtime fixture was generated with a second disposable venv outside the
repository:
`C:\Users\88888888\AppData\Local\Temp\opencode\flask-reference-runtime-20261109\venv`.
It uses Python 3.12.10, Flask 3.1.3, mongoengine 0.29.3, pymongo 4.17.0,
celery 5.6.3, boto3 1.43.80, oss2 2.19.1, google-cloud-storage 3.13.1,
PyJWT 2.13.0, cryptography 50.0.1, Pillow 12.3.0, flask-babel 4.0.0,
marshmallow 3.26.2, and itsdangerous 2.2.0. All packages are public PyPI
installations. The runtime used local MongoDB 8.0.34 at `127.0.0.1:27128`.
No production config, dotenv, credentials, real storage, mail, or Google
client was read or contacted. See `docs/reference-validation.md` for the full
runtime evidence and limitations.

## Generate and verify

From the C# repository root, with a private venv containing
`pymongo==4.18.3`:

```powershell
$py = "C:\path\to\private-venv\Scripts\python.exe"
& $py tools\legacy-reference\generate_goldens.py
& $py tools\legacy-reference\generate_goldens.py --check
& $py -m unittest discover -s tools\legacy-reference\tests -v
```

`--check` regenerates in memory and byte-compares the checked-in JSON. It fails
if the source, extraction, or expected values become stale. Use
`--python-root` only to point at a read-only copy of the legacy repository; the
source whitelist is enforced regardless of the root.

Runtime fixture generation and tests use the Python 3.12 venv:

```powershell
$pyRuntime = "C:\Users\88888888\AppData\Local\Temp\opencode\flask-reference-runtime-20261109\venv\Scripts\python.exe"
& $pyRuntime tools\legacy-reference\runtime_reference.py
$env:PYTHONPATH = (Resolve-Path tools\legacy-reference\tests)
& $pyRuntime -m unittest -v test_runtime_reference
```

The runtime requires local MongoDB at `127.0.0.1:27128`. It creates only
`shoreline_reference_<32 hex>`, installs a socket guard that permits only that
Mongo endpoint, and asserts `get_db().name` before touching seed collections.
After collecting the fixture it drops and verifies that exact database, then
writes the fixture atomically with `database.cleanup` set only on success. The
output path is resolved before the process changes into the temp tree. Keep the
stateless 10-test check in its Python 3.10/pymongo 4.18.3 venv; its
determinism test compares the recorded generation environment. The runtime
fixture tests only read JSON or exercise pure guard helpers, so both suites
(22 tests) also pass together under that 3.10 venv, while runtime
**generation** still requires Python 3.12.

## What is covered

- `Filename` legality, suffix/file type, folder mode, and numeric sort-name
  padding, including Unicode, emoji, HTML specials, and invalid names.
- `load_from_labelplus` with two files, three labels, multiline translations,
  empty translation, Unicode, emoji, and HTML specials.
- `File.to_labelplus` with ancestor paths, CRLF output, proofread preference,
  empty translations, staff block first/last, and export-name override.
- BBCode `parse_body` trees, bounded/unsafe error cases, `safe_url`,
  NFKC/casefold normalization, 2-gram search tokens, and
  `project_accessible` with a fixed callback scope.
- `order_snapshot` with Python's default JSON separators and its SHA-256, plus
  `export_file_names` with and without manual order.
- `data_digest` and `create_snapshot` with typed raw BSON rows, multi-row
  chunk boundaries, chunk SHA-256, metadata digest, and counts.
- Standalone raw BSON type cases (int32/int64/Decimal128/null/ObjectId/date/
  double/bool/nested order) with hex, base64, and SHA-256.
- Real Flask runtime cases: `/ping` text, missing-token 1003, authenticated
  `/v1/user/info`, missing `word` 110, team list, term-bank list, approved
  application with applicant role, and a 405 response. The fixture also records
  the seeded user/team/role/member/language/term-bank/application BSON and the
  exact owned database lifecycle.

Known non-coverage is explicit in `docs/reference-validation.md`: the runtime
is read-only and does not cover registration/mail, writes, optional modules,
real storage, background tasks, or a C# differential comparison yet. The
original test suite is still forbidden.
