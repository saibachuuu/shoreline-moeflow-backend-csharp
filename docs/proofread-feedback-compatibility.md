# G3 校对稿反馈兼容说明（ProofreadFeedback）

本文档记录 C# 校对稿反馈切片与 Python
`app/apis/project.py`（`ProjectSendProofreadDraftAPI`）、
`app/tasks/proofread_draft.py`、`app/services/proofread_image.py`
的兼容状态。实现位于：

- `src/Shoreline.Api/Features/ProofreadFeedbackService.cs`
- `src/Shoreline.Api/Features/ProofreadFeedbackService.Bake.cs`
- `src/Shoreline.Api/Features/ProofreadFeedbackService.Models.cs`
- `src/Shoreline.Api/Features/ProofreadFeedbackEndpoints.cs`
- `src/Shoreline.Api/Features/ProofreadFeedbackRegistration.cs`
- `src/Shoreline.Api/Features/ProofreadImagingAdapter.cs`
- `src/Shoreline.Api/Features/Proofread/ProofreadMailRenderer.cs`
- `src/Shoreline.Imaging/ProofreadImageProcessor.cs`
- `tests/Shoreline.Tests/ProofreadFeedbackTests.cs`

路由（与 Python 一致）：

| Method | Path |
| --- | --- |
| POST | `/v1/projects/{projectId}/targets/{targetId}/send-proofread-draft` |

`Program.cs` 在 `main` 上已经注册
`AddProofreadFeedbackServices()` / `AddMagickProofreadImageRenderer()` /
`MapProofreadFeedbackEndpoints()`，并在启动时执行
`ProofreadFeedbackRegistration.InitializeAsync`。本文档只声明该切片的行为兼容性，
不声明 SMTP 实际送达、对象存储生产签名或跨平台字体栅格化已经验证。

## 1. 配置开关

- **通知总开关**：`Notifications:Enabled`，回退 `ENABLE_NOTIFICATIONS`，缺省 `false`
  （`NotificationReadService.Enabled`）。
- **站点信息**：`Site:Name` / `SITE_NAME`（缺省 `萌翻`）、
  `Site:Origin` / `SITE_ORIGIN`。
- **邮件**：`Mail:*`；测试环境通过
  `ProtectedNotificationMail.Check` 的 allowlist 限制收件人。
- **信任既有邮箱**：`Notifications:TrustExistingEmails`，回退
  `NOTIFICATION_TRUST_EXISTING_EMAILS`，缺省 `false`。关闭时只有
  `notification_verified_email.digest = sha256(lower(email))` 存在的用户才发邮件，
  否则为 `unverified_email`。
- **团队标记设置**：团队文档 `pme`（`proofread_marker_enabled`，缺省 `true`）
  与 `pmo`（`proofread_marker_opacity`，缺省 `92`，限制在 0–100）。
  两者在寄送瞬间写入 pending，后续团队设置变化不影响已受理草稿。
- **存储**：`Storage:Type`/`Storage:Root` 或 S3 配置。Local 后端生成图片 URL
  需要 `Storage:Domain`；缺失时 `SignGetAsync` 抛错，图片降级为空 URL（邮件不带图）。

Python 的 `data.get("cc_myself", True)` 语义保留：请求体未给 `cc_myself` 时按
`true` 处理。

## 2. 通知开启路径（202 + durable bake）

请求处理顺序与 Python 对齐：

1. `projectId`/`targetId` 必须是 ObjectId，否则
   `TargetNotExistError` / code `4024` / `项目目标语言不存在`。
2. 项目不存在：`ProjectNotExistError` / `4001` / `项目不存在`。
3. target 不存在，或 `target.t`（Python `Target.project` 的 db_field）不是该项目：
   `4024`。**注意：** 早期实现误用了 `target.p`，已修正为 `t`。
4. 需要 `project:PROOFREAD_TRA`，否则 `NoPermissionError` / `103` /
   `您没有校对权限，无法寄送校对稿`，HTTP 403。
5. 请求体必须是 JSON 对象；未知字段或非布尔 `cc_myself` 返回 `ValidateError` /
   code `2`；非法 `file_id` 返回 `FileNotExistError` / `8001`。
6. `file_id` 存在时必须属于本项目图片文件，否则 `8001`。
7. 标签快照在项目锁（label revision）内构建：
   - 图片文件：`p=project`、`t=2`、`ac=true`；
   - 页号按项目全部图片的 `dir_sort_name, sn, _id` 排序后的全局页号计算；
   - 只收录存在修改或批注的页面；
   - 无页面：`ProofreadDraftNoChangesError` / `4025` /
     `当前目标语言没有可寄送的校对修改`。
8. 收件人：项目内 `status=active`、`tags` 含 `translator`、用户存在且未被封禁；
   邮箱 trim + lower + 去重。`cc_myself=true` 且发件邮箱不在收件人列表时追加发件人。
   无翻译成员：`NoTranslatorMemberError` / `4026`。
9. 幂等：`sha256("{actorId}:core:{Idempotency-Key}")` 写入 notification `key`。
   相同键 + 相同 operation（`target_id`/`file_id`/`cc_myself`）返回原通知并重新入队，
   仍为 202；内容不同返回 409 `相同操作键不能用于不同内容`。
10. 发布草稿通知：`event_type=proofread_feedback`、`category=project`、
    `audience={mode: condition, tags:[translator]}`、`email=true`、`draft=true`，
    标题 `「{project}」校对稿 · 正在生成标注图（0/{pages}）`。
    该调用走内部 trusted publish（Python `trusted=True`），不经过
    `can_publish` 的普通发布权限检查。
11. **发件人 receipt**：发布后立即 upsert
    `notification_receipt(notification_id, user_id=actor)`。因此草稿/`preparing`
    阶段发件人能在自己的通知列表看到进行中的任务；其他用户仍不可见。
12. 入队 durable job：`kind=proofread_draft_bake`，payload 含
    `notification_id` / `project_id` / `label_revision`。
13. 202 响应：
    `message=校对反馈已受理，正在后台生成标注图，完成后自动寄出`、
    `notification_id`、`state`、`changed_pages_count`、`changed_labels_count`。

## 3. 通知关闭路径（同步直发，200）

1. 同步生成标注图后直接走受保护邮件发送，不创建 notification/job。
2. 无翻译成员时：`cc_myself=true` 回退为只发给发件人；否则
   `NoTranslatorMemberError` / `4026`。
3. 邮件逐收件人发送；`ProtectedNotificationMail.Check` 失败或 transport 返回非
   `accepted`/`unknown` 时 `NotificationError` / `4600` /
   `校对稿邮件无法发送`，HTTP 502。
4. 200 响应：
   `message=校对稿已成功发送至翻译邮箱`、`recipients`、
   `changed_pages_count`、`changed_labels_count`。

## 4. 后台烤图（bake）语义

`BakeAsync` 只处理 `state=draft` 且未 revoked 的通知；不满足时直接返回
（幂等，不重复渲染/不重复 receipt）。

- 项目 `label_revision` 与 payload/job 不一致：`cancelled` +
  `error=label_revision_stale`。
- 项目已删除：`cancelled` + `error=project_missing`。
- 逐页渲染时更新标题进度
  `「{project}」校对稿 · 正在生成标注图（{done}/{total}）`。
- 最终 payload：`html`、`text`、`reply_to`、`cc_myself`、`operation`。
- **8 MiB 上限**：按 Python `json.dumps(payload, default=str)` 的语义估算字节数
  （`ProofreadFeedbackService.PythonJsonSize`，`MaxPayloadBytes = 8 * 1024 * 1024`）。
  超限：`cancelled`、`error=payload_too_large`、
  标题 `「{project}」校对稿过大，已取消寄送`。
- 草稿 → `preparing` 的翻转是条件更新（`state=draft` 且 `revoked_at=null`），
  失败即说明其他执行者已处理，直接返回。
- 翻转成功后**同步**执行 dispatch + 对 `pending`/`retry_wait` delivery 执行 deliver
  （Python `bake_and_dispatch` 同样内联调用 `dispatch`/`deliver`）。因此一次 bake
  完成后通知通常已是 `published`；无有效收件人时 Python 语义同样是
  `published`（不是 cancelled），只是没有 delivery/mail。
- 发件人权限在 dispatch 阶段丢失：`cancelled` +
  `error=sender_permission_lost`。

### 标记图（与 `proofread_image.py` 对齐）

- 底图优先 `files/resample-{stem}.webp`（仅当项目无 `csid` 时），否则原图
  `files/{save_name}`。
- 仅当 `marker_enabled` 且页面有标签时渲染；失败降级为原图/已有 URL。
- 缓存键 `files/proofread-{fingerprint}-{stem}.webp`；
  fingerprint = `md5(repr([(x, y, position_type, label_num), ...] + [int(opacity)]))[:8]`。
- 目标宽度 `640`（仅当原图更宽时缩放，Lanczos），高度按比例四舍五入。
- 圆点直径 = 图宽 × `0.05`，最小半径 `9`；编号字号 `max(9, radius)`。
- 颜色：框内 `(255,101,124)`、框外 `(255,213,131)`，文字
  `(255,255,255)` / `(102,77,3)`。
- 不透明度：`alpha = round(clamp(opacity,0,100) × 255 / 100)`（银行家舍入）。
  圆点像素按 Pillow 语义**直接写入 RGBA**（包括 alpha），不做
  ImageMagick 的 alpha 合成；因此 `opacity=0` 得到 `A=0`，而不是叠加在白色底图后的
  `A=255`。编号文字仍为不透明。
- `x==0 && y==0` 的标签不绘制。
- 输出 WebP，quality `85`。
- 已知渲染差异：Pillow 与 ImageMagick 的字体/字形栅格化不完全一致。测试像素采样
  避开编号字形覆盖的中心点，只断言圆点底色与 alpha；不声称逐字节一致。

## 5. 错误与状态码

| 场景 | error | code | HTTP |
| --- | --- | --- | --- |
| 非法 JSON 字段 / 类型 | `ValidateError` | 2 | 400 |
| 项目不存在 | `ProjectNotExistError` | 4001 | 400 |
| target 不存在/不属于项目 | `TargetNotExistError` | 4024 | 400 |
| 文件不存在/不属于项目 | `FileNotExistError` | 8001 | 400 |
| 无校对权限 | `NoPermissionError` | 103 | 403 |
| 无修改 | `ProofreadDraftNoChangesError` | 4025 | 400 |
| 无翻译成员 | `NoTranslatorMemberError` | 4026 | 400 |
| 幂等键内容冲突 | `NotificationError` | 4600 | 409 |
| 邮件无法发送 | `NotificationError` | 4600 | 502 |
| 通知启用受理成功 | — | — | 202 |
| 通知关闭直发成功 | — | — | 200 |

## 6. 集合与索引

- 读取/写入：`project`、`target`、`language`、`file`、`source`、`translation`、
  `tip`、`user`、`team`、`project_member`、`notification`、
  `notification_receipt`、`notification_delivery`、`csharp_job`。
- `csharp_job` 增加 `kind/state/available_at` 查询索引
  （`ProofreadFeedbackRegistration.InitializeAsync`）。
- notification 的 `key`/状态/受众与 receipt/delivery 索引由通知模块初始化负责；
  本切片不重复创建。

## 7. 已验证范围

本机隔离副本（mongo/moto/mailpit + 硬编码端口转发）上：

- `dotnet build Shoreline.sln -warnaserror`：0 warning / 0 error。
- `ProofreadFeedbackTests`：**36/36 通过**（修复前 4 通过 / 32 失败）。
- 覆盖：直接 service 校验/权限/页面构建、通知关闭直发（含 cc、封禁、去重）、
  通知开启草稿 + durable job、幂等与冲突、bake 渲染/幂等/revoked/revision 过期/
  取消/收件人移除/权限丢失、8 MiB 上限、fingerprint 与 Python oracle、
  640 缩放与标记颜色/透明度/跳过规则、diff HTML、HTTP 401/400/403/4024/
  200/202/409。

## 8. 未验证 / 已知边界

- 真实 SMTP 投递、退信、重试与邮件服务器限流；测试使用 fake transport 或
  mailpit。
- S3 生产签名/权限；测试主要为 Local 后端（需要 `Storage:Domain`）。
- Pillow 与 ImageMagick 的字体栅格化、WebP 编码字节级差异；只保证尺寸、颜色、
  alpha、fingerprint 与采样像素兼容。
- Python `notification_scan` 作为 bake 崩溃后的兜底恢复路径；C# 依赖
  `JobWorker` 的 lease/retry，未单独验证扫描恢复等价性。
- Python Celery `time_limit=900` 与 C# worker 心跳/租约的极端超时行为。
- 超过 `NOTIFICATION_MAX_AUDIENCE`（默认 20000）的受众分块与多轮 dispatch。
- 多进程并发 bake、revoke、权限变化的所有竞态；只覆盖测试中的顺序场景。
- 收件人 locale/多语言邮件模板；C# 使用站点默认语言。
- 旧 Python pending payload（无 `marker_enabled`/`marker_opacity` 字段）的兼容读取只做了字段缺省回退，未做历史数据回放验证。

## 9. 测试隔离说明

`ProofreadFeedbackTests` 曾因共享 fixture 数据库里的固定 `user.e`/`user.n`、
`team.n`、`language.co` 唯一索引而出现 29 个 E11000。现在测试使用每测试唯一的
邮箱（`+suffix`）、用户名/团队名后缀与唯一 `language.co`；直接 service 用例的
`csharp_job` 写入独立临时数据库，避免共享宿主的 `JobWorker` 与显式
`BakeAsync` 抢同一个 job。这些是测试隔离措施，不改变生产唯一索引与 worker 语义。
