# 生产替换审计：2026-11-10

> 状态：第一阶段诊断进行中，**尚不具备替换生产 Python 的证据**。
> 本报告不是发布批准；README 的 WIP / 不能替代生产表述保持不变。

## 本轮收尾更新

用户准备关机，已停止派新任务，不开始远程部署/压测。以下更新优先于下文第一阶段历史状态：

- HTTP 审计工具已 review 合并；原工具提交 `985f596` 经 `d72e8e5` / `d678f60` 修正，再由主提交 `8b0b940` 修正“含 HTTP 的测试函数”误计。工具自测 **20 通过**。
- 正确静态分母为 **192**（core 160 + conditional 5 + optional 27），Python 静态命中 139，C# 138，两侧均命中 102；C# 无静态命中 54；Python/C# unresolved 5/1。静态命中不是执行成功，未命中不是证明无测试。
- 核心 Python/C# 命中 117/127，可选 22/6，通知 19/14，认证 12/9；archive import 3 条与 storage pool 18 条没有 C# 静态 HTTP 命中。逐路由调用及函数级断言证据可由 `tools/Audit-TestParity.py` 重建。
- 测试端口隔离分支 `test/readiness-isolation-20261110` 已合并；主提交 `c596089` 修复默认端口测试污染环境变量、恢复 Compose 原默认 project name，补充宿主测试需显式导出相同端口的说明。
- 合并后 `dotnet build Shoreline.sln --nologo` 为 **0 warning / 0 error**；TestEnvironment / Security / StorageCompatibilityUnit / Email / StoragePool / Language / LegacyType 聚焦合集 **99 通过 / 0 失败 / 0 跳过**，TRX 为批准 Temp `moeflow-discovery/merged-isolation-ready.trx`。
- 首次合并复验因依赖容器停止，**40 通过 / 59 失败**，保留 `merged-isolation.trx`。检查到容器退出不是 OOM；在同一 WSL 会话内启动本轮依赖并调用 Windows dotnet，复验 99/99 通过。独立单元另有 40/40 的 `merged-isolation-unit.trx`。
- 不因聚焦绿色修改全量 suite / 差分 / 远程验收状态；最终镜像与 bootstrap 分支结果以关机交接为准。

复现已合并聚焦合集：先按 `docs/test-isolation.md` 启动独占依赖，导出该栈的五个 `SHORELINE_TEST_*_PORT`，再执行：

```powershell
dotnet build Shoreline.sln --nologo
dotnet test Shoreline.sln --no-build --nologo --filter "FullyQualifiedName~TestEnvironmentTests|FullyQualifiedName~Shoreline.Tests.SecurityTests|FullyQualifiedName~StorageCompatibilityUnitTests|FullyQualifiedName~EmailTests|FullyQualifiedName~StoragePoolTests|FullyQualifiedName~LanguageTests|FullyQualifiedName~LegacyTypeTests"
python -m unittest discover -s tools/tests -p "test_*.py"
python tools/Audit-TestParity.py --python-root ../shoreline-moeflow-backend --csharp-root . --output /path/to/approved-temp/parity.json
```

WSL 后台容器可能在没有 Linux 会话时随 WSL 停止；验收前检查容器状态及宿主端口，不以旧 healthy 记录替代当前检查。

## 1. 基线与执行边界

- Python：同级 `shoreline-moeflow-backend`，`dev`，`be0b717`；初始工作树干净。
- C#：本仓库 `main`，`c29888e`；初始工作树干净。审计时不得覆盖未提交工作。
- 第一阶段没有修改业务实现，没有向远程 push，没有启动新的依赖栈。
- 不读取 `.env*`、生产环境变量或凭证。生产 SSH 仅采集操作系统和容器资源快照；不修改配置、容器或数据库。
- 测试机已有隔离栈不是本轮创建的资源；未经确认不删除其数据或重建数据库。
- 每个子代理独占 worktree、分支和端口段；合并前须 review、构建和聚焦测试。
- 成本约束：后续子代理默认 `opencode-go/beam-free`，小范围派发；复杂复核可用用户明确确认免费的 `agrt-gateway/gpt-6.1-sol`，不再新增收费模型。

## 2. 当前证据，不把发现数当通过数

| 检查 | 结果 | 边界 |
|---|---|---|
| 三个首轮审计 worktree 的 `dotnet build Shoreline.sln --nologo` | 各自 0 warning / 0 error | 只证明该基线本机可构建；不证明 Dockerfile 可构建 |
| 本机测试发现 | 837 个用例 | `--list-tests`，执行数为 0 |
| 独立单元聚焦测试 | 18 通过，0 失败，0 跳过 | 只覆盖 `SecurityTests`、`StorageCompatibilityUnitTests` |
| 首次宽泛 Golden 过滤 | 23 通过，9 失败，0 跳过 | 9 个用例因 collection fixture 连接缺失 Mongo 失败；另有 cleanup `NullReferenceException`；不是业务差分结果 |
| C# 全量、Python 全量、真实差分全量 | 未运行 | 必须先建立安全、独占、可复现的测试环境 |
| 镜像、部署、负载、长稳、备份恢复、回滚 | 未验证 | 源码中的文档和旧测试数字不能替代执行证据 |

复现（C# 仓库根目录，SDK `10.0.204`）：

```powershell
dotnet build Shoreline.sln --nologo
dotnet test Shoreline.sln --list-tests --nologo
dotnet test Shoreline.sln --no-build --nologo --filter "FullyQualifiedName~Shoreline.Tests.SecurityTests|FullyQualifiedName~Shoreline.Tests.StorageCompatibilityUnitTests"
```

本轮 TRX / 发现日志在本机批准的临时目录
`C:\Users\88888888\AppData\Local\Temp\opencode\moeflow-discovery`：
`discovery-baseline.log`、`pure-baseline.trx`、`unit-baseline.trx`。
它们未作为仓库内可移植验收产物归档，不能当作 CI 或生产机结果。

## 3. 路由装配与测试意图

- 首轮 `tools/Audit-LegacyRoutes.py` 的静态结果：核心有效路由 160，匹配 156，缺失 4；四个可选模块有效路由 27，匹配 27；另有 2 个注册但无方法实现的 405 契约。
- 上述是静态注册匹配，不是 HTTP 行为或授权正确性的证明；需真实运行时注册表及逐路由 HTTP 测试复核。
- `src/Shoreline.Api/Program.cs:48-54` 注册四模块服务，`src/Shoreline.Api/Program.cs:154-157` 映射四模块 endpoint；不能把已有实现重复判成缺失。
- MIT 另有条件装配：`src/Shoreline.Api/Program.cs:158-162`，配置 `Mit:StorageRoot` 或 `MIT_STORAGE_ROOT`，排除 `/MIT_STORAGE_ROOT_UNDEFINED`。必须分别验收启用和禁用形态。
- `ZitengPartnerTests` 已有 HTTP 用例，包括 config 授权、disabled key、外部用户、POST shape、项目不存在错误码。首轮按 `*HttpTests.cs` 文件名猜测覆盖不可靠，已纠正。
- archive import / storage pool 的直接 HTTP 覆盖是重点候选缺口；最终清单须经全测试调用解析和人工复核，不能只凭测试类名判断。
- 第二轮 beam-free 的 `tools/Audit-TestParity.py` 提交暂不合并：漏 `PostAsJsonAsync` / `PutAsJsonAsync` / `PatchAsJsonAsync`、重复计算同一调用、未剥离 query、模块分类不一致，且把部分非有效业务路由混入分母。其 327 / 94 / 68 等覆盖数字不是接受的验收证据。
- 修正工具须保留动态调用未解析清单，输出 method / route / file:line / test function / assertion 意图，并明确“静态命中”不等于“测试通过”，“未命中”不等于“没有测试”。

### 待产品确认的例外（目前全部未批准）

| 路由或功能 | Python 参考 | 现状 / 拟处理 |
|---|---|---|
| `GET /docs/<path:path>` | `app/apis/urls.py:121` | C# 未注册；诊断文档路由，不能直接从分母消失 |
| `GET /error` | `app/apis/urls.py:126` | C# 未注册；错误诊断路由，待决定实现或明确例外 |
| `GET /warning` | `app/apis/urls.py:129` | C# 未注册；诊断路由，待决定实现或明确例外 |
| `GET /urls` | `app/apis/urls.py:134` | C# 未注册；路由索引，待决定实现或明确例外 |
| OCR | `app/tasks/ocr.py:421` 抛 `NotImplementedError("OCR under reconstruction")` | Python 主动禁用；须逐项核对 HTTP 返回，不伪造 OCR 成功 |
| 自定义角色注释注册 | `app/apis/urls.py:564-569` | Python 已注释禁用，不能作为有效业务路由；保留解释 |
| application PUT / project-zips GET | Python 注册但对应方法未实现 | 保留 405 契约，不把它们当作待补成功业务 |

## 4. 真实运行时差分缺口

现有 `tools/legacy-reference/fixtures/runtime_flask_reference.json` 只有 8 个请求：
读 ping / 用户资料 / 团队 / 认证校验 / term banks / applications，另有 POST ping 的 405。
全部数据库 digest 不变；没有有效写入，也没有四个可选模块。

- 同种子回灌、前后 digest 框架已有，但种子只有 7 个 collection：application 1、language 2、team 1、team_member 2、team_role 5、term_bank 1、user 2。
- C# replay 用 C# 自己生成的 bearer；不证明 Python 发出的真实 token 可被接受。
- 仅覆盖 `Content-Type` / `X-Pagination-*` 等少量 headers；错误、认证、上传、下载和并发契约不足。
- `tools/legacy-reference/runtime_reference.py:75-90` 的复制允许目录不含实际可选模块实现，不能声称完整 Flask app 的可选模块已被差分测试。
- `docs/runtime-differential.md` 中历史 330 用例和旧快照结果，不是本基线全量证据；已有 framework / wire 差异也必须继续记录，不删除字段来变绿。

必须扩展：真实 Flask 与 C# 同一批请求、真实 Python token、读写和可选模块、失败/越权路径；比较 status、完整 body、业务错误码、契约 headers、数据库前后 BSON 类型保持的 digest。动态 ID / 时间的处理规则必须显式且经 review，不能忽略业务字段。

## 5. 数据与任务审计状态

- 已完成 20 个真实 Celery decorator 的静态映射和关键数据路径复核，详见 [data-job-parity-audit-20261110.md](data-job-parity-audit-20261110.md)；全 collection / alias / BSON 类型 / index / unique 及历史数据运行时矩阵仍未验收。
- 首轮数据报告包含未证实的任务和 seed 猜测，已拒绝作为缺陷证据；不能因此重写既有功能。
- 已确认必须显式评估旧 Python `OSS_KEY_PREFIX` 默认 `storage/` 与 C# 默认空 prefix；真实生产为 Cloudflare R2，不可把 S3Mock 测试当作 R2 签名验证。
- `file_target_cache`、project `m_e` / projectset `et`、`output.file_name`、角色默认权限等必须用当前 Python 源码和测试核对，不沿用历史快照结论。
- Mongo standalone 跨文档写入不能直接宣称 exactly-once；unknown commit、lease 续约/超时、stable job/item IDs、owner CAS 锁释放、重试和崩溃恢复必须有故障测试。
- `src/Shoreline.Api/Program.cs:50-54` 默认 `StoragePool:WorkerEnabled=false`；上线形态需要明确开启 handler 或产品接受替代，不能以 HTTP 路由存在代替后台任务验收。
- 确认普通创建忽略 LabelPlus、fresh install 缺 admin/default team/settings 持久化、用户/日志邮件开关不一致、新 C# 安全码回滚 Python 无法读取，以及启用 MIT 后缺外部消费者；不伪造成功或将条件功能默认为不适用。

## 6. 测试环境隔离前提

### C#

`tests/Shoreline.Tests/LocalServices.cs:44-55` 及多个独立 fixture 硬编码 Mongo `27128`、S3 `29128`、SMTP `21128` / Mailpit `28128`。
直接并行会共用依赖；须统一 test-only 可配置入口、逐个移除硬编码，并测试实际使用的配置。
`compose.test.yml:1` 的固定 project name 也需独占，且显式非 dotenv 名称的 `--env-file` 防止 Compose 自动读 `.env`。
只允许清理本轮随机名测试数据库和本轮创建的容器/网络。

### Python

- `app/config.py:9` 默认 `dotenv.load_dotenv()`，不能直接 import 原工作目录的 app。
- `pytest.ini:11` 为 `env_files = .env.test`，须禁用 dotenv 自动加载。
- `tests/__init__.py:31-39` 先 create app，再检查 `*_test`，随后 `drop_database`。单有 `_test` 后缀不足以证明本轮所有权，且初始化写入可能早于 guard。
- `conftest.py:18-35` 编译 `.po` 为 `.mo`，须在不含 env 文件的公开源码隔离副本运行，不覆盖原目录 catalog。
- 后续全 suite 必须使用 disposable 配置、独占随机测试库、隔离存储路径和 broker；不削弱原测试断言。

## 7. 运维 / 安全静态发现

| 发现 | 证据 | 验收要求 |
|---|---|---|
| Docker 构建上下文缺 Imaging 项目 | `Dockerfile:4-7` 只 COPY Api；`src/Shoreline.Api/Shoreline.Api.csproj:19` 引用 Imaging | 修复复制/restore/publish后实际构建镜像 |
| 镜像 tag 浮动 | `Dockerfile:1,9` 使用 `10.0` | 固定版本及 digest，记录更新流程；lock restore 不能替代固定基础镜像 |
| 非 root 仅静态 | `Dockerfile:12` 为 `USER $APP_UID` | 容器内验证 UID、目录权限和 local/object storage 写入 |
| readiness 仅 Mongo ping | `src/Shoreline.Api/Program.cs:163-168` | 明确关键依赖和失败返回；容器/反代 healthcheck、startup 验证 |
| 配置失败可能延迟到首请求 | `ObjectStorage` 注册未主动解析；startup 有多处写入初始化 | 启动前完整校验、disposable 无凭证负测、避免无效配置先写 DB |
| endpoint 限流顺序待运行时验证 | `src/Shoreline.Api/Program.cs:97` UseRateLimiter 在 `src/Shoreline.Api/Program.cs:121` UseRouting 前；Email/User endpoint RequireRateLimiting | 真实 HTTP 请求验证第 21 次及窗口/分区，不能仅测 limiter 类 |
| 反代身份不明确 | 无显式可信 ForwardedHeaders；PartnerSearch 直接读 XFF / client_key | 限定 trusted proxy、抗伪造测试；不要信任任意头 |
| 上传资源放大 | File / ProjectImport 允许 1 GiB | 保持 Python 业务语义并建立并发、流式处理、反代/API 资源上限测试 |
| SMTP TLS 形态 | `SecurityEmailHandler.cs:24-32` StartTls=false 使用 None | production fail-fast / 必须 TLS；Testing Mailpit 显式例外 |
| Ziteng HTTP client 的 SSRF 防护需补证据 | `Modules/ZitengPartner/ZitengPartnerRegistration.cs:23-31`、`Modules/ZitengPartner/ZitengPartnerSettings.cs:38-55` | allowlist、DNS/连接地址、重定向、防私网及配置更新路径测试 |
| 可观测性 / 安全响应策略不完整 | request error 只记录类型，未见 metrics exporter | 无密钥日志、request correlation、指标/告警和安全策略实际验收 |

备份恢复、TLS/反代、资源限额、迁移与回滚的生产 runbook 尚待本轮实际演练支持。
旧部署目录中的 nginx 模板不是生产生效配置，不能用模板推断生产 TLS 状态。

## 8. 容量与远程演练边界

- 本机 WSL Debian：23 GiB RAM / 20 GiB available，16 核；Docker 26.1.5，Compose v2 `2.26.1-4`（支持顶层 `name:`）；未发现 Linux dotnet，首轮构建使用 Windows SDK。
- 测试机 `ssh -p 50600 root@103.195.188.139`：2 核、1.9 GiB RAM、3.3 GiB swap；磁盘约 2.1 GiB available / 85% used。已有 `/root/moeflow-csharp-isolated` 栈；不能擅自 prune、down -v、删除 dump 或重建其数据库。
- 生产 `ssh -p 23213 root@dash.usag.cc`：仅只读资源快照。Python web 约 258 MiB，三类 Celery worker 合计约 477 MiB；测试机旧 C# 约 163 MiB。不是同等负载对比，不能据此宣称节省内存或吞吐更好。
- 后续新隔离栈只用本轮资源和开放端口 `50601-50609`；部署前复核容量、占用及测试 dump 副本位置/使用授权。

## 9. 代理分支与 review 状态

| 独占 worktree（批准 Temp 下） | 分支 | 模型 / 状态 |
|---|---|---|
| `moeflow-readiness-http` | `audit/readiness-http-20261110` | beam-free 首轮及工具；因工具误计，已启动 deepseek-v4.1-flash 修正复核；`985f596` 尚未合并 |
| `moeflow-readiness-data-jobs` | `audit/readiness-data-jobs-20261110` | beam-free 首轮；deepseek-v4.1-flash 复核完成；已保留并移动到 workspace 内，由 beam-free 补齐独占路径 LSP / build；无提交 |
| `moeflow-readiness-ops` | `audit/readiness-ops-20261110` | beam-free 静态审计完成；已保留并移动到 workspace 内，补齐独占路径 LSP / build；无提交 |

首轮备用端口：N=0/1/2 分别使用 Mongo `27201/27211/27221`、S3 `29201/29211/29221`、SMTP `21201/21211/21221`、Mailpit `28201/28211/28221`、API `25201/25211/25221`；本轮未监听。

下一步：接受纠正的逐路由测试与数据/任务矩阵，形成不重叠的 3–6 工作流；先完成 test-only 隔离前提，再派 beam-free 小任务，逐提交 review / 合并 / 验收。

## 10. 夜间更新（bootstrap 与诊断路由）

- 参考 Python 固定为 `be0b717`（含 label snapshots）。用该提交的独立 worktree 重新生成 `docs/route-coverage.md`：核心有效路由 **160/160**，可选模块 **27/27**，MIT 条件路由 **5/5**，缺失 **0**；此前 4 条缺失均为根诊断路由。
- 4 条诊断路由已由 `LegacyDiagnosticsEndpoints` 实现，`LegacyDiagnosticsTests` 断言 `/urls` 200、`/error` 500、`/warning` 500、缺失文档 404。
- `BootstrapService` 已实现 Python `init_db` 等价流程；`Testing` 环境默认关闭，部署默认开启。`BootstrapTests` 4/4。
- 当前工作树全量：**888 passed / 0 failed / 0 skipped**（10 m 01 s，TRX `/tmp/opencode/full-post-bootstrap.trx`），包含 8 例 Python/C# 差分（0 business differences）。
- 尚未完成：测试机空库 HTTP 启动/登录、迁移/备份/回滚、完整差分与负载长稳。
