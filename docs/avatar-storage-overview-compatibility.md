# Avatar / storage overview / site homepage compatibility

This slice ports three independent legacy surfaces without changing their existing
C# call sites:

- `PUT /v1/avatar`
- `GET /v1/admin/storage/default`
- `GET /v1/site/homepage` (alias of the existing `/v1/homepage`)

The joint integration wires only the four required lines in the shared
`Program.cs`:

```csharp
builder.Services.AddAvatarServices();
builder.Services.AddAdminStorageOverviewServices();

app.MapAvatarEndpoints();
app.MapAdminStorageOverviewEndpoints();
```

No other untracked module is wired by this slice.

## New files

| File | Purpose |
| --- | --- |
| `src/Shoreline.Api/Features/AvatarService.cs` | Upload, storage prefix, persistence, old-object cleanup |
| `src/Shoreline.Api/Features/AvatarEndpoints.cs` | `PUT /v1/avatar` multipart endpoint |
| `src/Shoreline.Api/Features/AvatarRegistration.cs` | DI and route registration extensions |
| `src/Shoreline.Api/Features/AdminStorageOverviewService.cs` | Redacted legacy overview shape |
| `src/Shoreline.Api/Features/AdminStorageOverviewEndpoints.cs` | `GET /v1/admin/storage/default` |
| `src/Shoreline.Api/Features/AdminStorageOverviewRegistration.cs` | DI and route registration extensions |
| `src/Shoreline.Api/Features/SiteSettings.cs` | Only added the `/v1/site/homepage` alias; `/v1/homepage` remains |
| `tests/Shoreline.Tests/AvatarStorageOverviewTests.cs` | 14 direct + 3 snapshot-only HTTP facts |
| `docs/avatar-storage-overview-compatibility.md` | This document |

No indexes are created. The legacy models do not declare avatar/storage metadata
indexes that this slice would need to replicate.

## Avatar

Legacy reference: `app/apis/avatar.py:19-75`.

Request: `multipart/form-data`, token required.

| Form field | Meaning |
| --- | --- |
| `file` | Required image upload. Missing means code 109. |
| `type` | `user` or `team`; anything else means code 111. |
| `id` | Used only for `type=team`; ignored for `user`. |

Behavior:

1. The file check happens first, matching Flask's `request.files.get("file")`.
2. `user` uploads always use the authenticated user, even if a different `id` is
   supplied.
3. `team` uploads parse and load the team first (invalid or missing id → 3001),
   then require `team:CHANGE`. Site admins do not bypass team membership.
4. The object key is `<prefix><new ObjectId>.jpg`.
5. The new object is uploaded and signed before `a` is persisted.
6. If upload or signing fails before persistence, the old `a` value remains and
   the newly uploaded object is cleaned up. A known `MatchedCount == 0` result
   (for example, the owner was concurrently deleted) also removes the new object
   before returning the legacy not-found error.
7. If `UpdateOneAsync` itself throws, the storage outcome is unknown: the server
   may have committed the new `a` before the acknowledgement was lost. The new
   object is deliberately retained and the exception is propagated, because
   deleting it could remove the avatar that is now referenced. This prefers an
   orphan object over a dangling avatar reference. Only the exception type is
   logged; object keys, URLs, and credentials are never logged.
8. After a successful persist, the previous object is deleted best-effort.
9. Compensating deletes and old-object cleanup use an independent five-second
   timeout; they do not pass an unbounded token or inherit a cancelled request
   token.
10. The response is `{ "message": "修改成功", "avatar": "<signed URL>" }`.

Errors:

| Situation | Code | Message |
| --- | --- | --- |
| Missing file | 109 | `上传未包含文件: 请选择图片` |
| Bad type | 111 | `请求参数错误: 不支持的头像类型` |
| Invalid/missing team id | 3001 | `团队不存在` |
| No `team:CHANGE` | 103 | `没有权限` |
| Configured byte cap exceeded | 2 / 413 | `上传文件超过大小限制` |

Prefixes and URLs:

- `user`: `Storage:UserAvatarPrefix`, falling back to `Storage:AvatarPrefix`,
  then `user-avatars/`.
- `team`: `Storage:TeamAvatarPrefix`, then `team-avatars/`.
- An explicitly configured empty prefix is honored as an empty prefix.
- Signed URL validity is one hour. The legacy Python S3 signer defaults to seven
  days; this slice uses the existing C# storage convention.
- Object content type is passed through; existing C# storage does not inspect
  image bytes. Python also accepts arbitrary file content.

### Deliberate safety addition

Rows are streamed into an owned temporary file with a bounded byte count. The
default cap is 20 MiB; `Storage:AvatarUploadLimitBytes` can lower or raise it. If
the form body itself exceeds the configured cap plus 1 MiB of multipart overhead,
the endpoint returns 413. This differs from
Python, which has no explicit avatar byte cap, and is documented rather than
silently claiming byte-for-byte equivalence.

### User avatar prefix consistency

The uploader, `GET /v1/user/info` (`UserView`), and presence views all construct
user avatar keys through `AvatarStoragePrefixes.User(configuration)` followed by
the stored `a` filename. They therefore agree when:
- `Storage:UserAvatarPrefix` is configured (new key style);
- only the legacy `Storage:AvatarPrefix` is configured; or
- neither is configured (both fall back to `user-avatars/`).

`TeamService` still builds team avatar keys independently. Its default
`team-avatars/` matches the avatar service, so it was intentionally left
unchanged in this minimal fix. Explicitly configured empty-prefix edge semantics
for team avatars are not unified here.

## Admin storage overview

Legacy reference: `app/apis/storage.py:51-125`.

`GET /v1/admin/storage/default` requires an authenticated site admin. Response
keys are exactly:

`storage_type`, `bucket_style`, `bucket_style_raw`, `endpoint_host`, `bucket`,
`key_prefix`, `via_cdn`, `cdn_domain`, `prefixes`, `capabilities`,
`requires_local_thumbnail`, `warning`.

Mapping:

| Input | `storage_type` | `bucket_style` |
| --- | --- | --- |
| absent / S3 / R2 / AWS / MINIO / GARAGE / S3_COMPATIBLE | OSS | S3 |
| ALI / ALIYUN / ALIYUN_OSS / OSS | OSS | ALI |
| LOCAL / FILESYSTEM / FILE / DISK | LOCAL_STORAGE | null |
| unknown | OSS | S3 + unknown-style warning |

Details:

- `bucket_style_raw` reports `Storage:Type`, falling back to
  `Storage:BucketStyle`, and stays empty when neither is configured. For local
  storage it stays empty because legacy local storage has no OSS bucket-style
  value. The C# backend's implicit S3 default is not invented as a raw value.
- `endpoint_host` is `Uri.Host` only: no scheme, port, account id, or path.
- `bucket` is exposed only for OSS.
- `key_prefix` uses the actual C# `Storage:KeyPrefix` only for OSS + S3. It does
  not guess the legacy env default `storage/`, and it is empty for ALI/local.
- `via_cdn` is `Storage:ViaCdn` only for OSS.
- `cdn_domain` is `Storage:Domain`.
- Prefixes use `Storage:FilePrefix ?? "files/"`,
  `Storage:OutputPrefix ?? "outputs/"`, and the same user/team prefixes as the
  avatar service.
- S3 capabilities are `false/false`; ALI capabilities are `true/true`.
- `requires_local_thumbnail` is true for local and S3, false for ALI.
- The unknown-style warning is:
  `OSS_BUCKET_STYLE='<raw>' 不是已知取值，已按标准 S3 处理。可用取值：S3 / ALI。`
- An `aliyuncs.com` endpoint resolved as S3 gets the legacy danger warning:
  `配置危险：endpoint 指向阿里云，但储存风格被解析为标准 S3。阿里云请显式设置 OSS_BUCKET_STYLE=ALI，否则签名方式与图片处理都会失效。`
- Access key, secret key, credentials, full endpoint URLs, ports, and account
  paths are never part of the response. Exceptions are not echoed.

### ALI limitation

The overview can report ALI for configuration inspection, and its capability
flags reflect the legacy shape. However the current `ObjectStorage` constructor
throws for ALI/OSS native signing at startup. This endpoint does not imply that
an ALI deployment is operational. No Aliyun signer is added in this slice.

### C# configuration is not legacy env migration

The overview derives values from typed C# configuration (`Storage:*`). It does
not read Python `.env`/deployment settings and does not claim that legacy
`OSS_*` environment variables are migrated automatically. The main deployment
pass must map or intentionally leave those values.

## Site homepage alias

`app/apis/urls.py` exposes `GET /v1/site/homepage`; the existing C# route was
`/v1/homepage`. Both paths now invoke the identical handler and return the same
body. `/v1/homepage` is retained for existing clients.

No authentication was added and no response fields changed.

## Verification

Verification is isolated-snapshot evidence, not a shared-tree build.

Historical pre-joint snapshot:

- Source archive: commit `0dfa13b`.
- Result: **275 passed** (**254 baseline + 21 H1 tests**), with snapshot-only
  `Program.cs` wiring.

Joint integration snapshot:

- Source archive: commit `e910e33` (notifications baseline, **283 tests**) plus
  the H1 eleven files and the two registration/two mapping `Program.cs` lines.
- Snapshot: `C:\Users\88888888\AppData\Local\Temp\shoreline-h1-joint-20261109-224900`.
- Shared `Program.cs` now contains only:
  `AddAvatarServices()`, `AddAdminStorageOverviewServices()`,
  `MapAvatarEndpoints()`, `MapAdminStorageOverviewEndpoints()`.
- `dotnet build --nologo -v:minimal`: **0 warnings, 0 errors**.
- `dotnet test --filter "FullyQualifiedName~AvatarStorageOverviewTests"`:
  **21 passed** (15 direct + 6 HTTP; the prefix regression is a 3-case theory
  covering new key, legacy key, and unset fallback through real upload →
  `/v1/user/info` and presence).
- Full joint snapshot suite: **304 passed** (**283 baseline + 21 H1 tests**).

The shared working tree was not built in this slice. The isolated joint snapshot
is the build/test evidence; the committed tree is a curated 12-path change.

The executed storage backend is the existing **local** test backend. R2 live
credentials/network were not exercised; ALI/OSS native signing remains
unsupported; this document does not claim a live R2 or ALI deployment.

## Remaining boundaries

- Existing encrypted/legacy avatar values are not migrated; only new uploads are
  covered.
- An unknown persistence outcome intentionally retains the newly uploaded
  object, so a failed acknowledgement can leave an orphan object. This is
  preferred over deleting an object that the server may already reference.
- `TeamService` was left unchanged. Its default `team-avatars/` matches the
  avatar service, but explicitly empty team-prefix semantics are not unified.
- No local static-file serving route is added; signed local URLs require the
  existing domain/static wiring.
- No Aliyun native signer; ALI startup remains unsupported.
- No avatar content validation or image re-encoding is added, matching Python.
- The 20 MiB streamed cap is a documented safety deviation.
- This slice is not a claim that Python can be replaced. Route presence,
  integration wiring, deployment configuration, and the remaining findings must
  be reviewed by the main agent.
