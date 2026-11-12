# Parity harness

Two tools that compare the C# rewrite against the legacy Python app. They exist
because a green unit suite only proves the C# code matches *the tests*, and the
tests were written from the C# implementation. These tools compare against the
real thing.

## 1. Read-only probe — `tools/parity-probe.mjs`

Replays every `GET /v1/...` route it can discover in `src/Shoreline.Api/Features`
against two live backends that read the **same** database, then diffs the JSON
shape recursively.

```bash
PARITY_TOKEN=<legacy token> node tools/parity-probe.mjs
PARITY_TOKEN=... node tools/parity-probe.mjs --only projects
```

Environment:

| Variable | Default | Meaning |
|---|---|---|
| `PY_BASE` | `http://127.0.0.1:5080/api` | Python app (through the frontend proxy) |
| `CS_BASE` | `http://127.0.0.1:5090/api` | C# app (through its proxy) |
| `PARITY_TOKEN` | — | A legacy token; both apps must accept it, so `Auth:Secret` must equal `SECRET_KEY` |
| `PARITY_IDS` | sample ids | Overrides for the placeholder ids used to fill `{id}` segments |

Only `GET` is issued, so it is safe to point at a live copy.

Reported kinds: `status`, `missing`, `extra`, `type`, `array-length` (structural —
these are the ones that break the frontend) and `value` (usually timestamps,
counters or signed URLs).

## 2. Two-database replay — `tools/replay-parity.mjs`

Restore the same dump into two databases, point one backend at each, then run the
same request sequence against both. Writes are safe because each side owns its
copy; the legacy app is never asked to read the other side's writes.

```bash
# once
mongorestore --nsFrom='moeflow_prod.*' --nsTo='parity_py.*' ...
mongorestore --nsFrom='moeflow_prod.*' --nsTo='parity_cs.*' ...

PARITY_TOKEN=<token> \
PARITY_TEAM_ROLE=<team_role member id> \
node tools/replay-parity.mjs
```

Created ids are captured per side and fed back into later steps through
`{{alias}}` placeholders, so the two runs stay symmetric even though each side
generates its own ObjectIds. Set `PARITY_IDS_OUT=ids.json` to dump the captured
ids, then compare the raw documents (the API hides some persisted fields):

```bash
bash tools/diff-parity-docs.sh ids.json
```

Flows: `teams` (create / rename / members / project-sets), `projects`
(create with `allow_apply_type = 3` / rename / targets / files), `members`
(batch add), `reads` (twelve representative GETs).

## What the first runs found

Read probe, 51 discovered routes, 37 comparable:

- **0 structural differences.** Every wrapper shape, key set and array/object
  decision matched.
- Value-only drift came from two sources: signed R2 URLs vs the CDN prefix, and
  i18n (fixed — see below).

Replay, 4 flows:

- 1 structural difference: `GET /v1/admin/users` returns **500 on Python** for
  this dump (`KeyError` inside the legacy serializer). C# returns 200. The legacy
  side is the broken one here.
- Everything else matched after normalising generated ids and avatars.

## Differences found and their disposition

| Difference | Impact | Status |
|---|---|---|
| No `Accept-Language` → C# answered English, Flask-Babel defaults to `zh` | All default-labelled UI text for non-browser clients | Fixed in `91bdcd7`; `TestApp` pins `en` for the English-quoting tests |
| `allow_apply_type = 3` (`ProjectAllowApplyType.TEAM_USER`) rejected by the project create/update paths | Frontend offered three choices, only two worked | Fixed in `78857c9`; the shared helper had hard-coded `1 or 2` and ignored its own bounds |
| `POST /v1/teams/{id}/projects`: Python requires `default_role` and `project_set`, C# accepts their absence | C# can create a project with `ps: null`; the frontend always sends both | Recorded, not changed — tightening it is a behaviour change worth its own decision |
| C# omits `csid`, `csw`, `fol`, `label_recovery`, `slp`, `tb` when creating a project | The API response is identical because C# substitutes the same defaults, and mongoengine fills them on read after a rollback | Recorded; harmless today |
| Python renders `ValidationError` message values as a **character array** (`["O","n","l","y",...]`), C# as one string | Error toasts: Python shows one letter, C# shows the sentence | C# is correct; left as-is |
| Avatar/file URLs differ (CDN prefix vs signed R2 URL) | Cosmetic in every probe; set `STORAGE_DOMAIN` + `STORAGE_VIA_CDN` to match the deployed stack | Configuration, not code |

## Caveats

- The probe only proves *shape* parity. Pagination semantics, permission
  filtering and state machines are covered by the replay flows and the focused
  test suites, not by the probe.
- `both-error` results mean both sides rejected the request; the probe does not
  currently compare the error bodies.
- Signed URLs embed a timestamp, so `value` drift on URL fields is expected even
  when both backends are configured identically.
