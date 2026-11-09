# 通知发布与邮件投递（N2）

本文描述 C# 后端中通知的创建、预览、候选收件人、草稿编辑、撤回，以及后台扫描、收件人分片、邮件投递的实现约定。读取侧（收件箱、回执、偏好、团队策略、项目卡片）见 `docs/notifications-compatibility.md`。

## 组成

| 文件 | 职责 |
| --- | --- |
| `src/Shoreline.Api/Features/NotificationPublishService.cs` | 发布、预览、候选收件人、草稿编辑、撤回、收件人计算、单收件人资格、邮件原因、内部 personal 来源注册 |
| `src/Shoreline.Api/Features/NotificationPublishService.Dispatch.cs` | 数据库扫描 `ScanAsync`、通知推进 `DispatchAsync`、邮件投递 `DeliverAsync` |
| `src/Shoreline.Api/Features/NotificationPublishEndpoints.cs` | N2 HTTP 路由 |
| `src/Shoreline.Api/Features/NotificationPublishRegistration.cs` | DI 注册、路由注册、N2 索引初始化 |
| `src/Shoreline.Api/Jobs/NotificationScanWorker.cs` | `BackgroundService` 周期扫描 |
| `src/Shoreline.Api/Notifications/ProtectedNotificationMail.cs` | 受保护邮件策略与 MailKit 传输 |
| `tests/Shoreline.Tests/NotificationPublishTests.cs` | 发布与投递测试（fake time / fake transport） |

N1 的 `NotificationContent`、`NotificationReadService`、`NotificationRegistration`、`NotificationEndpoints` 不在此范围，也不在本实现中修改。

## 接入方式

`Program.cs` 已做三处最小接线（DI、端点映射、索引初始化）：

```csharp
builder.Services.AddNotificationPublishServices();
// after MongoStore.InitializeAsync
await NotificationPublishRegistration.InitializeAsync(store, cancellationToken);
// in the endpoint section
app.MapNotificationPublishEndpoints();
```

`AddNotificationPublishServices` 注册：

- Scoped `NotificationPublishService`；
- Singleton `INotificationMailTransport` → `MailKitNotificationTransport`；
- Hosted `NotificationScanWorker`。

N2 索引与 N1 索引是互补的（N2 只补 `notification` key、分片、回执、投递、已验证邮箱、审计的 unique 约束）。`Notifications:WorkerEnabled=false` 时 worker 直接退出；`Notifications:Enabled=false` 时即使 worker 启动也不做数据库扫描。测试使用自己的 HTTP 配置关闭 worker，避免后台扫描污染共享测试库。

## 路由

普通入口（需要登录 token）：

- `POST /v1/notifications` — 发布；`Idempotency-Key` 头 8–160 字符（按 rune）；返回管理视角详情，HTTP 202。
- `POST /v1/notifications/preview` — 预览人数、邮件统计、前 100 名候选与可见正文；限流 60 次/分钟。
- `GET /v1/notification-recipients` — 候选收件人搜索；`q` ≤100 字符、对 `n`/`as` 做转义后的不区分大小写匹配；`limit` 1–100（默认 20）；按 id 升序 + `cursor` 分页；限流 120 次/分钟。
- `PATCH /v1/notifications/{id}` — 仅编辑本人可管理的草稿（version CAS）。
- `POST /v1/notifications/{id}/revoke` — 撤回（`confirmed=true`、`version`、`reason`、`Idempotency-Key`）。

管理员入口（必须有效站点管理员）：

- `POST /v1/admin/notifications` — 只能新建 `category=system` 且无 `scope_id` 的通知，不得代发团队/项目通知。
- `POST /v1/admin/notifications/preview` — 同上限制。
- `GET /v1/admin/notification-recipients` — 只能选择站点收件人。
- `POST /v1/admin/notifications/{id}/revoke` — 管理员撤回。

发布与预览请求必须是 JSON 对象，未知字段会被拒绝。

## 发布语义

`make_note` 对应 `MakeNoteAsync`：

- 允许字段：`category`、`scope_id`、`title`、`body`、`audience`、`email`、`publish_at`、`expires_at`、`draft`。
- `category` 只能是 `system` / `team` / `project` / `personal`；HTTP 不能创建 `personal`，也不能传 `payload`、`source`、`event_type`。
- `title` 去空白后 1–200 字符且不含控制字符；`body` 上限 10000 字符。
- `email`、`draft` 必须是布尔值。
- 时间必须带时区，统一转 UTC；`publish_at` 不能超过当前时间 366 天；`expires_at` 必须晚于 `max(now, publish_at)`。
- 状态：`draft` 为草稿；否则 `publish_at` 在未来为 `scheduled`，否则 `preparing`。
- 搜索文本为 `Normalize(title + " " + Plain(nodes))`，搜索 token 由 `NotificationContent.SearchTokens` 生成。

发布还要经过：

1. 接收人计算：空集合直接失败；`manual` 模式必须所有手选用户仍在有效范围内（不允许静默丢弃）。
2. 限流：每用户每分钟 20 次。
3. 以 `key` 做唯一插入，竞争失败时重新查询并比较 fingerprint。

幂等键与 fingerprint 与 Python 实现兼容：

- 文档 `key = SHA256("{actor_id}:{source}:{header_key}")`（UTF-8）。
- `fingerprint = SHA256(json.dumps({"data": data, "type": event_type, "payload": payload}, sort_keys=True, default=str))`，等价于 Python 的 `ensure_ascii=True`、默认分隔符、保留 `1.0` / `-0.0` / `1e+20` / `1e-05` 形式，并区分布尔与整数。
- 同 key 同 fingerprint 直接返回已有通知；同 key 不同 fingerprint 返回 409。

## 收件人规则

`audience` 模式为 `all` / `condition` / `manual`：

- `all` 只能包含 `mode`。
- `manual` 只能包含 `mode` + 非空 `user_ids`（≤1000），且用户必须落在当前范围的有效收件人集合内。
- `condition` 至少一项非空，且不能带 `user_ids`。
- 每个条件列表去重并排序；`user_ids` 上限 1000，其它条件上限 30。

按类别：

- `system`：不能使用 `base_roles` / `worker_qualifications` / `tags`；`site_roles` 只能是 `admin` / `member`；`team_ids` 与 `project_ids` 互斥且必须都存在。
- `team` / `project`：不能使用 `site_roles` / `team_ids` / `project_ids`；`base_roles` 限 `creator` / `admin` / `member`；`worker_qualifications` / `tags` 限工作人员标签（`raw_provider` 起）。
- `personal`：只能 `manual`，且只能由内部受信来源发布。

计算规则：

- 初始候选为未封禁用户。
- 团队按 active 成员、`base_tag` 与工作人员资格筛选。
- 项目按 active 且 `user` 非空的成员筛选；`base_roles` 的 `member` 表示“没有 creator/admin 标签的成员”；外部成员（user 为空）不参与。
- `site_roles`、`team_ids` / `project_ids`、`manual` 取交集；`manual` 中出现无效用户或范围外用户直接拒绝。
- 结果按 id 升序，最多 `Notifications:MaxAudience`（默认 20000）人；超过上限报错（内部会多取 1 条检测）。
- `proofread_feedback` + `payload.cc_myself` 且 actor 拥有 `project:PROOFREAD_TRA` 时，即使没有项目管理权限也可以抄送自己。

单收件人复核 `RecipientEligibleAsync` 每次只做有界查询，并重新检查 active、封禁、成员关系与资格，避免为每个投递加载全量候选。

## 草稿编辑与撤回

草稿编辑：

- 必须通过普通管理权限（`ordinary_manage`）。
- `version` 必须是整数（布尔不算）。
- 只允许 `draft` / `scheduled`、未撤回、`lease_until <= now`。
- 不能修改 `category`、`scope_id`、`author`、`event_type`。
- 重新构建数据并重新校验收件人。
- 以 `(version, revoked_at=null, state, lease_until)` 做 CAS，冲突返回 409，并写审计。

撤回：

- 普通入口走普通管理权限；管理入口要求管理员。
- 只允许 `confirmed` / `version` / `reason`，`confirmed` 必须为 `true`；`reason` 去空白 1–500 字符；`version` 整数；操作键 8–160 字符。
- 操作标识 `SHA256("{actor_id}:{key}")`。
- 已撤回且操作标识一致：补写审计并返回（幂等）；操作标识不一致：409。
- CAS `(version, revoked_at=null)` 后设置 `state=cancelled`、撤回人/原因/操作标识/动作，`moderation_audited=false`，版本 +1。
- 审计成功后置 `moderation_audited=true`，并把 `pending` / `retry_wait` 的投递取消。
- 撤回不会把已进入传输中的邮件“拉回”；见下文边界。

## 后台扫描

`NotificationScanWorker` 默认每 10 秒（`Notifications:ScanIntervalSeconds`）调用一次 `ScanAsync`；`Notifications:WorkerEnabled=false` 时 worker 不启动循环，`Notifications:Enabled=false` 时即使运行也不扫描数据库。通过数据库状态扫描继续到期通知、分片和投递；进程重启后从这些检查点恢复。

每次扫描：

1. 过期 `leased` 投递 → `unknown` / `lease_expired`，绝不盲目重发。
2. 最多 10 条到期通知（`scheduled` / `preparing` / `dispatching`、未撤回、`publish_at<=now`、`lease_until<=now`），总预算 45 秒。
3. 最多 50 条到期投递（`pending` / `retry_wait`、`next_attempt_at<=now`），总预算 75 秒。
4. 最多 100 条已撤回但未审计的通知，补写撤回审计。

### 通知推进

`DispatchAsync` 先以 UUID token 做 120 秒租约的 CAS 领取；之后所有关键写都带 `lease_token` 与 `revoked_at=null` 守卫，结束时释放租约。

- 未审计的发布先补 `publish` 审计，再置 `publish_audited=true`。
- 已过期 → `cancelled` / `expired`。
- `scheduled` 且晚于计划时间 1 小时 → 回 `draft` / `schedule_late`。
- 作者已失去权限 → `cancelled` / `sender_permission_lost`。
- 收件人计算失败 → `cancelled` / `invalid_audience`。

分片（准备阶段）：

- 每次 tick 最多 4 个分片，每片最多 100 人。
- 以 `(notification_id, number)` 唯一 upsert 持久化分片（`users`、`cursor`、`done=false`），再用 CAS 推进通知的 `next_chunk` / `cursor` / `recipient_count`。
- 崩溃恢复后沿用已有分片，绝不重新选择另一批用户。
- 没有更多用户时切换到 `dispatching`；小范围可以在同一 tick 继续分发。

分发阶段：

- 每次 tick 最多处理 4 个未完成分片。
- 每个用户写之前都检查通知仍为 `dispatching` 且租约有效；用户已不在当前有效集合则跳过。
- 回执使用 `(notification_id, user_id)` 唯一 upsert，重复分发不会重复创建。
- 只有 `requested_email=true` 才创建投递，消息 id 固定为 `<notification.{note_id}.{user_id}@moeflow.invalid>`。
- 分片标记 `done` 前必须仍持有有效租约，避免旧租约抢占后重复计数。
- 全部分片完成后置 `published`，`recipient_count` 使用实际回执数。

### 邮件投递

`DeliverAsync` 以 CAS 把 `pending` / `retry_wait` 且到期的投递改为 `leased`（120 秒，`attempts+1`），然后：

1. 重新读取通知与用户；通知必须仍存活。
2. 重新检查作者权限与收件人资格。
3. 计算邮件原因：`no_email`、`channel_disabled`、`preference_disabled`、`unverified_email`；只有原因为空才发送。
4. 发送前最后检查通知未撤回且状态为 `dispatching` / `published`。
5. 只有仍持有租约的实例能写回结果。

内容：

- 团队 / 项目：固定文案“您收到一条通知，请登录查看：” + `{SiteOrigin}/dashboard/notifications/{id}`，主题“萌翻通知”。
- 站点：主题为通知标题，正文为纯文本 + 链接，HTML 为渲染后的节点 + 链接。
- 校对反馈：主题为通知标题，正文/HTML/回复地址来自内部 `payload`（`text` / `html` / `reply_to`）。

结果：

- `retry_wait`：`attempts >= 5` 转 `failed`，否则 `next_attempt_at = now + min(3600, 30 * 2^attempts)` 秒。
- `accepted`：记录 `accepted_at`。
- `unknown`：不重发（传输中断时无法确定对端是否已接受）。
- 其它 `skipped` / `cancelled` / `failed` 持久化原因。

## 邮件保护

`ProtectedNotificationMail.Check` 的检查顺序：

1. `Notifications:UserEmailEnabled` / `ENABLE_USER_EMAIL`（兼容 `Notifications:LogEmailEnabled` / `ENABLE_LOG_EMAIL`）关闭 → `skipped` / `channel_disabled`。
2. 地址必须匹配 `[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+`，去空白并小写 → 否则 `skipped` / `invalid_email`。
3. 允许列表（`Notifications:EmailAllowlist` / `NOTIFICATION_EMAIL_ALLOWLIST`），未配置时为空；`Notifications:EmailUnrestricted` / `NOTIFICATION_EMAIL_UNRESTRICTED` 默认 false。测试/调试环境**始终**要求允许列表，即使 unrestricted 为 true。
4. 缺少 SMTP host 或 From → `skipped` / `smtp_unconfigured`。
5. 主题、From、回复地址、消息 id 含 CR/LF → `failed` / `invalid_header`。

传输（`MailKitNotificationTransport`）：

- 主机/端口：`Mail:Host`、`Mail:Port`（默认 465）；`Mail:UseSsl` 为 true 时要求 SMTPS（`SecureSocketOptions.SslOnConnect`），否则必须 STARTTLS；不允许无 TLS，也不跳过证书校验（使用系统信任链）。
- `Mail:Username` 可选；存在时使用 `Mail:Password` 认证。
- 连接、认证、发送分离；连接与发送超时 10 秒。
- `SendAsync` 开始即进入“传输中”边界。

错误映射：

- 4xx（含收件人被拒）→ `retry_wait`（`recipient_refused` / `smtp_response`）；5xx → `failed`。
- TLS、协议不支持、配置/地址格式错误 → `failed` / `smtp_configuration`。
- 连接阶段中断 → `retry_wait`；发送阶段中断 → `unknown`；发送已接受 → `accepted`。
- `finally` 中断开连接的异常不会覆盖 `accepted`；断开使用独立的 5 秒取消令牌，绝不使用请求的 `CancellationToken`，避免清理无限阻塞 worker 的 75 秒预算。
- 日志只记录状态与原因，不记录端点、密钥、地址或邮件内容。

租约恢复边界：worker 用请求 `CancellationToken` 执行 guard/release 时，如果令牌已经取消，`finally` 可能无法立即释放租约，通知或投递会保持 `lease_until = now + 120s`。这不是永久锁：下一个扫描周期在租约到期后会重新领取，`publish_audited`、durable chunk 与唯一索引保证不会重复发送或重复建回执。

明确边界：邮件一旦进入传输阶段，撤回或状态检查无法保证对端未收到；`unknown` 一律不重发。若需要更强的“绝不重发”保证，应依赖对端幂等（固定 Message-ID）。

## 配置项

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Notifications:Enabled` | false | 总开关；关闭时不扫描、不发布 |
| `Notifications:SiteOrigin` | — | 邮件中的站点地址 |
| `Notifications:MaxAudience` | 20000 | 单条通知最大收件人数 |
| `Notifications:TrustExistingEmails` | false | 是否信任未验证的历史邮箱 |
| `Notifications:ScanIntervalSeconds` | 10 | 扫描周期 |
| `Notifications:WorkerEnabled` | true | 是否启动后台扫描；测试通过它关闭 worker，避免随机抢占 |
| `Notifications:UserEmailEnabled` | false | 用户邮件总开关 |
| `Notifications:EmailAllowlist` | 空 | 允许收件地址列表 |
| `Notifications:EmailUnrestricted` | false | 是否允许列表外地址（测试/调试仍强制列表） |
| `Mail:Host` / `Mail:Port` | — / 465 | SMTP 端点 |
| `Mail:UseSsl` | false | true 为 SMTPS，false 必须 STARTTLS |
| `Mail:From` | — | 发件地址 |
| `Mail:Username` / `Mail:Password` | 空 | 可选认证 |

环境变量兼容：`ENABLE_NOTIFICATIONS`、`ENABLE_USER_EMAIL`、`ENABLE_LOG_EMAIL`、`NOTIFICATION_MAX_AUDIENCE`、`NOTIFICATION_TRUST_EXISTING_EMAILS`、`NOTIFICATION_EMAIL_ALLOWLIST`、`NOTIFICATION_EMAIL_UNRESTRICTED`、`EMAIL_SMTP_HOST`、`EMAIL_SMTP_PORT`、`EMAIL_USE_SSL`、`EMAIL_ADDRESS`、`EMAIL_USERNAME`、`EMAIL_PASSWORD`。

## 内部来源与个人通知

`NotificationPersonalSources` 供内部模块注册：

- 名称必须匹配 `module:[a-z][a-z0-9_]{0,63}`，且不可重复。
- 只有已注册来源、校验回调返回 true、且 `category=personal` 时才允许 `PublishPersonalAsync`。
- 核心代码不引用可选模块；模块通过 `RegisterSource` 自行接入。
- `MarkVerifiedAsync(email)` 供后续邮箱验证流程调用（当前不改 `EmailCodeService`）。

## 测试

`NotificationPublishTests` 使用 `LocalServices` 的内存 Mongo、可设置的 `TimeProvider` 与 fake `INotificationMailTransport`，覆盖：

- Python fingerprint 向量与幂等/冲突；
- 限流、项目成员与继承管理员边界；
- 手选用户的封禁/范围校验、团队资格筛选；
- 450 人 4×100 分片与崩溃续跑；
- 定时过晚、过期、作者失权、分发前封禁；
- 草稿 CAS 与撤回幂等/审计；
- 扫描补审计与过期租约；
- 邮件原因、允许列表、TLS 策略、投递重试 5 次、`unknown` 不重发、租约抢占；
- 候选搜索转义与分页、预览统计、禁用开关。

不连接真实 SMTP；测试只使用 fake transport。本地 Mailpit 没有 STARTTLS，不能为了测试放宽 TLS 要求。

### 隔离验证记录

- 基线：提交 `c70c67b`（H1/I1/N1 已包含）。
- 隔离目录：系统临时目录 `n2snap-c70c67b`，由 `git archive c70c67b` 解出；只覆盖 N2 自有源码、测试、`Shoreline.Api.csproj` 与最小 `Program.cs` 接线（`AddNotificationPublishServices`、`MapNotificationPublishEndpoints`、`NotificationPublishRegistration.InitializeAsync`）。没有复制其他未提交 WIP。
- 构建：正常 `dotnet restore` + `dotnet build Shoreline.sln`（NuGet 审计保持默认全启用，未使用 `NoWarn`、`DisableAudit`、`TreatWarningsAsErrors=false` 或 `NuGetAuditMode=direct`），0 warnings / 0 errors。
- 聚焦：`dotnet test --filter FullyQualifiedName~NotificationPublishTests`，31/31 通过（26 个 direct-service Fact + 5 个 HTTP Fact）。
- 全量：`dotnet test`，354/354 通过。
- 历史验证：更早的 `e910e33` 基线是 314/314；`c70c67b` 新增 H1/I1/N1 测试后为 354/354。
- Fingerprint：用本机 Python 3.10.11 实际执行 `json.dumps(..., sort_keys=True, default=str)` 生成三个 oracle（含 Unicode、`1.0`/`1e+20`/`1e-05`/`-0.0`，以及 BSON `datetime`/`ObjectId` 的 `default=str` 语义），C# 结果逐字节一致。
- 邮件：全部使用 fake `INotificationMailTransport`，没有真实 SMTP 网络连接。取消分类由 `MailKitNotificationTransport.ClassifyFailure` 直接覆盖：发送前取消→`retry_wait`，发送中取消→`unknown`，已接受后取消→`accepted`，配置异常→`failed`/`smtp_configuration`。
- 依赖：`MailKit 4.16.0`（`MimeKit 4.16.0` 依赖 `BouncyCastle.Cryptography 2.6.2`，与项目固定版本一致）。MimeKit 传递的 `System.Security.Cryptography.Pkcs` 默认 10.0.0 命中 GHSA-2fmv-rv2c-59x7 / GHSA-846f-f764-76mx，因此显式固定 `System.Security.Cryptography.Pkcs 10.0.14`（NuGet 稳定版）；正常审计 restore/build 0 warnings，未弱化审计。不要回退到带已知漏洞的更低 MailKit 或 Pkcs 版本。
