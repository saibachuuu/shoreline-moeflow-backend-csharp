# 可选模块逐路由 parity 审计（Python oracle → C#）

- Oracle：`shoreline-moeflow-backend`（`app/modules/*`，`app/apis/*.py`）
- C#：`src/Shoreline.Api`（`Modules/*`，`Features/*`）
- 状态图例：`parity` = 一致 · `gap-fixed` = 本轮修复 · `fail-closed` = 有意降级/拒绝（不伪造实现）
  · `missing` = C# 缺实现 · `note` = 记录在案、超出本次范围
- 本轮修复见 commits `6a463e6` `bc016eb` `94a06a8`。

## 1. archive_import

路由（`app/modules/archive_import/api.py`）

| Python 路由 | C# 映射 | 状态 |
|---|---|---|
| `POST /v1/projects/<project_id>/import-from-archive`（`ArchiveImportAPI.post`：`api.py:36`；`ProjectFinishedError`(4002, status≠WORKING=0)、`project:ADD_FILE`、`ValidateError`(2, 运行中任务去重)） | `Modules/ArchiveImport/ArchiveImportEndpoints.cs:22`（`st != 0 → 4002`）、`:32,82`（ADD_FILE/ACCESS 103/400）、`ArchiveImportService.cs:563-594`（去重 `ValidateError` code 2） | parity |
| `GET /v1/projects/<project_id>/import-task`（需 `project:ACCESS`） | 同文件取最新任务 | parity |
| `POST /v1/projects/<project_id>/import-task/dismiss`（运行中任务不可 dismiss） | 同文件 dismiss | parity |

字段/行为：

| 检查项 | 结论 |
|---|---|
| 请求体 `gid`（必填、去空后纯数字）、`token`（必填非空）、`gallery_url`（可选，空→`""`，`validators.py`） | parity（`ArchiveImportEndpoints.cs` ASCII 数字检查；`OptionalText→""`） |
| 任务视图字段 `id/project_id/gid/token/gallery_url/status/status_name/stage/total_pages/completed_pages/error/dismissed/create_time/update_time`（`models.py`，无 `zip_url`） | parity（`ArchiveImportView.cs` 同字段；时间 `yyyy-MM-ddTHH:mm:ss.ffffff`） |
| 配置键与默认值：`ARCHIVE_PROVIDER_API_URL`、`ARCHIVE_MAX_ZIP_BYTES`(500 MiB)、`ARCHIVE_MAX_ZIP_UNCOMPRESSED_MB`(2048)、`ARCHIVE_MAX_ZIP_ENTRIES`(5000)、`ARCHIVE_MAX_ENTRY_BYTES`(128 MiB)（`config.py:48-84`） | parity（`ArchiveImportTypes.cs:47-52` 含 MB/bytes 双读） |
| `ARCHIVE_PROVIDER_API_ALLOWED_HOSTS`（逗号分隔、lower、去尾点，`config.py:48`；消费于 `apis/team.py:285`、`tasks.py:293`） | **gap-fixed**：此前 C# 只读 `Archive:AllowedHosts`；新增 `ExternalApiUrl.ReadArchiveAllowedHosts`（`Security/ExternalApiUrl.cs`），`ArchiveImportTypes.cs:70` 与 `TeamService.cs:178` 共用，回退读 legacy env |

## 2. ziteng_partner

路由（`app/modules/ziteng_partner/api.py`）

| Python 路由 | C# 映射 | 状态 |
|---|---|---|
| `GET /v1/projects/<project_id>/ziteng-check`（需 `project:ACCESS`，返回 `{check: to_api\|null}`，`api.py`） | `Modules/ZitengPartner/ZitengPartnerEndpoints.cs` GET | parity（fail-closed 见下） |
| `POST /v1/projects/<project_id>/ziteng-check`（`ensure_check` + `reset_for_retry` 后入队，返回 check） | 同文件 POST（`RequestCheckAsync` 恒入队） | parity（fail-closed 见下） |
| `GET /v1/ziteng-partner/config`（返回 `{enabled, max_results}`，不含密钥） | `ZitengPartnerEndpoints.cs:65-70` 同形状 | parity |
| （无）`GET /v1/config/ziteng-partner` | C# 额外别名路由 | note：只读信息路由，无害 |

字段/行为：

| 检查项 | 结论 |
|---|---|
| 检查视图 `project_id/keyword/status/verdict/suspects/suspect_count/error/updated_at`（`models.py`；status 0-3，verdict clear/suspected/failed） | parity（`ZitengPartnerStore.cs` `CheckSnapshot.ToApi`，时间 `yyyy-MM-ddTHH:mm:ss.ffffff`） |
| 默认配置 `API_URL=https://partner-api.ziteng.org/...`、`MAX_PAGES=3`、`TIMEOUT=20.0`、`MIN_INTERVAL=1.1`、`MAX_RESULTS=5`、`CACHE_TTL=604800`、`TASK_TIMEOUT=180.0`，优先级 env > `config.local.py` > 默认值（`config.py`） | parity（`ZitengPartnerSettings.cs` 全量默认值；`Enabled=!IsNullOrWhiteSpace(ApiKey)`；URL 归一失败回退默认、要求绝对 HTTPS） |
| 无 key 时行为：路由仍注册，检查落 `failed`（`__init__.py` 无 key 也挂路由；worker 无 key 报失败） | parity（C# `RequestCheckAsync` 无 key 也入队，worker fail-closed 报缺 key） |
| `project-created` 钩子异常吞掉 | parity（注释说明） |
| 无权限 HTTP 状态：Python `APIException` 默认 400 | **gap-fixed**（`bc016eb`）：`ZitengPartnerEndpoints.cs:62` 曾显式 `403`，与 Python 及 C# 全库默认 400 不一致，已去掉；注释同步修正 |

## 3. term_bank（含 terms）

路由（`app/apis/term.py`）

| Python 路由 | C# 映射 | 状态 |
|---|---|---|
| `GET /v1/teams/<team_id>/term-banks`（`team:ACCESS`，`word` 过滤，分页 default 30/max 100） | `Features/TermEndpoints.cs` GET list | parity |
| `POST /v1/teams/<team_id>/term-banks`（`team:CREATE_TERM_BANK` 103） | 同上 POST | parity（含「创建术语库」中文消息） |
| `PUT /v1/term-banks/<term_bank_id>`（`CHANGE_TERM_BANK` 或 owner） | PUT bank | parity |
| `DELETE /v1/term-banks/<term_bank_id>`（`DELETE_TERM_BANK` 或 owner，级联删 terms + `project.tb` PULL + `source.pt` PULL） | DELETE bank（`TermService.cs:123-154` 分批 200 游标级联） | parity |
| `GET /v1/term-banks/<term_bank_id>/terms`（`ACCESS_TERM_BANK` 或 owner，`word` 搜 source/target/tip） | GET terms | parity |
| `POST /v1/term-banks/<term_bank_id>/terms`（`CREATE_TERM` 或 bank-owner） | POST term | parity |
| `PUT /v1/terms/<term_id>`（`CHANGE_TERM` 或 bank-owner 或 term-owner） | PUT term | parity |
| `DELETE /v1/terms/<term_id>`（`DELETE_TERM` 或 bank-owner 或 term-owner，`source.pt` PULL + `need_find_terms=True`） | DELETE term | parity |

字段/行为：`name 1-40 rune`、`tip 0-140 rune`、`source/target 1-40 rune`、语言 objectId 校验、同语言 `TargetAndSourceLanguageSameError`(6002)（`validators/term.py`、`models/term.py`）；bank 视图 `id/name/source_language/target_language/tip/user/create_time/edit_time`、term 视图 `id/source/target/tip/user/...`；`unknown=EXCLUDE`。C# `TermService.cs`（权限位 + owner 回退 + 中文消息）、`TermsView.cs` 形状一致 → parity。

## 4. thumbnail

路由（`app/apis/project.py:ProjectThumbnailAPI`，`app/apis/file.py:FileThumbnailAPI`）

| Python 路由 | C# 映射 | 状态 |
|---|---|---|
| `POST /v1/projects/<project_id>/thumbnails`（需 `project:CHANGE`；非本地生成 → `{message, count: 0}`；否则逐 image `create_thumbnail` 计数） | `Features/ThumbnailEndpoints.cs:ProjectThumbnailsAsync`（CHANGE；`LocalGenerationRequired` 为 false → `{message, count: 0}`） | parity |
| `POST /v1/files/<file_id>/thumbnail`（需项目 ACCESS；非图片 → `FileTypeNotSupportError`(108)；非本地生成 → `{message, count: 0}`；否则入队） | `ThumbnailEndpoints.cs:FileThumbnailAsync`（`:43-46` count:0 分支；`t != 2 → 108`，`ThumbnailService.cs:133`） | parity |

`requires_local_thumbnail`（`constants/storage.py`）：非 OSS 看 `STORAGE_TYPE == LOCAL_STORAGE`；OSS 看 `local_thumbnail_generation` 能力（local/S3/R2/MinIO/Garage 有，Aliyun `x-oss-process` 无）。C# `ThumbnailService.LocalGenerationRequired`（`Thumbnails:Mode` 默认 local，仅 `server/server-side/oss` 为 false）→ parity。

## 5. proofread_draft

路由（`app/apis/project.py:432 ProjectSendProofreadDraftAPI`）

| Python 路由 | C# 映射 | 状态 |
|---|---|---|
| `POST /v1/projects/<project_id>/targets/<target_id>/send-proofread-draft`（需 `project:PROOFREAD_TRA`；target 跨项目 → `TargetNotExistError`(4024)；`Idempotency-Key` 202 重放、操作不一致 409；无变更 → 4030；无译者 → 4031；通知开 → 202 `{message,notification_id,state,...}`，通知关 → 200 `{message,recipients,...}`） | `Features/ProofreadFeedbackEndpoints.cs` + `ProofreadFeedbackService.cs`（幂等/direct 双路径、202/200/409 语义一致） | **gap-fixed**（下表） |

| 检查项 | 结论 |
|---|---|
| `ProofreadDraftNoChangesError` 编码 | **gap-fixed**（`6a463e6`）：C# 曾用 `4025`（= `ProjectHasDeletePlanError`），改为 Python 的 **4030**（`ProofreadFeedbackService.cs:142`） |
| `NoTranslatorMemberError` 编码（3 处：直接路径/通知路径/发布无接收人） | **gap-fixed**（`6a463e6`）：C# 曾用 `4026`（= `ProjectHasFinishPlanError`），改为 Python 的 **4031**（`:174,205,246`） |
| 无权限 HTTP 状态 | **gap-fixed**（`bc016eb`）：`:91` 曾显式 `403`，改为默认 400（Python `APIException` 默认 400） |
| 请求体 `cc_myself` bool 缺省 true、`file_id` 可空 objectId（`validators`） | parity（`ParseBody`: 非对象/未知字段/类型错 → code 2；C# 对未知字段略严于 Python `EXCLUDE`，既有测试锁定该行为，保持） |

## 6. insight

路由（`app/apis/team.py:654/674/715/741`）

| Python 路由 | C# 映射 | 状态 |
|---|---|---|
| `GET /v1/teams/<team_id>/insight/users`（需 `team:INSIGHT`；`MoePagination(max_limit=10)` → 实际 default 10/max 10；嵌套 projects 默认 limit 5；count=全员） | `Features/TeamInsightEndpoints.cs`（`Page(context,10)`）+ `TeamInsightService.cs`（`RequireInsightAsync` 103，嵌套 `Take(5)`） | parity |
| `GET .../insight/users/<user_id>/projects`（INSIGHT；成员不存在 → `UserNotExistError`；默认分页 30/100） | 同上（`Page(context,100)`） | parity |
| `GET .../insight/projects`（INSIGHT；`word`；max 10；嵌套 users limit 5；仅当 `AUTO_BECOME_PROJECT_ADMIN` 输出 outputs） | 同上（outputs 门控一致） | parity |
| `GET .../insight/projects/<project_id>/users`（INSIGHT；`project.team != team → ProjectNotExistError`；默认分页） | 同上 | parity |

附：`member_summary(include_permissions=False)`、`external=True` 成员名回退、C# avatar 签名 URL（全库约定）→ parity/note。

## 7. presence

路由（`app/apis/presence.py`、`app/apis/team.py:TeamProjectsActivePresenceAPI`）

| Python 路由 | C# 映射 | 状态 |
|---|---|---|
| `POST /v1/projects/<project_id>/presence/heartbeat`（`action` 缺省 `working`，非 str→`working`，截断 64 字符；返回 `message/project_id/active_users`） | `Features/PresenceEndpoints.cs` + `PresenceService.cs`（`NormalizeAction` rune-截断 64；读路径空 action 显示 `working` 与 Python 一致） | parity |
| `POST /v1/projects/<project_id>/presence/leave`（返回 `message/project_id`） | 同上 | parity |
| `GET /v1/projects/<project_id>/presence`（返回 `project_id/active_users`） | 同上 | parity |
| `GET /v1/teams/<team_id>/projects/active-presence`（返回 `{active_projects}`；`user_count`） | 同上（含 team 路由） | parity |

`PRESENCE_TIMEOUT_SECONDS=45`（`models/presence.py`）、`active_users` 项 `id/name/avatar/action/last_heartbeat`（member `display_name` 优先）、team 变体按 `project_id` 分组。Python 端 presence 路由无成员/ACCESS 门控（仅 token + 404），C# 同样无门控（注释标明）→ parity。C# avatar 用签名 URL 系全库约定，仅 note。

## 8. ocr

| Python 路由 | C# 映射 | 状态 |
|---|---|---|
| `POST /v1/files/<file_id>/ocr`（`USE_OCR_QUOTA`、`g_ocr_code`、图片类型、剩余额度；`tasks/ocr.py:422 merge_and_ocr` 直接 `raise NotImplementedError`「OCR 正在重构」） | `Features/OcrEndpoints.cs`：token + 存在性检查后直接 501 `OcrUnavailableError`(5010)（跳过权限/配额门控） | **fail-closed**（有意） |
| `POST /v1/projects/<project_id>/ocr`（同上门控 + `ocring` 标志 + 仅 `NOT_START/PARSE_FAILED` 图片 + 配额） | 同上 | **fail-closed**（有意） |

**待决策**：Python 真实 OCR 本体重构中（不可运行），C# 以 501 明确拒绝而非伪造结果；配额扣减/任务派发语义待 OCR 方案确定后再对齐，不在本轮实现。

## 9. mit

| Python 路由 | C# 映射 | 状态 |
|---|---|---|
| `POST /v1/mit/images`、`POST /v1/mit/image-tasks`、`GET /v1/mit/image-tasks/<task_id>`、`POST /v1/mit/translate-tasks`、`GET /v1/mit/translate-tasks/<task_id>`（`mit` blueprint 仅当 `MIT_STORAGE_ROOT` 配置时挂载；**无鉴权**；默认 `detector_key=default/detect_size=2560/text_threshold=0.5/box_threshold=0.7/unclip_ratio=2.3/ocr_key=48px`） | `Features/MitEndpoints.cs:274-287` 默认值逐项一致；同样无鉴权；`Program.cs` 仅当 `Mit:StorageRoot` 配置 **且** `FindMissingConsumers` 为空才挂载路由，否则路由不存在 + fail-closed 日志 | **fail-closed**（有意） |

Python `manga_image_translator.py` 内部 `_run` 引用 `gpu`/`device=cuda` 仅属本地推理路径，不在 API 路径语义内。**待决策**：真实翻译执行（worker/consumer）双方均无可用实现，C# 不伪造；执行语义待产品决策。

## 跨模块记录（超出本次范围，仅 note）

- `FileNotExistError` 在 Python 有**两个**定义，不是漂移：`app/exceptions/file.py:16` 是 **8001**（“文件/文件夹不存在，可能已被删除”，`app/exceptions/__init__.py` 最后导出，通用文件 API 用它）；`app/exceptions/project.py:56` 是 **4006**（“文件不存在”，只有 `app/apis/project.py:450` 的 send-proofread-draft 显式 import）。C# 通用路径的 8001 正确；send-proofread-draft 曾误用 8001，已改为 4006（`ProofreadFeedbackService.cs:98,365`，`ProofreadFeedbackTests.Send_RejectsForeignFile`）。
- `NoPermissionError` 消息文本：Python 各模块多为具体中文消息，C# term/presence 等已对齐具体消息，其余为通用「没有权限」——code 一致，前端按 code 判断不受影响。
- `docs/env-compat-audit-2026-11-11.md` 与本结论一致：`ARCHIVE_*` legacy env 为兼容读取项（本轮补齐 allowlist 一项）。

## 统计

| 模块 | parity | gap-fixed | fail-closed | missing |
|---|---|---|---|---|
| archive_import | 3 | 1 | 0 | 0 |
| ziteng_partner | 3 | 1 | 0 | 0 |
| term_bank | 8 | 0 | 0 | 0 |
| thumbnail | 2 | 0 | 0 | 0 |
| proofread_draft | 1 | 3 | 0 | 0 |
| insight | 4 | 0 | 0 | 0 |
| presence | 4 | 0 | 0 | 0 |
| ocr | 0 | 0 | 2 | 0 |
| mit | 0 | 0 | 5 | 0 |
| **合计** | **25** | **5** | **7** | **0** |

（计数口径：路由条目；proofread 的 3 个 gap-fixed 为同一路由下的 3 类编码/状态修正。）

## 待决策清单

1. **OCR 真实执行**：Python `merge_and_ocr` 重构中不可运行；C# 501 fail-closed。待 OCR 方案确定后对齐配额/任务语义。原因：无可对齐的真实行为，伪造即造假。
2. **MIT 真实执行**：双方均无可用 worker；C# 路由门控 fail-closed。待产品决策执行方案。原因：同上。
3. ~~**`FileNotExistError` 8001 vs 4006**~~（已关闭）：两个错误类在 Python 中并存，见上文；只有 send-proofread-draft 需 4006，已修。
