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
(batch add), `teamMembers` (add / patch / aliases / display-name / remove),
`files` (upload / rename / delete), `fileOrder` (upload two files into a fresh
project and reorder them using the returned `version` hash), `labels` (source
create / edit / translation create / rank move / deletes), `terms` (term bank
create / rename / term create / edit / delete), `exports` (queue an export and
list it), `invitations` (create / list / revoke), `reads` (twelve representative
GETs).

## What the first runs found

Read probe, 51 discovered routes, 37 comparable:

- **0 structural differences.** Every wrapper shape, key set and array/object
  decision matched.
- Value-only drift came from two sources: signed R2 URLs vs the CDN prefix, and
  i18n (fixed — see below).

Replay, 10 flows:

- 1 structural difference: `GET /v1/admin/users` returns **500 on Python** for
  this dump (`KeyError` inside the legacy serializer). C# returns 200. The legacy
  side is the broken one here.
- Everything else matched after normalising generated ids and avatars. `labels`
  and `exports` both report `structural = 0` on their first run.
- `files` shows value drift on `cover_url`/`resample_url`/`thumbnail_status`
  because C# renders thumbnails inline while Python defers them to Celery; C# is
  ahead, not wrong.
- The `files` run also surfaced `parse_error_type: 0` on newly uploaded files,
  which Python never writes. Fixed in `ac56bce`.
- `exports` diverges on `link` only because the harness runs no Celery worker on
  the Python side, so its export row never gets a download URL. That is the
  environment, not the code.
- `members` and `storage-pool/sources` report `both-error` on a second run: both
  sides reject the duplicate member / the probe has no storage-pool credential
  configured. Status codes agree.

## Differences found and their disposition

| Difference | Impact | Status |
|---|---|---|
| No `Accept-Language` → C# answered English, Flask-Babel defaults to `zh` | All default-labelled UI text for non-browser clients | Fixed in `91bdcd7`; `TestApp` pins `en` for the English-quoting tests |
| `allow_apply_type = 3` (`ProjectAllowApplyType.TEAM_USER`) rejected by the project create/update paths | Frontend offered three choices, only two worked | Fixed in `78857c9`; the shared helper had hard-coded `1 or 2` and ignored its own bounds |
| `POST /v1/teams/{id}/projects`: Python requires `default_role` and `project_set`, C# accepts their absence | C# can create a project with `ps: null`; the frontend always sends both | Recorded, not changed — tightening it is a behaviour change worth its own decision |
| C# omits `csid`, `csw`, `fol`, `label_recovery`, `slp`, `tb` when creating a project | The API response is identical because C# substitutes the same defaults, and mongoengine fills them on read after a rollback | Recorded; harmless today |
| Python renders `ValidationError` message values as a **character array** (`["O","n","l","y",...]`), C# as one string | Error toasts: Python shows one letter, C# shows the sentence | C# is correct; left as-is |
| Avatar/file URLs differ (CDN prefix vs signed R2 URL) | Cosmetic in every probe; set `STORAGE_DOMAIN` + `STORAGE_VIA_CDN` to match the deployed stack | Configuration, not code |
| `file.parse_error_type` was written as `0` on upload, Python leaves it absent until parsing fails | The frontend renders "其他错误" for every fresh upload | Fixed in `ac56bce` (three writers) |
| `IdentityErrors` emitted the raw class message, but `app/translations/zh/LC_MESSAGES/messages.po` rewrites it (`身份标签请求参数错误` → `请求参数错误`, `项目成员不存在` → `项目不存在`, `团队成员不存在` → `团队不存在`) | Wrong toast text on three error paths | Fixed in `71587fe` and `129e7cb`; the catalogue scan below found exactly these three |
| String-form `ValidateError` was missing the `Validate Error: ` prefix that `APIError.__init__` prepends | Reorder/duplicate-file toasts lacked the prefix the frontend's legacy copy expects | Fixed in `0f10562` via `ApiError.Validate`; field errors keep the bare dict because `verify_data` passes `replace=True` |

## Catalogue scan

`app/translations/zh/LC_MESSAGES/messages.po` has 545 entries, 218 of which
differ from the `msgid` — but 215 of those map to an **empty** `msgstr`, which
Flask falls back to the `msgid` (the Chinese source string). Only three carry a
real rewrite, and all three are error-class messages that C# was emitting
verbatim. Turning the `.po` into a checklist is cheap and reproducible:

```bash
python3 - <<'EOF'
import re
s=open('app/translations/zh/LC_MESSAGES/messages.po',encoding='utf-8').read()
dec=lambda x:"".join(re.findall(r'"([^"]*)"',x))
for a,b in re.findall(r'msgid ((?:"[^"]*"\n)+)msgstr ((?:"[^"]*"\n)+)',s):
    if dec(a)!=dec(b) and dec(b): print(repr(dec(a)),'->',repr(dec(b)))
EOF
```

Run the probe with `PARITY_LANG=en` to compare the English side; the only drift
it reports is URLs and ids, so the `*En` tables match the catalogue.

## Caveats

- The probe only proves *shape* parity. Pagination semantics, permission
  filtering and state machines are covered by the replay flows and the focused
  test suites, not by the probe.
- `both-error` results mean both sides rejected the request; the probe does not
  currently compare the error bodies.
- Signed URLs embed a timestamp, so `value` drift on URL fields is expected even
  when both backends are configured identically.
