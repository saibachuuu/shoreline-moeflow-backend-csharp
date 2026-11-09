# G4 缩略图兼容说明（隔离快照）

## 状态与边界

本切片实现 Python 后端的两条缩略图 POST 接口，并接入真实图片处理与持久化任务队列：

- `POST /v1/files/{fileId}/thumbnail`
- `POST /v1/projects/{projectId}/thumbnails`

当前只在**隔离临时快照**中接线验证：

- 共享仓库的 `Program.cs` 没有加入本切片的服务注册或路由映射。
- 共享仓库的 `Shoreline.Api.csproj` 没有加入 `Shoreline.Imaging` 的 `ProjectReference`。
- 快照中临时加入了上述接线和项目引用，仅用于编译、测试与评审。
- 因此本文档不表示共享分支已接线，也不表示生产就绪。

## 新增文件

实现：

- `src/Shoreline.Imaging/Shoreline.Imaging.csproj`
- `src/Shoreline.Imaging/ImageProcessor.cs`
- `src/Shoreline.Api/Features/ThumbnailService.cs`
- `src/Shoreline.Api/Features/ThumbnailService.Processing.cs`
- `src/Shoreline.Api/Features/ThumbnailEndpoints.cs`
- `src/Shoreline.Api/Features/ThumbnailRegistration.cs`
- `src/Shoreline.Api/Jobs/ThumbnailJobHandler.cs`

测试：

- `tests/Shoreline.Tests/ThumbnailTests.cs`
- `tests/Shoreline.Tests/ThumbnailHttpTests.cs`

文档：

- `docs/thumbnails-compatibility.md`

不修改 D1 文件实现、E1/G1、存储层、D2 路由、共享 `Program.cs`、共享 API 项目文件、解决方案文件。

## 依赖

`Shoreline.Imaging` 是独立的 `net10.0` 项目，固定使用：

- `Magick.NET-Q8-AnyCPU` `15.1.0`
- 许可证：Apache-2.0
- 不使用 `System.Drawing`/GDI+，不使用 ImageSharp。

原生运行目前在 Windows 上验证；Linux 的原生运行与各 RID 的发布验证尚未完成，不能据此宣称跨平台生产可用。

## 接口行为

### POST /v1/files/{fileId}/thumbnail

1. 需要登录 token。
2. 文件不存在：`FileNotExistError`，code `8001`，message `文件/文件夹不存在，可能已被删除`。
3. 需要项目 `project:ACCESS`；无权限：`NoPermissionError`，code `103`，message `您没有此项目的访问权限`。
4. 文件类型必须为图片（`t == 2`）：否则 `FileTypeNotSupportError`，code `108`，message `文件类型不支持此操作: 仅图片文件可生成缩略图`。
5. 当前存储模式不需要本地缩略图时，返回：
   `{"message":"当前存储模式无需重建缩略图","count":0}`
6. 成功排队后返回（与原接口一致，成功对象没有 `count` 字段）：
   `{"message":"缩略图生成任务已触发"}`

### POST /v1/projects/{projectId}/thumbnails

1. 需要登录 token。
2. 项目不存在：`ProjectNotExistError`，code `4001`，message `项目不存在`。
3. 需要项目 `project:CHANGE`；无权限：`NoPermissionError`，code `103`，message `您没有此项目的访问权限`。
4. 当前存储模式不需要本地缩略图时，返回：
   `{"message":"当前存储模式无需重建缩略图","count":0}`
5. 查询 `{project, type: 图片(2), activated: true}` 的图片，逐张排队，返回：
   `{"message":"已为 N 张图片触发缩略图生成任务","count":N}`
   计数语义与原接口一致：每成功排队一张图片加一。

## 存储模式

- Local、S3、R2 都走本地图片处理；这与原 Python 的本地缩略图能力判断一致：S3/R2 没有服务端图片处理。
- 阿里云 OSS 的本地/服务端路径当前在 C# 存储层没有移植，存储层会明确拒绝，不伪造成功。
- `Thumbnails:Mode` 默认 `local`。只有显式配置为 `server`/`server-side`/`oss` 时才走“当前存储模式无需重建缩略图、count=0”的分支；生产配置不得在没有真实服务端处理能力时打开该分支。
- 项目 `csid`（存储池）非空时，本切片**失败关闭**：返回 code `2`、HTTP `409`，message `当前切片尚未接线存储池路由，无法在迁移后的存储池项目上执行文件操作`。等待 D2 存储路由验收后再接入，不静默回退默认存储。

## 图片处理

- 仅接受 JPEG、PNG、BMP、GIF、WEBP；拒绝 SVG、PDF、HTTP 输入以及外部 coder/委托，不运行 shell 或 ImageMagick 命令。
- 只处理第一帧。
- 不调用自动旋转，避免改变原 Python/Pillow 的方向语义。
- Cover：中心裁剪并缩放为 `180x140`，Lanczos，WebP quality `50`。
- Resample：仅当宽度大于 `1920` 时按比例缩放到宽 `1920`，高度 `(int)(height * 1920 / width)`，最小 1；否则保持原尺寸。WebP quality `50`。
- RGBA/P 等带透明通道的输入按原逻辑转 RGB（移除 alpha），不合成白底。
- 输出去除元数据；源文件字节数、像素数、尺寸、解码内存/磁盘均有可配置上限。
- 下载与成像不整段持有项目标签锁；只在短状态切换和发布阶段持锁。

## 任务与状态

- 队列 kind：`create_thumbnail`。
- payload：`file_id`、`project_id`、`label_revision`、`save_name`、`run_id`。
- 文件状态：`0 UNKNOWN`、`1 QUEUING`、`2 GENERATING`、`3 SUCCEEDED`、`4 FAILED`，字段为 `th/the/tht`。
- 先写入可持久化任务，再在标签锁内写入 `th=1`、清空 `the`、更新 `tht`；入队失败不写假的 QUEUING 状态。
- 处理阶段重新持锁校验项目 `label_revision`、恢复状态、`csid`、文件 `_id/p/sa`、`label_epoch` 和运行者 `run_id`，通过 CAS 进入 GENERATING。
- 发布前再次持锁校验同一组条件；旧上传、恢复、删除或旧 `run_id` 不能覆盖新状态或最终对象。
- 同一文件重复 worker 会在锁上串行；已 SUCCEEDED 或存在其他活跃 run 时跳过。
- 源对象缺失是终态：`th=4`、`the="source file is missing"`，不重试。
- 可重试错误保持 GENERATING 并交回 JobQueue 重试；达到本切片最大尝试次数（默认 4，即首次 + 3 次重试）后写终态 `th=4`、安全错误文本 `thumbnail generation failed`。
- 取消操作不会发布对象；清理使用独立的 5 秒预算，不受续租失败影响。临时目录与 staging 对象在 finally 中尽力删除。

### 已知队列契约差异

现有 `JobWorker` 只向 handler 传递 payload，不传递 job id 或当前 attempts。本切片的 handler 通过只读查询最近的、运行中的 `csharp_job`（kind 与 payload.file_id/save_name 匹配）读取 attempts，以复现 Python 的最大重试终态语义。该差异应在后续主协调时决定是否给 JobWorker/handler 增加 job id/attempts 参数；本切片没有修改 Worker。

## 字节与项目统计

- 文件字段 `cover_bytes`、`resample_bytes` 使用真实生成文件的字节数。
- 项目 `cover_bytes`、`resample_bytes` 在发布锁内根据该项目所有文件的真实汇总值重算，不使用每次 `+=` 的增量累加，避免重跑、崩溃重试和重复 worker 造成重复计数。
- 项目 `fs` 以 KB 计，按 `round((cover+resample)/1024)` 的银行家舍入（`MidpointRounding.ToEven`）计算新旧差量后更新。
- 不覆盖原图 `files/{save_name}`，也不改写项目的 outputs 等无关字段。
- 发布后若进程在文件状态与项目统计之间崩溃，下一次重试会重新按真实文件汇总修复统计；测试覆盖了相关故障点。
- 与原 Python 不同，本切片**不吞掉统计异常后假装 SUCCEEDED**；统计失败会让任务保持可重试/最终失败，并且不会把未完成的发布标记为成功。

## 对象命名

- Cover：`files/cover-{save_name_stem}.webp`
- Resample：`files/resample-{save_name_stem}.webp`
- staging：`files/.thumbnail-staging/{run_id}/cover.webp`、`resample.webp`
- `save_name_stem` 为原 `save_name` 去掉最后一个扩展名。

## 测试覆盖

隔离快照包含 17 个直接测试与 5 个 HTTP 测试（快照接线）：

- JPEG 2400x1200 → cover 180x140、resample 1920x960，输出可读 WebP。
- 小尺寸 PNG（含 alpha）→ cover 180x140、resample 保持原尺寸、alpha 被移除。
- SVG 拒绝、源字节上限。
- enqueue 先于 QUEUING，payload 与文件状态正确。
- 成功生成、项目统计与银行家舍入。
- 源缺失终态与临时目录清理。
- 旧 revision、重新上传 save_name、重复 worker 不发布/不覆盖。
- 项目历史统计不会被二次累加。
- 存储池 `csid` 失败关闭。
- 非图片文件 legacy 错误。
- 项目批量只排队激活图片。
- Local 对象端到端。
- 输出存储失败后的重试与终态。
- 取消不发布。
- HTTP：缺 token、普通成员/创建者权限、108/103/8001/2 错误、数量与消息形状。

测试使用本地 MongoDB `127.0.0.1:27128`、S3Mock `127.0.0.1:29128`、Mailpit `21128`，随机数据库；不重启服务、不构建容器、不发送真实邮件。测试对象使用唯一前缀，避免共享 S3Mock 桶中的其他 agent 对象互相污染。

## 验证记录

- `Shoreline.Imaging` 独立项目：编译 0 warning / 0 error。
- 隔离快照 `c70c67b31de9a786139d41075c5fdf6c1f362a32` + 本切片文件 + 临时 E1 标签依赖：API/测试项目编译 0 warning / 0 error。
- focused 缩略图测试曾达到 22/22；随后加入了取消检查、cleanup 重试与唯一对象名前缀，最终完整套件需要在 MongoDB 27128 恢复后复测。
- 基线 `c70c67b` 为 323 个测试；本切片新增 22 个，理论合并值为 345。该值必须由实际完整测试确认，不能当作已验证事实。
- 最近一次完整测试启动时 MongoDB 27128 拒绝连接，导致 297 个测试在 `LocalServices.InitializeAsync` 处失败；这是环境故障，不是缩略图断言失败。不得把它报告为代码通过或代码失败，需在服务恢复后重跑。
