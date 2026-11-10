# 数据与后台任务静态复核（2026-11-10）

基线 Python `be0b717` / C# `c29888e`。以下是源码事实，**不是运行时或测试通过声明**。
首轮“没有 storage pool worker / LabelPlus / generic email / locked tags / csid 不一致”被否定，不据此重写已有功能。
Temp worktree 的 LSP 被 project-directory guard 拒绝；保留分支并移动到 workspace 的 `.readiness-worktrees/moeflow-readiness-data-jobs` 后，beam-free 已在独占路径补齐 documentSymbol / hover 和 build 0/0。

## 20 个真实 Celery task 的完整映射

Python 路径相对同级 `shoreline-moeflow-backend`；C# 路径相对本仓库。handler 存在不代表重试/恢复已通过。

| Python decorator / task | C# 消费者 / 替代 | 状态 |
|---|---|---|
| `app/tasks/email.py:16` `tasks.email_task` | `src/Shoreline.Api/Features/ProofreadFeedbackService.cs:179-193` 内联；`src/Shoreline.Api/Jobs/SecurityEmailHandler.cs:12` 安全码 | 通用异步投递被替代；失败/重试语义和产品接受未验证 |
| `app/tasks/file_parse.py:24` `parse_text_task` | `src/Shoreline.Api/Jobs/FileParseJobHandlers.cs:14` `parse_text` | 已接线，生产 `src/Shoreline.Api/Features/FileParseService.cs:86` |
| `app/tasks/file_parse.py:129` `find_terms_task` | `src/Shoreline.Api/Jobs/FileParseJobHandlers.cs:27` `find_terms` | 已接线，生产 `src/Shoreline.Api/Features/FileParseService.cs:278` |
| `app/tasks/import_from_labelplus.py:24` `import_from_labelplus_task` | `src/Shoreline.Api/Jobs/ProjectImportJobHandler.cs:14` | 生产 `src/Shoreline.Api/Features/ProjectImportService.cs:161`；普通创建断链 |
| `app/tasks/notification_security.py:25` `notification_security_email` | `src/Shoreline.Api/Jobs/SecurityEmailHandler.cs:12` `security-email` | 生产 `src/Shoreline.Api/Features/EmailCodeService.cs:34`；开关/模板/码存储不同 |
| `app/tasks/notification.py:224` `notification_scan` | `src/Shoreline.Api/Jobs/NotificationScanWorker.cs:20-49` | `src/Shoreline.Api/Features/NotificationPublishRegistration.cs:16-17` 注册，默认 10 秒 |
| `app/tasks/ocr.py:580` `ocr_task` | `src/Shoreline.Api/Features/OcrEndpoints.cs:7-17` 返回 501 | Python 主动重构禁用；例外/HTTP 契约仍待确认 |
| `app/tasks/output_project.py:47` `output_project_task` | `src/Shoreline.Api/Jobs/OutputJobHandler.cs:13` | 生产 `src/Shoreline.Api/Features/OutputService.cs:279` |
| `app/tasks/output_team_projects.py:20` `output_team_projects_task` | `src/Shoreline.Api/Jobs/TeamOutputJobHandler.cs:14` | 生产 `src/Shoreline.Api/Features/OutputService.cs:204` |
| `app/tasks/proofread_draft.py:28` `proofread_draft_bake` | `src/Shoreline.Api/Jobs/ProofreadDraftJobHandler.cs:13-15` | 生产 `src/Shoreline.Api/Features/ProofreadFeedbackService.cs:256,334` |
| `app/tasks/thumbnail.py:88` `create_thumbnail_task` | `src/Shoreline.Api/Jobs/ThumbnailJobHandler.cs:13` | 生产 `src/Shoreline.Api/Features/ThumbnailService.cs:191` |
| `app/modules/archive_import/tasks.py:228` `archive_import_task` | `src/Shoreline.Api/Jobs/ArchiveImportJobHandler.cs:9` | 生产 `src/Shoreline.Api/Modules/ArchiveImport/ArchiveImportService.cs:111` |
| `app/modules/storage_pool/tasks.py:52` `storage_pool_scan` | `src/Shoreline.Api/Jobs/StoragePoolWorker.cs:15-85` | worker 默认关闭，需部署开关与调度验证 |
| `app/modules/storage_pool/tasks.py:216` `storage_pool_run` | `src/Shoreline.Api/Modules/StoragePool/StoragePoolTransferService.cs:87`；`src/Shoreline.Api/Jobs/StoragePoolWorker.cs:63-75` | 默认关闭影响 active transfer 恢复 |
| `app/modules/ziteng_partner/tasks.py:20` `tasks.ziteng_partner.check_project` | `src/Shoreline.Api/Jobs/ZitengCheckJobHandler.cs:14` | 生产 `src/Shoreline.Api/Features/ZitengPartnerService.cs:35,53` |
| `app/tasks/mit.py:18` `tasks.mit.detect_text` | 无 handler；`src/Shoreline.Api/Features/MitEndpoints.cs:267` 入队 | 条件启用时缺消费者 |
| `app/tasks/mit.py:23` `tasks.mit.ocr` | 无 handler | 同上 |
| `app/tasks/mit.py:28` `tasks.mit.translate` | 无 handler；`src/Shoreline.Api/Features/MitEndpoints.cs:279` 入队 `mit_translate` | 同上 |
| `app/tasks/mit.py:33` `tasks.mit.inpaint` | 无 handler | 同上 |
| `app/tasks/mit.py:109` `tasks.preprocess_mit` | 无 handler | 同上 |

MIT 文件头说明真实任务实现位于外部 `manga_translator/moeflow_worker.py`。`tests/Shoreline.Tests/MitHttpTests.cs:209,237-243` 只证明入队 pending，不证明完成；须外部 worker 桥接或产品明确接受禁用，不能伪造 AI 完成。
队列 `src/Shoreline.Api/Jobs/JobQueue.cs:31,34,61,110,141`：attempts < 5、lease 2 分钟、退避 `min(300,5*2^attempts)`。
`src/Shoreline.Api/Jobs/JobWorker.cs:11,24-26` 单池默认并发 1，clamp 1..8；不同于 Python `app/factory.py:138-153` 多队列。重任务阻塞邮件、lease / crash / unknown-commit 均仍需实际验收。

## 确认的缺口与待验收边界

| 项目 | 证据 | 下一步 |
|---|---|---|
| 普通创建忽略 LabelPlus | Python `app/models/project.py:477-481,538-544`；C# `src/Shoreline.Api/Features/ProjectService.cs:199-200` 明确忽略 | 普通 POST 的单/多 target、解析错误、状态/DB/任务恢复测试；ZIP 通过不能替代 |
| fresh bootstrap 缺失 | Python `app/factory.py:191-228`；C# `src/Shoreline.Api/Features/SiteSettings.cs:18-21` 仅内存默认，无启动创建 admin/default team | fresh/existing/concurrent/retry 初始化，不覆盖现有用户密码 |
| prefix 默认不同 | Python `app/config.py:81` `storage/`；C# `src/Shoreline.Api/Storage/ObjectStorage.cs:99` 默认空 | 迁移显式 prefix、专门 regression；真实 R2 不能以 S3Mock 代替 |
| 用户/日志邮件 OR 放行 | Python `app/services/notification_mail.py:36` 分类开关；C# `src/Shoreline.Api/Notifications/ProtectedNotificationMail.cs:103` OR | 所有组合测试，log=true/user=false 不得发用户邮件 |
| 安全码邮件语义 | `src/Shoreline.Api/Jobs/SecurityEmailHandler.cs:20,46-49` 仅 Mail:Enabled / 纯文本 | Python 开关、模板/站点链接及 HTTP oracle；不只靠配置规避 |
| 新安全码回滚不可见 | `src/Shoreline.Api/Features/EmailCodeService.cs:18-37` 新码 csharp_job、`src/Shoreline.Api/Features/EmailCodeService.cs:79-98` 旧码 fallback | C# 发码后 Python 验证、双向切换与 backlog 策略 |
| StoragePool 默认无调度 | `src/Shoreline.Api/Program.cs:50-52`；Python beat 常驻而业务 scan_enabled 默认 false | 显式启用 worker，保留业务开关，跨日/重启测试 |
| identity 查询索引缺失 | Python `app/models/team_member.py:38-51`；C# `src/Shoreline.Api/Infrastructure/MongoStore.cs:56-61` | 对齐 3 个 exact index/options/name，existing 数据无损，explain/负载 |
| schema migration 无 gate | C# 未执行 Python `app/migrations/versions/m0000..m0009` | 前置最新迁移验证；m0009 project.fs、m0004/5/7 identity、m0008 file.mo 的回填/回滚 |

三个索引：`team_member_aliases_v1` `(team,aliases)`、`team_member_worker_qualifications_v1` `(team,worker_qualifications)`、`team_member_alias_search_v1` `(team,status,aliases_search)`。不是首轮口误所称的两个。

## 数据抽查一致（未覆盖全部历史 BSON）

- StoragePool collection：`storage_pool_source/transfer/transfer_object/setting/lock/run_lock/audit`（每项均加 `storage_pool_` 前缀），`src/Shoreline.Api/Modules/StoragePool/StoragePoolConstants.cs:10-36` 名称/状态抽查一致；`src/Shoreline.Api/Modules/StoragePool/SourceService.cs:60,90-110` sub_path 默认 storage/、缩写字段抽查一致。
- archive_import_task 的 `p,u,g,t,gu,s,st,n,c,e,zu,d,ct,ut` 一致；C# 内部 `lr/lo/lu/pi/ic` 在 `src/Shoreline.Api/Modules/ArchiveImport/ArchiveImportTaskStore.cs:16-21` 注明；`src/Shoreline.Api/Modules/ArchiveImport/ArchiveImportTypes.cs:7-44` 状态 0..6。
- output.file_name 是 Python 真实历史字段：`app/models/output.py:32-43` / `src/Shoreline.Api/Features/OutputService.cs:258-271`。roles/lang/type 已有 seed；settings 字段匹配但默认不插入。
- project.csid 对照 `app/models/project.py:428` 一致；`src/Shoreline.Api/Storage/ProjectStorageRouter.cs:102-109` 兼容 ObjectId/string，但 `src/Shoreline.Api/Features/ProjectService.cs:563` view 只接受 ObjectId，历史 string 边缘需验证。
- C# 内部 `csharp_job/csharp_seed/csharp_captcha/project_import_checkpoint` 要验证 Python 回滚及残留任务处理，不能随意清理。
- 权限继承 `src/Shoreline.Api/Security/IdentityPermissions.cs:208-215` 对照 `app/services/identity_permission.py:306-330` 一致；locked tags / lifecycle 已有实现及测试，执行/并发仍未验收。
- token 静态匹配：`app/models/user.py:257-303` / `src/Shoreline.Api/Security/LegacyToken.cs:26-35`，URLSafeTimedSerializer、30 天 `2592000`、claims `{id,pc}`；须真实互操作。
- `src/Shoreline.Api/Security/PasswordHash.cs:10-15,19-50` 新 PBKDF2-SHA256 1,000,000、验证多类 PBKDF2/scrypt；C# 新密码回滚 Python 尚未实测。
- 非 BMP 排序：Python `app/models/identity_tag.py:285-300` code point，C# `src/Shoreline.Api/Security/IdentityAssignment.cs:24-36` Ordinal UTF-16；不能由 BMP 样例推广。
- `src/Shoreline.Api/Features/LegacyTypes.cs:31-40` m_o partial unique index 在历史重复 role 上可能失败；需无损 preflight。通知 publish_at 索引反向差异需 explain，不直接认定结果错误。

## 六类后续工作流

运维/安全 → 数据兼容 → 认证/邮箱 → 核心（含普通创建 LabelPlus）→ 可选模块 → 通知；主负责人统一 test-only 隔离、真实差分和共同 `Program.cs` 装配。
小任务优先 beam-free；复杂/混乱可用用户确认免费的 `agrt-gateway/gpt-6.1-sol`。各分支/端口/文件独占，逐逻辑 commit，不 push；每次需自己的 worktree LSP、build 0/0、聚焦测试和 review。
