# LabelPlus 项目导入兼容说明（G2）

本文记录 C# 后端对 Python `TeamProjectImportAPI`、`Project.create(labelplus_txt)` 与
`import_from_labelplus` 后台任务的兼容实现。生产代码尚未接入共享 `Program.cs` /
`ProjectService.cs`，接线方式见文末。

## 1. 文件与职责

| 文件 | 职责 |
| --- | --- |
| `src/Shoreline.Api/Features/LabelPlusReader.cs` | 纯解析器，对应 `app/utils/labelplus.py:17-53` |
| `src/Shoreline.Api/Features/ProjectImportService.cs` | 校验、初始化标志、job 执行、JSON schema 预处理 |
| `src/Shoreline.Api/Features/ProjectImportService.Files.cs` | LabelPlus 占位文件创建与复用 |
| `src/Shoreline.Api/Features/ProjectImportEndpoints.cs` | `POST /v1/teams/{teamId}/project-sets/{projectSetId}/project-zips` |
| `src/Shoreline.Api/Jobs/ProjectImportJobHandler.cs` | `IJobHandler` 适配器，kind = `import_from_labelplus` |
| `src/Shoreline.Api/Features/ProjectImportRegistration.cs` | 扩展注册/路由入口 |
| `tests/Shoreline.Tests/ProjectImportTests.cs` | 15 个 direct Facts |

本实现只新增上述文件，不修改 `Program.cs`、`ProjectService.cs`、`FileService.cs`、
`LabelService.cs` 或任何共享路由。

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
  - project `$set it=<原文>, is=0, ip=0`；
  - 删除该 project 的 import checkpoint；
  - 入队 `import_from_labelplus`，payload 为
    `{ project_id, label_revision }`。

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
6. 否则 `is=RUNNING, ip=0`，按文件创建占位文件，再逐标签调用
   `LabelService.CreateSourceAsync` / `CreateTranslationAsync`。
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

## 8. 接线步骤（待主代理执行）

1. 在 `Program.cs` 添加：
   ```csharp
   builder.Services.AddProjectImports();
   ...
   app.MapProjectImports();
   ```
   `AddProjectImports` 会 `TryAdd` `ProjectLabelLock`、`LabelRequestContext`、
   `LabelService`，再注册 `ProjectImportService` 与 `ProjectImportJobHandler`。
2. `ProjectService.CreateProjectAsync` 在创建 project 之前调用
   `ValidateBeforeCreate(labelplus_txt)`；创建、target、creator member 完成后调用
   `InitializeAfterCreateAsync(project, actor, labelplus_txt, requestId, ct)`。
   注意：即使 target 数大于 1，也要先解析，不能只在最终可导入时才解析。
3. project JSON 中的 `labelplus_txt` 需要由普通创建端点（非导入端点）传给上述 hook；
   `ProjectService` 当前忽略该字段。
4. `JobWorker` 已能发现所有 `IJobHandler`；确保其注册发生在 worker 启动之前。

## 9. 验证与隔离

验证在 task-owned 最小快照
`C:\Users\88888888\AppData\Local\Temp\opencode\g2-min-20261109` 中进行：

- 基线：`git archive HEAD`（421208d，含 C1/C2/Presence）。
- 叠加：本模块 6 个生产文件、`LabelPlusReader.cs`、`ProjectImportTests.cs`，以及
  G2 依赖的 `FileService*` / `LabelService*` / `ProjectLabelLock.cs` /
  `FileMutationGate.cs`。
- 快照内仅为让并行未完成文件编译做了局部修正（`Integer` 返回 long 的强制转换、
  `UpdateOneAsync` 命名参数、`BsonObjectId` 模式、`FileService` 静态字段初始化顺序）；
  这些修正没有写回共享工作区。
- 结果：build 0 warnings / 0 errors；`ProjectImportTests` 17 通过 / 6 跳过
  （等待 E1 `CreateSourceOnceAsync`）；快照全量 271 通过 / 6 跳过。
- 跳过的 6 个用例覆盖真实 source/translation 写入、取消恢复、未知结果回放、
  epoch 变更和 checkpoint 续跑，不能在缺少稳定 id writer 时伪造通过。
- 共享工作区仍有其它 agent 的未完成模块，本结果不应被当作它们的验收证据。
- 该快照基于 421208d，只用于开发自检；最终验收需等待 e910e33 + 稳定 E1/D1
  依赖的新快照（见第 11 节 blocker）。

## 10. 已知边界

- 普通项目创建端点尚未调用导入 hook；HTTP 导入路由尚未接线。
- 生产导入仅支持图片占位文件；文本文件与多 target 不导入（与 Python 一致）。
- 未实现图片上传/对象存储，`fs` 等字段保持 0。
- `LabelService` 的计数缓存使用 `filetargetcache`，本模块与 `FileService` 使用
  `file_target_cache`；该差异属于并行 Label 模块，本模块未修改。
- 未改动 `UpdateAsync`、`AddExternal`、恢复等其它 identity 生命周期路径。

## 11. 导入幂等 intent 与 E1 下层 helper 契约

当前 `RunImportAsync` 已有独立的 `project_import_journal`：每个 `(project,
label_revision, file_index, label_index)` 在写 source 之前先持久化 intent，并在
插入前预分配 `source_id`、`translation_id`（ObjectId）。重试时只按这些 `_id`
查找并校验 `file_id`、`source_rank`、`label_epoch`、文件/标签位置、坐标和
position_type；translation 完成后跳过；checkpoint 只在 journal 更新之后推进。
旧 revision 的 journal 按 200 条一批有界清理，避免长时间占用 project label lease。

**尚未通过最终验收的缺陷（必须由 E1 下层 helper 解决，不能用 rank 认领）：**

- rank 只是排序字段，不是身份。intent 已落库但 source 尚未插入时崩溃，另一个
  合法 writer 可能占用同一 rank；因此实现**不再**用 `{f, r}` 查找或认领 source。
  找不到预分配 `source_id` 对应的 source 时直接进入可恢复的 blocked（`is=Error`,
  `ie=ErrorUnknown`），保留 intent，等待稳定 id writer，而不是猜测。
- 早期未包含 `source_id`/`translation_id` 的 journal 视为有歧义，直接拒绝恢复，
  不做生产迁移；本轮 journal 尚未发布，因此不需要兼容旧格式。
- `AdjustSourceCountAsync` 会跨 `source`、`file`、祖先文件、`project`、`target`、
  `file_target_cache` 多个文档 `$inc`。source 已插入而计数中途失败时会产生部分
  计数；仅靠 `(source,target,user)` 的 translation 去重也无法修复未完成的计数。
  因此不实现“旁路写入版本”来伪造 exactly-once。

### 需要 E1 提供的最小公开契约

建议放在 `LabelService`（或 E1 owner 指定的 lower service）上：

```csharp
public enum SourceWriteOutcome { Created, AlreadyApplied }

public sealed record SourceWriteResult(
    SourceWriteOutcome Outcome,
    ObjectId SourceId,
    long Revision,
    bool CountersApplied);

public Task<SourceWriteResult> CreateSourceOnceAsync(
    HttpContext context,
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

语义要求：

1. source 文档以调用方传入的 `sourceId` 作为 `_id` 插入（`_id` 才是 identity）。
2. 若同 `_id` 已存在，必须校验 `f == fileId`、`r == sourceRank`、
   `label_epoch == expectedRevision`；匹配则返回 `AlreadyApplied` 且绝不重复计数，
   不匹配则失败（不得覆盖/认领）。
3. `Created` 分支必须把 source 插入和所有计数更新做成可恢复的 exactly-once：
   `source.sc`、`file.sc`、祖先文件计数、`project` 计数、`target`/cache 计数，
   要么全部生效，要么可通过同一 `sourceId` 的 durable marker 检测并补齐；
   重试 `AlreadyApplied` 时不得再次 `$inc`。
4. expectedRevision 不符时返回 409 Busy，不写任何文档。
5. translation 也应同样接受调用方稳定 id，或提供
   `CreateTranslationOnceAsync(..., ObjectId translationId, ObjectId sourceId, ...)`：
   以 `(o,t,u)` 唯一键幂等，只有真正新建 translation 时才增加 `tsc`；空 content
   返回 null 且不增加计数。

在 helper 落地前，G2 会预分配 intent 并把缺少 writer 的情况标记为 blocked；这
不是可验收的导入实现，6 个真实写入/恢复测试保持 skip，直到 helper 接线完成。
未知 enqueue 结果也已区分：捕获异常后先查找同一 kind + project_id +
label_revision 的 pending/running job，找到则保持 `is=Pending`（job 可能已提交），
只有确认不存在时才标记 `is=Error, ie=ErrorUnknown`。

### 新基线 blocker

e910e33 + 当前 E1/D1 文件的最小快照 restore 成功，但 build 被并行 `FileService`
自身的 long/int 未适配阻塞（不得在 snapshot-only 修改他人源码）：

```
src\Shoreline.Api\Features\FileService.Mutations.cs(113,73): error CS1503 参数 3: 无法从“long”转换为“int”
src\Shoreline.Api\Features\FileService.Serialization.cs(87,82): error CS1503 参数 3: 无法从“long”转换为“int”
src\Shoreline.Api\Features\FileService.Serialization.cs(89,81): error CS1503 参数 3: 无法从“long”转换为“int”
src\Shoreline.Api\Features\FileService.Serialization.cs(90,87): error CS1503 参数 3: 无法从“long”转换为“int”
src\Shoreline.Api\Features\FileService.Serialization.cs(95,86): error CS1503 参数 3: 无法从“long”转换为“int”
src\Shoreline.Api\Features\FileService.Serialization.cs(99,77): error CS1503 参数 3: 无法从“long”转换为“int”
```

需要主代理协调 E1/D1 的稳定版本或明确授权修正这些 FileService 调用。