# 关机交接：2026-11-10 生产就绪审计与第一波前置

> 用户要求“干完这波就收尾”，随后确认“继续收尾”。已停止派发新任务，不开始部署、压测或下一轮修复。
> **仍不能替换生产 Python**；README WIP 未改。不要把下面的聚焦绿色误称全量验收通过。

## 已完成并合并

工作根目录：`C:\Users\88888888\shoreline-moeflow\shoreline-moeflow-backend-csharp`。
Python oracle：同级 `shoreline-moeflow-backend`，`dev`，`be0b717`；未改 Python 业务/测试/配置。
C# 初始 `main c29888e`；本轮只本地提交、未 push。

| 主提交 / 合并 | 内容 |
|---|---|
| `6bc67a4` | 诊断底稿、生产硬门槛状态表 |
| `2306053` | 20 个真实 Celery task 映射、数据语义纠错与缺口 |
| `7bf41b7` | 合并 `audit/readiness-http-20261110`：`985f596` + `d72e8e5` + `d678f60`；拒绝首版误计后修正 |
| `8b0b940` | 主 review 修复 auditor 把非 HTTP 测试计入“含 HTTP 测试”，新增回归自测 |
| `test/readiness-isolation-20261110` 合并 | `94dac3a`、`8153bac`、`b115e4c`、`72e5f5d`、`34d71bb`、`a9aa095`；loopback 端口可配置、fixture 替换、Compose/文档 |
| `c596089` | 修复默认端口自测未恢复环境变量；恢复 Compose 原默认项目名，说明宿主配置与停止规则 |
| `cd79693` | 静态矩阵和合并复验记录 |

上次发现 837 仅为初始测试发现数。新增测试后总数会变化；本轮未运行全量。

## 本轮证据

| 验证 | 真实结果 | 范围 / 限制 |
|---|---|---|
| main 合并后 `dotnet build Shoreline.sln --nologo` | 0 warning / 0 error | 审计工具 + 测试隔离合并状态，不代表尚未合并分支 |
| auditor 自测 `python -m unittest discover -s tools/tests -p "test_*.py"` | 20 通过 / 0 失败 | 合成源码验证 AST/词法器/路由匹配，非 Flask 业务测试 |
| main 聚焦合集 | 99 通过 / 0 失败 / 0 跳过 | TestEnvironment / Security / StorageCompatibilityUnit / Email / StoragePool / Language / LegacyType |
| 首次 main 聚焦合集 | 40 通过 / 59 失败 | 依赖容器停止，Mongo loopback connection refused；未删证据，未弱化断言 |
| 修正 auditor 全源码离线运行 | 192 有效 legacy routes；Python/C# 静态命中 139/138；两侧均命中 102 | 不是 HTTP 执行；未解析 5/1，C# 无静态命中 54；可选模块仅 6/27 C# 静态命中 |
| 子代理 isolation TRX | 22/22 端口测试、42/42 宽泛单元过滤、59/59 依赖聚焦 | 42 的过滤比主的 exact SecurityTests 较宽；不要与主 40 混为同一集合 |
| C# / Python / 真实差分全量 | 未执行 | 保持未验证 |

主聚焦复现：按 [test-isolation.md](test-isolation.md) 起独占依赖并导出五个端口变量，再执行：

```powershell
dotnet build Shoreline.sln --nologo
dotnet test Shoreline.sln --no-build --nologo --filter "FullyQualifiedName~TestEnvironmentTests|FullyQualifiedName~Shoreline.Tests.SecurityTests|FullyQualifiedName~StorageCompatibilityUnitTests|FullyQualifiedName~EmailTests|FullyQualifiedName~StoragePoolTests|FullyQualifiedName~LanguageTests|FullyQualifiedName~LegacyTypeTests"
python -m unittest discover -s tools/tests -p "test_*.py"
```

本轮主验收端口：Mongo 27231、S3 29231、SMTP 21231、Mailpit 28231、API 25231（未起 API）。先检查端口是否仍由本轮容器拥有，不通过任意 URI 或 SSH tunnel 使用生产库。
TRX 时间（UTC+08:00）：首次依赖失败 16:51:28–16:52:04；纯单元 16:54:15–16:54:16；依赖就绪复验 16:56:09–16:56:29。三个结果均读取 XML Counters 核对，不以控制台截断推算。
WSL 空闲退出导致本轮容器停止，退出码 Mongo/Mailpit 0、S3 143、OOM=false；不能以 Windows 进程运行推断 Linux 依赖存活。
复验使用同一有界 Linux shell 起依赖并调用 Windows dotnet.exe，维持 WSL 会话，99/99 通过。

## 证据存放（未提交临时原件）

批准 Temp：`C:\Users\88888888\AppData\Local\Temp\opencode`。

| 文件 | SHA256 / 说明 |
|---|---|
| `moeflow-discovery\merged-isolation-ready.trx` | `4D0270FC7401FAABB0A32B50A35BCDB910E8A679635C4197403ABB8075AC5967`，99/99 |
| `moeflow-discovery\merged-isolation.trx` | `AE26013ADA3B8B26D8AAA5781F12FE7BB5D7A111C6AF9F0B3D66202BBF259904`，40/99，缺依赖失败 |
| `moeflow-discovery\merged-isolation-unit.trx` | 独立单元 40/40 |
| `readiness-http-audit\reviewed-parity.json` | `DCE2F947D8E3A289666047F3FD0EE894163D43B8E5224C40F146CAC940FB2485`，含逐路由调用与函数断言证据 |
| `readiness-http-audit\reviewed-parity.md` | 同批 Markdown；module hits 是调用数，不是路由通过数 |
| `testenvironment.trx` / `security-storageunit.trx` / `isolation-focused.trx` | 已读取真实计数 22/22、42/42、59/59 |
| `readiness-merge-check.sh` | 本轮 WSL 会话内有界验收脚本，disposable 常量配置，不含生产凭证 |

临时目录可能被系统清理；摘要与命令已入库。重验时新建独占资源和证据，不复用旧随机 DB。

## 未合并分支与保留工作区

| 分支 / 独占 worktree | 状态 |
|---|---|
| `audit/readiness-http-20261110` / `C:\Users\88888888\AppData\Local\Temp\opencode\moeflow-readiness-http` | 已合并，保留历史原版及修正提交；勿 reset/drop |
| `test/readiness-isolation-20261110` / `.readiness-worktrees/moeflow-readiness-isolation` | 已合并，保留；主的 review 修复在 main |
| `audit/readiness-data-jobs-20261110` / 原 data worktree 的历史分支 | 无代码提交；20-task 审计已写 docs |
| `audit/readiness-ops-20261110` / 原 ops worktree 的历史分支 | 无代码提交；诊断已写 docs |
| `fix/readiness-image-20261110` / `.readiness-worktrees/moeflow-readiness-ops` | **未合并**，`a93a1f3` 仅 WIP 文档；没有修 Dockerfile、没有镜像构建/非 root 验收，LSP 新任务证据缺失；明确不接受为完成 |
| `fix/readiness-bootstrap-20261110` / `.readiness-worktrees/moeflow-readiness-data-jobs` | 收尾结果另补，不能预先认定已合并或通过 |

`.readiness-worktrees` 位于 workspace 下但在主仓外，防 LSP project-directory guard 拒绝 Temp 路径；不要跨 worktree 改文件。
每 agent 独占端口段（Mongo/S3/SMTP/Mailpit/API）：data N1 `27211/29211/21211/28211/25211`，ops N2 `27221/29221/21221/28221/25221`，isolation N3 `27231/29231/21231/28231/25231`。

## 资源与关机

本轮创建并需停止的容器：

- `shoreline-readiness-isolation-20261110-mongo-1`
- `shoreline-readiness-isolation-20261110-s3-1`
- `shoreline-readiness-isolation-20261110-mailpit-1`
- `shoreline-readiness-bootstrap-20261110`

收尾已显式执行 `docker stop --time 15` 上述四个名字，再逐个 inspect 确认为 `status=exited oom=false`，没有删除或重建。
保留容器/网络/worktree/分支，不做 down -v、prune、删除库或 dump。tmpfs 中的测试数据在容器停止后不可靠，验证证据在宿主 Temp。
观察到另有 `debug-s3`（本日 08:41:47Z 创建，端口 29999），代理最终报告没有声明其来源；不擅自删除或变更，该容器检查时已退出。
不停止既有 `moeflow-deploy-*`、`ar-io-gateway`，不改 Docker daemon / WSL / 用户配置。
镜像任务没有创建 `shoreline-readiness-image:20261110`，没有 ImageID/UID 可验收。
生产和测试服务器在收尾阶段没有连接；生产始终只读，没有读取 `.env*`/生产 env/凭证。

## 继续工作的起点（下次用户恢复任务后）

1. 先读本交接及 [readiness-audit-20261110.md](readiness-audit-20261110.md)、[data-job-parity-audit-20261110.md](data-job-parity-audit-20261110.md)、[production-readiness-checklist.md](production-readiness-checklist.md)，再 status/log/worktree inventory；保留所有未提交工作。
2. review bootstrap 保存结果；有实际 code/test 证据再决定合并及 Program 装配，不从 WIP 文档推断功能完成。
3. 修复 Dockerfile 的 Imaging COPY/restore/publish 缺口，固定可验证版本/digest，实际 build/run nonroot；本轮未实现，仍阻塞。
4. 静态 C# 无命中重点：archive 3、storage pool 18、通知 12、认证 4，以及普通项目 LabelPlus、部分修改/删除/list/insight HTTP；以 Python oracle 验证，不削弱断言。
5. 修复 fresh init / code rollback / 邮件开关 / prefix / schema-index；MIT 需真实外部消费者或明确产品接受例外，不能伪造完成。
6. 安全隔离 Python full suite（不读取 dotenv、owned 随机 DB、隔离 catalogs/storage/broker）；扩展真实 Flask/C# 读写及可选模块差分，保留完整 BSON digest/headers/errors。
7. 全部本机门槛满足后才恢复测试机隔离部署、dump 副本、回滚、负载长稳/安全验收。测试机仅约 2.1 GiB 磁盘余量，旧栈数据删除/重建须先确认；生产只读。

成本偏好：小范围优先 `opencode-go/beam-free`；用户明确确认免费 `agrt-gateway/gpt-6.1-sol` 可用于复杂或低质量代理接替；不自动新增收费模型。
