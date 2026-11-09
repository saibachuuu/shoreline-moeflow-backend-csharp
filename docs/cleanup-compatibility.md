# 项目内容清理兼容说明（B 阶段）

本文件记录 C# 后端 `ProjectLifecycleService.ClearAsync` / `ProjectService.CreateTargetAsync` /
`ProjectService.DeleteTargetAsync` 与旧 Python BSON 数据的兼容规则。实现集中在：

- `src/Shoreline.Api/Features/ProjectLifecycleService.cs`
- `src/Shoreline.Api/Features/ProjectService.cs`
- `src/Shoreline.Api/Features/ProjectContentCleanup.cs`

`ProjectContentCleanup` 是静态辅助类，直接接收已注册的 `MongoStore`、`ObjectStorage` 和
`IConfiguration`，不需要新的 DI 注册，也没有改动 `Program.cs`。生产环境沿现有 constructor
注入即可；测试中可直接用临时本地存储目录构造 `ObjectStorage`。

## 数据字段（旧 BSON 是权威）

清理逻辑只使用 Python/MongoEngine 的真实 `db_field`：

| 文档 | 字段 | BSON key |
| --- | --- | --- |
| file | project | `p` |
| file | type | `t`（`FOLDER == 1`） |
| file | save_name | `sa` |
| output | project | `p` |
| output | target | `t` |
| output | file_name | `file_name`（历史 Python 的 `df_field="fn"` 拼写无效） |
| output | storage_bytes | `sb` |
| file_target_cache | file | `f` |
| file_target_cache | target | `t` |
| source | file | `f` |
| translation / tip | source | `o` |
| translation / tip | target | `t` |

不要用 `project` 字段过滤旧数据。`ProjectTests` 当前文件没有 file/output fixture 键
需要改写；`ProjectMemberTests.CompleteReopenAndClearLifecycle` 的合成 fixture 已按主会话
单独授权改成真实 `p` / `t` / `sa` / `file_name` / `sb` 字段，查询也改用 `p`，断言没有
放宽。新测试 `tests/Shoreline.Tests/ProjectCleanupTests.cs` 全部使用上述真实字段。

## 对象 key 与默认前缀

默认值核对自 Python `app/config.py` 和 `app/models/file.py` / `output.py`：

- 文件及派生图前缀：`files/`（硬编码，配置键 `Storage:FilePrefix`）
- 封面派生名：`files/cover-<save_stem>.webp`（`Storage:CoverPrefix`，默认 `cover`）
- 重采样派生名：`files/resample-<save_stem>.webp`（`Storage:ResamplePrefix`，默认 `resample`）
- 输出对象：`outputs/<output_id>/<file_name>`（`Storage:OutputPrefix`，默认 `outputs/`）

`<save_stem>` 是 `save_name` 最后一个点之前的部分。全局 `Storage:KeyPrefix`（Python 的
`OSS_KEY_PREFIX`，默认 `storage/`）由 `ObjectStorage`/`S3StorageBackend` 统一添加，清理层
不再重复拼。`ObjectStorage` 的 key 是相对于该全局前缀的 key。

## 清理语义

### 整个项目 clear

对应 Python `ProjectLifecycleService.clear_contents`：

1. 对项目的每个 `file`（所有 revision，包括 folder/未激活）：
   - folder（`t == 1`）没有对象，只删文档；
   - 非 folder 且有 `sa` 时删除原文件、cover、resample；`proofread-*.webp` 故意保留，
     因为通知邮件中的链接可能长期有效；
   - 删除该文件的 `source`（`f`），以及这些 source 对应的 `translation`/`tip`（`o`）、
     `file_target_cache`（`f`），最后删除 file 文档。
2. 对项目的每个 `output`：先严格删除对象 `outputs/<id>/<file_name>`，全部成功后删除
   output 文档。
3. 项目 9 个账目字段归零：`fs, cover_bytes, resample_bytes, ob, fo, fc, sc, tsc, csc`。

`project` 文档本身、`target` 文档与 `tc`、`project_member`、application、snapshot/metadata
均保留；这与 Python `clear_contents` 一致。整个操作使用按 `_id` 排序的有界 cursor
（每批 100 条）和按范围的小批量删除，避免把整个项目一次性 `ToList`。

### target 创建

对应 Python `Target.create`：

- 先校验同一项目/语言没有重复 target，且目标语言不能等于项目源语言；
- 插入 target 后，为项目的 **所有** file（包括 folder 和 inactive/deactivated revision）
  创建 `file_target_cache`，字段为 `f/t/ts=0/cs=0/e`；
- `tc` 在调用层递增一次。
- 如果后续创建失败，已插入的 target 与 cache 会回滚（项目创建路径也会清理已创建的
  cache 行）。

### target 删除

对应 Python `Target.clear`：

1. 删除该 target 的 output 对象；这一步是 best-effort（缺失对象也幂等成功），只有对象
   删除成功且 `sb > 0` 时才释放项目 `ob`，并把该 output 的 `sb` 归零；
2. 删除该 target 的 `output`、`translation`、`tip`、`file_target_cache`；
3. 删除 target 文档；
4. 仅当 target 删除真正匹配到一行时，`tc` 减一（重复请求不会重复扣减）。

只按该 target id 删除，不触碰同项目其它 target，也不触碰其它项目。source 文档属于
file，不属于 target，因此 target 删除不会删除 source。

## 失败与并发

- **atomic claim**：`clear` 在调用 `clear_contents` 之前，用
  `_id + st in (0,5) + stv=expected + cip != true` 原子 claim `cip=true`，并写入
  本次调用私有的 `clear_owner`（新 ObjectId）。expected_version 过期时在 claim 阶段
  就失败，不删除任何 DB 行或对象。
- 旧项目如果缺少 `stv`，过滤条件按 Python 默认值 0 处理，同时接受“字段不存在”和
  “字段等于 0”。
- 清理过程中任何对象/DB 错误都会释放本次 claim（filter 同时匹配 `cip=true` 与
  `clear_owner`），写 `project_clear_retryable_failure` 审计并返回可重试的 503
  （`ClearOperationRetryableError` / `CLEAR_OPERATION_RETRYABLE`）；项目不会标记为
  `CLEARED`，已经删除的对象可幂等重试。
- **取消不是 503**：请求的 `CancellationToken` 在对象删除期间被取消时，使用独立的
  5 秒 `CancellationTokenSource` 释放本次 claim，然后原样抛出
  `OperationCanceledException`，不会伪装成可重试 503。
- **owner token**：claim、最终 CAS、释放 claim 的 filter 都包含本次调用的
  `clear_owner`。旧调用不能释放新调用持有的 claim，也不能提交最终 CLEARED 状态。
  成功时 `clear_owner` 被 `$unset`，不会出现在返回的项目文档中。
- 释放 claim 或审计使用独立的有界 CTS，不依赖已取消的请求 token；审计失败不会阻止
  claim 释放。如果释放本身无法证明成功，日志只包含 projectId 和异常类型（不含 owner
  token/请求内容），项目保留 `cip=true` 作为重试标记，并写
  `project_clear_recovery_required` 审计；此时不能声称已经解锁。
- 最终状态 CAS 使用 `_id + st in (0,5) + stv=expected + cip=true + clear_owner`；
  失败时只释放本 owner 的 claim 并报 `ProjectStateConflict`，项目不会停留在永久锁定的
  `cip=true`，也不会被旧 owner 覆盖。
- `complete` / `reopen` 使用旧字段 `ctm`（不是 `completed_time`）；reopen 会 unset
  `ctm`。`expected_status` 必须同时满足 allowed 集合且等于当前状态。

## 尚未接线 / 已知缺口

1. **storage pool router**：项目 `csid`（`storage_source_id`）非空时，当前没有
   `storage_router.backend_for(project)` 的实现。清理层会显式 fail closed，抛可重试
   错误并保留所有未处理的 DB 行和对象，绝不误删默认 bucket。等主会话接入 router 后，
   按 project 选择 backend 再允许清理。
2. **跨项目 label guard**：`ProjectLabelLock` 尚未稳定，本轮没有依赖或编译它。当前只
   保证单 writer/单 claim 语义；跨项目共享 label 的并发保护需要等主会话把清理接入
   共享 `ProjectLabelLock(context, EnterAsync)`，并同时使用 expectedRevision（不能只靠
   `cip`）后才完整，不能声称现在已覆盖所有并发场景。
3. **HTTP 接线**：新增的 cleanup Facts 是 direct-service 测试；本轮只修正了既有
   `ProjectMemberTests` 的 fixture 字段，没有新增/修改 HTTP 路由；`Program.cs` 未改。
   `ProjectContentCleanup` 没有独立 DI 注册，但由现有 scoped 服务的 constructor 注入
   `ObjectStorage`/`IConfiguration` 即可工作。
4. `proofread-*` 对象有意保留；不要把它当成泄漏补删。

## 测试

`tests/Shoreline.Tests/ProjectCleanupTests.cs` 使用 `LocalServices` 的随机测试库和每个
Fact 自己拥有的临时本地对象目录，`DisposeAsync` 会删除本 Fact 创建的 DB 行和临时目录。
当前共 13 个 direct-service Facts，覆盖：

- 真实 legacy BSON 的 file/output/source/translation/tip/cache 清理，且其它项目保留；
- 真实对象删除（original/cover/resample/output）与 proofread 保留；
- `csid` 非空 fail closed、可重试且不破坏数据；
- storage 失败注入后项目/行保留、状态不标 CLEARED，并可重试成功；
- stale expected_version 无任何破坏；
- 并发 clear 只有一个成功 claim，审计只记一次；
- 对象删除期间真实取消：owned claim 被释放、状态不标 CLEARED、原取消异常传播、同版本可重试；
- 旧 owner 不能释放或提交新 owner 的 claim，项目保留新 owner 的 `cip=true` 与
  `project_clear_recovery_required` 记录；
- `ctm` 的 complete/reopen 与缺失 `stv` 兼容；
- target 创建为所有 revision 建 cache；
- target 删除的对象/行/tc 级联，且不碰其它 target/项目；
- 缺失 output 对象的幂等删除。

## Isolated snapshot 验证记录

按主会话要求，验证在仓库外的临时快照中进行，不复制完整 WIP：

- baseline：`33c93ea`（stable，已包含 D0 的 S3 + Local storage 实现）；
- 快照只加入本任务的 3 个生产文件（`ProjectLifecycleService.cs`、`ProjectService.cs`、
  新增 `ProjectContentCleanup.cs`）、新增 `ProjectCleanupTests.cs`（13 Facts），以及按
  单独授权修正 fixture 的 `ProjectMemberTests.cs`；没有带入其它并行 source 或 csproj；
- 快照路径：
  `C:\Users\88888888\AppData\Local\Temp\shoreline-cleanup-snapshot-d9912103bcc946ea8fb6a6584b6431db`；
- 结果：`dotnet build Shoreline.sln` **0 warning / 0 error**；
  `--filter FullyQualifiedName~ProjectCleanupTests` **13/13 passed**；
  full suite **239 total / 239 passed / 0 failed**。
- `ProjectMemberTests.CompleteReopenAndClearLifecycle` 的 fixture 已改为 canonical
  `p`/`t`/`sa`/`file_name`/`sb`，查询用 `p`，仍断言 file/output 真为空且 `st`/`fo`/`ob`
  符合预期，没有放宽断言。
- 未接线：HTTP 路由本身未新增（现有路由已 wired，无需改 `Program.cs`）；storage pool
  router、跨 project 的 `ProjectLabelLock(context, EnterAsync)`/expectedRevision 仍是
  后续门槛。
