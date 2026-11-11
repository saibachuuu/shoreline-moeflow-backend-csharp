# 预发环境 walkthrough（2026-11-11 之后要做的两件环境事）

两个门禁不需要改代码，只需要**不涉及生产写入**的独立环境。本文按可执行顺序写，每步都有
「通过标准」和「不通过时怎么办」。

前置：C# 镜像已按 digest 部署在预发；预发有独立的 Mongo（或生产 dump 的副本库）、独立的
R2 桶（或 MinIO）、独立的 SMTP 中继。**不要复用生产桶/生产 SMTP 凭据。**

---

## A. 真实 R2 + 真实 SMTP 隔离验收

目标：证明签名 URL、multipart、CDN 直出、邮件投递在真实服务上成立，而不是只在 S3Mock/Mailpit 上成立。

| # | 操作 | 通过标准 | 不通过时 |
|---|---|---|---|
| A1 | 用预发 R2 凭据启动 api（`Storage__*` 指向预发桶，`Storage__Domain` 指向边缘 `/storage` 或 CDN 域名） | `/health/ready` 200 | 404/403：凭据或桶名错；503 `migrations-pending`：先跑 `Migrations__Mode=apply` |
| A2 | 上传一个 20 MiB 文件 | 200 + 返回 key/URL | 413：边缘 `client_max_body_size` < 20 MiB；500：R2 凭据或 CORS |
| A3 | 用返回的 URL 下载该文件 | 字节一致、`Content-Length` = 20 MiB | 403：签名过期或时钟偏差；404：`Storage:Domain` 与实际桶路径不一致 |
| A4 | 上传一个 200 MiB 文件 | 走 multipart（后端日志或 S3 请求可见 `CreateMultipartUpload`） | 若走单次 PUT：`content.CanSeek` 为 false（流式上传），记录为已知限制 |
| A5 | 触发一封通知邮件（团队邀请或项目通知） | 真实 SMTP 收到，内容与 `docs/notification-contract.md` 模板一致 | 认证失败：检查 `Mail__SecureSocketOptions`（默认 StartTls）与中继端口；465 端口需 `Mail__UseSsl=true` |
| A6 | 触发一封验证码邮件 | 收到，`v_code` 文档的 `c` 与邮件正文一致 | 未收到：验证码走机会式 STARTTLS（`app/tasks/email.py:99-104` 语义），中继不支持 STARTTLS 时应自动降级；查看 `SecurityEmailHandler` 日志 |
| A7 | 让签名 URL 过期（或把有效期临时改 60 秒重建） | 过期后 403 | 仍可访问：CDN 缓存了签名 URL，需在 CDN 侧配置不缓存带签名的查询串 |

记录：把 A2–A6 的实际返回码与耗时填进 `docs/replacement-acceptance-20261111.md` 第 4 节对应行。

---

## B. 生产 dump 副本迁移演练

目标：证明 C# 迁移链能在**真实数据形状**上跑通，并且回滚脚本能让 Python 版重新接管。

| # | 操作 | 通过标准 | 不通过时 |
|---|---|---|---|
| B1 | 把生产 dump 恢复到副本库（**不是生产库**），记下复制集/单机形态 | 恢复成功，`db.stats()` 集合数与生产量级一致 | — |
| B2 | 对副本库跑 `Migrations__Mode=dry-run`（或直接 `check`） | 列出 0000–0009，无异常；不写库 | 报错：把集合名/文档样例（脱敏）带回，属于迁移 bug |
| B3 | 切 `Migrations__Mode=apply` 启动 api | 10 条 `migration_record` 写入，`/health/ready` 200 | 中途失败：`migration_record` 里已成功的版本不会重跑，修复后重试即可 |
| B4 | 用老版 Python 代码（只读模式）连副本库做一次关键查询：项目列表、成员、文件、译文 | 全部可读，字段类型未破坏（尤其 `int` 与 `long`） | 字段类型变化：属于已知单向门，记录具体字段 |
| B5 | 跑 `tools/legacy-rollback-strip.js`（副本库） | 打印删除的 `migration_record` 行数（10）与 `migration_lock` 行数（1） | — |
| B6 | 再启动 Python 版（副本库，只读） | 启动成功，`--check` 通过（不再误报 pending） | 仍报 pending：`migration_record` 里还有 `runner:"csharp"` 行残留 |
| B7 | 记录耗时：B2/B3 各阶段时长、B3 对 `/health/ready` 的影响 | 冷启动窗口可接受（当前 0000–0009 在空库约 0.6 秒，真实数据量级需实测） | 若超过可接受的停机窗口，改用「先只读副本演练 → 生产低峰窗口执行」 |

**注意**：B5 之后副本库处于「Python 可接管」状态；如果还要再用 C# 验证，重跑 B3 即可（幂等）。

---

## C. 完成后

1. 把 A/B 两组的通过状态填进 `docs/replacement-acceptance-20261111.md` 第 4 节，并把第 6 节结论从
   「预发 GO / 生产 NO-GO」改为「生产 GO」。
2. 在 `docs/production-readiness-checklist.md` 里把「真实 R2 / SMTP」「生产数据迁移」两行改成「已验证」，
   并附上演练日期与执行人。
3. 任何一项不通过，保持 NO-GO，把失败现象写成新的 issue/blocker，不要用「代码看起来对」结案。
