# 标签快照 / 恢复 / 清空 / 校对合并兼容说明

本文件记录 E2 标签快照功能的 C# 实现与 Python 后端
`app/services/label_snapshot.py`、`app/services/project_statistics.py`、
`app/apis/label_snapshot.py` 的兼容边界。共享 `Program.cs` 已加入最小 E2
接线（未 commit），但共享工作树整体仍受其他并行 WIP 影响；本文同时记录
接线内容、隔离验证结果与已知限制。

## 新增文件

- `src/Shoreline.Api/Features/LabelSnapshotService.cs`：快照数据模型、原始 BSON
  编解码、分块、摘要、创建、读取、列表、指纹校验。
- `src/Shoreline.Api/Features/LabelSnapshotService.Operations.cs`：权限能力、
  label-state、恢复、清空、recover、保护日志、epoch 推进、统计重建入口。
- `src/Shoreline.Api/Features/LabelSnapshotService.Statistics.cs`：完整移植
  `project_statistics.rebuild_project_statistics`，包括目录祖先汇总与
  banker's rounding。
- `src/Shoreline.Api/Features/LabelSnapshotService.Merge.cs`：校对预览与合并。
- `src/Shoreline.Api/Features/LabelSnapshotEndpoints.cs`：HTTP 路由。
- `src/Shoreline.Api/Features/LabelSnapshotRegistration.cs`：DI 与路由注册扩展。
- `tests/Shoreline.Tests/LabelSnapshotTests.cs`：24 个直接服务/中间件测试。
- `tests/Shoreline.Tests/LabelSnapshotGoldenTests.cs`：4 个真实 Python AST golden
  raw BSON/typed BSON/digest 测试。
- `tests/Shoreline.Tests/LabelSnapshotHttpTests.cs`：4 个真实 HTTP 管线测试。
- 本文件。

除共享 `Program.cs` 的 3 行 E2 接线外，没有修改其他现有文件；没有执行
`git add` / `git commit`。

## HTTP 接口

所有路由都位于 `/v1/projects/{projectId}` 下，与 Python `apis/urls.py` 一致：

| 方法 | 路径 | 能力 | 成功响应 |
| --- | --- | --- | --- |
| GET | `/label-state` | `ACCESS` | `{capabilities, has_source_content, counts, recovery_required}` |
| GET | `/snapshots` | `create` | 快照数组，分页默认 20、最大 100 |
| POST | `/snapshots` | `create` | `{snapshot_id}` |
| POST | `/snapshots/{snapshotId}/restore` | `restore` | `{snapshot_id}`（保护快照 id） |
| POST | `/label-clear` | `clear_proofread`（字段 `proofread`）或 `restore` | `{snapshot_id}` |
| GET | `/label-merge?target_id=...` | `merge` | 合并预览 |
| POST | `/label-merge` | `merge` | `{snapshot_id}` |
| POST | `/label-recover` | `restore` | `{recovered: true}` |

能力计算严格按 Python：

- `access`：`project:ACCESS`。
- `manager`：`access && project:MANAGE_MEMBERS`。站点管理员不会自动绕过项目权限。
- `proofread`：`access && project:PROOFREAD_TRA`。
- `create`：`access && (manager || proofread || project:OUTPUT_TRA)`。
- `restore`：`manager`。
- `clear_proofread`：`manager || proofread`。
- `merge`：`manager || (proofread && CHECK_TRA && ADD_TRA)`。注意 Python 的
  proofreader 默认标签同时包含 `ADD_TRA` 与 `CHECK_TRA`，因此默认 proofreader
  可以合并。

`GET /label-merge` 按 Python Flask `request.args.get("target_id")` 读取 snake_case
查询参数，不能使用 `targetId`；并且先检查 `merge` 能力，再解析目标语言，
与 Python 的权限/错误优先级一致。`targetId` 会被当作缺失目标并以
`TargetNotExistError` / 4024 拒绝。

POST body 不是 JSON 对象时统一抛出 `LabelOperationError("请求数据无效")`
（HTTP 409）。其他校验：

- 快照名称超过 120 个 Unicode rune：`快照名称最多 120 个字符`。
- 清空选项存在但不是布尔值：`清空选项必须为布尔值`。
- 合并 `versions` / `selections` 存在但不是对象：`冲突选择数据无效`。显式
  JSON `null` 与任何非对象值一样被拒绝；只有键缺失才使用缺省空映射。
- 非法项目 id：`ProjectNotExistError` / 4001 / `项目不存在`。
- 非法目标 id：`TargetNotExistError` / 4024 / `项目目标语言不存在`。

`GET /snapshots` 使用 `Pagination.From`（默认 `page=1, limit=20`，上限 100），
响应头由 `Pagination.Apply` 写入 `X-Pagination-*`。列表只返回
`ready=true`，按 `created` 降序、`_id` 降序。

## 持久化格式

集合与索引：

- `project_label_snapshot`：索引 `{project:1, created:-1, _id:-1}`。
- `project_label_snapshot_chunk`：唯一索引 `{snapshot:1, index:1}`。
- FileTargetCache 使用 canonical 的 `file_target_cache`（MongoEngine 默认
  CamelCase→snake_case，Python `FileTargetCache` 没有 collection 覆盖）。本服务
  不会创建或读取 `filetargetcache` 影子集合。

`LabelSnapshotService.InitializeAsync` 会创建上述索引。共享的
`MongoStore.InitializeAsync` 不包含快照索引，因此共享 `Program.cs` 在启动阶段
调用 `InitializeLabelSnapshotIndexesAsync()`；该扩展从 DI 解析本服务并调用
`InitializeAsync`。

快照 metadata 字段名与 Python 版本 1 完全一致：

```text
_id, project, name, actor, actor_name, created, version, reason,
ready, digest, counts, revision
```

其中 `version=1`，`counts` 为 `{files, targets, sources, translations, tips}`
的行数，`revision` 是创建时的 `project.label_revision`。

原始 BSON 快照内容：

- `files`：仅保留 `_id, sa, md, t, v`，缺失字段以 `null` 补齐。
- `targets`：仅保留 `_id, l`。
- `sources` / `translations` / `tips`：读取完整原始 BSON 文档，仅删除
  `label_epoch`。不会转成 JSON，不会丢字段顺序、数值宽度或 ObjectId。
- 每种 kind 按 `_id` 升序。
- 分块目标大小为 4 MiB（`ChunkBytes`）；每个 chunk 的 `data` 是
  `BSONBinary`，内容为 `BSON({"rows":[...]})`，`hash` 为原始二进制 SHA-256
  十六进制。聚合判断沿用 Python：只累加每行 `ToBson()` 的字节数，不包含
  `{"rows":[...]}` 文档包装、数组元素名等 BSON 开销，因此实际 chunk 二进制
  可能略大于 4 MiB。它是目标分块大小，不是严格的 Mongo 硬限制；单行自身超过
  4 MiB 时直接拒绝。若将来改成严格字节上限，必须保持 legacy golden 的
  chunk/`rows` 布局、空 kind 与全局 digest 不变。
- 全局 `digest` 的计算顺序固定为
  `files, targets, sources, translations, tips`：先写入 kind 的 UTF-8 字节，
  再依次写入每行 `ToBson()`；空 kind 也会写入 kind 名。

真实 Python AST golden 位于已提交的
`tools/legacy-reference/fixtures/goldens.json`（测试项目按现有链接复制到输出，
未修改 csproj）。`fixtures.label_snapshot` 提供原始 row BSON、7 个 chunk、
field order、int32/int64/decimal128/null/ObjectId/date 类型以及全局 digest；
`fixtures.bson_typed.cases` 提供额外 typed BSON 字节序样本。C# 测试不使用
自写期望值，而是直接比较 fixture 的原始字节、SHA-256、字段顺序、BSON 类型和
digest。该 fixture 用 `chunk_bytes_override=200` 强制产生多个 chunk，这是
oracle 输入，不是生产 `ChunkBytes=4 MiB`；运行时仍使用 4 MiB 目标值。

创建顺序：先插入 `ready=false` 的 metadata，再逐 chunk 写入数据与 hash，
最后更新 `ready=true` 与 `chunks`。任何异常只删除本次创建的 metadata 与
chunks，不碰其他快照。

读取校验顺序：归属项目与 `ready`、`version==1`、chunk 数量、索引连续、
chunk kind 顺序、chunk hash、全局 digest、counts。所有校验都在删除/替换现有
标签前完成，因此损坏快照不会先破坏线上数据。

## 恢复、清空、保护日志

`restore` / `clear` / `merge` 都通过 `ProtectedOperationAsync`：

1. 先创建 reason 为 `restore` / `clear` / `merge` 的完整保护快照。
2. 将 `project.label_recovery` 持久化为保护快照 id。
3. 执行破坏性操作。
4. 重建统计并推进 `label_revision`；最后才 unset `label_recovery`、更新
   `project.label_revision` 与原始字段 `m_e`（Project 继承
   `GroupMixin.edit_time db_field="m_e"`；`edit_time` 只是项目 API 视图字段），
   从而发布新版本。
5. 任一步异常则加载保护快照、替换标签、重建统计、推进 epoch，然后重新抛出。
   如果回滚本身也失败，`label_recovery` 必须保留，禁止普通写请求继续。
6. 只有显式 `/label-recover`（`recovery=true` 借锁）可以消费该日志。

恢复前还要求快照中的 files/targets 仍存在且指纹完全一致：

- file 指纹：`_id, sa, md, t, v`。
- target 指纹：`_id, l`。
- 当前项目允许存在快照之外的新 files/targets；恢复只清空这些新文件的标签，
  不会删除文件本身。

推进 epoch 时会给以下所有行写 `label_epoch = newRevision`：项目 File、Target、
相关 FileTargetCache、File 下的 Source、Source 下的 Translation 与 Tip。
所有自有数据写完后才更新 project 版本并移除 journal。

清空语义：

- `source`：删除该源文所有翻译；图片源保留原文与 `b=false`；文本文件源将
  `c=""`、`pt=[]`、`b=true`。
- `translation`：删除该源文所有翻译，保留 tips 与 source。
- `proofread`：`p=""` 且 unset `pr`，保留 `s`（包括选中的空翻译）。
- `delete_labels=true` 会删除 sources；`proofread` 字段禁止 `delete_labels`。
- `translation + delete_labels` 要求所有 source 内容为空。
- `clear_tips=true` 或 `delete_labels=true` 会删除 tips。

## 查询与批处理边界

所有以 `$in` 引用 source/translation/tip/user id 的数据库查询与更新都按
`MutationBatch = 1000` 拆分，包括快照读取、统计重建、`ReplaceLabels`、
`AdvanceRevision`、proofread 清空、文本文件 source 清空以及 merge 的最终清理。
跨批次读取完成后统一按 `_id` 做一次全局排序，而不是“每批排序后直接拼接”，
因此 raw BSON digest 与 Python 的全量排序结果一致；1001 条交错 source 的回归
测试会验证第二批没有被遗漏。批量插入同样按 1000 分片。

这解决的是单条 Mongo 查询/更新的资源上界，不是整个进程的内存上界：
`LoadLabelDataAsync` 仍会把项目的 files/targets/sources/translations/tips 全部
保存在 `LabelData` 中，metadata、counts 与 chunk 读取也仍使用 `ToList`，所以
内存会随项目标签数据总量增长。完整 streaming 快照需要在后续修改
`LabelData` API，本次没有声称 bounded memory。

## 统计重建

`LabelSnapshotService.Statistics.cs` 按 Python 读取 primary 数据并写回：

- 目录 `fo` 汇总；非目录 `fc/fs/sc/tsc/csc` 汇总到祖先。
- 非空白 source 计入 `sc`；已翻译/已选中按目标语言计入 cache 与 `tsc/csc`。
- `stored` 为 `sa` 为真的非目录文件；`cover_bytes` / `resample_bytes` 求和。
- `ob` 为项目下 `file_name > ""` 的 Output `sb` 之和。
- `original_bytes = sum(fs * 1024)`；总字节与 `fs` 使用整数 banker's rounding
  （不是 C# 默认的 `AwayFromZero`，也不是 double 大数运算）。
- 写回 File counts、FileTargetCache（`$setOnInsert label_epoch`）、Target
  totals 与 Project totals。

## 校对合并

预览只考虑当前目标语言中 `p` 非空且 `p != c` 的 translation 行，按
`(source, proofreader)` 分组：

- 缺 proofreader 或用户：`校对者账号缺失，不能合并`。
- 同一组内按 `p` 去重，首个内容保留，形成 `versions`。
- 同一 source 多个校对者进入 `selection_conflicts`。
- `token` 是完整标签数据（任何 source/tip 变化都会失效）的 digest。

提交时：

- token 不匹配：`预览后数据已改变，请重新确认冲突`，在创建保护快照前拒绝。
- 多版本但未指定 version：`请解决所有校对内容冲突`。
- 多校对者但未指定 winner：`请选择每个标签最终选定的翻译`。
- 对每个 winner 以 `{o, t, u}` upsert 校对译文，设置 `c`、`mt=false`、
  `p=""`，并设置唯一 `s=true`；其他 translation 的 `s=false`，所有当前
  目标语言的 `p` 清空、`pr` unset；空且未选中的 translation 删除。
- tips 不参与合并，保留。

## 共享接线状态

共享 `Program.cs` 已完成 E2 最小接线（仅 3 行，未 commit）：

1. `builder.Services.AddLabelSnapshots();`，位于 `AddLabelServices()` 之后。
   该扩展以 `TryAddScoped` 复用 E1 已注册的请求级 `LabelRequestContext` 与
   `ProjectLabelLock`，不覆盖现有生命周期；中间件与 service 共用同一个请求级
   context，避免 HTTP 路径二次获取 Mongo 锁造成死锁。
2. `app.MapLabelSnapshots();`，位于 `MapLabels()` 之后。
3. `await app.Services.InitializeLabelSnapshotIndexesAsync();`，位于
   `InitializeLabelIndexesAsync()` 之后；该扩展从 DI 解析
   `LabelSnapshotService` 并调用 `InitializeAsync` 创建快照索引。

E1 的 `AddLabelServices`、`UseRouting`、`UseLabelRequestGuard`、`MapLabels`、
`InitializeLabelIndexesAsync` 全部原样保留，没有重复 E1 注册。共享接线只表示
标签快照路由与索引已接入，不代表 File/Lifecycle 等全链路已经完成，也不代表
共享工作树整体 ready。

## 验证状态与限制

E2 最终隔离验证以 stable E1 commit
`3edd6839b5209dd425052f1b03c311590e9ea45a`（`3edd683`，parent `d76f587`，
baseline full 386）的 `git archive` 为基线，只覆盖本功能 10 个文件；不再复制
E1 七个生产文件、E1 tests 或其他 WIP。私有 `Program.cs` 只在 E1 接线之外增加
上述 3 行 E2 接线。共享 `Program.cs` 的 git blob 与隔离副本完全一致，
本功能生产/测试/文档文件也与隔离副本一致（11 个路径逐一比对 0 mismatch）。

隔离验证结果：

- `dotnet restore --locked-mode`：成功。
- API 与 tests build：0 warning / 0 error。
- focused：`dotnet test --filter FullyQualifiedName~LabelSnapshot` =>
  **32 passed / 0 failed**（24 service/middleware + 4 golden + 4 HTTP）。
- full：**418 passed / 0 failed**（baseline 386 + 本功能 32）。
- 共享工作树 `dotnet restore --locked-mode` 成功；随后正常
  `dotnet build tests/Shoreline.Tests/Shoreline.Tests.csproj --no-restore`
  被其他 agent 的未提交 WIP 阻断：`Thumbnail*` 引用不存在的
  `Shoreline.Imaging` 项目，`ProofreadFeedback*` 缺少 `MongoStore` /
  `IdentityPermissions` 等 using，共 9 个编译错误。这些文件不属于本功能，本次
  没有修改、移动或删除它们，也没有用排除参数伪造 shared build 结果。因此
  418 是“3edd683 + 本功能 10 文件 + 相同 Program 接线”的隔离实测结果，共享
  工作树整体构建/测试需等其他 WIP 编译通过后重跑。

测试覆盖：真实 Python AST golden 原始 row/chunk/typed BSON 字节、SHA-256、
字段顺序、数值类型与全局 digest；`LoadLabelDataAsync` 对 golden JSON 的读取并
只剥离 `label_epoch`；创建/恢复 roundtrip、epoch 与 counts；损坏 chunk 拒绝且
线上数据不变；外部项目、错误 version、错误 owner；文件指纹变化拒绝；额外文件
允许但标签清空；目标语言被替换拒绝；三种 clear 字段语义；delete_labels 保护；
统计失败自动回滚；双重失败保留 journal 后 recover；无 journal recover；merge
单 winner、多版本冲突、多校对者 selection、stale token；stale revision 在写入
前拒绝；列表/状态/分页；manager/proofreader/site-admin 权限；canonical
`file_target_cache` 行复用与 epoch；固定 TimeProvider 下 Project 原始 `m_e`
更新且无 `edit_time`；以及 1001 条交错 source/translation 的跨批次读取、恢复、
清空与 stamp。

HTTP 测试通过真实 `UseLabelRequestGuard` + `MapLabelSnapshots` 管线验证：
legacy `target_id` 查询、权限先于目标解析、显式 null `versions`/`selections`
的 409 JSON、merge 预览与提交、stale token、restore 的
`X-Label-Revision`/stale 409、journal 阻塞普通路由、`/label-recover` 放行、
列表分页头、label-state 字段和请求结束后无残留锁行。错误路径均检查
`error`/`code`/`message` JSON，没有接受 500 或空响应。

锁与失败边界：`ProjectLabelLock` 获取锁最多轮询 2 秒（`WaitMilliseconds=2000`，
每 25 ms 一次），超时返回 `LabelBusyError`/409；lease 没有 TTL，释放使用独立的
5 秒预算。进程被直接杀死仍可能留下锁行，本功能不提供自动剥夺 owner。HTTP
路径必须让中间件与 service 共用同一个请求级 `LabelRequestContext`，否则会
二次获取同一项目锁并可能死锁；本次共享 Program 与隔离验证均使用同一
registration 保证这一点。

尚未解决的限制：快照读取仍将项目全量标签保存在内存（`LabelData` 全量物化，
metadata/chunks 也使用 `ToList`）；4 MiB 是近似目标 chunk 大小，不包含
`{"rows":[...]}` BSON 包装开销，不是严格 Mongo 硬上限；共享工作树整体验证
仍被其他 WIP 阻断。