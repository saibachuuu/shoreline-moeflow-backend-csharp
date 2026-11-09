# Shoreline C# 后端（开发中）

> **状态：开发中，不能替代现有 Python 后端，也不能用于生产部署。**
>
> 这个仓库的目标是用 ASP.NET Core / .NET 10 逐步重写 Shoreline 后端，同时兼容旧
> Python 服务写入的 MongoDB BSON、对象存储 key 和 HTTP 行为。当前已有若干可验证的
> 垂直切片，但还有许多并行模块仍在开发、未接线或未 review。

## 当前能做什么

已经完成并做过隔离验证的部分包括：

- 共享 HTTP、MongoDB、S3 客户端、持久化任务队列和基础安全设施；
- 登录、密码/token 兼容、验证码、用户资料、邮箱验证、密码找回、站点设置；
- 团队/项目身份权限、标签资格、成员管理、邀请和项目申请；
- 项目核心、项目成员和生命周期操作（`complete` / `reopen` / `clear`）；
- Presence 心跳与在线列表；
- 语言目录、公开类型选项、术语库路由；
- 存储兼容层：S3 兼容后端、Local 后端、对象 key 前缀和签名 URL；
- 项目内容清理：旧 `p` / `t` / `o` / `f` 字段、文件/输出对象删除、target 级联和
  owner claim（见 [docs/cleanup-compatibility.md](docs/cleanup-compatibility.md)）。

这些切片各有自己的验证快照，**测试数字不能相加**，也不代表把这些功能组合到同一个
共享工作树后仍然全部通过：

- C2 + Presence 隔离快照：210 tests；
- Storage 隔离快照：226 tests；
- F1 术语库隔离快照：241 tests；
- 项目清理隔离快照：239 tests（13 个 cleanup facts，full suite 239/239）。

## 还在 WIP、不要当成已完成

当前共享工作树里还有并行开发的 File、Label、Label Snapshot、Output、Import、
Notification、Storage Pool 等模块。它们可能是未跟踪文件、未注册路由或未 review 的
代码，**不能据此宣称这些模块可替代 Python**。

尚未完成或尚未验证的边界包括：

- 端到端兼容性和完整业务路由仍有缺口；静态路由存在不代表已经接线或行为兼容；
- `proofread` 图片、OCR、导入等后台任务仍有缺失；
- 真实生产配置适配、storage pool router、真实 R2 授权尚未验证；
- 跨 label/项目的共享锁和 expectedRevision 并发保护仍在后续阶段；
- 原生阿里云 OSS 签名未移植，相关配置会显式失败；
- 没有任何性能、内存或生产容量结论。不要引用早期空闲内存数字。

## 仓库结构

```text
src/Shoreline.Api/
  Features/        业务服务、endpoint 和垂直切片
  Infrastructure/  MongoStore、审计、权限错误、进程内 mutation gate
  Security/        身份、权限和兼容逻辑
  Storage/         ObjectStorage、S3/Local 后端、key 和签名
  Formats/         LabelPlus、输出归档、JSON 兼容格式
  Jobs/            持久化任务队列
tests/Shoreline.Tests/    xUnit 测试
tools/legacy-reference/   离线 Python AST 参考与 golden fixtures
docs/                     兼容性说明、快照和边界记录
compose.test.yml          本地临时 Mongo / S3Mock / Mailpit
```

## 本地环境

需要：

- .NET SDK 10.0.204，或 `global.json` 的 `latestFeature` 策略接受的更高 .NET 10
  feature band；
- Docker / Docker Compose，用于临时 MongoDB、S3Mock 和 Mailpit；
- 不需要 Python 才能启动 C# 服务。Python 只作为离线参考，见下文。

启动隔离测试依赖：

```bash
docker compose -f compose.test.yml up -d mongo s3 mailpit
dotnet restore
dotnet test Shoreline.sln
```

测试使用 `compose.test.yml` 中的临时服务：

- MongoDB：`127.0.0.1:27128`，随机测试库；
- S3Mock：`127.0.0.1:29128`；
- Mailpit SMTP `127.0.0.1:21128`，Web UI `127.0.0.1:28128`；
- `compose.test.yml` 里的 `api` 是可选 profile，端口 `25128`，只用于容器自测，
  不是生产镜像，也不要把它当成 `latest` 发布产物。

> **重要：** 当前共享工作树混有多个并行模块，直接 `dotnet test Shoreline.sln`
> 可能因为未接线/未完成的 source 而编译失败。上面的命令是本地复现方式；实际
> 验证某个切片时，应使用该切片对应的 commit 和隔离快照，不要把共享 WIP 混进去。

## 最小配置

服务从 ASP.NET Core 配置读取设置；环境变量使用双下划线，例如 `Mongo__Uri`。
不走 dotenv 文件。

至少需要：

- `Mongo__Uri`：必须包含数据库名；
- `Auth__Secret`：认证/兼容加密相关密钥；
- 对象存储：
  - Local：`Storage__Type=Local` + `Storage__Root`（绝对路径，仅本地开发）；
  - S3/R2：`Storage__Type=S3`（或 `R2`）、`Storage__Endpoint`、`Storage__Bucket`，
    以及访问凭据（`Storage__AccessKey` / `Storage__SecretKey`，按环境注入）；
    `Storage__KeyPrefix` 按部署显式设置。

不要把真实生产凭据写进仓库、文档、测试或示例配置。本地测试的 disposable 值只在
`compose.test.yml` / 测试 fixture 中使用。

### 从 Python 配置迁移时

- Python 的 `STORAGE_TYPE=OSS` 不等于“阿里云 OSS”。实际后端由
  `OSS_BUCKET_STYLE` 决定：`R2`、`S3`、MinIO 等应映射为 C# 的
  `Storage:Type=S3` 或 `R2`；不要写成 `Storage:Type=OSS`，C# 里 `OSS` 代表阿里云并会
  显式拒绝。
- 旧 Python 的 `OSS_KEY_PREFIX` 默认是 `storage/`。C# 的 `Storage:KeyPrefix` 默认
  为空，因此需要兼容旧 R2 布局时必须显式设置 `Storage:KeyPrefix=storage/`；否则旧对象
  会 404。
- 原生阿里云 OSS（`ALI` / `ALIYUN` / `ALIYUN_OSS` / `OSS`）尚未移植签名，会 fail
  closed；`aliyuncs.com` endpoint 也会被拒绝。详见
  [docs/storage-compatibility.md](docs/storage-compatibility.md)。

存储测试覆盖 key、分页、独立 SigV4 签名核对和 Local 行为，但 S3Mock **不校验
SigV4**，不能证明真实 R2 会接受请求。

## Python 参考与文档

Python 旧实现只作为离线参考。`tools/legacy-reference/` 使用 AST 提取和 golden
fixtures，不导入生产 `app`、不连接数据库、不读取 `.env` 或部署配置。安全模型和用法见
[reference-validation.md](docs/reference-validation.md) 和
[`tools/legacy-reference/README.md`](tools/legacy-reference/README.md)。

相关说明：

- [重构 checkpoint](docs/refactor-checkpoint.md)；
- [存储兼容层](docs/storage-compatibility.md)；
- [术语库兼容](docs/terms-compatibility.md)；
- [项目内容清理兼容](docs/cleanup-compatibility.md)；
- [Python 参考验证](docs/reference-validation.md)；
- [英文 README](README.md)（早期说明，部分数字和“未 port”描述已过时，以本文件和
  最新 docs 为准）。

docs 中的日期、测试数字和快照路径是当时的验证记录；它们不是当前共享工作树的实时
状态，也不要把不同快照的总数相加。

## 开发与提交约定

- 一次只提交一个经过隔离验证的切片；不要把并行 WIP 混进同一个 commit。
- 先本地 build/test 和 focused tests，再提交；提交信息说明兼容边界。
- 当前重写阶段只做本地验证和提交；推送、部署及生产切换需另行安排。不读取或复制
  真实生产凭据。
- 不根据单个切片的测试推断性能、容量或“完整替代 Python”。
- 共享工作树仍有未 review、未接线的模块；发现无关回归时先停下报告，不要顺手改别人
  的文件。

验证入口：[tests/Shoreline.Tests/](tests/Shoreline.Tests/)。项目文件是
[Shoreline.sln](Shoreline.sln)，SDK 锁定见 [global.json](global.json)，本地依赖编排见
[compose.test.yml](compose.test.yml)。
