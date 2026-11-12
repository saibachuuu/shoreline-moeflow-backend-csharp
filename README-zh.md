# Shoreline C# 后端

用 ASP.NET Core / .NET 10 重写的 Shoreline 后端，目标是**直接替代** Python 版。
它读写同一套 MongoDB 文档、同一套对象存储 key、同一套 HTTP 契约，因此切换期间
两个栈可以共用同一个数据库。

**状态：** 功能上已对齐 Python API，1157 个自动化测试通过，并已用「克隆库 + 双实例
差分回放」和 Python 老版逐端点对拍。可以进预发环境。正式切换仍需用真实 R2/SMTP
凭证走一遍演练，并显式接受清单里列出的残余风险 —— 见
[docs/production-readiness-checklist.md](docs/production-readiness-checklist.md)。

## 它是什么

**一个进程同时提供 HTTP 服务和消费持久化任务队列。** MongoDB 与 S3 客户端共享，
密码哈希有独立的并发限制。没有 RabbitMQ、没有 Celery、默认也没有第二个 worker
容器 —— 队列就在 `csharp_job` 集合里，带原子租约、重试与崩溃恢复。

```text
src/Shoreline.Api/
  Features/         各业务域的 endpoint 与 service（一个域一个文件）
  Modules/          可选模块：归档导入、紫藤合作方、MIT 桥
  Infrastructure/   MongoStore、schema 预检、迁移 runner、SSRF 防护
  Security/         旧版 itsdangerous token 兼容、权限、错误码
  Storage/          ObjectStorage、S3/R2 与 Local 后端、项目级路由
  Notifications/    发布、投递、邮件门控
  Jobs/             持久化队列、worker、后台扫描器
  Formats/          LabelPlus、归档、JSON 兼容格式
tests/Shoreline.Tests/   xUnit 测试（1157 个）
tools/                   全量测试脚本、只读探针、差分回放
docs/                    契约、兼容性说明与运维手册
```

## 与 Python 栈的关系

两个栈可以读同一个库，但**同一时间只能有一个写**。

| 关注点 | 规则 |
|---|---|
| 会话 | `Auth__Secret` **必须等于** 老版 `SECRET_KEY`，否则所有旧 token 报 `BadTokenError` / 1004 |
| 迁移 | 沿用旧版记录表；C# 写入的行带 `runner:"csharp"` 和独立 checksum |
| 存储 key | 旧版 `OSS_KEY_PREFIX=storage/` 对应 `Storage__KeyPrefix=storage/`，不一致会导致所有旧对象 404 |
| 对象存储 | `STORAGE_TYPE=OSS` + `OSS_BUCKET_STYLE=R2` 会解析成 S3 后端；原生阿里云 OSS 会被**显式拒绝** |
| 回滚 | `tools/legacy-rollback-strip.js` 会卸载 C# 字段、删 C# 索引、清迁移锁，之后 Python 可幂等重跑 |

旧版环境变量名也可以直接识别（`SECRET_KEY`、`MONGODB_URI`、`EMAIL_*`、`OSS_*`、
`ENABLE_NOTIFICATIONS`、`NOTIFICATION_*`、`ZITENG_PARTNER_API_KEY` 等），详见
[docs/legacy-environment.md](docs/legacy-environment.md)。显式的 `Section__Key`
优先级高于 legacy 名称。

## 本地快速开始

```bash
# 一次性 MongoDB + S3Mock + Mailpit
docker compose -f compose.test.yml up -d mongo s3 mailpit

dotnet restore
dotnet build Shoreline.sln
dotnet test Shoreline.sln --filter "FullyQualifiedName~Storage"
```

在独立端口上跑全量（自己起栈、结束自动清理）：

```bash
pwsh tools/Run-FullSuite.ps1 -BasePort 33400
```

默认测试端口：MongoDB `27128`、S3Mock `29128`、Mailpit SMTP `21128` / UI `28128`。
测试使用随机数据库，只删除自己创建的那些。

## 部署

推荐部署配置在 `shoreline-moeflow-deploy` 仓库：`docker-compose.csharp.yml` +
`.env-csharp.sample`。默认只跑一个后端容器 + 一个 nginx 前端，接入已有 MongoDB 所在
的网络。

最小配置（环境变量，`__` 等价于 `:`）：

- `Mongo__Uri` —— 必须带数据库名
- `Auth__Secret`、`Auth__AdminEmail`
- `Storage__Type`/`Endpoint`/`Bucket`/`AccessKey`/`SecretKey`，加
  `Storage__KeyPrefix`；要出公开 URL 还需 `Storage__Domain` + `Storage__ViaCdn`
- `Mail__*`（启用邮件时）

运维开关：

| 键 | 默认 | 作用 |
|---|---|---|
| `Jobs__WorkerEnabled` | true | 在本进程内消费持久化队列 |
| `Jobs__Concurrency` | 1 | 异步认领循环数（1–8），每个约 10 KB，不是进程 |
| `Jobs__MaxAttempts` | 5 | 超过后任务标记为 `failed` |
| `Migrations__Mode` | `check` | `check`：有未执行迁移就拒绝服务；`apply`：启动时执行（仅首次部署） |
| `Migrations__Enabled` | true | 副本/worker 角色设 false |
| `Notifications__WorkerEnabled` | false | 后台发布/投递通知邮件 |
| `StoragePool__WorkerEnabled` | false | 运行存储池搬运扫描器 |

健康检查：`/ping`、`/health/live`、`/health/ready`（后者会检查 MongoDB 和迁移状态）。
经 nginx 时 API 挂在 `/api` 下；代理必须剥掉该前缀，并把 `client_max_body_size`
调到不小于单文件上限 1 GiB。

完整部署、发布与回滚流程见
[docs/production-deploy-runbook.md](docs/production-deploy-runbook.md)。

## 验证方式

- 1157 个测试，0 失败 0 跳过（`tools/Run-FullSuite.ps1`，约 1 分 40 秒）
- `tools/parity-probe.mjs` 对 Python oracle 比对 51 个只读端点
- `tools/replay-parity.mjs` 在**两个克隆库**上回放 11 条写流程，同时比对响应与落库结果
- CI（`.github/workflows/check-pr.yml`）每次 push 跑同一套测试

两个差分工具都要求同时有 Python 与等价的 C# 实例；
[docs/parity-harness.md](docs/parity-harness.md) 说明如何搭这套环境。

## 已知限制

- 原生阿里云 OSS 签名未移植，相关配置直接 fail closed。
- OCR 端点与 Python 版一致（两边都是 `501` 空实现）。
- MIT 桥是可选的，需要进程内 handler 或 RabbitMQ URL；两者都没有时路由不映射。
- 465 端口的通知邮件必须设 `Mail__SecureSocketOptions=SslOnConnect`。旧版默认强制
  STARTTLS，在隐式 TLS 上本来就发不出去 —— 那是历史行为，为 parity 保留。
- 监控指标与压测从来不属于旧版功能，因此按「可选增强」处理，不作为切换门槛。

## 文档

先读验收与运维文档，再按需查各域契约：

- [验收门禁](docs/replacement-acceptance-20261111.md)
- [上线就绪清单](docs/production-readiness-checklist.md)
- [部署手册](docs/production-deploy-runbook.md)
- [迁移对拍](docs/migration-parity.md) · [回滚](docs/legacy-rollback.md)
- [存储契约](docs/storage-contract.md) · [通知契约](docs/notification-contract.md)
- [授权矩阵](docs/authz-matrix.md) · [路由覆盖](docs/route-coverage.md)
- [Celery 切换分析](docs/celery-cutover-analysis.md) · [MIT 桥](docs/mit-bridge-spike.md)
- [差分对拍工具](docs/parity-harness.md)

文件名带 `20261110` / `20261111` / `20261112` 的是某次排查的日期记录，属于历史，
不代表当前状态。[docs/README.md](docs/README.md) 标注了哪些文档才是权威的。

## 开发约定

- 一个已验证的改动一个 commit，提交信息里附 focused 测试结果。
- 迭代用 `dotnet test --filter`，声称「全绿」前必须跑 `tools/Run-FullSuite.ps1`。
- 不要把测试指向真实部署库，也不要把凭证提交进仓库。
