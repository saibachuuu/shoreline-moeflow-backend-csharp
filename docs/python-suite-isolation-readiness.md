# Python full-suite isolation readiness

Status as of 2026-11-10 (end of session): **runner implemented, runner self-tests
green, isolated full-suite run NOT passing yet - one harness bug remains.**
Nothing was ever run inside the legacy workspace and no dotenv file or
credential was read. The result below is honest: the suite did start inside the
disposable container, but every test errored during collection because of a
harness stub bug, so the suite has **no** real pass/fail signal yet.

## Why the legacy suite cannot be run in place

| Mechanism | File | Consequence |
| --- | --- | --- |
| `dotenv.load_dotenv()` at import | `app/config.py:16` area | importing `app` reads whatever `.env*` the cwd carries |
| `env_files = .env.test` (pytest-dotenv) | `pytest.ini` | pytest itself loads a developer dotenv file |
| DB ownership checked *after* `create_app()` and `drop_database()` | `tests/__init__.py` | a run against a foreign Mongo drops that database before it notices |
| translation catalogs compiled at collection | root `conftest.py` | writes `.mo` files into the source tree |

## What `tools/Python-IsolatedSuite.py` does

`plan` (stdlib only, read-only):

* `git archive --format=tar <commit>` of one pinned commit of the legacy repo
  into a target dir that must be disjoint from the workspace. The worktree,
  its `.git` index, dotenv files and credentials are never read.
* Scans the extracted tree: dotenv files that are not `*.sample`, any
  `config_local.py`, and credential-shaped literals block the plan
  (`status=blocked`, exit 3). Test-tree literals are reported as
  `test-fixture-literal` evidence, not blockers.
* Writes `.pyiso-snapshot.json` (commit + sha256 manifest) into the snapshot and
  a plan JSON with the isolated DB name and the reserved ports
  (Mongo 27321, S3 29321, SMTP 21321, Mailpit 28321, API 25321, Rabbit 26321).

`run` (inside the disposable container) - gates, in order, all before anything
named `app` is imported:

1. `app` not already in `sys.modules` (pre-gate);
2. source is the extracted snapshot (no `.git`), digest matches the plan marker,
   work dir outside the source, template `.env.test.sample` exists;
3. fixture rendered from the committed template with freshly generated secrets
   into `<root>/runs/<ts>/fixture/isolated.env`, validated (mongomock URI with
   `/<isolated db>?authSource=admin`, `memory://` broker, empty backend URL,
   empty provider/SMTP credentials, storage domain confined to the run dir);
4. dotenv autoload disabled in the child env (`-p no:dotenv`, `env_files=`,
   `DOTENV*`/`PYTEST_*`/`CONDA_*` stripped) and `dotenv.load_dotenv` replaced by
   a stub via `sitecustomize` that only accepts the fixture path;
5. every port free, or listening only because it is the harness's *own*
   disposable endpoint recorded in `--owned-endpoint-manifest` (identity is
   re-verified: Mongo must be reachable and expose no other
   `moeflow_pyiso_*` database). A foreign listener blocks the run;
6. database ownership claimed before import: the `moeflow_pyiso_<hex>_test`
   database gets a `__moeflow_pyiso_owner__` marker with a per-run token; a
   leftover marker from an earlier same-tool run is re-claimed, anything else
   blocks;
7. writable per-run execution copy of the verified snapshot (digest must equal
   the snapshot digest) so translation compilation and test artefacts never
   touch the verified tree;
8. pytest started with fixed arguments (`-n=<workers>`, `-p
   python_isolation_guard`, `-c <source>/pytest.ini`, junit XML), a
   `sitecustomize` + `-p` guard that aborts collection if the gates are not
   installed, explicit `--timeout-seconds` (exit 124 on expiry) and the JUnit
   counts read back into the summary.

`endpoints start|stop` starts/stops the disposable Mongo endpoint and writes
the manifest. Names must start with `shoreline-pyiso-` and stop only addresses
that exact name, so no user service can be touched. `verify-endpoint` is the
identity probe used by the port gate.

## What was verified

* Plan on the real repository: commit
  `be0b717407a494f9daf636476ac7ec8319eb87bc`, 360 files, `status=planned`,
  0 blocking findings, only `.env.sample`, `.env.test.sample` and
  `app/modules/ziteng_partner/config_local.py.example` present; an independent
  `git grep` sweep for AKIA keys, private-key blocks and URI credentials found
  only placeholders (`CHANGE_ME`, `PLEASE_CHANGE_THIS`).
* Inside the container: fixture rendered + validated, dotenv autoload absent,
  ports free/owned, DB ownership claimed before import, execution copy digest
  equal to the snapshot digest.
* Runner self-tests (Windows, Python 3.12): `python -m unittest discover -s
  tools/tests -p "test_*.py"` -> 68 tests OK (66 of them new harness tests next
  to the 2 pre-existing tools tests).
* Docker isolation: image `shoreline-pyiso:20261110` (python:3.12-slim +
  pinned `requirements-dev.txt`, non-root uid 10001, `/opt/venv`), run container
  `--memory 4g --memory-swap 5g --cpus 4 --user 10001:10001 --network host`,
  outer `timeout -k 30 2100`, harness timeout 1800 s, dedicated Mongo container
  with `--tmpfs /data/db` and published only on `127.0.0.1:27321`.

## Honest result of the isolated full run

Two isolated runs were started in the disposable container against the
snapshot (never against the legacy workspace):

* run 1 stopped at collection: the snapshot was mounted read-only and the
  legacy `conftest.py` compiles translations at import
  (`OSError: [Errno 30] Read-only file system:
  .../app/translations/zh/LC_MESSAGES/tmp*.mo`). Fixed by running in the
  writable per-run execution copy.
* run 2 ran but every test errored during setup:
  `PermissionError: python-isolation: fixture may not set MOEFLOW_PYISO_TOKEN`
  (160 errors, 0 passed, 0 failures, 0 skipped, ~3 s). Root cause: the harness
  puts its own token key in the fixture, and the pytest-dotenv plugin - still
  installed, just pointed at the fixture - feeds the whole fixture through
  `dotenv.load_dotenv`, which refused its own key. A fix was written
  (skip the harness key instead of raising) but **not re-run** before the
  session had to stop.

So: **no real pass/fail/skip numbers exist yet**. Collected counts above are
setup errors, not test outcomes.

## Blockers / next steps

1. Re-run the full suite with the corrected stub (`tools/Python-IsolatedSuite.py`,
   `dotenv_stub_source`): expected failure line gone; then record the real
   pass/fail/skip counts from `junit.xml`/summary JSON.
2. If real failures appear, they are legacy failures to be reported as such -
   not to be skipped or patched.
3. Optional hardening: `pytest-randomly` is installed transitively but not
   loaded by default, so no ordering guard was kept.
