# Storage compatibility layer (D0)

Status: **implemented; isolated build and test passed; not validated against a
real R2 bucket.** This is the storage compatibility foundation only. File CRUD
and the storage-pool modules are intentionally out of scope.

## Isolated verification snapshot

The verification run used a throwaway snapshot outside the shared working tree:

- Source baseline: `git archive 710bbcb` of the C# repository (so `Program.cs`
  and all other shared files are the committed baseline, not any agent's
  in-progress edits).
- Copied into the snapshot only: the six D0 `src/Shoreline.Api/Storage/*.cs`
  files plus `tests/Shoreline.Tests/StorageCompatibilityTests.cs`. All Term,
  Label, Presence, Application, cleanup, and other in-progress agent files were
  excluded.
- Snapshot path:
  `C:\Users\88888888\AppData\Local\Temp\opencode\d0-storage-isolated-20261109-212734`.
- `dotnet build Shoreline.sln`: **0 warnings, 0 errors**.
- Storage tests: **17/17 passed** (the existing `StorageTests` fact plus the 16
  new compatibility facts).
- Full test suite: **189/189 passed** (173 baseline + 16 new), no skipped tests.
- The local Mongo, S3Mock, and Mailpit services were already running and were
  not restarted. The snapshot used the existing `local-services` fixture and its
  random test database.
- The archive extraction normalized LF to CRLF because the host has
  `core.autocrlf=true`; after normalizing line endings, the snapshot
  `Program.cs` blob hash equals commit `710bbcb`'s blob hash. No Program content
  was changed.
- This is still **not** real R2 authorization evidence: S3Mock does not validate
  signatures, and no production credentials or live R2 connection were used.

### Joint verification snapshot (current)

A stronger joint snapshot was then built from `git archive 306b10a` (application
approvals and presence already committed) plus the same six D0 storage files and
`StorageCompatibilityTests.cs`. No other agent work in progress was copied.

- Snapshot path:
  `C:\Users\88888888\AppData\Local\Temp\opencode\d0-storage-joint-20261109-2137`.
- `dotnet build Shoreline.sln`: **0 warnings, 0 errors**.
- Full test suite: **226/226 passed** (210 joint baseline + 16 new), no skipped
  tests.
- Storage tests: **17/17 passed**.
- ASP.NET DI resolution is exercised by the existing `PresenceTests` helper at
  `tests/Shoreline.Tests/PresenceTests.cs:352-354`, which calls
  `services.App.Services.GetRequiredService<ObjectStorage>()`; the joint full
  suite passed without a `Program.cs` change.
- The earlier 189-result run above remains as historical evidence; the 226-result
  joint run is the current evidence for the `306b10a` baseline.
- Real R2 authorization is still unverified: S3Mock does not validate signatures
  and no production credentials or live R2 connection were used.

## What this layer preserves

`ObjectStorage` remains the single public facade used by the application. It owns
a small S3-shaped surface and hides the backend choice:

- **S3-compatible** mode for AWS S3, Cloudflare R2, MinIO, Garage, and other
  S3-compatible services.
- **Local** mode for a filesystem root used by local development and tests.
- **Aliyun OSS** is recognized but deliberately not enabled: native Aliyun
  signing has not been ported, so configuration fails fast instead of silently
  behaving like generic S3.

The public key space is always **relative**. A configured `Storage:KeyPrefix` is
prepended exactly once inside the S3 backend. It is an opaque prefix: the code
does not trim it, rewrite it, or peel it off existing object keys.

## Configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `Storage:Type` | `S3` | Backend/provider selector. |
| `Storage:BucketStyle` | — | Alias used only when `Storage:Type` is empty. |
| `Storage:KeyPrefix` | empty | Physical S3 key prefix. Applied once to every S3 operation. |
| `Storage:Bucket` | required for S3 | Bucket name. |
| `Storage:Endpoint` | required for S3 | Absolute `http`/`https` endpoint. |
| `Storage:Region` | `us-east-1` | SigV4 region. `auto` is accepted for R2. |
| `Storage:ForcePathStyle` | `true` | Use path-style bucket addressing. |
| `Storage:AccessKey` | required for S3 | S3 access key ID. Never logged. |
| `Storage:SecretKey` | required for S3 | S3 secret key. Never logged. |
| `Storage:ViaCdn` | `false` | Return direct CDN URLs from `SignGetAsync` instead of presigned S3 URLs. |
| `Storage:Domain` | — | CDN/public domain. Required for Local signing and for `Storage:ViaCdn`. |
| `Storage:Root` | required for Local | Absolute, owned filesystem root. |
| `Storage:LocalRoot` | — | Alias for `Storage:Root`. |

Provider aliases:

- S3 family: `S3`, `R2`, `AWS`, `MINIO`, `GARAGE`, `S3_COMPATIBLE`, `S3-COMPATIBLE`.
- Local family: `LOCAL`, `FILESYSTEM`, `FILE`, `DISK`.
- Aliyun family: `ALI`, `ALIYUN`, `ALIYUN_OSS`, `OSS` -> `NotSupportedException`.
- Unknown values log a sanitized warning and fall back to S3 for compatibility.
  The warning contains the provider value with unsafe characters replaced and
  the length capped; it never logs credentials or endpoint credentials.

Endpoint validation requires an absolute `http`/`https` URI with a host and
rejects user info, query strings, and fragments. An endpoint whose host contains
`aliyuncs.com` is rejected with an explicit message: it must not be treated as a
plain S3 endpoint while Aliyun signing is unimplemented.

`Storage:KeyPrefix` is **S3-only**. Local storage uses its root as the namespace,
so Local signing URLs and keys stay prefix-free.

### Legacy Python configuration mapping

`STORAGE_TYPE=OSS` in the Python service does **not** by itself mean Aliyun OSS.
The actual backend was selected by `OSS_BUCKET_STYLE`, whose historical values
are confusing:

| Legacy Python configuration | Actual Python backend | C# configuration |
| --- | --- | --- |
| `STORAGE_TYPE=OSS` + `OSS_BUCKET_STYLE=S3`, `R2`, `MINIO`, `GARAGE`, `AWS`, or unset | Standard S3 via `boto3` (path style); Python normalizes all of these to `BucketStyle.S3` | `Storage:Type=S3` (or `R2`) |
| `STORAGE_TYPE=OSS` + `OSS_BUCKET_STYLE=ALI`, `ALIYUN`, `ALIYUN_OSS`, or `OSS` | Aliyun OSS native `oss2` signing | **Unsupported**: `Storage:Type=ALI` fails fast |
| `STORAGE_TYPE=LOCAL_STORAGE` | Local filesystem | `Storage:Type=Local` + `Storage:Root` |

Only the native Aliyun branch is currently unsupported. Do **not** translate
`STORAGE_TYPE=OSS` directly to `Storage:Type=OSS`: the C# alias `OSS` means
Aliyun and will deliberately fail fast. The Python module's historical comments
describe an older reversed meaning of the raw `S3` and `R2` labels; the current
normalizer maps both `S3` and `R2` to the standard S3 implementation. A
configuration migration adapter is a later phase; this document only records the
boundary.

The Python service used `OSS_KEY_PREFIX` with an old default of `storage/`.
The C# default is empty so the existing API behavior and existing tests remain
compatible. A deployment that needs the old R2 layout must set it explicitly:

```json
{
  "Storage": {
    "Type": "S3",
    "KeyPrefix": "storage/"
  }
}
```

Existing R2 originals were written under `OSS_KEY_PREFIX + OSS_FILE_PREFIX + sa`
(for example `storage/files/<sa>`). The later File layer must pass the relative
`files/<sa>` key while `Storage:KeyPrefix` supplies `storage/`. This layer does
not migrate or guess existing keys; without the explicit prefix, existing
objects will 404.

The prefix also applies to the existing avatar/team-avatar signing calls. When
enabling it, make sure the physical avatar keys are located under that same
prefix.

## Public API

```csharp
public ObjectStorage(IConfiguration configuration);
public ObjectStorage(IConfiguration configuration, ILogger<ObjectStorage>? logger);

public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);
public Task<ObjectDownload> GetAsync(string key, CancellationToken cancellationToken);
public Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default);
public Task<long?> SizeAsync(string key, CancellationToken cancellationToken);
public Task DeleteAsync(string key, CancellationToken cancellationToken);
public Task CopyAsync(string source, string destination, CancellationToken cancellationToken);
public Task<string> SignGetAsync(string key, TimeSpan validity);
public Task<ObjectPage> ListAsync(
    string prefix,
    string? startAfter,
    int maxKeys,
    CancellationToken cancellationToken = default);
public void Dispose();
```

Supporting public types:

```csharp
public sealed record ObjectInfo(
    string Key,
    long Size,
    string? ETag = null,
    string? ContentType = null,
    DateTime? LastModified = null);

public sealed record ObjectPage(IReadOnlyList<ObjectInfo> Objects, string? Next);

public sealed class ObjectDownload : IDisposable
{
    public ObjectDownload(GetObjectResponse response);
    public Stream Content { get; }
    public long Length { get; }
    public string ContentType { get; }
    public void Dispose();
}
```

`ObjectDownload` keeps the original public constructor and streaming behavior.
Callers own the wrapper and must dispose it. `GetAsync` never buffers the object
into a byte array.

The backend seam is also public for the next phase:

```csharp
public interface IStorageBackend : IDisposable
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);
    Task<ObjectDownload> GetAsync(string key, CancellationToken cancellationToken);
    Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken);
    Task<long?> SizeAsync(string key, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
    Task CopyAsync(string source, string destination, CancellationToken cancellationToken);
    Task<string> SignGetAsync(string key, TimeSpan validity);
    Task<ObjectPage> ListAsync(string prefix, string? startAfter, int maxKeys, CancellationToken cancellationToken);
}
```

This interface is intended to let a later storage-pool router compose backends.
No pool, descriptor/client-factory, or Program registration change is made in
this phase.

## Semantics

### Keys and prefixing

- Every public key and cursor is relative to the configured namespace.
- S3 physical key = `Storage:KeyPrefix + relativeKey`, applied exactly once.
- `CopyAsync` prefixes both source and destination.
- `HeadAsync` returns the **relative** key, size, ETag (quotes stripped), content
  type, and last-modified time. A missing object returns `null`.
- `SizeAsync` returns `null` for a missing object.
- `ListAsync` requests the physical prefix, strips `Storage:KeyPrefix` from each
  returned key, skips results outside the namespace, and never exposes another
  prefix's objects.
- An empty list prefix is allowed. A cursor (`startAfter`) is exclusive.
- `maxKeys` must be between `1` and `1000`.
- `ObjectPage.Next` is the last returned relative key when the backend reports a
  truncated page; it is `null` on the final page.

Key validation rejects empty keys, backslashes, NUL characters, rooted keys or
leading `/`, and `.` / `..` segments. Local storage additionally rejects empty
path segments and characters invalid in a Windows file name. List prefixes allow
a trailing slash and an empty value, but still reject traversal and rooted forms.

### Signing / URLs

- Without `Storage:ViaCdn`, `SignGetAsync` creates a normal SigV4 presigned GET
  URL for the physical S3 key (`Storage:KeyPrefix + relativeKey`).
- With `Storage:ViaCdn`, `SignGetAsync` returns
  `Storage:Domain + "/" + percent-encoded relative key`. The key prefix and AWS
  credentials are deliberately absent. The CDN/origin mapping is a deployment
  responsibility.
- Local `SignGetAsync` only builds `Storage:Domain + "/" + encoded relative key`.
  It does not read the filesystem, sign anything, or authorize a request. A
  later static-file/server wiring step must serve those URLs.
- Path segments are RFC 3986 percent-encoded: unreserved `ALPHA` / `DIGIT` /
  `-` / `.` / `_` / `~` stay literal, `/` is preserved, and other Unicode/bytes
  are emitted as uppercase `%XX`. This matches the Python CDN URL behavior for
  relative keys.
- Validity must be positive. No maximum-expiry check is added by this layer.

### Local backend

- Root must be an absolute, owned directory. It is created if missing.
- Reads and writes stream directly to disk. Writes go to a unique temporary file
  in the destination directory and are moved over the destination with
  `File.Move(..., overwrite: true)`, so a reader never sees a partially written
  object. Temporary files are removed on failure or cancellation.
- The caller retains ownership of the input stream; `PutAsync` does not close it.
- Get returns an async `FileStream` wrapped in `ObjectDownload`.
- Local `HeadAsync` exposes size and UTC last-modified time; ETag and content
  type are `null` (download content type is `application/octet-stream`).
- Delete is idempotent for files and refuses to delete directories or traverse
  reparse points.
- Listing recursively enumerates the root with one lazy directory enumerator per
  level on the current DFS path, so its traversal stack is bounded by directory
  depth rather than by the total number of directories. It skips every reparse
  point/symlink, checks each path stays under the root, and keeps only a bounded
  sorted set of at most `maxKeys + 1` relative keys. It does not call
  `Directory.GetFiles(..., SearchOption.AllDirectories)` or materialize all files
  in memory.
- Local keys are sorted with ordinal comparison, which gives deterministic
  pagination independent of filesystem enumeration order.

## AWSSDK.S3 4.x compatibility notes

The project stays on `AWSSDK.S3` 4.0.104.11. The client is configured with:

```csharp
RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
```

AWSSDK.S3 4.x defaults to `WHEN_SUPPORTED`, which can add optional flexible
checksum headers that some S3-compatible providers reject. `WHEN_REQUIRED` still
lets operations that require a checksum calculate one. HTTPS certificate
validation is not disabled, and no unknown SDK properties are set.

`PutObjectRequest.AutoCloseStream` is `false`; the SDK consumes the stream but
does not close it. Client retries are limited to two. The S3 client still owns
its HTTP connection lifecycle and is disposed by `ObjectStorage.Dispose`.

Runtime evidence from the isolated run:

- Put/get/head/copy/list/delete against S3Mock succeeded with both checksum
  settings at `WHEN_REQUIRED`; no optional-checksum rejection was observed.
- `Storage:Region=auto` produced a presigned credential scope containing
  `/auto/s3/aws4_request`, and the independent SigV4 verifier accepted it.
- A non-ASCII key (`dir/猫 1.txt`) was percent-encoded by the SDK and verified
  by the same independent canonical-request/HMAC calculation.
- The published AWS `get-vanilla` vector and tampered path/expiry/signature
  rejection cases passed.

S3Mock does not authenticate requests, and this run used no real R2 credentials.
Real R2 checksum/authorization behavior remains to be verified.

## Tests added

`tests/Shoreline.Tests/StorageCompatibilityTests.cs` contains 16 facts:

S3-compatible integration (uses the existing `local-services` S3Mock fixture):

1. Prefix CRUD/copy verifies the physical raw key through a second zero-prefix
   `ObjectStorage`, while `HeadAsync` exposes the relative key.
2. List/head namespace isolation: an object written outside the prefix is not
   listed or head-visible.
3. Relative-key pagination returns the expected cursor and final `null`.
4. Presigned GET shape for a non-ASCII key and `Region=auto` credential scope,
   plus independent SigV4 recomputation; tampered path, expiry, and signature are
   rejected.
5. Unicode CDN URL exact encoding, with no key prefix and no credentials.

Unit tests (no services needed):

6. Published AWS `get-vanilla` SigV4 vector, including the HMAC signing-key chain.
7. Unknown provider falls back to S3 with a sanitized warning and no secret/access
   key in captured logs.
8. Explicit ALI fails fast; an `aliyuncs.com` endpoint fails fast.
9. Endpoint user-info/non-HTTP and CDN-without-domain validation; password is not
   repeated in the exception message.
10. Local storage requires a missing/relative root to be rejected.

Local backend tests (temporary roots):

11. CRUD, copy, atomic replace, stream ownership, no leftover `*.tmp`, and local
    head/download metadata.
12. Cancellation during put leaves the caller's input stream open, removes the
    temporary file, and preserves an existing destination object unchanged
    instead of exposing a partial overwrite.
13. Rooted keys, backslashes, traversal, and empty keys are rejected across
    put/get/head/delete/copy/list.
14. Directory symlink/reparse escape is refused, and listing does not include
    symlinked content. The fact returns early if the host cannot create symlinks.
15. Ordinal sorted pagination and `maxKeys` bounds `0` / `1001`.
16. Local signing uses the domain and relative key, ignores `Storage:KeyPrefix`
    on disk, and does not read the object.

The SigV4 verifier in the test file reconstructs the canonical request from the
generated URL and recomputes the HMAC with the test secret. S3Mock itself does
**not** validate signatures, so the mock is not an authorization server and these
tests do not prove that R2 would accept the credentials.

## Remaining wiring and verification

- The joint snapshot above is not a shared-worktree build. The S3 facts require
  the existing `local-services` fixture (S3Mock, Mongo, Mailpit) from
  `compose.test.yml`.
- `Program.cs` already registers `ObjectStorage` as a singleton. The new optional
  `ILogger<ObjectStorage>` constructor is resolvable in ASP.NET DI, as exercised
  by `PresenceTests`; no Program or service-registration change is required.
- Set `Storage:Type`, `Storage:KeyPrefix`, bucket, endpoint, region, and
  credentials explicitly per environment. Do not commit credentials.
- A real R2 smoke test is still required: put/get/head/list/copy/delete with the
  production prefix, plus presigned GET and CDN URL checks. This layer has no
  real R2 credentials and has made no live connection.
- Local static-file serving is not wired. Local `SignGetAsync` returns a URL
  only; an authenticated/static server route must be added in a later phase.
- File CRUD (`files/<sa>`) and the storage-pool module are not implemented here.
- Aliyun OSS native signing, multipart upload, object ACL/tag/versioning
  operations, and conditional requests remain out of scope.
- The unknown-provider fallback is intentional compatibility behavior, but it
  can hide a typo in `Storage:Type`; check logs during deployment.
