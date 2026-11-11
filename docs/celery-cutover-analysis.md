# Celery / RabbitMQ 切换分析：为什么不需要协议级兼容

> 结论先行：C# 后端**不需要消费 RabbitMQ**。合理的切换方式是「停 beat → 等队列排空 → 冷切换流量」。把这当成"必须实现 Celery 协议"会显著高估工作量。

## 1. Python 侧任务分类

| 类别 | 任务 | 触发方式 | 切换后由谁执行 |
|---|---|---|---|
| 请求触发 | `email_task` | HTTP 请求内 `queue_task` | C# 请求内/`csharp_job` |
| 请求触发 | `parse_text_task` / `find_terms_task` | 文件写入后入队 | `FileParseJobHandlers` |
| 请求触发 | `create_thumbnail_task` | 上传/显式请求 | `ThumbnailJobHandler` |
| 请求触发 | `output_project_task` / `output_team_projects_task` | 导出请求 | `OutputJobHandler` / `TeamOutputJobHandler` |
| 请求触发 | `import_from_labelplus_task` | 导入请求 | `ProjectImportJobHandler` |
| 请求触发 | `proofread_draft_bake` | 送校对请求 | `ProofreadDraftJobHandler` |
| 请求触发 | `notification_security_email` | 验证码请求 | `SecurityEmailHandler` |
| 请求触发 | `ocr_task` | OCR 请求 | Python 侧亦 `NotImplementedError`；C# 501 |
| 请求触发 | `storage_pool` / `ziteng` / `archive_import` 模块任务 | 模块 HTTP | 对应 C# handler / 内置 worker |
| 定时 | `notification_scan`（10s） | beat | `NotificationScanWorker` |
| 定时 | `storage_pool` 每日 03:00 | beat | `StoragePoolWorker`（`lsd` 持久 claim） |
| 外部 | `tasks.mit.*`（`mit` 队列） | HTTP 请求入队 | **外部** `manga-image-translator` worker；C# 无桥 |

关键观察：**没有跨进程的"任务交接"需求**。Celery 只是 Python 进程间传递"现在去做这件事"；C# 用 Mongo `csharp_job` 完成同一语义。两者不共享任务格式也能共存，因为切换时点前后由不同进程各自处理各自入队的任务。

## 2. 切换方案

### 方案 A（推荐）：排空后冷切换

1. 停止 Python beat（避免新定时任务入队）。
2. 等待 RabbitMQ 各队列 `messages_ready = 0` 且 `messages_unacknowledged = 0`：
   - `default`、`output`、`security_email`、`notification`、`mit`、模块队列。
3. 停止 Python worker；确认没有 in-flight（unacked 为 0）。
4. 切换 nginx/流量到 C#；C# bootstrap/`StoragePoolWorker`/`NotificationScanWorker` 接管。
5. 观察 30 分钟：`csharp_job` pending 深度、失败率、邮件投递。

窗口内未被处理的任务：请求方会看到超时/失败，可重试；不存在"消息丢失后无人知晓"的情况（HTTP 请求本身会失败）。

### 方案 B（不推荐，仅在无法停机时）：双跑

需要实现：
- Celery 消息协议 v2（JSON body + `content-type`/`content-encoding` 头）；
- 任务路由与 `task_id` 生成（UUID4）；
- Celery result backend（Mongo `celery_taskmeta`）读写；
- 签名/认证（若 broker 配置了 user/password/vhost）。
- Python 与 C# 对同一 `v_code`/项目文档的并发写冲突处理。

收益仅是"无需停机几分钟"，成本是数天实现 + 长期双写风险。除非业务明确要求零停机，否则不做。

## 3. 残留风险与验收

| 风险 | 处理 |
|---|---|
| 切换瞬间 beat 刚写入的任务 | 排空检查包含所有队列；`mit` 队列可等待其外部 worker 完成 |
| 前端持有旧 `task_id` 轮询 | 页面刷新后由 C# 新 id 接管；不承诺旧 id 可查 |
| MIT 外部 worker | 不受切换影响；但 C# 无 `mit` 入队路径，MIT 功能在 C# 侧仍不可用（fail-closed） |
| Python 回滚 | `tools/legacy-rollback-strip.js` 清理 C# 专有字段；`csharp_job` 留在库中不影响 Python |
| 迁移版本表 | C# 必须能读/写 Python 的迁移版本记录（见 `docs/migrations.md`），否则回滚判断失真 |

## 4. 待补证据

- 真实 RabbitMQ 上执行一次排空演练（本地 compose 的 RabbitMQ 即可），记录队列深度与耗时。
- 在 `csharp_job` 仍有 pending 时切换流量，验证 worker 恢复后继续消费。
- 回滚演练中确认 `csharp_job`、`legacy_schema_migration`（或实际版本表名）不会被 Python 误读。
