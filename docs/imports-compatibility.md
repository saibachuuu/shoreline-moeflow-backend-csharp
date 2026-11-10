# LabelPlus 项目导入兼容说明（G2）

本文记录 C# 后端对 Python `TeamProjectImportAPI`、`Project.create(labelplus_txt)` 与
`import_from_labelplus` 后台任务的兼容实现。`Program.cs` 已接入导入路由与 job
handler；普通项目创建端点也已在 `ProjectService.CreateProjectAsync` 内完成
`labelplus_txt` 的创建前校验、单 target 入队与重新读取，见第 8 节。

## 1. 文件与职责

| 文件 | 职责 |
| --- | --- |
| `src/Shoreline.Api/Features/LabelPlusReader.cs` | 纯解析器，对应 `app/utils/labelplus.py:17-53` |
| `src/Shoreline.Api/Features/ProjectImportService.cs` | 校验、初始化标志、job 执行、JSON schema 预处理 |
| `src/Shoreline.Api/Features/ProjectImportService.Files.cs` | LabelPlus 占位文件创建与复用 |
| `src/Shoreline.Api/Features/ProjectImportEndpoints.cs` | `POST /v1/teams/{teamId}/project-sets/{projectSetId}/project-zips` |
| `src/Shoreline.Api/Jobs/ProjectImportJobHandler.cs` | `IJobHandler` 适配器，kind = `import_from_labelplus` |
| `src/Shoreline.Api/Features/ProjectImportRegistration.cs` | 扩展注册/路由入口 |
| `tests/Shoreline.Tests/ProjectImportTests.cs` | 23 个 direct Facts |
| `tests/Shoreline.Tests/ProjectCreateLabelPlusHttpTests.cs` | 普通创建端点 4 个真实 HTTP Facts |

本模块的注册与路由保持 additive；普通创建端点的接线只改
`src/Shoreline.Api/Features/ProjectService.cs`，不修改 `Program.cs`、
`FileService.cs`、`LabelService.cs` 或任何共享路由。

## 2. 解析语义

`LabelPlusReader.Read(string?)` 使用与 Python 相同的行级正则：

- 文件行：`.+>>>\[(.+)\]<<<.+`，第一个捕获组非空才创建文件。
- 标签行：`.+---\[.+\]---.+\[(.+,.+,.+)\]`，捕获 x / y / position_type。
- 其余行追加到当前标签的 `translation`，每行后补一个 `\n`。
- 结束后每个 translation 最多去掉一个末尾 `\n`。
- header 行忽略；文件行之前出现标签行会抛 `FormatException`（对应 Python 的
  `files[-1]` 解析失败）。
- 输入按 CRLF / CR / LF 分行，空输入返回空列表。

非法数字（`abc`、NaN 等）在解析阶段抛出，由
`ValidateBeforeCreate` 映射为 `LabelplusParseFailedError`（4029）。

## 3. 创建前校验（必须在任何 DB 写入之前）

`ProjectImportService.ValidateBeforeCreate(string? text)`：

- 解析失败 → `ApiError("LabelplusParseFailedError", 4029, "\"翻译数据.txt\" 解析失败")`。
- 即使最终有多个 target，也先执行解析，确保坏文件不会创建 project。
- 该方法不写数据库、不创建 project。

## 4. 创建后初始化

`InitializeAfterCreateAsync(project, actor, labelplusText, requestId, ct)`：

- 空文本 → 返回 `null`，不改 `is/ip/ie/it`。
- 再次解析并确认 target 数恰好为 1、存在 active creator；否则返回 `null` 且不写 flags。
- 满足条件时：
  - 从 project 的 durable `ij` 字段取/分配 job id（`ij` 现在是 intent，不再是
    入队结果的回填）；
  - project `$set it=<原文>, is=0, ip=0, ij=<jobId>`，先于任何队列写入；
  - 删除该 project 的 import checkpoint；
  - 用 `JobQueue.EnqueueOnceAsync(jobId, kind, payload)` 入队
    `import_from_labelplus`，payload 固定为
    `{ project_id: ObjectId, label_revision: Int64 }`（字段顺序与 BSON 类型固定，
    重试时必须从同一 intent 重建，不能走 JSON 往返）。
- `ij` 复用规则：同一 project + 同一 revision 且队列行仍为 `pending/running`
  时复用；队列行缺失（上次崩溃在 upsert 之前）也复用；其它 generation 或
  `done/failed/cancelled` 终态则分配新 id。同 id 不同 typed payload 视为
  generation 错配，标记 `is=Error, ie=ErrorUnknown` 并抛出，不覆盖队列行。
- `requestId`（`X-Request-ID` / `Idempotency-Key`）**不作为导入幂等键**：Python
  `TeamProjectImportAPI` 没有 request-id 去重，`request_id` 只用于 identity 审计
  日志。该参数仅为 HTTP 调用点保留。

`is/ip/ie/it` 与 Python 常量一致：

- 状态：`PENDING=0 / RUNNING=1 / SUCCEEDED=2 / ERROR=3`
- 错误：`UNKNOWN=0 / NO_TARGET=1 / NO_CREATOR=2 / PARSE_FAILED=3`

## 5. 后台任务

`ProjectImportService.RunImportAsync(payload, ct)`：

1. 读取 `project_id` / `label_revision`。
2. 通过 `ProjectLabelLock.EnterAsync(labelContext, projectId, expectedRevision: ...)`
   取得项目标签租约；revision 过期返回 `LabelErrors.Busy`（code 1 / HTTP 409），
   且不修改导入标志。
3. 重新读取 project：
   - `is == SUCCEEDED` 直接返回（幂等）。
   - `is == ERROR` 且 `it` 为空直接返回（避免重复报错）。
4. 缺 target → `is=3, ie=NO_TARGET, it=""`，`ip` 保留。
5. 缺 active creator → `is=3, ie=NO_CREATOR, it=""`。
6. 否则 `is=RUNNING, ip=0`，按文件创建占位文件，再逐标签调用 E1 稳定 id
   helper：`LabelService.CreateSourceOnceAsync(... sourceId, sourceRank,
   expectedRevision=session.Revision ...)` → `CreateTranslationOnceAsync(... translationId,
   sourceId, targetId, content, expectedRevision ...)`。helper 内部完成 pending
   marker + 基于主表的计数重建；空 translation 返回 `Empty`，不插行不计数。
7. 每处理一个标签写入 `project_import_checkpoint`
   （`_id=project_id, file_index, labels_done, label_revision`）；每个文件结束更新
   `ip = int(file_index * 100 / file_count)`。
8. 成功：`is=SUCCEEDED, ip=0, it=""`，删除 checkpoint。
9. 异常：`is=ERROR, ie=PARSE_FAILED, it=""`，保留已完成内容与 checkpoint；
   重试从 checkpoint 继续，不会重复已成功标签。

还原后（例如数据库快照回滚）使用 `ProjectLabelLock` 的恢复入口时，若 job 的
`label_revision` 与当前 project 不一致，任务应中止而不要改写导入标志。

## 6. 占位文件

`EnsurePlaceholderFileAsync(project, name, ct)`：

- 使用 `FileService.ParseFilename` 校验名称与类型。
- 仅支持图片（对应 Python `FileType.SUPPORTED=(IMAGE,)`）；文本等返回 107。
- 同目录下 active 同名图片（大小写不敏感）复用，不重复创建。
- 新建时写与 `FileService.CreateFileDocumentAsync` 一致的字段：
  `n,t,p,f,a,mo,sn,dn,sa,md,fs,cover_bytes,resample_bytes,fn,th,the,fo,fc,sc,tsc,csc,et,v,ac,ss,ps,pt,op,ft`。
- 为每个 target 插入 `file_target_cache`，并 `project.fc += 1`。
- 不写图片二进制、不调用对象存储，`fs/cover_bytes/resample_bytes` 均为 0。

## 7. HTTP 端与 schema

唯一路由：

`POST /v1/teams/{teamId}/project-sets/{projectSetId}/project-zips`

- 旧版 URL 声明 GET 但没有处理函数，本实现不注册 GET/PUT → 405。
- `multipart/form-data`，必填两个文件：`project`（JSON）与 `labelplus`（UTF-8 文本）。
- 先校验 token、team、project set、`team:CREATE_PROJECT`。
- project set 必须属于 URL 中的 team，否则按 Python 抛 4022
  `ProjectSetNotExistError`（注：Python 的 4014 是 `MoveToSelfSubfolderError`，
  与导入无关）。
- project JSON 的必需字段：`name,intro,allow_apply_type,application_check_type,
  default_role,project_set,source_language,output_language`；额外字段忽略。
- `project_set` 始终强制为 URL 中的 set；`default_role` 从系统角色 code 解析为 ID；
  `target_languages=[output_language]`；`output_language` / `labelplus_txt` 不传给
  `CreateProjectAsync`。
- 语言相同时返回 6002；creator / 自定义角色被拒绝。
- JSON 最多 1 MB，LabelPlus 文本最多 16 MB；multipart 请求体和单文件限制均为 1 GB，
  不做整包无限缓存，也不解压 ZIP。
- 创建成功返回 `{"message":"创建成功","project":<ProjectService.ViewAsync>}`。

## 8. 接线状态

1. `Program.cs` 已包含 `builder.Services.AddProjectImports()` 与
   `app.MapProjectImports()`；`AddProjectImports` 用 `TryAdd` 注册
   `ProjectLabelLock`、`LabelRequestContext`、`LabelService`，再注册
   `ProjectImportService` 与 `ProjectImportJobHandler`。
2. `POST /v1/teams/{teamId}/project-sets/{projectSetId}/project-zips` 已接线，并调用
   `ValidateBeforeCreate` → `CreateProjectAsync` → `InitializeAfterCreateAsync`。
3. 普通创建端点已接线（`ProjectService.CreateProjectAsync`）：
   - 读取普通 JSON 的 `labelplus_txt`（缺省/null 为空，非字符串按字段校验失败）。
   - 非空文本在**任何** project/target 写入之前调用 `ValidateBeforeCreate`；即使
     target 数大于 1 也先解析，坏文件返回 4029 且不产生 project/target/job。
   - project、targets、creator member 完成后，仅当文本非空且恰好 1 个 target 时调用
     `InitializeAfterCreateAsync(project, actor, labelplusText, "", ct)`。空文本与多
     target 不写 `it/is/ip`、不入队。
   - 普通创建写入的默认 `is` 与 Python 模型默认值一致，为 `SUCCEEDED=2`（不是
     `RUNNING=1`）；只有真正入队的导入才改写为 `PENDING/RUNNING`。
   - 入队后重新读取 project，使响应像 Python 的 `project.reload()` 一样反映已持久化的
     `is/it/ip`（可能是 pending、running 或已完成）。`requestId` 在该参数中不参与
     幂等，普通端点传空字符串。
4. `JobWorker` 已能发现所有 `IJobHandler`。调用 `RunImportAsync` 的测试使用
   `LocalServicesWithoutWorker`，由测试自己拥有 job，避免后台 worker 与直接调用
   竞争同一个导入。

## 9. 验证与隔离

验证在 task-owned 隔离树 `/tmp/opencode/g2e1-verify` 中进行：

- 基线：先验证 `git archive 5bb9404`，再验证期间前进到的 HEAD `2cdc3e6`。
  两棵树都只叠加本任务的 owned 文件：`ProjectImportService.cs`、
  `ProjectImportService.Files.cs`、`LabelService.Idempotent.cs`、
  `ProjectImportTests.cs` 与三份 docs；不包含其它并行 agent 的工作树修改。
- `dotnet restore --locked-mode` → `dotnet build Shoreline.sln -warnaserror`：
  0 warnings / 0 errors。
- `ProjectImportTests`：23 passed / 0 skipped（原 6 个 skip 全部恢复）；
  `LabelIdempotencyTests`：17 passed；`JobQueueIdempotencyTests`：14 passed。
- 全量 `dotnet test`：`5bb9404` 783 passed / 0 failed / 0 skipped；
  `2cdc3e6` 789 passed / 0 failed / 0 skipped（新增 6 个 OCR 测试）。
- 测试 host 使用 `LocalServicesWithoutWorker`（在 `ProjectImportTests.cs` 内声明
  独立 collection）：导入测试直接驱动 `RunImportAsync` 以观察 checkpoint /
  取消 / 重放，后台 `JobWorker` 若同时 claim 同一个 job 会让断言不确定。这与
  既有 thumbnail 测试“自己拥有 job”的模式一致。
- 共享工作树同时被其它 agent 修改，未在共享工作树直接 build/test，也未把其它
  agent 的改动写入隔离树。

## 10. 已知边界

- 普通创建端点与 HTTP 导入路由都已接线；普通端点的 `requestId` 传空字符串，因为该
  参数不参与导入幂等（durable `ij` job id 才是幂等锚点）。
- 生产导入仅支持图片占位文件；文本文件与多 target 不导入（与 Python 一致）。
- 未实现图片上传/对象存储，`fs` 等字段保持 0。
- 标签计数缓存统一为 canonical `file_target_cache`（E1 reconciler 与
  `LabelService` 都使用该集合）。
- 未改动 `UpdateAsync`、`AddExternal`、恢复等其它 identity 生命周期路径。
- K1 团队生命周期锁只在 `ValidateBeforeCreate` / `InitializeAfterCreateAsync` /
  `RunImportAsync` 预留调用点注释，未注入、未执行 `EnsureWritableAsync`。

## 11. 导入幂等 intent 与 E1 下层 helper 契约

当前 `RunImportAsync` 已有独立的 `project_import_journal`：每个 `(project,
label_revision, file_index, label_index)` 在写 source 之前先持久化 intent，并在
插入前预分配 `source_id`、`translation_id`（ObjectId）。重试时只按这些 `_id`
查找并校验 `file_id`、`source_rank`、`label_epoch`、文件/标签位置、坐标和
position_type；translation 完成后跳过；checkpoint 只在 journal 更新之后推进。
旧 revision 的 journal 按 200 条一批有界清理，避免长时间占用 project label lease。

**已由 E1 helper 解决、G2 已接线的点：**

- rank 只是排序字段，不是身份。intent 已落库但 source 尚未插入时崩溃，另一个
  合法 writer 可能占用同一 rank；因此实现**不再**用 `{f, r}` 查找或认领 source。
  预分配 `source_id` 是唯一身份；只有 `_id` 相同、`f/content/x/y/p/label_epoch`
  都匹配的行才复用（`r` 不参与身份比较），否则 helper 拒绝认领。
- source 插入与计数不再是多次 `$inc`。helper 先写 pending marker，再由
  `LabelCounterReconciler` 从主表 `$set` 重建 `sc/tsc/csc`、祖先 rollup、project、
  target 与 canonical `file_target_cache`；未知结果或部分计数重试收敛。
- 早期未包含 `source_id`/`translation_id` 的 journal 视为有歧义，`EnsureJournalShape`
  直接拒绝恢复（`is=Error, ie=ErrorUnknown`），不做生产迁移。

### E1 已交付的公开契约（G2 已接入）

```csharp
public enum SourceWriteOutcome { Created, AlreadyApplied }

public sealed record SourceWriteResult(
    SourceWriteOutcome Outcome,
    ObjectId SourceId,
    long Revision,
    bool CountersApplied);

public Task<SourceWriteResult> CreateSourceOnceAsync(
    HttpContext? context,     // 可为 null：job 不在 HTTP 请求内，helper 不读它
    BsonDocument actor,
    ObjectId fileId,
    ObjectId sourceId,        // 调用方在写 journal intent 时生成的稳定 _id
    string content,
    double x,
    double y,
    int positionType,
    int sourceRank,           // 仅用于排序；不能作为身份
    long expectedRevision,    // 必须校验 project/session revision 与行 label_epoch
    CancellationToken ct);
```

实际语义：

1. source 文档以调用方传入的 `sourceId` 作为 `_id` 插入（`_id` 才是 identity）。
2. 若同 `_id` 已存在，校验 `f == fileId`、`c`（ordinal）、`x/y`（`<1e-9`）、`p`
   与 `label_epoch == expectedRevision`；匹配则返回 `AlreadyApplied` 并重跑 reconcile，
   绝不重复计数；不匹配则 `ValidateError` code 2，不覆盖/认领。`r` 只排序。
3. `Created` 分支插入带 `idempotent_pending/owner/revision` 的主行，然后由
   `LabelCounterReconciler` 从主表 `$set` 重建全部 label 计数；counters 重建后
   以 owner+revision CAS 清 marker。重试 `AlreadyApplied` 仍重跑 reconcile。
4. expectedRevision 不符时返回 409 `LabelBusyError`，不写任何文档。
5. `CreateTranslationOnceAsync(..., ObjectId translationId, ObjectId sourceId,
   ObjectId targetId, ...)` 校验 `o/t/u`（非空 content 还校验 `c`）；`mt=true`
   返回 8007；空 content 返回 `Empty`，不插行、不写 marker、不 reconcile。

G2 现在直接调用上述 helper（`Features/LabelService.Idempotent.cs`），不再有
`IdempotentWriterUnavailable` 分支。顺序保持
`journal intent → CreateSourceOnceAsync → CreateTranslationOnceAsync → journal
translation_done → checkpoint`；helper 的 pending marker / reconciler 保证 source 或
translation 已提交但确认丢失的重试收敛。旧无 stable-id journal 仍然 fail-closed
（`is=Error, ie=ErrorUnknown`，保留 intent），不做 rank 认领。

未知 enqueue 结果按 durable job id 区分：捕获异常后读取 `ij` 对应的队列行，存在
任意状态即保持 `is=Pending`（job 可能已提交），只有确认不存在时才标记
`is=Error, ie=ErrorUnknown`。这与旧实现按 kind+project+revision 近似查找不同，
不会再误认另一个 generation 的 job。

### K1 团队生命周期锁预留

`ProjectImportService` 构造函数保留可选 `TeamLifecycleLock`：
`InitializeAfterCreateAsync` 与 `RunImportAsync` 在持有 project label lock 后、
第一次标志写入前调用 `EnsureWritableAsync(teamId)`；`ValidateBeforeCreate` 保持
纯解析，不做团队写入检查。生产 DI 提供该服务；直接单测未注入时保持可构造。

### 旧 blocker 已解除

早期 `e910e33` 快照的 `FileService` long/int 编译错误属于并行 WIP，本任务在
`5bb9404` 干净基线上不再出现。
## 12. 本轮接线实证（2026-11-10）

### Python 绝对路径对照

- `C:\Users\88888888\shoreline-moeflow\shoreline-moeflow-backend\app\models\project.py:460-546`
  - `Project.create` 在第一次 `project.save()` 之前执行 `load_from_labelplus`
    （非空文本；失败抛 `LabelplusParseFailedError`）。
  - 只有 `labelplus_txt` 非空、`creator` 存在且 `len(targets) == 1` 时写
    `it/is=PENDING/ip=0` 并调用 `import_from_labelplus(project.id)`，随后 `reload()`。
  - `import_from_labelplus_status` 模型默认值是 `SUCCEEDED=2`
    （`project.py:415-417`，常量见 `app/constants/project.py:19-24`）。
- `C:\Users\88888888\shoreline-moeflow\shoreline-moeflow-backend\app\apis\team.py:472-489`
  - 普通创建先检查 `team:CREATE_PROJECT`，再把 JSON 的 `labelplus_txt` 传给
    `Project.create`。
- `C:\Users\88888888\shoreline-moeflow\shoreline-moeflow-backend\app\validators\project.py:29-75`
  - `labelplus_txt = fields.Str(missing=None)`：缺省/null 为空，非字符串是字段校验失败。
- `C:\Users\88888888\shoreline-moeflow\shoreline-moeflow-backend\app\apis\team.py:508-552`
  - 导入端点把上传的 labelplus 文本作为 `labelplus_txt` 传入同一个
    `Project.create`，C# 导入端点继续复用同一 hook。

### 与 Python 的已知差异

- Python 在 `Project.create` 内直接调用 Celery 任务；C# 使用已有的
  `InitializeAfterCreateAsync` 持久化 durable `ij` intent 后写入 `csharp_job`，
  由 `JobWorker` 执行。区别是队列实现，不是触发条件。
- Python 的 `request_id` 不参与导入去重；普通创建端点在 C# 中传空字符串，
  durable `ij` job id 才是幂等锚点。
- C# 原先普通创建写 `is=1`；本轮改为 Python 默认的 `is=SUCCEEDED=2`。只有真正
  入队的导入才进入 `PENDING=0 / RUNNING=1`。

### 验证记录

- 基线：`f2a73253dfd9d4b2cd7caf0bc8f282b9795c3f4c`（分支
  `fix/readiness-labelplus-20261110`）。本轮没有其它提交或未知提交。
- 修改文件：`src/Shoreline.Api/Features/ProjectService.cs`、
  `tests/Shoreline.Tests/ProjectCreateLabelPlusHttpTests.cs`、
  `tests/Shoreline.Tests/ProjectImportTests.cs`、
  `tests/Shoreline.Tests/TeamDissolutionTests.cs`、本文档。
- `dotnet restore Shoreline.sln --locked-mode`：成功。
- `dotnet build Shoreline.sln -warnaserror --no-restore`：0 warnings / 0 errors。
- 新 HTTP 测试 `ProjectCreateLabelPlusHttpTests`：4 passed / 0 failed / 0 skipped。
  - invalid 单/多 target：4029，project/target/csharp_job 计数不变。
  - valid 单 target：durable job 入队，`JobWorker` 执行至 `is=2`；实测 1 file、
    2 source、1 translation、1 target。
  - 空文本与多 target：`is=2`、`it=""`、无 job。
  - `beginner`/无 `CREATE_PROJECT` 的团队成员：103，project/target/job 计数不变。
- 回归子集（`ProjectTests` + `ProjectImportTests` + `TeamDissolutionTests` +
  新 HTTP 测试）：60 passed / 0 failed / 0 skipped。
- 全量 `dotnet test Shoreline.sln` 曾在本轮 N5 Mongo 上启动，但 Mongo 容器
  (`mem_limit: 512m`) 在约 25 分钟后被 OOM kill（`OOMKilled=true`, exit 137），
  因此**没有**可信的全量通过结果；本文不把它当作验收证据。
- LSP：本环境只配置了 C# LSP（`csharp-ls`）。在 Api 源文件与 Tests 文件上实际
  成功执行 `documentSymbol`、`hover`、`findReferences`、`workspaceSymbol`；
  Markdown/YAML/JSON 没有可用 LSP server。
- 端口与隔离：N5 Mongo `27261` / S3 `29261` / SMTP `21261` / Mailpit `28261` /
  API `25261`；Compose 使用显式非 `.env` 的 `/tmp/labelplus-n5.env`，stack 名
  `shoreline-csharp-labelplus-n5-20261110`，测试库均为随机库。启动前 Windows
  TCP 与 WSL `ss` 均确认端口空闲。
