# 镜像可复现性与首次启动冒烟

记录日期：2026-11-11。证据日志在构建机上：`/tmp/r4img-a.log`、`/tmp/r4img-b.log`、`/tmp/r4img-c.log`。

## 1. 构建输入已固定

`Dockerfile`（仓库根）：

- 构建阶段 `mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e`
- 运行阶段 `mcr.microsoft.com/dotnet/aspnet:10.0.8@sha256:8c0b6857eab7b2aa57884c839bf4678414606bd7d17370f18a842ac5cf414711`
- `RUN find /out -exec touch -h -d '@0' {} +` 把 publish 产物的 mtime 归零，避免 restore/publish 阶段的时钟差异进入层内容
- `dotnet restore` 用 `packages.lock.json`（`RestoreLockedMode`），NuGet 版本不会漂移

## 2. 两次冷构建的实测对比

两次都是 `docker build --no-cache --platform linux/amd64`：

| 项目 | 第一次 | 第二次 |
| --- | --- | --- |
| ImageID | `sha256:5ede362ef14386d2cb28995ff64b39c2fe06c431cb4ffcc072ef8a08088ad9cc` | `sha256:99beea0d499b0a5dceb4c7284a2ed78443cc5c6fc8c50457d77f040eb4f60e45` |
| 大小 | 497130313 bytes | 497130313 bytes |
| `created` | `2026-11-11T20:12:46.420702311+08:00` | `2026-11-11T20:14:36.114106161+08:00` |
| 层数 | 19 | 19 |

`docker history --no-trunc` 的逐层对比只有第一行（顶层 image ID）不同，**19 个层的 Layer ID 完全一致**，每层 `CreatedBy` 与大小也一致。

### 结论

- **文件系统内容可复现**：任何一次冷构建产出的层内容相同，`docker save` 后逐层 tar 的哈希一致。
- **ImageID 不相等**：Docker 把构建时刻写进 image config 的 `created` 字段，config 参与 ImageID 计算。这是 Docker 的默认行为，不是构建不确定性。
- 需要 bit-identical ImageID（例如按 digest 做部署校验）时，用 BuildKit 的 `--rewrite-timestamp`（buildx ≥ 0.11，本机 `docker buildx 0.13.1+ds1` 可用），或 `SOURCE_DATE_EPOCH` + `buildx build --output type=docker`。
- 部署流程目前以「层内容一致 + `docker image inspect` 的记录 + 部署前 `docker build` 自检」为准，不依赖 ImageID 相等。

## 3. 首次启动冒烟（发现并修复一个阻断缺陷）

用冷构建产物在一次性栈上启动：

```bash
docker run -d --name r4img-mongo --network r4img-net -p 127.0.0.1:32270:27017 mongo:4.4.1
docker run -d --name r4img-s3 --network r4img-net \
  -e COM_ADOBE_TESTING_S3MOCK_STORE_INITIAL_BUCKETS=shoreline-test \
  adobe/s3mock@sha256:ab01a6946750f451ca215a47e91030695b260e4003b8a5a6201d25029b8fca92
docker run -d --name r4img-api --network r4img-net -p 127.0.0.1:32271:8080 \
  -e Mongo__Uri=mongodb://r4img-mongo:27017/moeflow_smoke2 \
  -e Auth__Secret=local-test-secret-012345678901234567890 \
  -e Auth__AdminEmail=owner@example.test -e Bootstrap__Enabled=false \
  -e Storage__Endpoint=http://r4img-s3:9090 -e Storage__Bucket=shoreline-test \
  -e Storage__AccessKey=local-test-only -e Storage__SecretKey=local-disposable-test-secret \
  -e Storage__ForcePathStyle=true -e Mail__Enabled=false \
  -e Migrations__Mode=apply r4img-c:fix
```

第一次冒烟直接崩溃：

```
Unhandled exception. MongoDB.Driver.MongoCommandException:
Command createIndexes failed: Index with name: project_member_identity_key_v1
already exists with different options.
   at Shoreline.Api.Infrastructure.MongoStore.InitializeCoreAsync(...) MongoStore.cs:line 94
   at Program.<Main>$(String[] args) Program.cs:line 234
```

根因：迁移 0004 建 `project_member_identity_key_v1`（`{ik:1}` unique，无 partial filter，与 `app/models/project_member.py:40-46` 一致），而 `MongoStore.InitializeCoreAsync` 又用同名但带 `partialFilterExpression: {ik: {$type: "string"}}` 的定义重建；同一个集合的第二份定义触发 `IndexOptionsConflict`。同一处还有两个 `$exists` / `$type` 不一致：`project_member_project_user_v1`、`project_member_project_external_v1`。

修复（`569be7b fix(schema): align identity index options with the legacy model`）：

- `src/Shoreline.Api/Infrastructure/Migrations/M0004IdentityMembers.cs:378-383` 的 partial filter 从 `$exists` 改为 `$type`（与 mongoengine 定义一致）
- `src/Shoreline.Api/Infrastructure/MongoStore.cs:94-97` 去掉 `project_member_identity_key_v1` 上多余的 partial filter
- 新增 `tests/Shoreline.Tests/MongoStoreIndexCompatTests.cs`：先按 Python 的 options 建好三个索引，再跑 `MongoStore.InitializeAsync`，断言不抛且索引仍在

修复后同一组命令：

```
/ping         -> 200 pong
/health/live  -> 200 {"status":"ok"}
/health/ready -> 200 {"status":"ok"}
```

启动日志依次 `APPLIED 0001`…`APPLIED 0009`，然后 `Now listening on: http://[::]:8080`。

## 4. 剩余待验证

- 真实 R2 / 真实 SMTP 的端到端 walkthrough（需要凭证，本机不执行）
- 从 Python 现网库接管：索引已由 mongoengine 建好，C# 启动不再重建冲突（由上面的测试覆盖）；迁移 0004 的 7 个 parity 修复见 `docs/migration-parity.md`
- 多副本同时启动时的迁移互斥：迁移 runner 有 `migration_lock`，但同一时刻启动多个 api 副本仍建议先起一个再扩
