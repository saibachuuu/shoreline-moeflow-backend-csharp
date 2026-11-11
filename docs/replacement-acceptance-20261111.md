# C# 后端替代 Python 后端的验收判定（2026-11-11）

目标：判定 `shoreline-moeflow-backend-csharp` 是否可以接管 `shoreline-moeflow-backend` 的流量。
本文只定义**判定标准与当前证据**，不重复各专项文档的细节（见文末索引）。

## 1. 判定原则

- **GO** = 下列 A/B/C 三组全部满足，且 D 组残余风险已被明确接受。
- **NO-GO** = 任一门禁项未满足；缺口必须能落到一个具体 issue 或后续任务。
- 任何"未验证"都按"不满足"处理，不允许以"代码看起来对"替代证据。

## 2. A 组：功能 parity（必须全绿）

| 门禁 | 判定标准 | 当前状态 | 证据 |
|---|---|---|---|
| 路由覆盖 | Python `app/apis/*.py` 的每个方法在 C# 有对应端点，或显式 fail-closed 并有文档说明 | ✅ | `ARCHITECTURE-PY-BASELINE-VS-CSHARP.md` 路由覆盖表 |
| 错误契约 | `{error,code,message[,identity_code]}` 形状与业务码一致（含批量端点 `failed.error_code` 包装） | ✅ 除已记录差异 | `docs/authz-matrix.md` §0、`docs/module-parity.md` |
| 认证 | itsdangerous/legacy token、`Authorization` 直接放 token、会话过期语义一致 | ✅ | `Security/LegacyToken.cs` + `AuthzMatrixTests` |
| 授权 | 读/写/管理端点 × 身份矩阵有测试；越权必须拒绝 | 🟡 读门 4 + 写门 5 已固化，扩展用例在 `agents/r3-authz-extend-20261111` | `AuthzReadGateTests`、`AuthzWriteGateTests` |
| 集合 schema | 所有集合名/字段/索引与 Python 一致；迁移可建 | ✅ | `docs/migration-parity.md`（0004 修 7 个 parity bug） |
| 存储 | 前缀规范化、签名 URL 7 天、CDN 直出、>16MB multipart、source 缺失 fail-closed 语义 | ✅ | `docs/storage-contract.md`、`StorageContractTests` |
| 任务 | `csharp_job` 幂等/CAS/重试/退避/优雅停机 | ✅ | `docs/job-semantics.md` |
| 通知 | 扫描、租赁、重试、邮件投递契约 | ✅ | `docs/notification-contract.md`、`NotificationE2ETests` |
| MIT | `detect_text`/`ocr`/`translate` 经 bridge，或显式 fail-closed | 🟡 `inpaint` 待核实（`agents/r3-parity-gaps-20261111`） | `docs/mit-bridge-spike.md` |
| 可选模块（proofread/ziteng/archive） | parity 或 fail-closed 有据 | ✅ | `docs/module-parity.md`（parity 25 / gap-fixed 5 / fail-closed 7 / missing 0） |
| 全量测试 | 全绿，无 skip | ✅ **1101/1101**（`ba61b1d`，Mongo 4.4.1 隔离栈） | `tools/Run-FullSuite.ps1 -BasePort 33100` |

## 3. B 组：数据与回滚（必须全绿）

| 门禁 | 判定标准 | 当前状态 | 证据 |
|---|---|---|---|
| 迁移执行 | `Migrations:Mode=apply` 可按序建库；`check` 阻止带 pending 的实例接流量 | ✅ | `Infrastructure/Migrations/*`、`MigrationsTests` 37/37 |
| 迁移对拍 | 0000–0009 与 Python 结果等价（含 0004 修复） | ✅ | `docs/migration-parity.md` |
| 回滚 | `tools/legacy-rollback-strip.js` 清理 `runner:"csharp"` 记录与 `migrate` 锁后，Python 可幂等重跑 | ✅（脚本层验证） | `docs/production-deploy-runbook.md` |
| 单向门告知 | 切主后旧 Python `--check` 会误报，操作手册已写明 | ✅ | 同上 |

## 4. C 组：运维（必须全绿）

| 门禁 | 判定标准 | 当前状态 | 证据 |
|---|---|---|---|
| 部署形态 | `compose.prod.example.yml` 可 `config`，api/worker 分离，`Jobs:WorkerEnabled` 生效 | ✅ | `DeployHealthTests`/`DeployComposeTests` |
| 健康检查 | `/ping`、`/health/live`、`/health/ready` 语义明确，ready 受 Mongo 与迁移门控 | ✅ | `DeployHealthTests` |
| 切换前排队排空 | Celery 队列 `ready`/`unacked` 均为 0 才停 worker | ✅ 工具就绪 | `tools/celery-drain-check.sh` |
| 切换方案 | 排空冷切换（方案 A）步骤可执行 | ✅ | `docs/celery-cutover-analysis.md` |
| 观察指标 | `csharp_job` 深度/失败率、邮件投递、5xx 率 | 🟡 指标定义已有，生产监控接入未做 | runbook 告警阈值表 |

## 5. D 组：残余风险（需产品/运维显式接受）

1. **真实 R2/SMTP 未验证**：签名 URL、multipart、CDN 直出只在 S3Mock/Mailpit 上验证；切换前必须在预发用真实桶与真实 SMTP 跑一遍上传/下载/发信。
2. **无压测**：并发上传、批量成员变更、通知风暴未做容量验证。
3. **MIT 桥**：报文格式来自实测采样；publisher 无 confirm、`celery_taskmeta` 无 TTL、broker 密码在配置中明文（应交由 secret 管理）。
4. **通知 STARTTLS 策略**：正在与 Python 对齐（`agents/r3-parity-gaps-20261111`），对齐前真实 SMTP 环境行为未证。
5. **多实例 worker**：lease 竞争与 SIGTERM 回收只在测试环境验证过一次，生产需单实例起步。
6. **迁移的单向性**：C# 写 int64 + 自有 checksum，Python 无法识别；回滚必须走 strip 脚本。

## 6. GO / NO-GO 结论（2026-11-11 19:45）

- A 组：除"授权扩展"与"MIT inpaint"两项在收尾外，其余全绿 → **接近满足**。
- B 组：**满足**。
- C 组：部署/健康/排空工具满足；生产监控接入未做 → **部分满足**。
- D 组：需显式接受，尤其第 1、2 项。

**当前判定：NO-GO（预发可开始验收，生产切换需补完 D1/D2 并在预发实测）。**

完成 D1/D2 后，以本表逐项打勾作为 GO 依据；任何一项回退即回到 NO-GO。

## 7. 文档索引

- `ARCHITECTURE-PY-BASELINE-VS-CSHARP.md`（架构对比与未重构项）
- `docs/production-readiness-checklist.md`（逐项状态）
- `docs/readiness-20261111.md`（本轮变更记录）
- `docs/authz-matrix.md`、`docs/module-parity.md`、`docs/storage-contract.md`
- `docs/job-semantics.md`、`docs/notification-contract.md`、`docs/mit-bridge-spike.md`
- `docs/migration-parity.md`、`docs/celery-cutover-analysis.md`、`docs/production-deploy-runbook.md`
