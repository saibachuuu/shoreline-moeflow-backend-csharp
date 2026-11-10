# 文件域兼容说明（D1 切片）

本文记录 C# 文件域实现与 Python 后端（`app/apis/file.py`、`app/models/file.py`、`app/models/project.py:640-871`、`app/services/file_order.py`）的兼容边界。除明确列出的差异外，行为、错误码、错误文案、BSON 字段与 Python 保持一致。

## 1. 覆盖范围

已实现；服务层测试、Python golden 与真实 HTTP 测试均已在隔离快照通过（见第 11 节）：

- `GET /v1/projects/{projectId}/files` 列表、分页、过滤、排序、target 缓存
- `POST /v1/projects/{projectId}/files` multipart 上传（新建/覆盖/修订）
- `GET /v1/files/{fileId}` 详情、邻图、target 缓存
- `PUT /v1/files/{fileId}` 改名/移动
- `DELETE /v1/files/{fileId}` 删除（文件/文件夹、修订版、关联数据）
- `GET/PUT /v1/projects/{projectId}/files/order` 手动排序与快照
- `GET /v1/admin/files` 管理员列表
- `POST /v1/files/{fileId}/ocr` 显式 fail-closed（501，见第 12 节）
- `POST /v1/projects/{projectId}/ocr` 同一 OCR 禁用语义（501，见第 12 节）

未实现（明确不做假成功）：

- 缩略图生成、封面/重采样生成（`FileThumbnailAPI`、`File.parseOCR` 仍保持未接线）
- 非默认对象存储池的写入/删除路由；当前对 `project.csid` 非空采取 fail-closed
- 文件审校图片（proofread）生命周期，删除时按 Python 语义保留

## 2. 数据模型

集合 `file` 保留旧 BSON 字段名：

`n / t / p / f / a / mo / sn / dn / sa / md / fs / cover_bytes / resample_bytes / fn / th / the / tht / fo / fc / sc / tsc / csc / et / ov / v / ac / ss / sti / sri / sst / ps / pt / pti / pst / pe / op / ft / ftt / fts / sm / label_epoch`

- `label_epoch` 由 E1 项目标签锁写入；新文件/文件夹/修订版取当前 `LabelSession.Revision`。旧数据缺省按 0 处理。

- `fs` 是 KB：`ceil(bytes / 1024)`（Python `math.ceil`）。
- `cover_bytes` / `resample_bytes` 是字节，且没有缩短字段名。
- `mo` 为 `null` 或从 1 开始的手动序号；`sn` 为数字分段补零到 6 位的排序名；`dn` 是父目录路径前缀。
- `file_target_cache` 字段为 `f / t / ts / cs / e`，每个文件每个 target 一行。`ts/cs` 变更时同一 `$update` 内刷新 `e`；项目/文件/团队编辑时间分别为 `m_e / et / m_e`。

索引（`FileService.InitializeAsync`）：

- `(ac, n, f, p)`
- `(t, sn)`
- `(t, -sn)`
- `(dn, t, sn)`
- `(dn, t, -sn)`
- 命名 `file_manual_order_v1`：`(p, ac, f, dn, t, mo, sn, _id)`
- 命名 `file_admin_edit_time_v1`：`(-et)`
- 命名 `file_list_v2`：`(p, ac, f, dn, t, sn)`
- `file_target_cache`: `(f, t)`

没有全局唯一文件名索引。重名由 `(p, f, name iexact)` 的业务校验 + 项目级锁保证。

## 3. 权限与项目状态

- 列表/详情：项目 `ACCESS` 或团队 `ACCESS`；否则 103 `您没有此项目的访问权限`。
- 列表/上传：项目必须处于 `WORKING`（`st == 0`），否则 4002 `项目已完结`。
- 上传：`project:ADD_FILE`，否则 103 `您没有此项目的上传文件权限`。
- 移动：`project:MOVE_FILE`；改名：`project:RENAME_FILE`；删除：`project:DELETE_FILE`。
- 排序（GET/PUT）：比普通权限更严格。必须是项目 `WORKING`、拥有 `project:ACCESS`，且权限来源包含以下之一：
  - `team_inherited:creator`
  - `team_inherited:admin`
  - `project_tag:creator`
  - `project_tag:admin`

  “项目管理员/团队成员”这类泛化权限不足，Python 与 C# 均如此。

## 4. 文件名

校验顺序与文案严格对齐 Python：

- 去掉空格后为空 → `文件名为空`
- Unicode 长度 > 127 → `文件名过长`
- 去空格后为 `.` 或 `..` → `文件名不能是 . 或 ..`
- 去空格后以 `.` 结尾 → `文件名不能以 . 结尾`
- 最后一个 `.` 之前去空格为空 → `文件名前缀为空`
- 包含 `\ / : * ? " < > |` → `文件名不能包含下列任何字符: \ / : * ? " < > |`

错误统一为 `FilenameIllegalError` / 107 / `文件名错误: <detail>`。

后缀不区分大小写：`jpg/jpeg/png/bmp/gif/webp` → IMAGE，`txt` → TEXT。`sort_name` 使用 `\d+|\D+` 分段，纯数字段左侧补零到 6 位。

## 5. 上传与覆盖

- 上传前按 `(project, parent, filename iexact)` 查找旧文件，`upload_overwrite` 以查找结果为准。
- 同名 IMAGE：复用同一个文件 ID，只替换对象与 `sa/md/fs/fn`，旧对象（原图、cover、resample）在数据库持久化成功后尽力删除。
- **阶段保护**：新对象上传成功、`sa/md/fn` 元数据已提交后，若后续计数/`edit_time` 更新失败，绝不删除新对象（Mongo 更新可能已提交但驱动报错）；只有确定尚未发生元数据写入的失败才清理新对象。测试通过 `AfterMetadataWriteForTests` 注入“元数据成功后失败”验证对象仍存在。
- 新文件：先创建文件行（`fn = NOT_UPLOAD`），再写对象；失败时保留空文件行，与 Python 的 `create_file` + `upload_real_file` 顺序一致。
- TEXT 在生产默认不允许（`SUPPORTED = (IMAGE,)`）。为测试保留显式开关 `File:AllowText=true`，默认关闭，不会静默放开。
- 文件流写入 task 临时目录 `Path.GetTempPath()/opencode`，同步计算 MD5（小写十六进制）与长度；临时文件最终必删。
- 大小上限 `Upload:MaxBytes`，默认 1 GiB（对应 Python `MAX_CONTENT_LENGTH_MB=1024`）；超限 413。
- `save_name = ObjectId + "." + suffix`；对象 key = `files/<save_name>`。`files/` 是文件域逻辑前缀；全局存储前缀只由 `ObjectStorage` 内部加一次，文件域不再重复添加。

## 6. 列表与分页

- `parent_id` 缺省为根目录；`only_folder` 与 `only_file` 同时为真 → 2 `can not use \`only_file\` and \`only_folder\` at one time`。
- `word` 使用大小写不敏感的转义子串匹配。
- `order_by` 仅允许旧索引/字段白名单，非法字段 → 2 `不支持使用 ... 排序`。
- 默认排序：`dn, t, sn`；当前目录存在任意 `mo != null` 时：`dn, t, mo, sn, _id`。
- `target` 必须是本项目 target，否则 4024；缺少缓存行时返回 `{"id":null,"translated_source_count":0,"checked_source_count":0}`，只有传了 `target` 才输出 `file_target_cache` 键。
- 响应体是 JSON 数组，分页信息在 `X-Pagination-Page/Limit/Count/Page-Count` 响应头。

**分页（待主整合）**：文件路由只调用共享 `Pagination.From(HttpRequest)`，**没有**在文件端点另写 hardcode 30 的解析器。共享 `Infrastructure/Pagination.cs` 已出现配置化稳定版本（SHA256 `7DFC5FB6DAAD1FA3524084491FAEAF0D0AC0025849794464BFF0FA543812EA32`），默认 `Pagination:DefaultLimit=30`、`Pagination:MaxLimit=100`，与 Python APIKit 部署默认值一致；本切片快照复制该版本验证 `X-Pagination-Limit=30`。该共享文件尚由其他子任务持有、未提交，主整合时需确认其最终版本。

## 7. 改名/移动/删除

改名：

- 未激活修订版 → 4016；文件夹改名走 `folder` 模式。
- 同名同父级且为自己 → no-op；其他同名 → 4007；后缀类型变化 → 8003。
- 文件夹改名批量重写后代 `dn` 前缀，游标分批处理。

移动：

- 目标不存在/非文件夹 → 8002；移到自己 → 4012；移到自己的子目录 → 4014；与原父级相同 → 4013；目标目录重名 → 4007。
- 目标目录存在手动排序时，移动到末尾（`max(mo)+1`），否则 `mo = null`。
- 文件夹移动会同步后代 `a`、`dn`，并维护新旧父级的 `fc/fo/sc/fs` 与各 target 的 `tsc/csc`。
- 项目级 `sc/fs/tsc/csc` 在移动中不变（`update_project=False`）；与 Python 一致，移动与改名都不更新 `edit_time`。

删除：

- 文件夹递归删除所有后代（含修订版）及其对象。
- 文件删除自己的修订版、`file_target_cache`、`source`、`translation`、`tip`（按 `source` ID 关联）。级联按 200 条游标批处理，不会对单个文件的全部 source 构造巨型 `$in`。
- 删除对象：原图 + `cover-<stem>.webp` + `resample-<stem>.webp`；主删除使用请求取消令牌，旧对象在持久化后的“尽力清理”使用独立 5 秒超时，避免请求已断开时无限挂起。
- `proofread/` 前缀对象永不删除（长期邮件链接）。
- 项目计数、父级计数、各 target 计数按 Python `clear()` 扣减。
- `project.csid` 非空时删除/上传/覆盖直接 409 fail-closed，避免误删默认后端对象。完整 storage router 接入后需再放开。

## 8. 文件顺序

- 快照仅根目录、激活的 IMAGE。
- 版本号：`sha256(UTF8(json.dumps([[str(id), name, manual_order], ...], ensure_ascii=False)))`，Python 默认分隔符 `", "` 与 `": "`，`null` 保留为 JSON null。跨语言 golden vector：

```json
[["651111111111111111111111", "001.png", null], ["652222222222222222222222", "作品 2.jpg", 3]]
```

SHA-256：`141c271429d8e7a8aaef0ed105ff0e8cc0eeabad3b933520f9ab8ab50c554306`

- PUT 必须提交当前目录全部图片 ID，严格唯一集合；数量/重复/集合不符 → `必须提供当前目录全部图片，且不可重复`。
- 版本不符 → `文件列表或顺序已变化，请取消编辑后重新加载`。
- `reset_to_default=true`：清除 `mo` 后立即返回，不会重新启用排序。
- 提交顺序与当前顺序完全相同：no-op，不写入 `mo`（避免把“默认排序”误变成手动排序）。
- 否则按提交顺序 bulk 写入 `mo = 1..n`。
- 旧 `project.file_order_lock`（BSON `fol`）5 分钟 lease 保留：仅当为 `null` 或已过期时抢锁；结束时用 CAS 仅释放自己的锁。
- 所有会修改文件/排序/关联计数的入口（上传、改名、移动、删除、排序、新增文件夹）先进入 E1 `ProjectLabelLock`，再进入 `FileMutationGate`；顺序固定为 **label → file gate**，避免与 E1 恢复/清理任务反向加锁。HTTP 中间件已持锁时通过同一个 scoped `LabelRequestContext` 借用租约，不重复抢数据库锁；直接服务调用也会获取/释放自己的租约。
- 进程内并发：每个项目独立 `FileMutationGate`（与邀请/团队锁分离），上传、排序、改名、移动、删除都串行化。

## 9. URL 与存储

- 原图 URL：`files/<save_name>` 签名（默认 1 小时）。
- 封面始终走默认后端；重采样跟随项目存储指针，缺省回退默认后端。
- 封面/重采样一律先 `HeadAsync` 探测派生对象：存在才签名，不存在（含覆盖后旧派生对象已删、缩略图尚未重生成）返回 `"generating"`，绝不伪造成功或伪造 URL。
- 当前 `project.csid` 非空时原图 URL 返回空串、重采样返回 `"generating"`，属于 fail-closed 的兼容占位。

## 10. 已知差异与待接线

1. **Program.cs 未接线**（本切片禁止修改）。需要主代理在 `Program.cs`：
   - `builder.Services.AddFileServices();`
   - `app.MapFileEndpoints();`
   - 启动初始化处 `await app.Services.InitializeFileIndexesAsync();`
2. **管理员列表**：已与 Python 对齐——任意一行未激活修订版都会使整个请求返回 4016 `此修订版未激活`；测试 `AdminList_InactiveRevisionReturns4016` 覆盖。
3. **分页默认值**：待共享 `Pagination.cs` 配置化，详见第 6 节；文件域不维护第二套默认值。
4. **存储池**：非默认 backend 的写入、删除、封面/重采样跨后端删除尚未实现；当前 fail-closed。
5. **缩略图/OCR**：缩略图任务由 G4 `ThumbnailService` 实现；D1 上传现已按
   Python `app/models/file.py:867-891` 自动调用 `IThumbnailScheduler`（见第 13 节）。
   覆盖上传会删除旧封面/重采样对象，`cover_bytes/resample_bytes` 在任务完成前
   仍保留旧值（与 Python 相同，由随后的缩略图任务刷新）。OCR 仍显式 501。
6. **TEXT 上传**：仅 `File:AllowText=true` 时开放，生产默认关闭；默认生产行为与 Python `SUPPORTED=(IMAGE,)` 一致。
7. **新增文件夹**：`FileService.CreateFolderAsync(project, name, parentId, ct)` 已按 Python 语义实现（重名 4007、folder_count、target 缓存、`label_epoch`），但本切片没有注册创建文件夹 HTTP 路由；后续接路由时无需再改服务层。

## 11. 隔离验收证据

- 快照：`C:\Users\88888888\AppData\Local\Temp\opencode\d1-snapshot-20261109`，基于提交 `c70c67b31de9a786139d41075c5fdf6c1f362a32`。
- 构建：`dotnet build src\Shoreline.Api\Shoreline.Api.csproj -c Release` → 0 警告、0 错误。
- 文件域测试：`dotnet test ... --filter "FileTests|FileGoldenTests|FileHttpTests"` → 40/40 通过（33 服务层 + 2 Python golden + 5 HTTP）。
- 快照全量测试：`dotnet test ...` → 353/353 通过。
- Python golden：`Fixtures/file-goldens.json` 由实际 Python 源码 AST 执行生成（`Filename` 16 例、`order_snapshot` 4 例），未手工改写期望值；`FileGoldenTests` 通过 `CallerFilePath` 读取同一 fixture。
- 测试依赖：本地 MongoDB `127.0.0.1:27128`、S3 兼容存储 `127.0.0.1:29128`（bucket `shoreline-test`）；dotnet SDK 10.0.204。
- HTTP 覆盖：无令牌 1003、项目 4001、文件 8001、schema 2、无权限 103、外来 parent 8002、外来 target 4024、multipart 上传→详情→覆盖同 ID→删除、`X-Label-Revision` 正确/过期、恢复中 409、HTTP 中间件持锁时服务层借用租约不死锁。

## 12. OCR 路由：显式 fail-closed

Python 的 OCR 管线处于重建状态：`app/tasks/ocr.py:421` 直接
`raise NotImplementedError("OCR under reconstruction")`，因此
`FileOCRAPI`（`app/apis/file.py:304`）与 `ProjectOCRAPI`
（`app/apis/project.py:317`）即使通过权限/额度校验，也无法产生真实结果。
单文件路径更早在 `File.parse` 就返回字符串 `"单个图片OCR暂未实现..."`
（`app/models/file.py:1116`），不会排入任务。

C# 不伪造能力，两个路由统一显式禁用：

- `POST /v1/files/{fileId}/ocr`
- `POST /v1/projects/{projectId}/ocr`

行为契约：

- 无令牌 → `NeedTokenError` / 1003 / HTTP 401（沿用全局认证）。
- 文件或项目 id 不存在 → `FileNotExistError` / 8001 或
  `ProjectNotExistError` / 4001，与既有资源路由一致。
- 其他情况（包括本可有权限/额度的调用）→
  `OcrUnavailableError` / 5010 / HTTP 501，文案
  `OCR 功能暂不可用（后端重建中）`。
- 不写 `parse_status`/任务行，不修改项目 `ocring`，不返回成功消息；测试
  `OcrHttpTests.OcrRoutes_ReturnExplicit501AndDoNotMutateState` 断言了这些禁用语义。
- 权限与 OCR 额度检查刻意不执行：功能整体不可用，先鉴权并解析资源，再统一
  501，避免对不同角色返回“可用但稍后失败”的假象。真实 OCR 恢复后需要重新
  接入 Python 的 `USE_OCR_QUOTA` / 源语言 `g_ocr_code` / 额度语义。
- 快照的 E1 文件（`ProjectLabelLock`、`LabelRequestGuard`、`LabelRegistration`、`LabelEndpoints`、`LabelService*`、`LabelPlusReader`）按 SHA256 固定复制，未修改；`Infrastructure/Pagination.cs` 复制共享配置化稳定版 SHA256 `7DFC5FB6DAAD1FA3524084491FAEAF0D0AC0025849794464BFF0FA543812EA32`；共享工作区 `Program.cs` 未改。依赖 E1 的 `file_target_cache (f,t)` 索引已存在（E1 命名为 `label_file_target_cache`），`FileService.EnsureCacheIndexAsync` 检测到同键索引后跳过创建。

## 13. 文本解析 job 与上传后缩略图调度（2026-11-09 补齐）

### 13.1 文本解析 / 寻找术语

Python 对照：

- `app/tasks/file_parse.py:23-106` `parse_text_task(file_id, old_revision_id=None)`；
  `:109-116` `parse_text(..., run_sync=...)`。
- `app/tasks/file_parse.py:119-156` `find_terms_task(file_id)`；
  `:159-166` `find_terms(..., run_sync=...)`。
- `app/models/file.py:1105-1145` `File.parse()`：TEXT 先
  `clear_all_sources()`、写 `QUEUING`、取 `old_revision`、入队并记录
  `parse_task_id`。
- `app/models/file.py:1172-1184` `_create_text_source`：`content.strip()==""`
  时 `blank=True` 且不增加 `source_count`；`:1220-1234` `clear_all_sources`
  按 file/project/target 缓存扣减；`:1495-1549` `Source.copy` 克隆旧修订版的
  translation/tip 并增加 `translated_source_count` / `checked_source_count`。
- `app/models/file.py:1485-1493` `Source.find_terms()`；`app/models/project.py:575-592`
  项目级 `find_terms` 批量入队。

C# 实现（新增，未改其他业务模块）：

| 文件 | 作用 |
| --- | --- |
| `src/Shoreline.Api/Features/FileParseService.cs` | 两个 task body + `Schedule*` / `parse_text(..., run_sync)` 等价入口 |
| `src/Shoreline.Api/Jobs/FileParseJobHandlers.cs` | `ParseTextJobHandler`（kind `parse_text`）、`FindTermsJobHandler`（kind `find_terms`） |
| `src/Shoreline.Api/Features/FileParseRegistration.cs` | `AddFileParseServices()` |
| `tests/Shoreline.Tests/FileParseTests.cs` | direct 成功/失败/旧修订版/队列恢复/术语匹配与恢复 |

语义：

- `parse_text` 跳过不存在/非 TEXT/已完成/其他 run 正在解析的文件；成功写
  `ps=4` 并 unset `pt/pti/pst`。下载失败写 `ps=3, pe=2, pt+=1`；字符集失败写
  `ps=3, pe=1, pt+=1`，与 Python 的 `FILE_CAN_NOT_READ` /
  `TEXT_UNKNOWN_CHARSET` 对齐。
- 换行按 Python `re.split(r"\n|\r\n|\r", text)`；空白行也创建 source，
  `r` 为 0-based rank，`cm=true`，非空白行才增加 `sc`。
- `old_revision_id` 只接受 TEXT 文件；同一内容行拷贝 translation/tip、选中的
  translation 计 `checked`，与 `Source.copy` 一致。
- Python 用 chardet；C# 无 chardet 依赖，按 BOM → strict UTF-8 → GB18030 →
  Windows-1252 的顺序解码。GB18030/1252 需要运行时的 CodePages provider；
  不可解码的 UTF-32 等仍映射为 `TEXT_UNKNOWN_CHARSET`（见第 13.3 节）。
- `parse_text` / `find_terms` 的 Celery `time_limit=1200` 在 C# 没有硬超时，
  由共享 `JobWorker` 的 2 分钟 lease + 30 秒续租 + 5 次 attempt 上限近似。
- 恢复：queue 重投的 payload 带固定 `run_id`；文件上的 `parse_run` /
  `find_terms_run` 只在同一 run 时允许从 `PARSING` / `FINDING` 继续，
  避免 Python “正在处理则跳过”在 C# 重投场景把状态永久卡住。
- `run_sync=true` 走进程内直接执行（测试/管理路径），不写 `csharp_job`；
  `run_sync=false` 只持久化 job 并在 `ScheduleParseTextAsync` 里先清空旧
  source、再写 `QUEUING` 和 `parse_task_id`（与 `File.parse` 顺序一致）。
- 尚未接入：没有对应的 C# HTTP parse 路由；本轮只交付 task/handler 与
  direct/run_sync 调用面。项目级“改术语库后批量 find_terms”同样未接路由。

### 13.2 上传后自动调度缩略图

Python 上传路径在 `app/models/file.py:867-891` **自动**调用
`create_thumbnail(str(self.id))`（`app/tasks/thumbnail.py:330-360`）：先写
`thumbnail_status=QUEUING`，`run_sync`/`_FORCE_SYNC_TASK` 或
`THUMBNAIL_SYNC_FALLBACK` 且无 worker 时同步执行，否则 `.delay()`；
入队失败写 `FAILED` 并重新抛出，由上传层兜底 inline 生成。

结论：是自动入队，不是仅手动 `POST /v1/files/<id>/thumbnail`。

C# 改动：`FileService.UploadAsync` 在对象与元数据持久化、计数/`edit_time` 更新
和旧对象清理之后，对 IMAGE 调用 `IThumbnailScheduler.ScheduleAsync(...)`
（`IThumbnailScheduler` 由 `ThumbnailRegistration` 注册到同一个 scoped
`ThumbnailService`）。默认开启，与 Python 默认行为一致；server-side/Alibaba
模式由 `ThumbnailService.LocalGenerationRequired` 内部 no-op，无需额外开关。
调度失败只记 warning，不让已成功的上传失败，也不伪造 `QUEUING`；这与 Python
“入队失败再尝试 inline”不同：C# 没有 worker 时没有独立的 inline Pillow 路径，
保留而非伪造状态，URL 仍按存储探测回退。

覆盖：`tests/Shoreline.Tests/FileUploadThumbnailHookTests.cs` 验证
IMAGE 上传产生 `create_thumbnail` pending job + `th=1`，以及 scheduler 抛错时
上传仍成功且 `th` 保持原值。

### 13.3 CodePages 运行时注意

`DecodeText` 的 GB18030 / Windows-1252 分支依赖
`System.Text.Encoding.CodePages`。该程序集属于 .NET shared framework 时可直接
`Encoding.GetEncoding(54936/1252)`；若部署裁剪（trimming）移除 provider，
需要显式 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 并在
项目文件保留对应引用。未注册时异常会落到 `TEXT_UNKNOWN_CHARSET`，不会崩溃。
