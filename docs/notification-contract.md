# 通知端到端契约（站内通知 + 邮件通知）

子代理 F（`agents/r2-notification-e2e-20261111`）的 parity 工作底稿。
Python oracle：`shoreline-moeflow-backend`；C# 实现：本 worktree。

## 能力 / Python / C# / 测试 / 结论

| 能力 | Python（oracle） | C# | 测试（`NotificationE2ETests`） | 结论 |
| --- | --- | --- | --- | --- |
| 发布事件字段 type/subtype/target + actor | `category`/`source`/`event_type`/`actor_id`/`team_id`/`project_id`（`app/services/notifications.py:444-466`） | `NotificationPublishService.MakeNoteAsync` 同名字段（`NotificationPublishService.cs`） | `Publish_RecordsEventFieldsTargetsAndDedupKey` | ✅ 一致 |
| 去重键（幂等） | `key = SHA256("{actor}:{source}:{header_key}")`，同 key 同 fingerprint 复用，同 key 异内容 409（`notifications.py:506-535`） | 同算法（`NotificationPublishService.cs:118-129`）+ fingerprint 向量已锁定（`NotificationPublishTests`） | 同上（复用 + 409 + `NotificationError`/4600） | ✅ 一致 |
| 同一 target 聚合（更新 vs 新增） | 分片 `notification_audience_chunk` 去重 + 回执 `(notification_id,user_id)` upsert；重复 dispatch 无新增（`app/tasks/notification.py:78-143`） | 同机制（`NotificationPublishService.Dispatch.cs:142-260`） | `Dispatch_SameTargetAggregatesWithoutDuplicates`（并发 dispatch + 扫描后复查） | ✅ 一致 |
| `unread` 计数 | 收件箱 `read_at=None` 聚合（`app/apis/notification.py:71-104`） | `UnreadCountsAsync`（`NotificationReadService.cs:352`） | `Inbox_PaginationSortingAndUnreadCounts` | ✅ 一致 |
| 拉取：分页/排序 | cursor（receipt `_id` 降序）+ `limit` 1–100（`notification.py:37-61`） | `InboxAsync`（`NotificationReadService.cs:322`） | 同上（limit=2 翻页 + 新旧排序） | ✅ 一致 |
| 已读过滤 | `unread=true` → `read_at=None, archived=False`（`notification.py:72-76`） | 同条件（`NotificationReadService.ReceiptPageAsync`） | `Inbox_UnreadFilterAndArchivedSoftDelete` | ✅ 一致 |
| 软删除（归档） | `archived` 标记；归档即已读（`notification.py:71-76`, `services` mark-read 语义） | `UpdateReceiptAsync` 归档置 `read_at`（`NotificationReadService.cs:409`） | 同上 | ✅ 一致 |
| TTL | `notification_throttle` 按 `expires_at` TTL 0 秒（`app/models/notification.py:156`） | 同名 TTL 索引（`NotificationRegistration.cs:62-64`） | `Ttl_ThrottleBucketIndexExpiresDocuments` | ✅ 一致 |
| 单条已读 | 回执 owner 读写（`notification.py` receipt API） | `UpdateReceiptAsync`（`NotificationReadService.cs:409`） | `Read_SingleReadAndForeignReceiptIsolation` | ✅ 一致 |
| 全部已读（cutoff） | cutoff + `read_at` CAS，cutoff 后到达保持未读（N1 文档语义） | `MarkReadAsync`（`NotificationReadService.cs:372`） | `Read_MarkAllReadCutoffKeepsNewArrivalsUnread` | ✅ 一致 |
| 他人通知不可改 | 回执按 `(notification_id, user_id)` 属主查询（`notifications.py:578-585`） | 同属主守卫（`NotificationReadService.cs:127`） | 同上（404 + 计数隔离） | ✅ 一致 |
| 谁能收到邮件 | `email_reason`：`no_email`/`channel_disabled`/`preference_disabled`/`unverified_email`（`notifications.py:376-390`） | `EmailReasonAsync` 同四原因（`NotificationPublishService.cs:910`） | `Subscription_PreferenceAndChannelGates` | ✅ 一致 |
| 订阅开关/退订 | `notification_preference.categories` 版本 CAS（`notifications.py` preference） | `SavePreferencesAsync`（`NotificationReadService.cs:455`） | 同上（system=false 仅屏蔽 system） | ✅ 一致 |
| 验证码邮件 vs 通知邮件 | 验证码走 `Mail:Enabled` + 独立 SMTP 任务（`EmailCodeService`/`SecurityEmailHandler`）；通知走 `ENABLE_USER_EMAIL` + 允许列表（`notification_mail.py:25-51`） | 验证码：`Mail:Enabled` + 原生 `SmtpClient`（`SecurityEmailHandler.cs`）；通知：`INotificationMailTransport` + `UserEmailEnabled`（`ProtectedNotificationMail.cs:111` 显式 channel） | `Mail_VerificationCodePathIsIndependentOfNotificationChannel` | ✅ 一致 |
| 扫描器 leased 超时 | `leased` 且过期 → `unknown`/`lease_expired`，绝不盲目重发（`app/tasks/notification.py:231-233`） | 同语义（`Dispatch.cs:23-27`） | `Scan_ExpiredLeaseBecomesUnknownWithoutResend` | ✅ 一致 |
| 投递 5 次上限 + 指数退避 | `attempts>=5` → `failed`；`next_attempt_at = now + min(3600, 30*2^attempts)`（`notification.py:210-216`） | 同公式（`Dispatch.cs:376-392`） | `Deliver_BackoffScheduleAndFiveAttemptCap`（60/120/240/480s + 5 次转 failed + 终态不再投） | ✅ 一致 |
| 并发扫描不重复投递 | 租约 CAS + 分片/delivery 唯一 upsert（`notification.py:25-33,116-133`） | 租约 CAS + 同唯一约束（`Dispatch.cs:82-90,281-302`） | `Scan_ConcurrentScansDeliverExactlyOnce`（fake transport 精确计数 2 封；Mailpit 真实投递未做，见局限） | ✅ 一致（fake 内；Mailpit 待确认见下） |
| 错误码 `{error,code,message}` | `{"error": 类名, "code": 4600, "message": ...}`（`app/core/api.py:34-39`，`notification_content.py:12`） | `{"error","code","message"}`（`Program.cs:127`，`NotificationErrors` 4600） | `ErrorContract_*`（常量/状态表 + HTTP 404 三元组） | ✅ 一致 |

## 待确认（需求不明确）

- [x] G1（已关闭）：Mailpit 真实投递。Python 通知路径就是**强制** STARTTLS（`app/services/notification_mail.py:72-83`，`starttls()` 无 try/except），所以「一次性 Mailpit 投递失败」是忠实复刻。新增 `Mail:SecureSocketOptions` 显式 opt-out（`4d87c2e`）后，测试可用 `None` 对 Mailpit 真实投递并断言收件数（`NotificationMailTlsTests` 12/12）；默认行为零变化。
- 附：验证码邮件路径不同——Python `app/tasks/email.py:99-104` 是**机会式** STARTTLS（只吞 `SMTPNotSupportedError`），C# 已改为 `StartTlsWhenAvailable`（`71eebb6`，`SecurityEmailTlsTests` 5/5）。两条路径的差异是 legacy 事实，不要统一。
- [ ] G2：`同一 target 的聚合（更新 vs 新增）`——Python 无“同一事件更新已有通知”语义（幂等键相同内容复用、不同内容 409；重复 dispatch 只补回执）。若“聚合”指别的（如同类通知合并计数），无 oracle 对应——标**待确认**（`app/services/notifications.py:502-535`）。
- [ ] G3：个人通知（`personal`）端到端：仅内部受信来源可发，无 HTTP 入口（`notifications.py:875-885`）。本次未覆盖 `PublishPersonalAsync` 的真实来源注册——标**待确认**是否需要。

## 验证（focused，实际执行）

- `tools/Run-FullSuite.ps1 -BasePort 33050 -Filter "FullyQualifiedName~NotificationE2E"`：
  **14/14 通过**（13 个 service-level 事实 + 1 HTTP 事实 + 1 纯单元形状断言，共 14 个 `[Fact]`）。
  首次运行 12/14：两个失败均为测试搭建问题（项目发布需作者同时是活跃项目成员，
  与 Python `app/services/notifications.py:96` 一致；`unverified_email` 需 `trust:false`
  实例），已修正，无产品代码改动。
- `tools/Run-FullSuite.ps1 -BasePort 33051 -Filter "FullyQualifiedName~Notification"`：
  **107/107 通过**（新增 14 + 既有 N1/N2/N3/HTTP 93），确认共享库下无交叉污染。
- 构建：`dotnet build Shoreline.sln` 0 警告 0 错误。
- 未跑全量（按任务要求只跑 focused）。
- Mailpit 真实投递：未做（见待确认 G1）。一次性栈自带 Mailpit 容器（33053/33054），
  但生产 MailKit 传输强制 STARTTLS/SMTPS + 系统信任链，Mailpit 明文监听无法完成
  accepted 路径；投递决策与精确计数已由 fake transport 锁定。

## 真实 SMTP 端到端（2026-11-12，生产 dump 副本）

在 `moeflow_prod`（生产 dump 还原库）上跑通一次完整投递，链路是
`publish → notification_delivery(pending) → scanner lease → MailKit → SMTPS 465`：

| 阶段 | 观察 |
|---|---|
| 首次投递 | `state=skipped`, `reason=unverified_email` —— `Notifications:TrustExistingEmails=false` 时要求收件地址出现在已登记验证表里，迁移过来的老账号没有这条记录 |
| 开关调整 | `NOTIFICATION_TRUST_EXISTING_EMAILS=True`（legacy 同名键，`x-csharp-env` 已映射） |
| 重试后 | `pending → leased → accepted`，`attempts=1`，`accepted_at` 落库，日志 `notification mail result accepted/` |

三个开关的职责边界（缺一个都不会发信）：

- `Notifications:Enabled`：只控制读写 API，与发信无关。
- `Notifications:UserEmailEnabled` / `LogEmailEnabled`（`ENABLE_USER_EMAIL` / `ENABLE_LOG_EMAIL`）：为 false 时 delivery 直接 `skipped/channel_disabled`。
- `Notifications:EmailUnrestricted`（`NOTIFICATION_EMAIL_UNRESTRICTED`）：为 false 时收件人必须在 `EmailAllowlist` 里，否则 `skipped/recipient_not_allowed`。
- `Notifications:TrustExistingEmails`（`NOTIFICATION_TRUST_EXISTING_EMAILS`）：为 false 时收件地址必须已在本系统验证过，否则 `skipped/unverified_email`。**生产 dump 切换后必须置 true**，否则只有本系统发过验证码的地址能收信。

TLS：生产 SMTP 是 465 隐式 TLS，`Mail:UseSsl=true` + `Mail:SecureSocketOptions=SslOnConnect`。
通知邮件在 Python 侧强制 `starttls()`，那种配置下本就发不出去；C# 用 `SslOnConnect` 才真正跑通。
