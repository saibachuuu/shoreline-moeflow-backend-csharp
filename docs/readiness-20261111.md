# 2026-11-11 夜间就绪增量报告

本文记录 2026-11-11 18:00–24:00 这一轮"向可替代 Python 后端推进"的实际变更与证据。
`docs/production-readiness-checklist.md` 仍是总表；本文只记本轮增量，避免总表被单日流水淹没。

基线：main `d9cdc77`（第一轮 5 分支合并）→ 本文写作时 main `733e239`。
测试环境：Windows `dotnet 10.0.204`；Mongo **4.4.1**；`tools/Run-FullSuite.ps1` 独占端口栈（默认 33000，本轮用 33090）。

## 1. 本轮合并的分支

| 分支 | 主题 | 关键提交 | 结论 |
|---|---|---|---|
| `agents/r2-migrations-20261111` | 迁移运行器 + m0001–m0009 | `9d3bf52`,`435f517`,`9fd7a67` | C# 首次具备与 Python 同名的 `migration_record`/`migration_lock`；`Mode=check` 有 pending 时 `/health/ready` 503 `{"status":"migrations-pending"}` |
| `agents/r2-deploy-runbook-20261111` | 生产 compose 形状 + runbook + `Jobs:WorkerEnabled` | `328c1aa`,`72bb37e`,`cc1ae8c` | api/worker 拆分；`stop_grace_period: 40s`；runbook 含滚动/回滚/排空 |
| `agents/r2-job-semantics-20261111` | 有界重试/退避/优雅停机/幂等 | `30ea5ea`,`e176e0d`,`30878c5` | `Jobs:MaxAttempts=5`；`RetryDelay=min(300s, 5·2^n)`；停机只停认领；邮件 `verification.sent` CAS 先占 |
| `agents/r2-storage-contract-20261111` | 存储契约测试 + prefix 归一化 | `27c2d43`,`9b52162`,`25f460a` | `docs/storage-contract.md`；尾斜杠归一化与 Python `normalize_sub_path` 一致 |
| `agents/r2-module-parity-20261111` | 可选模块逐路由审计 | `6a463e6`,`bc016eb`,`94a06a8`,`276ecf9` | `docs/module-parity.md`：parity 25 / gap-fixed 5 / fail-closed 7 / missing 0 |
| `agents/r2-mit-bridge-spike-20261111` | MIT 桥可行性 spike | `d2f6b50`,`8724930` | 实测 Celery protocol v2 报文；方案 A 3–5 人时；**保持 fail-closed** |

第一轮已合并（`d9cdc77`）：`Mail:UseSsl`/archive MB 换算、邮件 `v_code` 恢复、StoragePool 每日扫描 `lsd` claim、MIT 无消费者不装配、`PublicNetworkGuard` + Ziteng 连接级 SSRF。

## 2. 主代理直接修复

| 提交 | 内容 | 理由 |
|---|---|---|
| `8efc6c7` | `SocketsHttpHandler.UseProxy=false`（archive transport、ziteng registration） | `ConnectCallback` 是 SSRF 安全边界；走环境代理会让 guard 只看到代理地址 |
| `7c24af8` / `523ebeb` | `tools/Run-FullSuite.ps1` 独占端口栈 + 并行栈隔离 + mongo 4.4.1 + `mem_limit 1g` | 全量可重复、可与子代理并行 |
| `c0f5dbd` | `docs/celery-cutover-analysis.md` | 结论：C# 不需要消费 RabbitMQ；排空 + 冷切换 |
| `264344d` | 测试夹具 `Migrations:Enabled=false` | 每类独立空库上 `Mode=check` 永远 pending，会误伤 `/health/ready` 测试 |
| `ead3e33` | `compose.prod.example.yml` + runbook 修补 | api 独占迁移闸门；api 关掉 notification/storage 扫描；补 `Mail__UseSsl`；回滚删 `runner:"csharp"` 迁移行 |
| `ac81252` | `ObjectStorage.DefaultSignedUrlValidity = 7d` | 对齐 Python `oss.py` 604800s；7 处服务统一引用 |
| `14466da` | send-proofread-draft 未知文件 → 4006 | Python 有两个 `FileNotExistError`：通用 8001、项目域 4006 |
| `0167826` | `S3StorageBackend` >16 MB 走 `TransferUtility` 分片 | 对齐 boto3 `upload_fileobj`，消除大文件全量进内存 |
| `a5832f8` | `tools/check-storage-sources.js` | 只读统计 `project.csid` 指向缺失/禁用 source 的项目 |

## 3. 测试证据

- 全量（`523ebeb` 之后，含 E/G 与主代理修复）：`tools/Run-FullSuite.ps1 -BasePort 33090` → **1028 passed / 0 failed / 0 skipped**，1m27s，Mongo 4.4.1。
- 迁移：`MigrationsTests` 8/8；默认 `Mode=check` 在全新空库上的 503 行为有 `DeployHealthTests` 覆盖（夹具侧关闭迁移开关以免互相污染）。
- 任务语义：`JobRetrySemantics` / `EmailTests.ReplayedJob…`（由 2 封改 1 封）全绿。
- 存储：`StorageContract` 23/23（含 20 MB multipart roundtrip）、`Storage` 139/139。
- 模块：`ProofreadFilter` 48/48、模块 parity focused 140/140。
- SSRF：`ZitengPartnerSsrf|PublicNetworkGuard|ArchiveImport` 77/77。

## 4. 明确仍未验证（不因本轮而改变）

1. **真实生产数据上的迁移**：本轮只有空库/happy-path 与边界测试；生产 dump 副本上的 `m0004` 等尚未执行（正在单独做逐字段对拍）。
2. **真实 R2 / SMTP+TLS**：按用户要求不读生产凭证；需在隔离测试机用独立凭据验收。
3. **RabbitMQ 排空演练**：方案已定，未在真实 broker 上演练 ready/unacked 归零与回滚。
4. **MIT 功能**：spike 完成，仍 fail-closed（若产品需要，按方案 A 接线）。
5. **授权矩阵**：矩阵文档与测试仍在收尾（端点×身份），未全部落地。
6. **通知端到端**：站内通知 + 邮件 + 扫描器语义的端到端断言仍在收尾。
7. **观测性/限流/压测**：无 metrics、无限流真实请求矩阵、无 load/soak 基线。

## 5. 替代判据（当前口径）

可以切换的**必要条件**（全部满足才谈切换）：

- 全量 suite 0 failed（当前满足，但需在最终合并后再跑一次）。
- `Migrations:Mode=check` 在**生产 dump 副本**上返回 ready，且 `dry-run` 改动行数与预期一致。
- 授权矩阵覆盖全部写/管理端点，且与 Python oracle 对齐。
- `/health/ready`、登录、团队/项目/文件/译文/导出、邀请/申请、密码重置在测试机 E2E 全绿。
- 回滚路径演练：`legacy-rollback-strip.js` 后 Python 可直接读（已有 2026-11-10 证据，需在新 schema 上复跑）。

**尚不满足**，因此本轮结束后仍不能替换生产 Python。

## 6. 明日/下一轮建议顺序

1. 生产 dump 副本上跑迁移 `dry-run` → `check` → `apply` → Python 回滚读。
2. 收尾授权矩阵与通知端到端，纳入全量。
3. 真实 R2/SMTP 的隔离测试机验收（独立凭据）。
4. RabbitMQ 排空冷切换演练（staging 栈）。
5. 观测性最小集：结构化日志字段、`/health/ready` 依赖、失败率告警阈值。
