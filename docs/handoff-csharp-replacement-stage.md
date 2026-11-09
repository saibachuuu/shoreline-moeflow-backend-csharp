# C# 替代迁移停工交接

更新日期：2026-11-09。当前为收尾草稿，现有后台任务交付后继续补充。

## 断电前 WIP 保存（用户最新授权）

用户要求为转云端继续而提交现场：现在允许把当前未完成迁移代码保存为明确的 WIP 检查点。它不代表任何未验收模块已通过，不能部署；此前“仅模块验收后提交”的约束在本次紧急保存上由用户新要求覆盖。本文件同步保存到仓库 `docs/handoff-csharp-replacement-stage.md`，云端优先阅读该文件。

本次保存时仍可能有已在运行的 agent 写文件；提交后新增或修改的文件需要另作补充检查点。不得新派或续派。最后可发布功能基线仍为 `3edd683`。此 C# 仓库检查时 `git remote -v` 无输出，不能假设本地 commit 已自动传到云端；需携带 Git bundle 或之后配置并推送到用户指定的远程。

## 用户最新要求

- 不再新派或续派智能体；等待本批任务结束，然后整理待办，准备宿舍断电停工。
- 原目标仍是 C# 后端能够实际替代 Python；本轮暂停不代表已达到替代门槛。
- 保留经验证的本地 Git 检查点；不把尚未验证的并行改动混入功能提交。
- 不 push、不部署、不连接测试或生产服务器，不读取真实凭据或部署配置。

## 工作目录和当前基线

- C#：`C:\Users\88888888\shoreline-moeflow\shoreline-moeflow-backend-csharp`
- Python 只读参考：`C:\Users\88888888\shoreline-moeflow\shoreline-moeflow-backend`
- 工作区根目录不是 Git 仓库，Git 命令应在 C# 仓库运行。
- 核对时 HEAD：`3edd6839b5209dd425052f1b03c311590e9ea45a`。
- 该检查点：标签编辑、项目 revision guard 已接线；隔离快照完整测试 **386/386**，build **0 warning / 0 error**。
- 工作树还有大量并行改动，**386 不代表当前整个工作树已通过测试**。
- E2 已完成 `Program.cs` 的三行接线并交付，未提交；收尾期间不再安排其他任务接线。

## 已有检查点

| Commit | 内容 |
|---|---|
| `3edd683` | 标签编辑、项目锁、revision guard |
| `d76f587` | 邮件依赖锁；MailKit/MimeKit 4.16.0、Pkcs 10.0.14 |
| `b528acb` | 通知发布、分发及受保护邮件传输 |
| `c70c67b` | 用户项目和验证码列表 |
| `0eddb2e` | 头像上传、默认存储概览、站点 homepage 兼容路由 |
| `6967a0c` | 真正运行 Python Flask 生成的参考 fixture |
| `e910e33` | 通知收件箱、偏好、策略及内容解析 |
| `0dfa13b` | 中文 README |
| `421208d` | 项目清理、target 级联、取消后的 owner claim 释放 |
| `98affea` | 术语库、术语路由 |
| `738b617` | Python AST 原函数 golden fixtures |
| `33c93ea` | 带前缀的 S3/Local 存储后端 |
| `306b10a` | 申请审批、在线状态 |
| `710bbcb` | 邀请阶段检查点 |

## 本批任务清单

以下是暂停指令到达时的状态，不能把“正在验收”当作“已提交”。没有授权重新派发这些任务。

| 任务 | 会话 | 当前状态 / 下一项 |
|---|---|---|
| D1 文件操作 | `ses_e3f9daa22ffejuuEAgsJqnf9yd` | 已交付，未接共享 Program、未提交。隔离 focused 40/40、报告 full 353/353、build 0 warning / 0 error；基线与测试总数存在待核对差异，依赖含显式复制的 E1 和未提交 Pagination，详见下文。 |
| D2 存储池 | `ses_e3fa5832bffeP1BWgNs5RumYiN` | 已交付，未接线、未提交。34 focused 通过；旧 33-fact 版本 full 419/419；最新 420 总数重跑 Mongo 连接故障，372 failed / 48 passed，不能宣称最新全量通过。 |
| E1 幂等下层写入 | `ses_e3f9c0ce3ffe2NfNbXRQtZyCtL` | 新增稳定 source/translation ID 接口和基于 primary 数据的计数收敛；旧 E1 已提交，新 helper 未验收。 |
| E2 标签快照 | `ses_e3f88ea64ffeO63z0MFA0mSe3g` | 已交付、待主审查、未提交。`3edd683` 隔离快照 32 focused / 418 full，build 0 warning / 0 error；11 路径 git blob 与共享目录一致。共享树因其他 WIP 构建失败，详见下文。 |
| G1 导出 | `ses_e3f7fd0d1ffebgMenKj1QPo9jB` | 27 direct tests 已写；增加真实 HTTP/Python golden、lease 故障和计数恢复验证。 |
| G2 LabelPlus 导入 | `ses_e3fd33de7ffe3T2GDNfXFL6Zdc` | 最新交付为预分配稳定 ID + 可恢复 blocked，未接 E1 helper、未接 ProjectService、未提交。旧 `421208d` 开发快照 focused 17 通过 / 6 跳过，full 271 通过 / 6 跳过；不是导入最终验收。 |
| G3 校对反馈 | `ses_e3f7adac3ffeggwEZHBRrdXPtZ` | 通知开/关两种路径、标记图、草稿任务和假邮件测试；不得真实发送邮件。 |
| G4 缩略图 | `ses_e3f54753bffee5xXNcT2PdL2Yk` | 新 Imaging 项目及真实 native 图像测试；API ProjectReference、上传后调度仍待统一接线。 |
| J1 团队洞察 | `ses_e3f9825fdffeHpesq49i0SKd0X` | 已交付，未接共享 Program、未提交。基线 `0eddb2e` 304 + 20 = 324 full，focused 20/20，build 0 warning / 0 error；内置角色权限和 pool link 仍有兼容缺口。 |
| K1 团队解散 | `ses_e3fad8335ffejxRwq35xkXlmdY` | 已交付，未接共享 Program、未提交。基线 `c70c67b` 323 + 21 = 344 full，focused 21/21，build 0 warning / 0 error；现有创建、移动、加入及 import 写入路径尚未接生命周期锁。 |
| N3 通知管理/sync | `ses_e3f9646a8ffe3OhL2EscZ2cc10` | 已交付，未接共享 Program、未提交。`d76f587` 隔离快照 focused 24/24、full 378/378，正常 locked restore / audit、build 0 warning / 0 error，七文件 SHA 全匹配。Decimal128 超出 double 精度仍有限制。 |
| 真正运行时差分 | `ses_e3fa820b8ffe5vOCN3BmdPy0dU` | 已交付，待主审查、未提交。基线 `c70c67b`：7 focused / 330 full，build 0 warning / 0 error；8 个参考场景 business 0、framework 3、wire-format 16，后两者仍须评估兼容影响。 |
| Partner Search | `ses_e3f223596ffeV0nXVO2gI9EXiS` | 新模块、限流/配置/公开查询、假数据测试，未接线。 |
| Archive Import | `ses_e3f207859ffe9iRVQc0hDto3JX` | 新模块和归档安全验证；需要 D1 上传接口，不能自行重写核心计数。 |
| Ziteng Partner | `ses_e3f1cb934ffeoIKtnZYbDGlT8l` | 新模块、真实 AST 标题/匹配 golden、假 HTTP、跨任务限速和 generation fencing，未接线。 |
| 幂等任务排队 | `ses_e3f1bbcd5ffeVBzw1hnebh9N2f` | 已交付、未提交。`3edd683` 隔离组合 focused 14/14、full 400/400、正常审计/build 0 warning / 0 error；旧 producer 未迁移。测试时共享 Mongo 已 OOM 退出，详见下文。 |

## 必须保留的技术结论

### 已收到：K1 团队解散级联

- 隔离快照：`C:\Users\88888888\AppData\Local\Temp\shoreline-k1-snapshot-20261109-231500`，基线 `c70c67b` **323**，focused **21/21**（17 direct + 4 HTTP），full **344/344 / 0 skipped**，build **0 warning / 0 error**。Mongo 曾短暂不可达，agent 报告重试后以上均实际通过；未在共享树 build/test。
- 新增六文件：`Infrastructure/TeamLifecycleLock.cs`、`Features/TeamDissolutionService.cs`、`TeamDissolutionEndpoints.cs`、`TeamDissolutionRegistration.cs`、`tests/Shoreline.Tests/TeamDissolutionTests.cs`、`docs/team-dissolution-compatibility.md`，未 add/commit。锁 SHA-256 `183BB8BFC10189B3A221B59D71D8E5CB080884CC35BB370835B97EFF40949C70`，service SHA-256 `2483E6C20C026EF0A7F8EE56EBC543CE849C3E031B14300BAC4BB854158736B8`，文档 SHA-256 `0DF97524A6F2C3B33BFE422D52647DEC1DE354DDE68BF8D18A6185F3EE01E845`。
- `DELETE /v1/teams/{teamId}`：token 1003，严格 team DELETE，不额外加 ACCESS、不让 site admin 绕过 membership；working `st=0` 或缺失 st 时 103，消息 `此团队含有未完结的项目，不能解散（请先完结或者转移项目）`，成功 `{"message":"解散成功"}`。
- 项目逐个持 label lock 重读团队归属/状态，严格物理内容清理后删关联行；团队 terms 批次 200、source.pt 与 project.tb PULL，随后泛型 application/invitation 引用、sets、relations、members、仅本团队 role，最后 team。保留系统 role、audit、notification、proofread 长期对象、team/default avatar 和无关 scope 数据。非空 csid 继续 5113 fail closed。
- `team_lifecycle_lock` 无 TTL，唯一 _id、owner CAS、独立五秒释放；team marker `lc_phase=deleting/lc_owner/lc_started/lc_updated/lc_error` 支持失败重试。仍无全局事务，对象/行删除间崩溃依赖重试，非原子删除。
- 公开 helper：`AcquireAsync(ObjectId teamId, CancellationToken ct = default)`、`MarkDeletingAsync(TeamLifecycleLease lease, CancellationToken ct = default)`、`ReadOwnedTeamAsync(TeamLifecycleLease lease, CancellationToken ct = default)`、`EnsureWritableAsync(ObjectId teamId, CancellationToken ct = default)`、`IsDeletingAsync(ObjectId teamId, CancellationToken ct = default)`、`MarkFailureAsync(TeamLifecycleLease lease, string errorType)`。
- **尚未接写入路径，不能声称解散与并发创建/加入已无竞态。** 待整合：ProjectService.CreateProjectAsync / CreateProjectSetAsync / UpdateProjectAsync；ProjectImportService.RunImportAsync；TeamMemberService.AddAsync / JoinTrustedAsync / JoinApplicationRoleAsync；InvitationService.JoinTeamAsync；ProjectInvitationService.CreatePendingInvitationAsync / JoinProjectAsync。主审时应验证仅一次 EnsureWritable 检查是否充分，需要将相应写操作放进正确锁范围，不能检查后再无锁写。
- 共享 Program 尚未暴露 DELETE；后续接线 `AddTeamDissolutionServices()` / `MapTeamDissolution()` / `InitializeTeamDissolutionAsync()`。仅验证本地对象存储，未验证真实 S3/R2。term_banks 项目选择与 find_terms 后台刷新仍是待办。本轮不续派、不接线。

### 已收到：D2 存储路由与存储池

- 隔离快照：`C:\Users\88888888\AppData\Local\Temp\opencode\d2-storage-pool-isolated-20261109-231336`；`git archive 3edd683` + 仅 D2 文件，build **0 warning / 0 error**，focused **34/34**。
- 旧 33-fact 版本完整 **419/419**（386 + 33）。增加 stream fallback 第 34 项后，最新全量 **420 total / 372 failed / 48 passed**，失败报告为 `SocketException 10061 由于目标计算机积极拒绝`、`TimeoutException selecting server 127.0.0.1:27128`、`LocalServices.InitializeAsync`。Agent 判断为 Mongo 基础设施故障，不能据此判定所有业务断言通过，也不能写成 420 全绿；待环境恢复后重验最新版本。
- 新增通用层 `Storage/ProjectStorageRouter.cs`、`StorageBackendFactory.cs`；模块 `Modules/StoragePool/` 下 ApiJson、Constants、Endpoints、ProjectService、Registration、ScanService、SettingsService、SourceResolver、SourceService、TransferEngine、TransferService、TransferView、Worker；`tests/Shoreline.Tests/StoragePoolTests.cs` 和 `docs/storage-pool-compatibility.md`。未修改 Program、D0、File/Output/Project/Lifecycle/Label、csproj 或现有测试，未 add/commit。
- `IProjectStorageResolver.ResolveAsync(ObjectId sourceId, CancellationToken ct)` 返回 `StorageBackendLease?`；router 提供 `ForDefaultAsync` / `ForOriginalAsync` / `ForResampleAsync` / `ForOutputAsync` / `ForProjectAsync` / `ForSourceAsync`，返回可释放 lease。后续统一替换调用方默认 storage，不能只注册模块便声称池中项目已可用。
- 已修短写：source HEAD size 为基准，CountingReadStream 验证实际源读取，destination size 必须相同，含零字节；1000→500 不标 copied、不切换、不删源。**跨 provider 当前仅 size 完整性校验，ETag 只诊断，未比较 checksum，不能宣称能发现等长损坏。**
- 同项目 active transfer 仅相同 source/direction/target 可复用；新增唯一 partial index `storage_transfer_one_active_project`，`{p}`、`st 0..3`。manifest rows 先写、transfer 后可见。上线前必须检测历史同项目多 active 数据，否则建索引失败；并发失败的孤立 manifest 清理也应在主审查中核对。
- runlock insert 异常时独立五秒 owner-CAS cleanup，pool/runlock release 有界；**尚缺真实 insert 已提交再抛 unknown 的专门故障测试**，不要以代码路径存在代替测试证据。
- pointer、metrics、SWITCHED 在 project label lock + pool lock 内；已在 destination 时可继续恢复、不反向 copy。已有 `AlreadySwitchedPointerResumesWithoutReverseCopy`，**尚缺 metrics 写失败专门 fault test**；不能把已切换 happy recovery 当成所有未知提交窗口都覆盖。
- copy/verify/delete/metrics/recompute 已 cursor/aggregate；`BuildManifestAsync` 仍物化清单，非 constant memory。usage 按 Python `bytes=fs*1024`，derived/output 单列。
- 后续接线：`AddStoragePoolServices()`、`InitializeStoragePoolAsync()`、`MapStoragePool()`；`StoragePool:WorkerEnabled` 默认 true，首次接线要明确 worker 是否启动。停工期间不执行接线或重启。
- ALI 原生签名仍 fail-fast；无真实 R2 验证、multipart/ACL/tag/versioning 支持或跨 provider checksum。核心 File/Output/cleanup 等非空 csid 仍未接新 router，继续是替代门槛缺口。

### 已收到：JobQueue 稳定 jobId 幂等入队

- 隔离目录：`C:\Users\88888888\AppData\Local\Temp\opencode\shoreline-jobidemp-3edd683`；`git archive 3edd6839b5209dd425052f1b03c311590e9ea45a` 仅覆盖三个 owned 文件。正常 locked restore / NuGet audit 无告警；Debug build -warnaserror **0 warning / 0 error**，focused **14/14**，full **400/400**（386 + 14）。未提交。
- 精确新增 API：`public Task<ObjectId> EnqueueOnceAsync(ObjectId jobId, string kind, BsonDocument payload, CancellationToken ct)`。
- 构造器：`public sealed class JobQueue(MongoStore store, TimeProvider clock, JobQueueWriteObserver? writeObserver = null)`；保留原两参数构造调用及 DI。另有 `JobQueueWritePhase { BeforeWrite, AfterWrite }`、`JobQueueWriteObserver` 测试注入和 sanitized `JobEnqueueConflictException`。
- `_id == jobId` 的 `$setOnInsert` upsert；payload 先 DeepClone，逐节点严格比较 BsonType，Int32/Int64/Double 不等，document 字段顺序和数组顺序均严格。相同输入命中已有 job 不修改任何状态、attempts、owner、lease 或时间；不同 kind/payload 抛冲突。
- 未知写异常原样传播，调用方必须预先持久化 jobId 与原 typed payload 后同 ID 重试；取消不吞。仅 duplicate-key 已知竞态有最长真实时间五秒回读，不盲目推断成功。旧 EnqueueAsync、ClaimAsync、RenewAsync 及旧 producer 未迁移。
- 三文件 SHA-256：`src/Shoreline.Api/Jobs/JobQueue.cs` = `B1B84F3DDC304FAA4170233E42045F5FAADA8C9A61BEB0321375CB0F45F70CFD`；`tests/Shoreline.Tests/JobQueueIdempotencyTests.cs` = `69B90DB4FB33AC98ACA0C70A940D195E91E5A1D72A8A247AD4B7886ABBCD4349`；`docs/job-enqueue-idempotency.md` = `06FA22F340326EBB8DBF01545EB2E56F08ECA9888B944721EBE4334452F44676`。
- 十四项测试包括并发单行、typed/字段顺序冲突、各 terminal/running 状态整行保持、真实提交后抛错或取消再重试、写前失败、payload 变异及非法输入触库前拒绝。下一步由主审查后形成三文件小提交，再与 G2/Archive 的 journal 接线；本轮不续派。

### 环境异常：本地共享 Mongo OOM 退出（agent 报告，待恢复前复核）

- JobQueue agent 开始测试前发现 `shoreline-csharp-test-mongo-1` 为 `Exited (137)`、`OOMKilled=true`。未 restart/reset 共享容器。
- 为完成自身测试，agent 临时启动 `shoreline-jobidemp-mongo`（`mongo:8.0`、768m、`127.0.0.1:27128`），测试后停止并自动删除；报告共享 Mongo 仍退出。
- 这不代表当前还有 Mongo 可用，后续其他 agent 的连接失败可能由此造成。停工期间不重启、不新增服务；下次先只读核对容器状态与 OOM 原因，再按当时授权恢复 disposable 开发服务。历史已完成测试证据不因此被改写为未执行，但禁止声称当前环境健康。

### 已收到：J1 团队洞察四个 GET

- 基线快照 `C:\Users\88888888\AppData\Local\Temp\opencode\j1-baseline-0eddb2e` 完整 **304 passed**；验证快照 `C:\Users\88888888\AppData\Local\Temp\opencode\j1-verify-0eddb2e` 仅叠加 J1 与私有 Program 接线，focused **20/20**（16 direct + 4 HTTP），full **324/324 / 0 skipped**，`dotnet build -warnaserror` **0 warning / 0 error**。
- 新增六文件：`Features/TeamInsightService.cs`、`TeamInsightEndpoints.cs`、`TeamInsightRegistration.cs`、`tests/Shoreline.Tests/TeamInsightTests.cs`、`TeamInsightHttpTests.cs`、`docs/team-insight-compatibility.md`。Agent 报告六文件 SHA 与隔离快照一致；生产 service SHA-256 `293B1E1A488B45D5C7DE89AA5C7AEAC9D6E57FA474DF4AB934949126847880DD`，文档 SHA-256 `33DE86E2D782AB4DFF59A5F1F950DFF26988317481E7CFB9F489DB36A07D6937`。
- token / scope / live team INSIGHT 权限按旧顺序；无权限 103。user list 仅 active user-backed TM，dangling 跳过；user projects 仅本团队 working 项目的 active PM，subject 非 active TM 为 1005；project users 含 external，跨团队项目 4001。列表预览最多五项，但 count 保持真实全量；关联数据批量读取。
- outputs key 仅 live permission snapshot 含 `team:AUTO_BECOME_PROJECT_ADMIN` 时出现。**已知缺口：共享 `IdentityPermissions.TeamDefaults` 的内置 admin/creator 未默认授予该权限，而 Python 默认有；目前需显式 tag policy。** 下次先对照 Python 与现有权限测试评估修复，不能把 J1 测试通过当作默认角色已完全兼容。
- **非空 `csid` 的 output link 当前为 null**，尚未接 storage router；这是功能缺口，不能宣称导出链接已可用。时间仍使用现有 C# 无 Z 的六位小数格式；用户排序使用 `SearchText.Normalize`，其对 Python casefold 的等价程度需扩展差分验证。
- 共享 Program 未接线；后续仅 `AddTeamInsightServices()` / `MapTeamInsights()`，当前 HTTP Facts 依赖隔离 Program，未接线前不应当作共享宿主已通过。未 add/commit，不再续派。

### 已收到：N3 通知管理与 sync 严格验收

- 快照：`C:\Users\88888888\AppData\Local\Temp\shoreline-n3-isolated-d76f587-http`；仅 committed `d76f587` + N3 七文件 + 私有 Program 两行接线。
- `dotnet restore Shoreline.sln --locked-mode` 成功；正常 build **0 warning / 0 error**，未关闭 NuGet audit；focused **24/24**，full **378/378**（基线 354 + 24），七个 N3 文件 SHA-256 与共享工作树全部匹配。此前使用 `NuGetAudit=false` 的旧结果已被本次正常验证取代。
- N3 七文件为四个生产文件 `Features/NotificationManagementService.cs` / `.Json.cs` / `NotificationManagementEndpoints.cs` / `NotificationManagementRegistration.cs`，两个测试 `NotificationManagementTests.cs` / `NotificationManagementHttpTests.cs`，以及 `docs/notification-management.md`。未 add/commit，未修改共享 Program。
- JSON 修复：PythonFloat 保留 0.0/-0.0、指数形式、NaN/Infinity；ScientificFromFixed 从数字字符串生成 mantissa/exponent，避免浮点除法改变末位；键按 Unicode codepoint 排序；日期按秒内 ticks 判断 fraction，BSON 毫秒 `.123` 输出 `.123000Z`。有三组真实 CPython JSON golden，不能用 .NET 自己生成 expected。
- 四项真实 WebApplicationFactory HTTP Facts 覆盖 missing token 的 401/1003/no-store、admin detail 审计与列表不泄漏正文、scope/category 转发及 unchanged revision、unknown view 和 disabled host 的 no-store。
- 删除 Registration 中重复的 map 扩展解决 CS0121；后续唯一接线为 `builder.Services.AddNotificationManagementServices()` 与 `app.MapNotificationManagementEndpoints()`。共享 Program 尚无这两行，本轮不再安排接线。
- **Decimal128 仍转 double，超过 double 精度不能保证逐位一致，且无独立 Decimal128 Python golden。** sync revision 兼容评估必须保留此限制，不描述为所有 BSON 数值完全兼容。

### 已收到：D1 文件域隔离验收

- 快照：`C:\Users\88888888\AppData\Local\Temp\opencode\d1-snapshot-20261109`；报告基线 `c70c67b31de9a786139d41075c5fdf6c1f362a32`。API Release build **0 warning / 0 error**，focused **40/40**（33 service + 2 Python golden + 5 HTTP），报告 full **353/353**。
- **计数待核对：** 已提交 `c70c67b` 的历史全量为 323，直接加 40 应为 363；交付 full 353 的差额原因未说明。不可改写原报告或自行推断漏测原因，下次检查测试发现清单/日志/快照文件。用户已要求停派，不为此续派。
- 不是仅基线加 D1：快照另显式复制七个 E1 生产依赖、G2 `LabelPlusReader.cs`、未提交的配置化 `Infrastructure/Pagination.cs`；最终必须改用包含正式 E1 与分页提交的最新组合复验。E1 依赖的 SHA 前缀仅见 agent 报告，不足以替代完整 SHA 核对。
- D1 新增八个生产文件：`Features/FileService.cs`、`.Serialization.cs`、`.Mutations.cs`、`.Order.cs`、`.Folders.cs`、`FileEndpoints.cs`、`FileRegistration.cs`、`Infrastructure/FileMutationGate.cs`；三个测试文件 `FileTests.cs` / `FileGoldenTests.cs` / `FileHttpTests.cs`，`tests/Shoreline.Tests/Fixtures/file-goldens.json` 和 `docs/files-compatibility.md`。均未提交，未修改共享 Program、Pagination 或 E1 文件，未在共享目录 build/test。
- 后续 Program 仅需在既有 E1 接线基础上添加 `AddFileServices()`、`MapFileEndpoints()`、`InitializeFileIndexesAsync()`；不要重复注册 E1、routing 或 guard。本轮不再安排接线。
- public 上传接口：`Task<Dictionary<string, object?>> UploadAsync(BsonDocument project, string filename, IFormFile upload, string? parentId, CancellationToken ct)`；Archive Import 后续应复用此接口，不能自己写对象/计数。
- 报告已覆盖：新对象可能已被 Mongo 引用后不补偿删除；label→FileMutationGate 锁序和 scoped context 借用；新文件/folder/revision 的 `label_epoch`；source 级联游标每批 200；canonical `file_target_cache` 的 ts/cs/e；project `m_e` / file `et`；管理员列表含 inactive revision 按旧行为返回 4016；非默认 `csid` fail closed。
- Python AST golden 为实际 Filename 16 例和 order snapshot 4 例。历史 G2 报告的六处 long→int 错误不能直接套用到本次已编译 D1，仍需最新组合复验。
- 尚无 folder HTTP 路由；不要根据 `CreateFolderAsync` 服务接口自行假定旧 API 有某个路由。缩略图/OCR、pool router 和上传后调度仍待统一整合；`File:AllowText=true` 是测试开关，生产默认 image-only。

### 已收到：G2 稳定身份预分配，写入链仍阻塞

- 最新改动仅 G2 的 `src/Shoreline.Api/Features/ProjectImportService.cs`、`tests/Shoreline.Tests/ProjectImportTests.cs`、`docs/imports-compatibility.md`，未修改 E1、Program 或 ProjectService，未提交。
- 新 journal 在 source 插入前持久化 ObjectId `source_id` / `translation_id`，同时记录 `file_index/label_index/file_name/x/y/p/revision/source_rank`。source 仅按预分配 `_id` 查询并校验 file/rank/epoch；translation 仅按预分配 `_id` 查询并校验 source/target/user。已经移除 `{f,r}` 的 rank 认领路径。
- 缺少稳定 ID 的旧 journal 明确 blocked，禁止猜测归属；当前缺少 E1 writer 也 blocked（`is=Error`、`ie=ErrorUnknown`），保留 intent/checkpoint。这只是安全中间态，**不是可验收的导入实现**。
- enqueue 抛错后查询相同 kind + project + revision 的 pending/running job；查到保留 Pending，确认没有才 Error。该查找尚不能替代预存 jobId + `EnqueueOnceAsync` 的稳定排队身份；检查与未知提交之间的窗口仍须在接线时审查。
- 新增 `JournalPreallocatesStableIdsAndNeverAdoptsAForeignRankSource`、`LegacyJournalWithoutStableIdsIsRejectedInsteadOfClaimingRank`；六个真正写入/取消/未知结果/epoch/续跑测试明确 Skip，等待 E1 `CreateSourceOnceAsync` 等接口，不能作为已通过。
- 仅旧 `421208d` 开发快照 build 0 warning / 0 error，focused **17 passed / 6 skipped**，full **271 passed / 6 skipped**。不要沿用更早的 21/21、275/275 当最新验证，也不能据此声称当前 HEAD 联验通过。
- Agent 提到的 `e910e33` 快照 FileService 六处 long→int CS1503 是旧快照 blocker，尚不能推断当前 D1 仍有同样错误；待 D1 最新交付再核对，不为历史错误覆盖别人的新文件。
- 停工后顺序：审查 E1 helper 最终 API → G2 将稳定 source/translation ID 接入幂等写入和计数修复 → 使用稳定 jobId 接入幂等排队 → 在 K1 团队生命周期锁下接 ProjectService 的 `ValidateBeforeCreate` / `InitializeAfterCreateAsync` → 恢复六项测试并在最新已提交组合完整联验。本轮不续派。

### 已收到：E2 标签快照最终集成

- 快照：`C:\Users\88888888\AppData\Local\Temp\opencode\e2snapshot-3edd683-final`，基线 `3edd6839b5209dd425052f1b03c311590e9ea45a`。
- 仅基线 + E2 十文件 + Program：正常 `dotnet restore --locked-mode` 成功，API/tests build **0 warning / 0 error**，focused **32/32**，full **418/418**。
- 共享 Program 仅添加 `AddLabelSnapshots()`、`MapLabelSnapshots()`、`InitializeLabelSnapshotIndexesAsync()`；既有 E1 注册、routing 和 revision guard 保留。
- E2 生产文件：`LabelSnapshotService.cs`、其 `.Operations.cs` / `.Statistics.cs` / `.Merge.cs`、`LabelSnapshotEndpoints.cs`、`LabelSnapshotRegistration.cs`；三个测试文件 `LabelSnapshotTests.cs` / `LabelSnapshotGoldenTests.cs` / `LabelSnapshotHttpTests.cs`，以及 `docs/label-snapshots-compatibility.md`。连同 Program 共十一条路径，交付时 git blob 全部一致。
- Program blob：`c6ca77317e29f6d182cf3725506e132206a9833f`；文档 blob：`98930911061b578a0018c63b1a9dbb14ccfef993`。后续若文件变化，需要重新核对验证对应关系。
- Agent 另行实际运行了共享树 restore/build（超出原先仅隔离构建约束）：restore 成功，build **9 errors / 0 warnings**；`Thumbnail*` 缺少 `Shoreline.Imaging` 项目引用，`ProofreadFeedback*` 缺少 `MongoStore` / `IdentityPermissions` 等 using。未修改其他任务文件以绕过错误，未取得共享树 full suite 结果。
- **418 是隔离组合证据，绝不是当前整个工作树通过。** 下次先完成 Imaging 项目引用及校对反馈编译修复，再做真正组合验证；现在不扩大停工收尾范围。
- `target_id` query、权限先于目标解析、显式 null 的 409、Python AST BSON golden 已覆盖。仍有项目标签全量内存、4 MiB 为近似 row budget、锁轮询 2 秒且无 TTL 等限制。
- 未 add/commit。待主审查十一条路径和隔离证据后才形成独立 E2 提交，不能直接混入其他 WIP。

### 已收到：真实运行时差分交付

- 隔离快照：`C:\Users\88888888\AppData\Local\Temp\opencode\runtime-compat-20261110-001500-5f3a9c`，基线 `c70c67b31de9a786139d41075c5fdf6c1f362a32`。
- 生产修改仅五文件：`ApplicationService.cs`、`InvitationService.cs`、`TeamEndpoints.cs`、`Infrastructure/Pagination.cs`、`TermEndpoints.cs`。未修改 Program、测试 fixture 公共配置或 csproj，未 add/commit，未在共享工作树运行 dotnet。
- application/invitation 接受 singular/plural scope，invitation 后续 role 分支使用归一化 scope；application 系统角色按 level 降序，无 Accept-Language 回退中文；team 空 word 文案匹配 Python。
- 保留 `Pagination.From(HttpRequest)`，配置默认 limit 30、max 100；差分测试显式对齐合成配置 limit 10，未以错误默认配置改写 expected。
- 新增 `LegacyRuntimeDifferentialTests.cs`、`RuntimeCompatibilityHttpTests.cs`（6 Facts）、`docs/runtime-differential.md`；focused 共 7/7，完整 330/330，属于该隔离组合，不能替代最新 HEAD 完整联验。
- 新报告 `runtime_csharp_differential_after_fixes.json`：26,544 bytes，SHA-256 `32f44a0b68f280242069cdd0d055a8b645c3fd75fc5777a78ed5821997cce7a7`。business **0** 仅限 8 个已覆盖场景；framework **3** 为 ping Content-Type 和 405 body/Content-Type，wire-format **16** 为 JSON Content-Type 与日期 ISO 格式，不能描述为所有输出完全一致。
- 后续先审查五文件改动、报告和 source SHA，再决定小提交；用户暂停指令后不得为此续派智能体。

- Mongo standalone 不能把跨文档 `$inc` 视为 exactly-once。导入必须先持久化稳定 source/translation ID，并在部分成功后从 primary 数据恢复计数，不能仅检查 source 存在便跳过。
- rank、content、x/y 都不是导入操作身份；中断后其他用户可能占用同 rank。
- 标签写入使用同一个 `ProjectLabelLock.EnterAsync(LabelRequestContext, ...)`。HTTP 与 service 借用相同 scoped context，避免重入死锁。
- 不带 TTL 的锁，Insert 成功后取消、未知写入结果都必须使用独立且有界的 cleanup token，按 owner CAS 释放。
- 文件、头像更新 Mongo 的结果未知时不能直接删除新对象，否则可能破坏已经提交的引用。
- 存储池不能以目标 Head 作为自己的校验基准。必须对比源大小/可兼容 checksum，先验证和切换指针，再删除源。
- 文件 canonical 集合 `file_target_cache`；project edit time 为 `m_e`，project set 才是 `et`。
- `output.file_name` 是旧 BSON 的完整字段名，不能改成猜测的 `fn`。
- 当前 Python OCR 明确抛出 `NotImplementedError("OCR under reconstruction")`，不能宣称已有可工作的 Google OCR；后续需明确兼容禁用状态。
- RabbitMQ 目前计划由 MongoDB durable JobQueue + Worker 替换，必须逐项补齐 Celery 原任务的恢复/重试行为。
- S3Mock 不验证真正的 R2 签名；独立 SigV4 golden 通过也不是实际 R2 联机验收。

## 后续接手顺序

1. 先读本文件和本批最终交付，核对 Git HEAD、staged 和工作树；保留所有未提交文件。
2. 收下已完成的隔离验证证据，逐模块 review；只有源码/test SHA 与验证快照一致才形成小提交。
3. 先稳定 D1、E2、E1 幂等接口，再联验 G2/G1/G4；不要沿用修过其他 agent 源码的旧快照作为最终证据。
4. 依次接线 N3、J1、K1 和可选模块；核心只引用通用注册/生命周期 hook，不能硬编码可选模块。
5. 统一接入 ProjectService 创建前 LabelPlus 校验、创建后 import 初始化、团队生命周期锁，以及缩略图上传后调度。
6. 将项目/file/output/thumbnail/cleanup 实际使用的存储指针接入 generic storage router；当前 `csid` 非空 fail closed 不能算存储池已可替代。
7. 补团队全部项目导出、文本解析/术语查找任务、OCR 禁用能力的明确响应，以及仍未完成的定时恢复任务。
8. 增加配置迁移/数据检查（尤其 OSS + S3/R2 类型与 `KeyPrefix=storage/`、avatar 前缀、分页配置）；不读取真实部署 secrets。
9. 用真实 Python reference 扩充 HTTP、BSON、锁、导入导出和权限差分，再对最终已提交组合运行完整测试。
10. 刷新静态 route audit；路由存在数量不能代表功能完成，更不能代替生产替代结论。

## 验证和操作边界

- 当前多个 agent 往共享目录写 `.cs`，不能在共享工作树随意 build/test；使用 `git archive <明确提交>` 的独立 Temp 快照，只叠加本次待验收文件。
- 正常 NuGet 审计与 locked restore；不能通过 `NuGetAudit=false` 或忽略告警形成最终通过结论。
- 本地服务为 disposable Mongo 27128 / S3Mock 29128 / Mailpit 21128；测试仅随机 owned DB。
- Python 原目录禁止 import app、执行原 conftest 或原 tests 启动逻辑；它们可能读取配置或删除非 owned 测试库。
- 参考工具位于 C# `tools/legacy-reference/`，其 fixture 已包含来源 SHA 与合成数据；不把合成配置差异误判为产品 bug。
- 不执行 reset/checkout 丢弃改动、不重启服务、不格式化整个仓库、不 push。
