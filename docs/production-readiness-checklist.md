# 生产替换硬门槛检查表

日期：2026-11-10。审计基线：Python `be0b717` / C# `c29888e`。

**发布结论：阻塞，不能替换生产 Python。**
“已验证”只代表表中具体证据；静态实现、测试发现、旧快照绿色结果均不等于生产验收。
本表当前没有产品批准的“不适用”项。README 的 WIP 表述不得提前移除。

详细诊断：[readiness-audit-20261110.md](readiness-audit-20261110.md)。

| 硬门槛 | 状态 | 证据 / 阻塞 / 仍需执行 |
|---|---|---|
| 核心有效路由注册 | 未验证 | 静态 156/160；4 条诊断路由没有实现或批准例外；还须运行时装配验证 |
| 全核心路由 C# HTTP 行为与权限 | 未验证 | 已有测试但没有接受的逐路由测试/意图矩阵，也未跑全量 |
| archive_import HTTP | 未验证 | 已装配；需 request/body/errors/auth/digest 测试 |
| partner_search HTTP | 未验证 | 已有 HTTP 测试；需全契约、写/索引排序和 Python oracle |
| storage_pool HTTP | 未验证 | 已装配；需完整 admin HTTP 及权限/任务状态测试 |
| ziteng_partner HTTP | 未验证 | 已有 HTTP 测试；不能误报全缺失；需全契约及安全检查 |
| MIT 条件模块 | 未验证 | 启用/禁用装配及 Python 契约待执行 |
| OCR / 405 / 注释禁用路由的逐条例外 | 未验证 | 已记录候选原因，尚无产品批准；须核对真实响应 |
| 真实 Flask / C# 同种子 BSON | 未验证 | 旧 fixture 有局部种子；需完整 app / modules、可复现 fresh run |
| 真实差分读与成功写 | 未验证 | 现有 8 cases 全部 DB 不变，包含的 POST 仅 405 |
| 差分 status/body/error/headers/前后 digest | 未验证 | 覆盖不足，旧 wire/framework 差异未成为已批准例外 |
| BSON collection/alias/type/index/unique | 未验证 | 逐项复核中；需要副本数据兼容和重复初始化证据 |
| Python auth token 互操作 | 未验证 | 旧 replay 使用 C# 自签 token；需真实 Python token |
| storage key/prefix/R2 | 未验证 | Python 默认 storage/，C# 默认空；真实 R2 尚无验证；不用生产凭证 |
| 设置、语言/类型/角色种子 | 未验证 | 当前初始化与 Python 逐项复核，不能沿用首轮猜测 |
| 管理员/默认团队初始化 | 未验证 | 必须 fresh DB + existing DB + concurrent/retry 测试 |
| 迁移及回滚数据兼容 | 未验证 | production-shaped disposable 副本尚未演练 |
| 每个 Celery 任务对应 handler/接受替代 | 未验证 | 已映射 20 个真实 task；MIT 无消费者、普通创建 LabelPlus 断链、storage worker 默认禁用；运行时及替代接受未验收 |
| 重试/lease/幂等/崩溃恢复 | 未验证 | 需全 handler 及 unknown commit / stale lock / worker crash 故障证据 |
| 本机基线 dotnet build 0 warning/0 error | 已验证 | 三个审计 worktree `dotnet build Shoreline.sln --nologo` 成功；仅 c29888e 首轮 |
| 最终合并后的 dotnet build 0/0 | 未验证 | 还未合并修复，不以基线构建替代最终构建 |
| C# 全量 suite | 未验证 | 仅发现 837；独立单元 18/18；宽泛过滤 23 通过/9 缺依赖失败 |
| Python 全量 suite | 未验证 | 需无 dotenv、owned DB、isolated catalog/storage/broker 的安全环境 |
| 真实差分全量 suite | 未验证 | 未运行本轮读写与可选模块扩展 |
| 可复现镜像 / lock / digest | 未验证 | Dockerfile 缺 Imaging 项目；基础镜像浮动；尚未构建 |
| 实际非 root / 文件权限 | 未验证 | Dockerfile USER 是静态证据，非容器验收 |
| health / readiness / 配置校验 | 未验证 | readiness 仅 Mongo；无完整启动前配置验证证据 |
| 日志 / metrics / 告警 | 未验证 | 需可观测性、隐私与故障告警验证 |
| 备份 / 副本恢复 / 恢复后的业务 smoke | 未验证 | 未执行；不得清理既有测试库 |
| TLS / 反代 / trusted proxy | 未验证 | 不读取生产 env/凭证；需 disposable 测试栈验收 |
| 资源限制 / graceful stop / rollback | 未验证 | 测试机可用磁盘约 2.1 GiB，需安全隔离部署及回滚演练 |
| 测试机负载 / 长稳 / 内存比较 | 未验证 | 现有资源快照不是等负载基准；需约定时长、数据和通过阈值 |
| 密钥与依赖安全 | 未验证 | 首轮依赖工具未报 vulnerable，不等于完整安全通过；禁止读取真实密钥 |
| 认证 / 越权 / 授权竞争窗口 | 未验证 | 需按 Python oracle 验证所有修改/删除/管理端及并发路径 |
| SSRF / allowlist / DNS / redirects | 未验证 | archive 已有防护；Ziteng 和全部出站路径需源码及 HTTP/连接测试 |
| 上传限制 / 内存放大 | 未验证 | 1 GiB 路径需兼容语义下的限额/并发/流式资源测试 |
| 真实 HTTP 限流 / XFF 防伪造 | 未验证 | limiter/routing 顺序、反代身份和 client_key 均需真实请求验证 |
| 部署 / 迁移 / 回滚 runbook | 未验证 | 需与本轮命令、镜像、备份和演练证据一致，非纸面声明 |

## 明确阻塞

1. 没有满足读写及四可选模块要求的真实运行时差分。
2. 全业务逐路由 HTTP 意图/执行证据和完整数据/任务矩阵尚未接受。
3. Dockerfile 的项目复制缺口、配置/health/security/observability 需要实现并验收。
4. 测试依赖硬编码端口、Python 默认 dotenv/重建测试库必须先安全隔离。
5. 全 C# / Python / 差分 suite、测试机生产形状部署、备份恢复/回滚、负载长稳、安全自查均未完成。
6. 未确认可用的生产 dump **副本**位置及所需容量；生产机仅只读，不通过读取生产凭证绕过限制。

数据/任务详细复核：[data-job-parity-audit-20261110.md](data-job-parity-audit-20261110.md)。

更新本表时必须附具体 commit、命令、通过/失败/跳过数及环境；如状态改为“不适用”，必须记录逐项原因和明确的产品接受决定。
