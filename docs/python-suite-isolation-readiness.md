# Python full-suite isolation readiness

Status as of 2026-11-11: **isolated full-suite run VERIFIED — all gates held,
914 collected tests passed (866 tests + 48 subtests), 0 failures/errors/skips.**
Nothing was ever run inside the legacy workspace and no dotenv file or
credential was read. The earlier harness bug described below was diagnosed and
fixed; the corrected stub is the one committed here.

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

## Verified isolated full run (2026-11-11)

* Prior failure diagnosed: run 2 of 2026-11-10 errored 160 tests at setup with
  `PermissionError: python-isolation: fixture may not set MOEFLOW_PYISO_TOKEN`
  because the `dotenv.load_dotenv` stub raised on the harness's own bookkeeping
  key when pytest-dotenv fed the isolated fixture back through it. The stub now
  skips that key (`tools/Python-IsolatedSuite.py:275` `_ALLOWED`) and accepts
  pytest-dotenv's keyword call (`dotenv_path/stream/verbose/override/
  interpolate/encoding`), so no code change beyond the already-committed stub
  was needed for this run. Prior "no valid result" = collection/setup errors
  only, not real test outcomes.
* Run 2026-11-11 (branch `agents/repl-python-suite-20261111`, worktree
  `moeflow-repl-python-20261111`): snapshot of legacy commit
  `be0b717407a494f9daf636476ac7ec8319eb87bc` (360 files, status=planned),
  disposable container `shoreline-pyiso:20261110`, own Mongo endpoint
  `shoreline-pyiso-repl-20261111-mongo` on `127.0.0.1:27271` (tmpfs, agent-owned
  ports: mongo 27271, s3 29271, smtp 21271, mailpit 28271, api 25271,
  rabbit 25272), DB `moeflow_pyiso_553ae973_test` claimed before import.
* All gates green: dotenv autoload absent, `imports_before_gates=0`,
  execution-copy digest equal to snapshot digest
  `a40a42a...dd4a727`, no foreign listeners, fixture credentials empty.
* Result: **914 collected items, passed 914 (866 tests + 48 subtests),
  failures 0, errors 0, skipped 0**, pytest exit 0, elapsed 123.5 s,
  Python 3.12.16, workers 2.
* Evidence (WSL host): plan `/home/eight8times/pyiso-repl-20261111/plan.json`,
  summary `/home/eight8times/pyiso-repl-20261111/work/summary.json`, JUnit
  `/home/eight8times/pyiso-repl-20261111/work/runs/20261111T041147/junit.xml`,
  pytest log `/home/eight8times/pyiso-repl-20261111/work/runs/20261111T041147/pytest.log`
  (sha256 `ce242f68ace22404e0d500c7a297c29ec16b08e26e8e45151790f2f84ddbc8b7`).
  Owned endpoint stopped after the run (`removed shoreline-pyiso-repl-20261111-mongo`).
* Runner self-tests re-run on WSL Python 3.13: `python3 -m unittest discover -s
  tools/tests -p "test_*.py"` -> 68 tests OK.

Commands used (WSL):
```
python3 tools/Python-IsolatedSuite.py plan \
  --legacy-directory <legacy-repo> --target <disjoint-snapshot-dir> \
  --commit be0b717407a494f9daf636476ac7ec8319eb87bc \
  --expect-commit be0b717407a494f9daf636476ac7ec8319eb87bc \
  --port-mongo 27271 --port-s3 29271 --port-smtp 21271 \
  --port-mailpit 28271 --port-api 25271 --port-rabbit 25272 \
  --json-out <plan.json> --print-plan
python3 tools/Python-IsolatedSuite.py endpoints --action start \
  --name shoreline-pyiso-repl-20261111-mongo --port 27271 \
  --database <db-name> --manifest <endpoints.json>
docker run --rm --network host --user 10001:10001 \
  -v <harness-tools-dir>:/harness:ro -v <snapshot>:/source:ro \
  -v <work-dir>:/work shoreline-pyiso:20261110 \
  timeout -k 30 2100 python /harness/Python-IsolatedSuite.py run \
  --source /source --root /work --db-name <db-name> --workers 2 \
  --owned-endpoint-manifest /work/endpoints.json \
  --port-mongo 27271 --port-s3 29271 --port-smtp 21271 \
  --port-mailpit 28271 --port-api 25271 --port-rabbit 25272 \
  --json-out /work/summary.json
python3 tools/Python-IsolatedSuite.py endpoints --action stop \
  --name shoreline-pyiso-repl-20261111-mongo --port 27271 \
  --database <db-name> --manifest <endpoints.json>
```

## Earlier isolated runs (2026-11-10, superseded by the run above)

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

1. ~~Re-run the full suite with the corrected stub~~ Done 2026-11-11 — see
   "Verified isolated full run" above.
2. Any real failures would be legacy failures to be reported as such -
   not to be skipped or patched.
3. Optional hardening: `pytest-randomly` is installed transitively but not
   loaded by default, so no ordering guard was kept.
