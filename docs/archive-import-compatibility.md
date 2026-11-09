# 归档导入兼容说明（Archive Import）

> 状态：模块已实现并通过隔离副本 focused 测试（2026-11-09）。本文件只描述当前实现与 Python 参考的对应关系；未 commit。
> Python 参考（只读）：`app/modules/archive_import/{api,models,tasks,constants,validators}.py`。

## 1. 范围与文件

C# 实现位于 `src/Shoreline.Api/Modules/ArchiveImport/`：

| 文件 | 职责 |
|------|------|
| `ArchiveImportEndpoints.cs` | 三条 HTTP 路由、参数校验、权限检查 |
| `ArchiveImportService.cs` | 创建任务、worker 主流程、claim 续租/接管、失败收敛 |
| `ArchiveImportTaskStore.cs` | `archive_import_task` 写入/状态 CAS；`archive_import_active` claim 存储 |
| `ArchiveImportTransport.cs` | provider `POST /api/v1/parse` 解析、https 下载、公网地址连接守卫 |
| `ArchiveZipArchive.cs` / `ArchiveCrc32.cs` | zip 结构/CRC/大小上限校验、条目枚举 |
| `ArchiveImportUploader.cs` | 通过 D1 `FileService.UploadAsync` 上传；文件名合法性与 D1 契约一致的回退解析 |
| `ArchiveImportView.cs` | 前端公开字段映射 |
| `ArchiveImportTypes.cs` | 状态/文案/配置/异常/BSON 辅助 |
| `ArchiveImportRegistration.cs` | `AddArchiveImportServices()`、`MapArchiveImports()`、索引初始化 |

后台入口为 `Jobs/ArchiveImportJobHandler.cs`；共享 `Program.cs` 的注册/映射/初始化由接线任务完成。

## 2. HTTP 契约

| 方法 | 路径 | Python 对照 | 说明 |
|------|------|-------------|------|
| POST | `/v1/projects/{projectId}/import-from-archive` | `api.py:18-70` | 创建任务；要求 `project:ADD_FILE`；项目必须 `st == 0` |
| GET | `/v1/projects/{projectId}/import-task` | `api.py:73-90` | 返回最新任务（按 `-_id`），可为 `null` |
| POST | `/v1/projects/{projectId}/import-task/dismiss` | `api.py:92-120` | 把所有非运行中任务的 `d` 置为 `true`；运行中不受影响 |

请求字段：`gid`（必填、纯 ASCII 数字）、`token`（必填）、`gallery_url`（可选）。错误文案沿用 Python：
`gid 不能为空`、`token 不能为空`、`归档导入任务已加入队列`、`您没有此项目的上传文件权限` 等。

GET/POST 的公开 task 字段顺序与 `ArchiveImportView` 一致：`id, project_id, gid, token, gallery_url, status,
status_name, stage, total_pages, completed_pages, error, dismissed, create_time, update_time`；`zip_url`/`api_key`
永不返回。

## 3. BSON 数据模型

`archive_import_task`（db_field 与 Python `models.py` 一致）：

`_id, p, u, g, t, gu, s, st, n, c, e, zu, d, ct, ut`

C# 为断点续跑和 fencing 额外写入：

- `lr`：创建时项目 `label_revision` 快照；
- `lo` / `lu`：当前 worker owner token / lease 截止时间；
- `pi` / `ic`：已处理条目下标 / 已成功导入数量（checkpoint）。

额外集合 `archive_import_active`：每个项目一条 claim（`_id = projectId`，含 `task_id, owner, lease_until,
reserved_until, created_at, updated_at`）。Python 没有该集合；它替代 Celery 的“同一 queued task 只能被一个
worker claim”语义，并允许同一项目存在多条历史 running 记录（因此不能直接在 `archive_import_task` 上建
partial unique index）。

状态值与 Python `constants.py` 完全一致：0 排队中、1 解析归档链接、2 下载归档中、3 校验归档中、4 导入图片中、
5 已完成、6 导入失败。`IsRunning` 覆盖 0–4。

## 4. 执行语义

1. **创建**：先抢占 `archive_import_active`，再插入任务，再入队 job（payload `project, taskid,
   label_revision`）。入队失败会把任务置为 6/`任务入队失败` 并释放 claim。
2. **领取**：worker 以 `taskid`（或旧 payload 的 `gid/token`）定位任务；claim 允许接管过期 lease，
   随后把任务行的 `lo/lu` 绑定到新 owner（`TryBindOwnerAsync`）。这一步是断点续跑的关键：
   旧 owner 的后续写入会被 `lo` fence 拒绝。
3. **项目校验**：任务存在、`st == 0`、`label_revision` 与 payload/`lr` 一致；每个图片上传前再次读取项目并复验，
   避免跨阶段的 epoch 变更。
4. **解析**：按团队 `aak` 顺序尝试 key（跳过 `enabled: false` 与无法解密的条目）；团队 `aau` 优先于全局
   `Archive:ProviderApiUrl`，团队地址必须过允许列表；响应解析为 `hath.network` 规范直链。
5. **下载/校验**：只允许 https、`*.hath.network`、无 userinfo/fragment、query 仅 `?start=1`；禁止重定向；
   连接地址必须是公网地址（DNS rebinding 防护）；流式大小上限 `MaxZipBytes`；zip 条目数/单条目/解压总量三级上限；
   CRC 由 `System.IO.Compression` 读取校验。
6. **导入**：剥掉 `images/`、取 basename、跳过隐藏/非图片/非法文件名/单条失败；上传经 `IArchiveProjectUploader`
   → D1 `FileService.UploadAsync`。单条 `ApiError` 跳过，`StoragePoolUnsupported` 等致命错误终止任务。
7. **完成**：成功写 5 / `导入完成（N 张）`；`total_pages = 条目数`，`completed_pages = 成功导入数`
   （与 Python `tasks.py:418-423` 相同）。

## 5. 与 Python 的对照及有意差异

- **`latest` / `dismiss`**：C# 与 Python `models.py:63-65` 相同，按 `-_id` 取最新；dismiss 仅改 `d`，
  不改变最新行。`LatestReturnsDismissedTaskForFrontend` 覆盖此语义。
- **续跑/checkpoint**：Python 的 Celery 任务要求任务仍为 QUEUED（`tasks.py:258-259`），没有断点续跑；
  C# 增加了 claim lease 与 `pi/ic` checkpoint，属于实现扩展，不改变已完成/失败任务的外部行为。
- **claim 并发**：Python 用 `ArchiveImportTask.claim()` 把 QUEUED→RESOLVING 一次；C# 用项目级 claim 加任务行
  `lo` fence，允许过期接管并让旧 worker 收到 `ArchiveLostException`。
- **每个 key 的 `X-Client: ""`**：Python `tasks.py:73` 发送空头；C# `ArchiveImportTransport.cs:54` 也发送空值，
  但服务端对该空头的真实要求仍未验证（源码 TODO 保留）。
- **label revision fencing**：当前是“上传前读取项目并比较 `label_revision`”，与 D1 上传内部取
  `ProjectLabelLock` 之间仍存在窗口；`ArchiveImportService.cs:267-268` 的 TODO 仍保留，未实现严格
  “expectedRevision 进入 D1 锁内 CAS”的 fencing。不得将本实现描述为完整 fencing。

## 6. 配置

`Archive` section 或兼容环境变量：`ProviderApiUrl` / `ARCHIVE_PROVIDER_API_URL`、
`AllowedHosts`、`MaxZipBytes` / `ARCHIVE_MAX_ZIP_BYTES`、`MaxZipEntries` / `ARCHIVE_MAX_ZIP_ENTRIES`、
`MaxEntryBytes` / `ARCHIVE_MAX_ENTRY_BYTES`、`MaxZipUncompressedBytes`（或 `..._MB`）、`TempRoot`。
默认值：zip 500 MiB、解压总量 2 GiB、条目 5000、单条目 128 MiB；claim lease 90 秒、stale task 1 小时。

## 7. 验证

隔离副本（`main` = `80b3893`，只叠加本次修复文件）中：

- build：0 warning / 0 error；
- `ArchiveImportTests`：32/32 通过；
- 组合 focused（`ArchiveImportTests` + `ZitengPartnerTests`）：56/56 通过。

未覆盖：真实 provider 请求、真实 OSS/R2、跨进程 claim 竞争、生产 `X-Client` 契约。
