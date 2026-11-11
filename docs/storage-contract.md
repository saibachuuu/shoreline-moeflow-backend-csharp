# 存储契约 Parity（Python → C#）

范围：S3/R2 对象存储的签名 URL、客户端配置、对象语义，以及存储池（source / transfer / lock / usage）。
结论以测试锁定为准；凡标 `待决策` 的均未擅自改默认行为。

- Python oracle（只读）：`shoreline-moeflow-backend`
- C# 实现：`src/Shoreline.Api/Storage/*`、`src/Shoreline.Api/Modules/StoragePool/*`
- 契约测试：`tests/Shoreline.Tests/StorageContractTests.cs`
  （另有既有覆盖：`StorageCompatibilityTests.cs`、`StorageTests.cs`、`StoragePoolTests.cs`）
- focused 结果：`StorageContract` 22/22；`Storage*` 139/139（见报告）

## 1. 签名 URL

| 能力 | Python（位置 + 行为） | C#（位置 + 行为） | 测试 | 结论 |
|---|---|---|---|---|
| HTTP 方法 | `generate_presigned_url("get_object")` → GET（`oss.py:487`） | `Verb = HttpVerb.GET`（`S3StorageBackend.cs:151`） | `SignedUrlUsesGetAndRequestedExpiry` | parity |
| 过期秒数 | 默认 `expires=604800`（7 天），`int(expires)`（`oss.py:464,480`；pool `backend.py:144` 同值） | 调用方传 `TimeSpan`；`Expires = UtcNow + validity`（`S3StorageBackend.cs:152`） | `SignedUrlExpiresMatchesRequestedValidity`（300s → `X-Amz-Expires=300`） | parity（机制）；默认值见 GAP-5 |
| disposition / Content-Type | `download=True` → `ResponseContentDisposition: attachment`（`oss.py:485`；`output.py:199-203` 导出用 `download=True`） | `contentDisposition` → `ResponseHeaderOverrides.ContentDisposition`（`S3StorageBackend.cs:155-158`） | 同上（`response-content-disposition=attachment` 断言） | parity |
| CDN 模式 | `OSS_VIA_CDN=True` 时直接拼 `oss_domain + path + quote(filename)`，不签名、无有效期（`oss.py:476-479`；pool `backend.py:150-153` 同逻辑） | `viaCdn` 时返回 `domain/key`，忽略 `validity`；`domain` 为空抛错（`S3StorageBackend.cs:134-145`） | `CdnModeIgnoresValidityAndIsUnsigned` | parity |
| 默认有效期 | 文件/头像/导出统一 604800s（`file.py:744`、`output.py:199` 均不传 `expires`） | 统一 `ObjectStorage.DefaultSignedUrlValidity` = 7 天（`ObjectStorage.cs`） | `SignedUrlDefaultsToSevenDays` | parity（已修，GAP-5） |

## 2. S3 客户端

| 能力 | Python | C# | 测试 | 结论 |
|---|---|---|---|---|
| 寻址/region/endpoint | boto3 默认 path 寻址，`signature_version=s3v4`，region 默认 `auto`（`oss.py:169`） | `ForcePathStyle` 默认 true（`ObjectStorage.cs:92`），默认 region `us-east-1` | `DefaultRegionIsDocumentedForR2StyleEndpoints` | 基本 parity；region 默认值见 GAP-7 |
| 重试 | `retries={"max_attempts":3,"mode":"standard"}`（`oss.py:169` ≈ 首试+2 次重试） | `MaxErrorRetry = 2`（= 首试 + 2 次重试）（`ObjectStorage.cs:94`、`StorageBackendFactory.cs:177`） | — | parity（语义等价） |
| 超时 | boto 默认连接/读取超时 | SDK 默认（`Timeout` 未显式配置） | — | 未验证（见 §5） |
| 大文件上传 | file-like 用 `upload_fileobj` 自动 multipart 流式（`oss.py:222-227`）；`str/bytes` 用 `put_object` | 一律单次 `PutObjectAsync`（`S3StorageBackend.cs:33-52`） | 4KB roundtrip（`StorageContractS3Tests`） | 见 GAP-2（未修） |
| 证书校验开关 | `verify` 可配（自签 S3 兼容端点） | `verify_ssl=false` 被显式拒绝（code 4，不静默降级） | `StoragePoolTests` 既有 + `InsecureSslIsRejectedWithActionableCode` | fail-closed（有意更严） |

## 3. 对象语义

| 能力 | Python | C# | 测试 | 结论 |
|---|---|---|---|---|
| 不存在 | `download` 抛 `NoSuchKey(404)`（`oss.py:257`）；`head_object` 返回 `None`（`oss.py:568`）；`is_exist` → bool（`oss.py:296`） | `GetAsync` 抛（SDK 映射为 `NoSuchKeyException : AmazonS3Exception`）；`HeadAsync` 在 404/`NoSuchKey`/`NotFound` 时返回 null（`S3StorageBackend.cs:64-81`，`IsNotFound` 见 `:237-239`）；`DeleteAsync` 不捕获（S3 删除本就幂等） | `MissingObjectMapsToNotFoundAndDeleteStaysIdempotent`（S3Mock） | parity |
| `copy_object` | 同桶服务端复制（`oss.py:610-624`） | 同桶 `CopyObjectAsync`（`S3StorageBackend.cs:106-120`） | —（迁移引擎集成覆盖） | parity |
| `list` 分页 | `list_objects(prefix, start_after, max_keys≤1000)` 返回 `{objects[], next}`（`oss.py:498-504`） | `ListAsync(prefix, startAfter, maxKeys 1..1000)`（`S3StorageBackend.cs:163` 起） | 既有 `StorageCompatibilityTests` 前缀/游标用例 | parity |
| ETag | `_strip_etag` 去引号 | 同样去引号（`S3StorageBackend.cs` Head 路径） | — | parity |
| 本地后端 | `STORAGE_PATH` 文件探针（`oss.py:600-608`） | `LocalStorageBackend`（`SignGetAsync` 要求 validity>0，`:185-187`） | 既有 `StorageCompatibilityTests` 本地用例 | parity |
| 阿里云分支 | oss2 原生 + CDN 鉴权 C + `x-oss-process`（`oss.py` `_sign_oss_url/_sign_cdn_url`、`file.py:766-768`） | provider map 遇到 aliyun 直接抛 `NotSupportedException`（`ObjectStorage.cs:169`） | 既有 `Aliyun*` fail-fast 用例 | fail-fast（有意，见 GAP-6） |

## 4. 存储池

| 能力 | Python | C# | 测试 | 结论 |
|---|---|---|---|---|
| source CRUD/启用/选择 | 后台管理 API（`storage_pool/api.py:814-888`：sources 增删改查/test、transfers、migrate/restore/move/lock、settings、scan） | 同等路由（`StoragePoolEndpoints.cs:18-160`） | 既有 `StoragePoolTests` | parity |
| key 前缀归一 | `normalize_sub_path` 去首尾 `/` 后恰补一个（`models.py`） | `NormalizePrefix` 修前保留尾部多 `/`（`StorageBackendFactory.cs:75-81`）；`StoragePoolBson.NormalizeSubPath` 用 `Trim('/')` 本就一致 | `NormalizePrefixMatchesPythonNormalizeSubPath`（含 `"a//b"→"a//b/"` 内部保留） | GAP-1（已修） |
| 脱敏 | 前 4…后 4，≤8 位全星号（`models.py`） | `MaskedAccessKey` 同规则（`StorageBackendFactory.cs`） | `MaskedAccessKeyMatchesPythonMasking` | parity |
| `ToString`/日志 | 不含密钥的描述串 | 同（`StorageBackendFactory.cs`） | `BackendDescriptorToStringOmitsCredentials` | parity |
| 状态机/锁 | `PENDING0/COPYING1/VERIFIED2/SWITCHED3/DONE4/FAILED5/ABORTED6/CANCELLED7`，`ACTIVE=(0,1,2,3)`，`RETRYABLE=(5,6)`；`storage_transfer_run_lock` 排他（`transfer.py:202-213`）；切换加 `StoragePoolLock` + `label_lock`（`:559-580`） | 常量/名称/分组逐值一致（`StoragePoolConstants.cs`，`IsActive` 见 `:48`，`IsRetryable` 见 `:49`，`LockTtlSeconds=1800` 见 `:46`）；`storage_transfer_run_lock` + `storage_pool_lock` + `Renew`（`StoragePoolTransferService.cs:21,96-108`、`StoragePoolSourceResolver.cs:46-126`）；仅 `RETRYABLE` 可重试（`:71-73`） | `StoragePoolConstantsMatchPythonTransferStates` | parity |
| 项目 `csid`/source 字段 | `storage_source_id` + 切换时间；`_resolve` 遇缺失/禁用返回 None → 调用方 `or oss` 回默认存储（`__init__.py`、`file.py:773`、`output.py:198`） | 同名字段；`ForProjectAsync` 遇未知/禁用抛 409/6，绝不回落（`ProjectStorageRouter.cs`）；`ForProjectOrNullAsync` 仅无 `csid` 时返回 null；resolver 异常向上传播 | `RouterFailsClosed*` ×3、`RouterBorrowsDefaultForMissingEmptyCsid*`、`ResolverExceptionPropagatesInsteadOfFallingBack`、`FileTests:286-291` 既有锁定 | fail-closed（有意；切换期风险见 §6，待决策） |
| `storage_usage` 增减 | `fs`/`ob` 随上传/删除/导出增减（`ProjectService.cs:584-616` 对应 `fs`/`ob` 读出；`FileService.cs:649,706` 上传记账、`Mutations.cs:266` 删除扣减） | 同（`IncFileChainAsync` 链式增减 `fs/fc/fo/sc`：`FileService.cs:649`、`Mutations.cs:171-279`；`OutputService` 维护 `ob`） | 既有 `FileTests`/`OutputTests` 记账用例 | parity（由既有测试覆盖） |
| resample 缺失回退 | 池内缺失 → 回默认 `oss` 再探一次（`file.py:788-794`“别让图裂”） | 缺失即返回 `"generating"`，无 oss 回退（`FileService.Serialization.cs:150`） | — | 见 GAP-4（待决策） |

## 5. Gap 清单

- GAP-1（已修）：`NormalizePrefix` 尾部多斜杠。`src/Shoreline.Api/Storage/StorageBackendFactory.cs:75-81`（修前）vs Python `models.py::normalize_sub_path`。`"/storage//"` 修前得 `"storage//"`，Python 得 `"storage/"` —— 物理 key 前缀错位。修：`Trim('/')` 后补单 `/`；测试 `NormalizePrefixMatchesPythonNormalizeSubPath`（本文件新增）。
- GAP-2（未修）：大文件无 multipart。`src/Shoreline.Api/Storage/S3StorageBackend.cs:33` 单次 `PutObjectAsync` 全量进内存；Python `upload_fileobj` 自动分片流式（`oss.py:222-227`）。当前文件体量下 roundtrip 一致；若出现 ≥100MB 上传会有 OOM/超时风险。建议后续切 `TransferUtility` + 大文件测试，本次不动。
- GAP-3（**决定：保持 fail-closed**）：source 缺失/禁用时 C# fail-closed（409/6）vs Python 回落全局 `oss`。证据：`ProjectStorageRouter.cs` vs `storage_pool/__init__.py::_resolve` + 调用方 `or oss`（`file.py:773`、`output.py:198`）。理由是切到默认桶会读到 stale 副本、造成静默数据错乱；宁可 409。上线前用 `tools/check-storage-sources.js`（只读）列出受影响项目，修复或书面接受后再切换。
- GAP-4（待决策，minor）：resample 无 oss 回退。`FileService.Serialization.cs:150` vs `file.py:788-794`。迁移窗口内旧重采样图在 C# 侧显示 `generating` 占位而非旧图。建议补一次 `oss.is_exist` 回退（读路径，低风险）。
- GAP-5（**已修**）：签名默认有效期。Python 全场景 604800s；C# 曾为文件/头像 1h、导出 7d。现统一为 `ObjectStorage.DefaultSignedUrlValidity`（7 天），与 Python 一致。
- GAP-6（fail-fast，有意）：阿里云 oss2 分支 Python 独有；C# 遇到 aliyun 配置直接抛（`ObjectStorage.cs:169`）。不影响 R2/自建 S3 部署；若将来要接阿里云需另立项实现 `AliyunStorageBackend`。
- GAP-7（parity，加注）：默认 region。Python `auto`（`oss.py`）；C# `us-east-1`（`ObjectStorage.cs`）。R2/自建端点下 endpoint 决定签名域，无影响；直连 AWS S3 且 bucket 不在 us-east-1 时以显式配置为准。测试 `DefaultRegionIsDocumentedForR2StyleEndpoints` 锁定。
- GAP-8（fail-closed，有意，与 GAP-3 同根）：resolver 异常 Python 吞掉继续 fallback；C# 向上传播。测试 `ResolverExceptionPropagatesInsteadOfFallingBack` 锁定。

## 6. 切换期“旧项目数据读不到”判定与兼容建议（待决策）

判定：风险真实存在。若某项目 `storage_source_id` 指向的 source 在 C# 侧不存在或被禁用，
Python 会静默读回全局默认存储（可能读到旧数据，也可能读到 stale 数据）；
C# 直接 409（`FileTests.cs:286-291` 已把该行为锁死）。也就是说：切到 C# 后，
这类项目的历史文件下载/签名 URL 会从“可能读到（旧）数据”变为“明确报错”。

建议（未实施，需产品决策）：

1. 上线前跑一次 `storage_source_id` 健康检查：凡指向缺失/禁用 source 的项目列清单，
   人工确认是指向回默认存储（清掉 `csid`）还是补建 source；不清不切。
2. 若必须平滑：仅对**读路径**（下载、签名 URL、cover/resample）加“回退并打标”兼容模式——
   回退到默认存储但打日志/指标，写路径与迁移指针仍 fail-closed；待存量消化后关闭。
   这与 `ForProjectOrNullAsync`（`ProjectStorageRouter.cs`，`ProjectContentCleanup.cs:86,191` 在用）
   的形状一致，改动面小。
3. 默认行为保持 fail-closed：写路径绝不能静默落到默认存储，否则新旧数据永久分裂。

## 7. 未验证项

- 真实 R2 / CDN（`OSS_VIA_CDN=True` + 真实域名）：仅以单元形式锁定 URL 拼接形状，未做端到端。
- S3 连接/读取超时与重试抖动的数值对等：两侧均为 SDK 默认，未逐项核对。
- 百 MB 级以上大文件上传（GAP-2 的另一半）。
- 阿里云 oss2 分支（C# 不支持，fail-fast；见 GAP-6）。
- `copy_object` 跨桶/跨服务商（Python 明确只支持同桶，阶段 3 才用 `S3Transfer`；C# 同为同桶语义）。
