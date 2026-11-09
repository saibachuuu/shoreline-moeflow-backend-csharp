# Legacy Python reference harness

This directory produces stateless golden fixtures from the original Python
implementation without importing the legacy application package. It is a
reference oracle for later C# differential tests; it is not a production
feature and it does not replace the Python test suite.

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
- The only non-stdlib dependency is `pymongo`/`bson`, used for BSON encoding.
  No production requirement lock is installed or executed here.

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
| `tests/test_reference_tools.py` | 10 stdlib `unittest` safety/determinism checks |

## Environment used

The checked-in fixture was generated on 2026-11-09 with:

- Python 3.10.11 (Windows)
- pymongo 4.18.3
- dnspython 2.8.0 (pymongo dependency)

A disposable venv was used outside the repository:
`C:\Users\88888888\AppData\Local\Temp\opencode\legacy-reference-venv-py310`.
Do not commit a venv into this directory.

The production Python requirements are compiled for Python 3.12 by `uv`.
A Python 3.12 interpreter is present on the development machine, but the full
production dependency set was intentionally not installed for this stateless
fixture step. See `docs/reference-validation.md` for the full-Flask assessment.

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

Known non-coverage is explicit in `docs/reference-validation.md`: no original
Flask app import, no real database persistence, no original test suite, and no
HTTP differential yet.
