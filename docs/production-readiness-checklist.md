# 生产替换硬门槛检查表

日期：2026-11-10（晚间更新）。Python 参考 `be0b717`；C# 主分支 `2ab6863`（差分扩展）+ 本文件更新。

**发布结论：阻塞，不能替换生产 Python。**
“已验证”只代表表中具体证据；静态实现、测试发现、旧快照绿色结果均不等于生产验收。
本表当前没有产品批准的“不适用”项。README 的 WIP 表述不得提前移除。

详细诊断：[readiness-audit-20261110.md](readiness-audit-20261110.md)。

| 硬门槛 | 状态 | 证据 / 阻塞 / 仍需执行 |
|---|---|---|
| 核心有效路由注册 | 已验证（静态 + 测试机抽查） | 静态 160/160；`LegacyDiagnosticsEndpoints` 的 `/urls`、`/error`、`/warning`、`/docs/<path>` 已在测试机容器内经 nginx 返回 200/500/500/404，`/ping`、`/health/*`、`/v1/site/homepage` 均为 200 |
| 全核心路由 C# HTTP 行为与权限 | 未验证 | 静态审计命中 160/160；不等于 HTTP 执行通过，未跑全量 |
| archive_import HTTP | 未验证 | 已装配；需 request/body/errors/auth/digest 测试 |
| partner_search HTTP | 未验证 | 已有 HTTP 测试；需全契约、写/索引排序和 Python oracle |
| storage_pool HTTP | 未验证 | 已装配；需完整 admin HTTP 及权限/任务状态测试 |
| ziteng_partner HTTP | 未验证 | 已有 HTTP 测试；不能误报全缺失；需全契约及安全检查 |
| MIT 条件模块 | 未验证 | 启用/禁用装配及 Python 契约待执行 |
| OCR / 405 / 注释禁用路由的逐条例外 | 未验证 | 已记录候选原因，尚无产品批准；须核对真实响应 |
| 真实 Flask / C# 同种子 BSON | 未验证 | 旧 fixture 有局部种子；需完整 app / modules、可复现 fresh run |
| 真实差分读与成功写 | 部分验证 | 真实 Flask 生成 19 个只读用例，C# replay **19/19 匹配，0 business differences**（`docs/runtime-differential.md`）；写请求仍未纳入差分，测试机 E2E 已覆盖建团队/项目/术语库/上传/源文/译文/导出/邀请/申请审批/密码重置 |
| 差分 status/body/error/headers/前后 digest | 已验证（只读 19 例） | 19 例 status/body/前后 digest 全匹配；框架级 wire 差异单独记录；写请求和 4 个可选模块仍未覆盖 |
| BSON collection/alias/type/index/unique | 部分验证 | Mongo 4.4.1 实测发现两处 `$lookup` 同时使用 `localField`+`pipeline`（5.0 才支持），成员列表 500；已改为 `let` 形式并回归（[mongodb-compatibility.md](mongodb-compatibility.md)）。 三个 team_member 索引（含 `team_member_alias_search_v1` on `{team,status,asrch}`）由 `MongoSchemaReadinessTests` 验证；冲突索引拒绝替换；测试机空库 BSON 字段与 Python 对齐。生产 dump 副本上的全集合对比未执行 |
| Python auth token 互操作 | 未验证 | 旧 replay 使用 C# 自签 token；需真实 Python token |
| storage key/prefix/R2 | 未验证 | Python 默认 storage/，C# 默认空；真实 R2 尚无验证；不用生产凭证 |
| 设置、语言/类型/角色种子 | 部分验证 | bootstrap/`LegacyTypes`/`LanguageCatalog` 已与 Python 逐项复核，测试机空库初始化通过；逐个种子集合的完整 BSON 差分仍未执行 |
| 管理员/默认团队初始化 | 已验证（测试机空库） | 测试机 `moeflow` 原为空库；新镜像启动日志 `Created bootstrap administrator …`、`Created bootstrap Default Team …`，二次重启无重复创建；验证码 + 密码登录 `POST /v1/user/token` 成功，`/v1/user/info`、`/v1/user/teams` 正常 |
| 迁移及回滚数据兼容 | 部分验证 | 邮件/安全码已双写：C# 签发时同时写入 Python `v_code`，验证消费后删除；测试机实测重置密码验证码在 `v_code` 可见、消费后为 0。完整 schema/数据迁移与回滚仍未演练 |
| 每个 Celery 任务对应 handler/接受替代 | 未验证 | 静态映射 20 个真实 task；普通创建 LabelPlus 已由 `ba56379` 接入并 focused 61/61；MIT 缺消费者，storage-pool worker 存在但默认关闭；其他 handler、队列等价及产品接受仍未验收 |
| 重试/lease/幂等/崩溃恢复 | 未验证 | 需全 handler 及 unknown commit / stale lock / worker crash 故障证据 |
| 本机基线 dotnet build 0 warning/0 error | 已验证 | 当前 `bc9557f` 执行 `dotnet build Shoreline.sln --nologo` 成功，0 warning / 0 error |
| 最终合并后的 dotnet build 0/0 | 已验证 | `2650a74` + bootstrap/诊断工作树：`dotnet build Shoreline.sln --nologo` 成功，0 warning / 0 error（2026-11-10） |
| C# 全量 suite | 已验证 | `505ffa7`：`dotnet test tests/Shoreline.Tests/Shoreline.Tests.csproj --no-build`，**888 passed / 0 failed / 0 skipped**，8 m 38 s；五端口隔离服务（Mongo 27241 / S3 29241 / SMTP 21241 / Mailpit 28241 / API 25241）。包含 19 例 `LegacyRuntimeDifferentialTests`，0 business differences |
| Python 全量 suite | 已验证 | `venv-moeflow/bin/pytest -q`：**836 passed, 48 subtests passed**, 446 s（`.env.test`，mongomock/memory broker/local storage） |
| 真实差分全量 suite | 部分验证 | 只读扩展 19/19、0 business differences；写路径和可选模块差分未覆盖，不能称全量 |
| 可复现镜像 / lock / digest | 部分验证 | `bc9557f` 内容两次 `--no-cache --build-arg SOURCE_DATE_EPOCH=0 --platform linux/amd64` 冷构建 ImageID 均为 `sha256:7a1936a2db68f84572a0ff41ce701f162b9f4873ef9361234faed19ebedd662e`，层摘要相同；SDK 10.0.403/runtime 10.0.8 按 digest 锁定。仅同 builder、平台和源树，不代表跨构建机或跨平台 |
| 实际非 root / 文件权限 | 已验证（镜像 + 测试机） | 镜像与进程 UID 均为 1654；测试机 bind mount `./storage` 初始为 root:root 755，首次上传 500 `UnauthorizedAccessException`，`chown -R 1654:1654 storage` 后上传/缩略图/导出全部通过。部署文档必须包含该 chown |
| health / readiness / 配置校验 | 部分验证 | 最终镜像 `/health/live` 与 Mongo-backed `/health/ready` 均 HTTP 200；配置校验矩阵、反代及部署形状仍未验收 |
| 日志 / metrics / 告警 | 未验证 | 需可观测性、隐私与故障告警验证 |
| 备份 / 副本恢复 / 恢复后的业务 smoke | 已验证（测试机） | `mongodump --gzip --archive` 13 KB / 158 文档；恢复到 `moeflow_restore` 后启动只读验证容器，登录/团队/成员/项目/文件/术语库均 200；验证后 drop 临时库。未在生产数据规模上计时 |
| TLS / 反代 / trusted proxy | 未验证 | 不读取生产 env/凭证；需 disposable 测试栈验收 |
| 资源限制 / graceful stop / rollback | 未验证 | 测试机可用磁盘约 2.1 GiB，需安全隔离部署及回滚演练 |
| 测试机负载 / 长稳 / 内存比较 | 未验证 | 现有资源快照不是等负载基准；需约定时长、数据和通过阈值 |
| 密钥与依赖安全 | 未验证 | 首轮依赖工具未报 vulnerable，不等于完整安全通过；禁止读取真实密钥 |
| 认证 / 越权 / 授权竞争窗口 | 未验证 | 需按 Python oracle 验证所有修改/删除/管理端及并发路径 |
| SSRF / allowlist / DNS / redirects | 未验证 | archive 已有防护；Ziteng 和全部出站路径需源码及 HTTP/连接测试 |
| 上传限制 / 内存放大 | 部分验证 | 测试机真实 multipart 上传图片成功，缩略图 worker 生成 cover/resample，导出 zip 可下载；1 GiB 边界与并发资源仍未验证 |
| 真实 HTTP 限流 / XFF 防伪造 | 未验证 | limiter/routing 顺序、反代身份和 client_key 均需真实请求验证 |
| 部署 / 迁移 / 回滚 runbook | 未验证 | 需与本轮命令、镜像、备份和演练证据一致，非纸面声明 |

## 明确阻塞

1. 真实运行时差分只覆盖 19 个只读请求；成功写、邮件、四可选模块、真实 OSS/R2 尚未纳入。
2. 静态路由 160/160 已覆盖，但全路由 HTTP 行为/越权矩阵未执行；20 个 Celery task 的故障/重试/幂等证据不足，MIT 缺外部消费者。
3. 最新镜像 `shoreline-moeflow:ready-20261110` 只做过单次构建；未重跑双构建 digest 一致性，也未在 CI 中复现。
4. Python/C# 全量 suite 已绿，但测试机只做了单用户 E2E；第二用户邀请/申请/密码重置邮件、跨天 StoragePool、并发/长稳未验证。
5. 备份恢复、迁移回滚、生产 dump 副本上的 schema/索引校验未执行；生产机仅只读参考。
6. 可观测性、限流/XFF、安全自查与部署 runbook 仍未完成。


## 测试机生产形状部署（2026-11-10）

镜像 `shoreline-moeflow:ready-20261110d`（ImageID `sha256:d0edc71ff1b8abf4d9a96900ef5336e11ef8dd2fdb56eebd5b2646b4b93def8b`）经 `docker save | ssh | docker load` 部署到 `103.195.188.139:50600` 的 `/root/moeflow-csharp-isolated`（Mongo 4.4.1、Local storage、nginx 反代、无 RabbitMQ）。

已执行的端到端路径（全部经 nginx `http://127.0.0.1:5080/api`）：

1. 空库启动：自动创建管理员、`Default Team`、默认项目集、creator 成员和 `site_setting.ajt`；重启后不重复创建。
2. 登录：`POST /v1/captchas` → 直接读 `csharp_captcha.digest` 反解 4 位码 → `POST /v1/user/token` 返回 token。
3. 团队/项目：`POST /v1/teams`、`POST /v1/teams/<id>/project-sets`、`POST /v1/teams/<id>/projects` 均成功，返回结构与 Python 紧凑 BSON 兼容。
4. 术语库：`POST /v1/teams/<id>/term-banks` 成功。
5. 文件：multipart `POST /v1/projects/<id>/files` 上传 PNG，`/storage/files/...` 原图 200，worker 生成 `cover-*.webp`/`resample-*.webp` 且 `thumbnail_status=3`。
6. 标注：`POST /v1/files/<id>/sources` 新增原文，`POST /v1/sources/<id>/translations` 新增韩语翻译。
7. 导出：`POST /v1/projects/<id>/outputs` 为两个 target 建任务，轮询 target outputs 到 `status=4`，zip 下载 200，含 `translations.txt`、`project.json`、`images/smoke.png`。
8. 第二用户：管理员 `POST /v1/admin/users` 创建 `SmokeTwo`；owner `POST /v1/teams/<id>/invitations`（role member），第二用户登录后 `PATCH /v1/invitations/<id>` `{allow:true}` 接受；`user_count=2`，成员列表返回两人，重复邀请返回 `UserAlreadyJoinedError` 5003。
9. 密码重置邮件：Stack 临时加入 `axllent/mailpit:v1.27` 与 `Mail__Host=mailpit`/`Mail__Port=1025`；`POST /v1/reset-password-codes` 后 Mailpit 收到主题“重置您的密码”和 6 位验证码；`DELETE /v1/user/password` 成功，旧密码登录 400，新密码登录 200。
10. 项目/团队申请：`SmokeThree` 登录后 `POST /v1/teams/<id>/applications`，owner `GET /v1/teams/<id>/applications` 看到 pending，`PATCH /v1/applications/<id>` `{allow:true}` 通过；第三人 `GET /v1/user/teams` 出现 `SmokeTeam`，成员数 3。
11. 备份/恢复：`mongodump --gzip --archive` 导出 `moeflow`（158 文档），`mongorestore --nsFrom=moeflow.* --nsTo=moeflow_restore.*` 恢复成功；用同一镜像、`Bootstrap__Enabled=false`、`moeflow_restore` 连接串启动第二个容器，登录、团队/成员/项目/文件/术语库读取全部 200；结束后删除容器并 `dropDatabase`。
12. Mongo 4.4.1 实测：第二成员加入后 `GET /v1/teams/<id>/members` 首次返回 500（`MongoCommandException`），原因是 `$lookup` 同时使用 `localField` 和 `pipeline`（Mongo 5.0 才支持）；改为 `let` 形式后返回 200。详见 [mongodb-compatibility.md](mongodb-compatibility.md)。

部署前置条件（必须写入 runbook）：

```bash
# 容器进程 UID 1654，bind mount 目录必须先交给该 UID
chown -R 1654:1654 /path/to/stack/storage
```

权限未修正时首次上传返回 500，日志为 `Request failed (UnauthorizedAccessException)`；修正后同一请求 200。挂载目录不能是 root:root 755。

`docker-compose up -d` 重建后端容器后必须重启 frontend：nginx 在启动时解析 `moeflow-csharp` 上游地址，容器 IP 变化后不重启会持续返回 502（本次实测）。命令：

```bash
docker-compose restart moeflow-frontend
```

本次未覆盖：项目申请/审批、修改邮箱、OCR、MIT 外部消费、跨天 StoragePool、备份恢复、并发与长稳。

数据/任务详细复核：[data-job-parity-audit-20261110.md](data-job-parity-audit-20261110.md)。

更新本表时必须附具体 commit、命令、通过/失败/跳过数及环境；如状态改为“不适用”，必须记录逐项原因和明确的产品接受决定。
