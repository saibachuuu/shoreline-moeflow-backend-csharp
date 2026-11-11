# 生产替换硬门槛检查表

日期：2026-11-11（深夜更新）。C# main `2b1b69f`（本轮 16 个分支全部 `--no-ff` 合并）；Python 参考 `be0b717`。
本轮增量、分支 hash 与证据：[readiness-20261111.md](readiness-20261111.md)。迁移逐字段对拍：[migration-parity.md](migration-parity.md)。存储契约：[storage-contract.md](storage-contract.md)。可选模块：[module-parity.md](module-parity.md)。通知：[notification-contract.md](notification-contract.md)。

**发布结论：仍然阻塞，不能替换生产 Python。**
“已验证”只代表表中具体证据；静态实现、测试发现、旧快照绿色结果均不等于生产验收。
本表没有产品批准的“不适用”项。README 的 WIP 表述不得提前移除。

| 硬门槛 | 状态 | 证据 / 阻塞 / 仍需执行 |
|---|---|---|
| 核心有效路由注册 | 已验证（静态 + 测试机抽查） | 静态 160/160；`/urls`、`/error`、`/warning`、`/docs/<path>` 在测试机容器内经 nginx 返回 200/500/500/404；`/ping`、`/health/*`、`/v1/site/homepage` 均 200 |
| 全核心路由 C# HTTP 行为与权限 | 已验证 | 静态 160/160；全量 suite 1149/1149；端点×身份授权矩阵 38/38（矩阵 9 + 读门 4 + 写门 5 + 扩展 12 + 团队申请 3 + 邀请层级/标签门 5），计划外差异仅 3 条且均有 oracle 依据 |
| archive_import HTTP | 部分验证 | 已装配；legacy `ARCHIVE_MAX_*` 按 MB 换算（`1933b9a`）；`ARCHIVE_PROVIDER_API_ALLOWED_HOSTS` allowlist（`94a06a8`）；`PublicNetworkGuard` + `UseProxy=false`（`8efc6c7`）。**Python oracle 差分未做** |
| partner_search HTTP | 部分验证 | 已有 HTTP 测试；写/索引排序与 Python oracle 未逐条对拍 |
| storage_pool HTTP | 部分验证 | 已装配；每日扫描 `lsd` 原子 claim（`06c1d6a`）；`StoragePool:WorkerEnabled` 可关；跨天/真实 OSS 未验 |
| ziteng_partner HTTP | 部分验证 | 契约测试 + 连接级 SSRF（`5817439`，`ZitengPartnerSsrfTests` 5）；错误码 403→400 修正（`bc016eb`） |
| MIT 条件模块 | 已实现（opt-in，默认 fail-closed） | Python `tasks.mit.*` 是 stub，真实实现在外部 `manga_translator/moeflow_worker.py`。C# 新增 `Modules/Mit/MitBridgePublisher.cs`：Celery protocol v2 → durable `mit` 队列 + `celery_taskmeta` 轮询；门控 = `Mit:StorageRoot` 且（三个 handler 齐全 **或** `RabbitMq:Enabled=true` 且 `Url` 非空），否则 404。`MitHttpTests|MitBridge` 19/19，含 throwaway broker 集成。**未做真实 MIT worker 联调**（`inpaint` 未接、共享卷/凭证待定） |
| OCR / 405 / 注释禁用路由的逐条例外 | 未验证 | 已记录候选原因，尚无产品批准；OCR 在 Python 侧同样 `NotImplementedError`，C# 501 fail-closed 等价 |
| 真实 Flask / C# 同种子 BSON | 部分验证 | 旧 fixture 有局部种子；完整 app/modules 可复现 fresh run 未做 |
| 真实差分读与成功写 | 部分验证 | 只读 19/19 匹配、0 business differences（`docs/runtime-differential.md`）；写请求仍未纳入差分；11-10 测试机 E2E 已覆盖主要写路径 |
| 差分 status/body/error/headers/前后 digest | 已验证（只读 19 例） | 19 例全匹配；框架级 wire 差异单独记录；写请求与 4 个可选模块未覆盖 |
| BSON collection/alias/type/index/unique | 部分验证 | Mongo 4.4.1 实测修复 `$lookup` `localField`+`pipeline`（5.0 才支持）；team_member 默认名索引与 `team_member_alias_search_v1` 由 `MongoSchemaReadinessTests` 验证；`a8b46e9` 修掉接管 Python 库时 `IndexOptionsConflict` 打死启动的问题（`MongoStore.EnsureIndexTolerantAsync` + 迁移 0004 的 `$type` 对齐，`MongoStoreIndexCompatTests`），并在镜像上用 mongoengine 形态的索引实测接管启动成功。**生产 dump 副本上的全集合对比未执行** |
| Python auth token 互操作 | 已验证（双向） | `SECRET_KEY` 下 Python 签发 → C# 200；C# 登录 token → Python `URLSafeTimedSerializer.loads` 解出 `{id,pc}`；C# 同时接受 itsdangerous 与历史 HS512 JWS（`Security/LegacyToken.cs`） |
| storage key/prefix/R2 | 部分验证 | `docs/storage-contract.md`：prefix 尾斜杠归一化（`25f460a`）、签名 URL 统一 7 天（`ac81252`，对齐 Python 604800s）、>16 MB `TransferUtility` 分片（`0167826`）；`StorageContract` 23/23。**真实 R2 写入/读取仍未用生产凭证验证** |
| 设置、语言/类型/角色种子 | 部分验证 | bootstrap/`LegacyTypes`/`LanguageCatalog` 已逐项复核，测试机空库初始化通过；逐集合 BSON 差分未做 |
| 管理员/默认团队初始化 | 已验证（测试机空库） | 新镜像日志 `Created bootstrap administrator …`、`Created bootstrap Default Team …`，二启不重复；验证码 + 密码登录成功 |
| 迁移及回滚数据兼容 | 部分验证（实现 + 对拍完成，生产 dump 未跑） | `Infrastructure/Migrations` 落地 m0001–m0009 + `migration_record`/`migration_lock`；`docs/migration-parity.md` 逐字段对拍：9 个迁移等价、0004 修 7 个 bug（含 F7 成员合并 projection bug）；`Migrations` 37/37。`Migrations:Mode=check` 有 pending 时 `/health/ready` 503。11-10 回滚演练（`legacy-rollback-strip.js`）仍有效，本轮补 `deleteMany({runner:"csharp"})` + 清 `migrate` 锁（`ead3e33`）。**生产 dump 副本未执行** |
| 每个 Celery 任务对应 handler/接受替代 | 部分验证 | 静态映射 20 个 task；MIT 已从 fail-closed 升级为 opt-in 桥；`docs/celery-cutover-analysis.md` 结论：排空 + 冷切换，不需要 MQ 协议兼容；`tools/celery-drain-check.sh` 已改为 fail-closed（显式 `?columns=`，字段缺失退出 2），一次性 broker 实测 0/1/2 三种退出码。**未在真实 broker 演练排空** |
| 重试/lease/幂等/崩溃恢复 | 部分验证 | `Jobs:MaxAttempts=5`、`RetryDelay=min(300s, 5·2^n)`、优雅停机（`30ea5ea`,`e176e0d`）；邮件 `verification.sent` CAS 防重发（`30878c5`）；通知扫描 lease 语义由 `NotificationE2ETests` 14/14 锁定；`JobFaultRecoveryTests` 6 例锁定租约过期回收、达 `Jobs:MaxAttempts` 终止、过期租约被扫成 failed、退避 5→300s 封顶、停机只停认领。**真实 kill -9 与 30s 停机超时丢弃路径未做** |
| 本机基线 dotnet build 0 warning/0 error | 已验证 | `96eef2a`：`dotnet build Shoreline.sln --nologo` 0/0 |
| 最终合并后的 dotnet build 0/0 | 已验证 | 同上（本轮每次合并后均复跑） |
| C# 全量 suite | 已验证 | `62ba5c2`：`tools/Run-FullSuite.ps1 -BasePort 33210` → **1149 passed / 0 failed / 0 skipped**（Mongo 4.4.1 隔离栈；1028 → 1092 → 1101 → 1129 → 1138 → 1144 → 1149） |
| Python 全量 suite | 已验证 | `836 passed, 48 subtests passed`，446 s（`.env.test`，mongomock/memory broker/local storage） |
| 真实差分全量 suite | 部分验证 | 只读 19/19；写路径与可选模块未覆盖 |
| 可复现镜像 / lock / digest | 已验证（层内容） | 两次 `--no-cache` 冷构建：19 个层的 Layer ID、大小（497130313 bytes）、`CreatedBy` 全部一致；ImageID 因 image config 的 `created` 时间戳不同而不相等（Docker 默认行为），需要 bit-identical 时用 buildx `--rewrite-timestamp`（需 ≥ 0.14；本机 0.13.1 不支持该 flag，已实测报错）。另有空库 `Migrations:Mode=apply` 首启冒烟与「Python 库已有同名索引」接管冒烟，均 `/ping`、`/health/live`、`/health/ready` 200。见 `docs/image-reproducibility.md`（`b4f77f9`, `bacd343`） |
| 实际非 root / 文件权限 | 已验证（镜像 + 测试机） | UID 1654；bind mount 需 `chown -R 1654:1654 storage`，否则首传 500；已写入 runbook §0 第 7 条（`16a6fab`） |
| health / readiness / 配置校验 | 已验证（容器实测） | 一次性栈上四种组合：`check` + 空库 → 503 `{"status":"migrations-pending","pending":["0000"…"0009"]}`；同库 `apply` → 200；已迁移后再 `check` → 200；`Migrations__Enabled=false`（worker 角色）→ 200。`/health/live` 恒 200。反代形状仍只靠 11-10 测试机证据 |
| 日志 / metrics / 告警 | 可选增强（非 parity 阻塞） | Python 老版同样无 metrics 导出（`app/**/*.py` 无 prometheus/statsd 匹配）；runbook 已有阈值表与排查路径。接入监控属运维增强，不构成替换阻塞 |
| 备份 / 副本恢复 / 恢复后的业务 smoke | 已验证（测试机） | 11-10：`mongodump --gzip --archive` 158 文档，恢复到 `moeflow_restore` 后登录/团队/成员/项目/文件/术语库均 200；生产规模计时未做 |
| TLS / 反代 / trusted proxy | 未验证 | 不读生产 env/凭证；需 disposable 测试栈验收 |
| 资源限制 / graceful stop / rollback | 部分验证 | `compose.prod.example.yml` 有 `mem_limit 512m`、`stop_grace_period 40s`、`Jobs:WorkerEnabled` 开关与 shutdown 测试（`cc1ae8c`）；未做真实 SIGTERM 下 lease reclaim 演练 |
| 测试机负载 / 长稳 / 内存比较 | 未验证 | 无等负载基准 |
| 密钥与依赖安全 | 未验证 | 依赖扫描非完整安全结论；`RabbitMq:Url` 含明文密码需 secret 管理；禁止读取真实密钥 |
| 认证 / 越权 / 授权竞争窗口 | 已验证（授权）/ 部分验证（竞争窗口） | 授权矩阵 38/38（矩阵 9 + 读门 4 + 写门 5 + 扩展 12 + 团队申请 3 + 邀请层级/标签门 5）；3 条已知差异均有 oracle 依据。`admin=false` 不绕过业务权限、项目 `st!=0` 时 normal-only 权限失效、批量端点 `failed.error_code` 形状已文档化。并发租约/竞争窗口仍只有单次验证 |
| SSRF / allowlist / DNS / redirects | 部分验证 | `PublicNetworkGuard`（29 测试）+ Ziteng 注册 client（5）+ archive transport；`UseProxy=false` 防环境代理绕过（`8efc6c7`）。其余出站路径（邮件/存储 SDK）未系统覆盖 |
| 上传限制 / 内存放大 | 部分验证 | >16 MB 走 multipart 分片，消除全量进内存；1 GiB 边界与并发资源未验 |
| 真实 HTTP 限流 / XFF 防伪造 | 未验证 | limiter/routing 顺序、反代身份与 client_key 未真实请求验证 |
| 部署 / 迁移 / 回滚 runbook | 部分验证（已固化） | `docs/production-deploy-runbook.md`：启动顺序（mongo → api 跑迁移 → worker → nginx）、回滚删 csharp 迁移行、bind mount `chown`、前端 restart 解 502、告警阈值表；`scripts/verify-deploy.ps1` 只读自检。**未在测试机复跑本轮版本** |

## 明确阻塞（2026-11-11 夜）

1. **生产 dump 副本上的迁移 dry-run/check/apply 未执行**；这是数据正确性的最后一道门（语义对拍已完成）。
2. **授权矩阵仍缺写/管理端点的系统覆盖**：读门 4 例 + 写门 5 例已固化，项目成员/邀请/申请/文件标签/团队管理 × 身份的扩展用例在 `agents/r3-authz-extend-20261111` 收尾中。
3. **真实 R2 / SMTP+TLS 未验收**（按约定不读生产凭证）；通知邮件 TLS 策略已与 Python 对齐并新增显式 `Mail:SecureSocketOptions` opt-out（`4d87c2e`），但 TLS 证书/中继认证仍只能在真实 SMTP 上验证。
4. **RabbitMQ 排空演练未做**；MIT 桥未与真实 worker 联调。
5. **镜像 digest/可复现构建**在本轮新增依赖后未复验；限流/XFF、load/soak 仍空白（Python 老版亦无压测基线，属同等水平）。

## 测试机生产形状部署（2026-11-10，历史证据）

镜像 `shoreline-moeflow:ready-20261110d`（ImageID `sha256:d0edc71f…def8b`）经 `docker save | ssh | docker load` 部署到 `/root/moeflow-csharp-isolated`（Mongo 4.4.1、Local storage、nginx 反代、无 RabbitMQ）。已执行：空库 bootstrap（管理员/Default Team/默认项目集）、登录（验证码 + token）、团队/项目/术语库创建、multipart 上传 + worker 生成 `cover-*`/`resample-*`、原文/译文、导出 zip（`translations.txt`/`project.json`/`images/smoke.png`）、第二用户邀请、密码重置邮件（Mailpit）、团队申请审批、`mongodump`/`mongorestore` 恢复后只读 smoke。踩坑：bind mount `chown`、`$lookup` 需 Mongo 5.0 语法（已改 `let`）、重建后端容器后需 `docker compose restart moeflow-frontend` 否则 502。

更新本表时必须附具体 commit、命令、通过/失败/跳过数及环境；状态改为“不适用”必须记录逐项原因与产品接受决定。
