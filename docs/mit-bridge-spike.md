# MIT 桥可行性 Spike（celery `mit` 队列桥接）

- 日期：2026-11-11；分支：`agents/r2-mit-bridge-spike-20261111`（基于 main `523ebeb`）
- 结论一句话：**Python 侧 MIT 是标准 Celery protocol v2 JSON 消息**（`tasks.mit.*` →
  durable `mit` 队列，结果进 Mongo `celery_taskmeta`）；C# 侧已 fail-closed。
  若产品需要 MIT，**推荐方案 A（C# 直发 MQ 消息）**，约 3–5 人时；否则保持方案 C。

## 1. Python 侧现状（只读 oracle：`shoreline-moeflow-backend`）

### 1.1 HTTP 入口（`app/apis/urls.py:632-658`）

`if app_config["MIT_STORAGE_ROOT"]:` 为真才注册 blueprint（默认 `None`，
见 `app/config.py:186`，未配置时整组路由不存在）：

| 方法 | 路径 | 处理类 | 说明 |
|---|---|---|---|
| POST | `/v1/mit/images` | `MitImageApi.post` (`app/apis/manga_image_translator.py:36-48`) | 校验后缀 `.jpg/.jpeg/.png/.gif`，落盘到 `MIT_STORAGE_ROOT` 下随机名文件，返回 `{"filename"}` |
| POST | `/v1/mit/image-tasks` | `MitImageTaskApi.post` (`:50-74`) | `{"filename", "task_name": "mit_detect_text" \| "mit_ocr", ...params}` → `{"task_id"}` |
| GET | `/v1/mit/image-tasks/<task_id>` | `MitImageTaskApi.get` (`:76-77`) | 轮询，1 秒超时 |
| POST | `/v1/mit/translate-tasks` | `MitTranslateTaskApi.post` (`:80-84`) | `{"query", "translator", "target_lang", ...}` → `{"task_id"}` |
| GET | `/v1/mit/translate-tasks/<task_id>` | `MitTranslateTaskApi.get` (`:86-87`) | 轮询，1 秒超时 |

注意 `MitImageTaskApi.post` 的路径检查用的是 `os.path.commonprefix`
（`:58`，字符串前缀比较，有绕过空间）；C# 侧已用规范路径比较重写
（`MitEndpoints.TryResolveSafePath`）。

### 1.2 任务名 / 队列 / 参数（`app/tasks/mit.py`，`app/factory.py`）

- 任务定义全是 stub（真实实现在外部 `manga_translator/moeflow_worker.py`，
  文件头注释 `app/tasks/mit.py:1-3` 与 `app/factory.py:127` 均有说明）：
  `tasks.mit.detect_text`（`:18`）、`tasks.mit.ocr`（`:23`）、
  `tasks.mit.translate`（`:28`）、`tasks.mit.inpaint`（`:33`），
  外加编排任务 `tasks.preprocess_mit`（`:109-128`，worker 内阻塞
  `.get()` 串行 detect → ocr → translate）。
- 路由 `app/factory.py:147`：`("tasks.mit.*", {"queue": "mit"})`；
  autodiscover 注册见 `app/factory.py:127`。
- 入队 helper `app/tasks/__init__.py:29-32`：`queue_task(task, *a, **kw)` =
  `task.delay(*a, **kw)` + `forget()`，返回 `result.id`（uuid）。
- 参数形状（HTTP 短名 → celery 任务名的映射在 `MitImageTaskApi.post`）：
  - `mit_detect_text`: 位置参数 `image_path`（`MIT_STORAGE_ROOT` 下本地路径，
    **要求 worker 与 API 共享该文件系统**）+ 默认 kwargs
    `mit_detect_text_default_params`（`app/tasks/mit.py:42-53`：
    `detector_key="default", detect_size=2560, text_threshold=0.5,
    box_threshold=0.7, unclip_ratio=2.3, invert/gamma_correct/rotate=False,
    verbose=True`）+ `gpu_options={"device": "cuda"}`（`:15, :56-65`）。
  - `mit_ocr`: 位置参数 `image_path` + `regions=[{...}]` +
    `mit_ocr_default_params`（`:68-74`：`ocr_key="48px", verbose=True`）。
  - `mit_translate`: 纯 kwargs（`:92-106`）：
    `query, translator="gpt3.5", target_lang="CHT"`。
- `tasks.mit.inpaint`（`:33-35`）只有 Celery stub，没有 HTTP 入口：
  `MitImageTaskApi.post`（`:62-74`）仅接受 `mit_detect_text`/`mit_ocr`，
  其余 `task_name` 一律 `ValueError("Invalid task name")`。C# 与之对齐，
  同样不暴露 inpaint（见 `module-parity.md` §9，非缺口；
  `MitEndpoints_ImageTask_InpaintTaskName_IsRejectedLikePython` 锁定）。

### 1.3 结果回写与轮询

- Result backend = MongoDB（`app/config.py:163-171`：
  `CELERY_BACKEND_URL` 默认 `DB_URI`，`mongodb_backend_settings =
  {database: <DB_URI path>, taskmeta_collection: "celery_taskmeta"}`）。
- Worker（celery `MongoBackend`, celery 5.6.3
  `backends/mongodb.py:182-192` + `backends/base.py:558-580`）以
  `replace_one({"_id": task_id}, meta, upsert=True)` 写入：

  ```json
  {"_id": "<task_id>", "status": "SUCCESS|FAILURE|...",
   "result": "<json/serialized>", "traceback": "...|null",
   "children": [], "date_done": "<datetime>",
   "group_id?": "...", "parent_id?": "..."}
  ```

  `date_done` 上有后台索引（`mongodb.py:297-312`）。以上来自镜像内
  celery 源码确认，**本次 spike 未起 Mongo，未做 live 观测**。
- 轮询语义 `app/apis/manga_image_translator.py:23-33`（`_wait_task_result`）：
  `AsyncResult.get(timeout=1)` → 成功 `{"status":"success","result"}`；
  1 秒超时 → `{"status":"pending"}`；异常 → `{"status":"fail","message"}`。
  前端/调用方靠短轮询 GET 驱动，无回调。

### 1.4 Worker 形态

`mit` 队列的消费者是**外部镜像 `manga-image-translator`** 里的
`manga_translator/moeflow_worker.py`（Celery worker，非 HTTP/其它协议）。
C# 仓库 `docs/celery-cutover-analysis.md:20` 同样记录
“外部 `tasks.mit.*`（`mit` 队列），C# 无桥”。

## 2. C# 侧 fail-closed 确认（本 worktree，只读引用）

- 门控 `src/Shoreline.Api/Program.cs:181-200`：仅当 `Mit:StorageRoot`
  已配置 **且** `MitEndpoints.FindMissingConsumers` 为空才
  `app.MapMitEndpoints()`；否则 `/v1/mit` 全组路由不映射并写一条 warning。
- 判定 `src/Shoreline.Api/Features/MitEndpoints.cs:28-50`：
  `RequiredJobKinds = ["mit_detect_text","mit_ocr","mit_translate"]`，
  缺任一 `IJobHandler` 即 fail closed。
- 路由/入队实现（有消费者时才启用）`MitEndpoints.cs:52-164`，
  Mongo `csharp_job` 状态机 `:313-363`（`pending/done/failed` →
  `pending/success/fail`，未知 id 按 `pending` 处理）。
- 回归测试 `tests/Shoreline.Tests/MitHttpTests.cs`：
  未配置 `:88-107`、无消费者 `:109-129`、启用目录但无消费者零入队
  `:131-181`、部分覆盖仍关闭 `:183-207`；夹具 handler 下的 lifecycle
  `:347-471` 只证明“有消费者时契约可入队”，不代表 MIT 可用。

## 3. 线上实验：真实报文（已脱敏）

环境：`rabbitmq:3-management`（3.13.7）容器 `r2-mit-rmq-33070`
（`127.0.0.1:33070` AMQP / `33071` 管理台），发布端用后端
`shoreline-moeflow-backend:dev` 镜像内 pinned 版本
**celery 5.6.3 / kombu 5.6.2**，`send_task` 复刻 §1.2 参数形状，
复现脚本见 `tools/spikes/`。

队列属性（`rabbitmqctl list_queues` + 管理 API `/api/queues/%2f/mit`，
发布后、消费前）：

```text
name   durable  messages  ready  unacked
mit    true     2         2       0
```

`type=classic, auto_delete=false, arguments={}, persistent 2/2`。

`tasks.mit.detect_text` 原始消息（`task_id f59b14f9-…`，已把随机
`origin`/`reply_to` 保留原样——均为一次性本地值，无敏感信息）：

```json
{
  "delivery_info": {"exchange": "", "routing_key": "mit"},
  "content_type": "application/json", "content_encoding": "utf-8",
  "headers": {
    "task": "tasks.mit.detect_text",
    "id": "f59b14f9-490c-42b9-9885-f95f2155b6eb",
    "root_id": "f59b14f9-490c-42b9-9885-f95f2155b6eb",
    "retries": 0, "timelimit": [null, null],
    "lang": "py", "origin": "gen1@236550727f2e",
    "argsrepr": "['/MIT_STORAGE_ROOT/spike_page.png']",
    "kwargsrepr": "{'detector_key': 'default', 'detect_size': 2560, 'text_threshold': 0.5, 'box_threshold': 0.7, 'unclip_ratio': 2.3, 'invert': False, 'gamma_correct': False, 'rotate': False, 'verbose': True, 'device': 'cuda'}",
    "eta": null, "expires": null, "group": null, "group_index": null,
    "parent_id": null, "shadow": null, "ignore_result": false,
    "stamped_headers": null, "stamps": {}
  },
  "properties": {
    "delivery_mode": 2, "priority": 0,
    "correlation_id": "f59b14f9-490c-42b9-9885-f95f2155b6eb",
    "reply_to": "a9b3c669-1e3e-3cba-a3fe-d496293decc7"
  },
  "body": [
    ["/MIT_STORAGE_ROOT/spike_page.png"],
    {"detector_key": "default", "detect_size": 2560, "text_threshold": 0.5,
     "box_threshold": 0.7, "unclip_ratio": 2.3, "invert": false,
     "gamma_correct": false, "rotate": false, "verbose": true, "device": "cuda"},
    {"callbacks": null, "errbacks": null, "chain": null, "chord": null}
  ]
}
```

`tasks.mit.translate`（`task_id 933b441f-…`）结构完全相同，
`body = [[], {"query": "こんにちは", "translator": "gpt3.5", "target_lang": "CHT"}, {callbacks/errbacks/chain/chord: null}]`，
headers 中 `task: tasks.mit.translate`，`argsrepr: "()"`。

### 协议结论：Celery protocol v2 字段清单（按本次实测，celery 5.6.3 默认配置）

- 信封：`exchange=""`（默认交换机直投），`routing_key="mit"`；
  队列需 durable，`delivery_mode=2` 持久化；`content_type=application/json`，
  `content_encoding=utf-8`（默认 json 序列化，无压缩；本任务要求清单里的
  `utc` 字段**在 5.6.3 的 headers 里不存在**，不要照抄旧文档）。
- `headers`（大小写敏感，C# 直发必须原样构造）：`task`（全名如
  `tasks.mit.detect_text`）、`id`、`root_id`（首任务与 `id` 相同）、
  `retries: 0`、`timelimit: [null, null]`、`lang: "py"`、`origin`、
  `argsrepr`/`kwargsrepr`（仅诊断用）、`eta`/`expires`/`group`/
  `group_index`/`parent_id`/`shadow`（本场景均为 null）、
  `ignore_result: false`（**结果会写回**，C# 轮询依赖它）、
  `stamped_headers: null`、`stamps: {}`。
- `properties`：`correlation_id = task id`，
  `reply_to = <publisher mailbox uuid>`（worker 侧基本忽略，可随机生成），
  `priority: 0`。
- `body = [args, kwargs, embed]` 三元组，`embed =
  {callbacks, errbacks, chain, chord}` 本场景全 null；
  `preprocess_mit` 类链式调用在 worker 内用阻塞 `.get()` 实现，
  不经过 embed chain（见 `app/tasks/mit.py:109-128`）。

## 4. 候选方案

### 方案 A：C# 直发 MQ 消息（C# → RabbitMQ `mit` 队列 → 外部 MIT worker）

步骤：
1. 引 `RabbitMQ.Client`（v6/v7 均可，需走评审钉版本），实现 protocol v2
   序列化器：`body=[args,kwargs,{callbacks:null,errbacks:null,chain:null,chord:null}]`
   + 上表 headers（`id/root_id/correlation_id` 用 `Guid`/`task_id`，
   `reply_to` 随机 uuid），`IBasicPublish(exchange:"", routingKey:"mit",
   mandatory:true, persistent:true)`；先声明 durable `mit` 队列（`arguments={}`）。
2. 结果轮询：用现有 `MongoDB.Driver` 直读 `<db>.celery_taskmeta`
   （`_id=task_id`，字段见 §1.3），`status` 映射
   `SUCCESS→success / FAILURE→fail / 其它或缺失→pending`，
   超时语义与 Python `_wait_task_result` 对齐（1 秒 get ≈ 查一次即返）。
3. `MitService.Enqueue*` 改走 MQ 发布（保留 `csharp_job` 做本地审计或去掉，
   待定——建议保留审计行但状态机以 `celery_taskmeta` 为准）；
   `FindMissingConsumers` 门改为“MQ bridge 可用”（broker 可达）判定。
4. 单测：v2 报文 golden test（与本 spike 实测 JSON 对拍）＋内存 broker
   fake；集成：本 spike 脚本即兼容性夹具。

- 工作量：**3–5 人时**（含测试，不含联调真实 worker）。
- 风险：wire 细节（`reply_to`/confirmed publish/returned-message 处理）；
  结果读存在竞态（worker 写 `celery_taskmeta` 与 C# 轮询的时序）；
  **文件传输是大项**：API 传的是 `MIT_STORAGE_ROOT` 本地路径，
  外部 worker 必须挂载同一共享卷；大图走 MQ 只传路径不传字节是优点也是约束；
  broker 凭证要进 C# 配置体系（`RabbitMq:*`），不能沿用 Python 的 env 名。

### 方案 B：C# 调 Python 薄网关（C# → HTTP → Flask sidecar → 同 §1.2 入队）

步骤：起一个仅含 `app.tasks` + `queue_task/wait_result_sync` 的 Flask/FastAPI
sidecar（复用 `manga_image_translator.py` 逻辑约 90 行），C# 经 `HttpClient`
调其 `POST /image-tasks` / `GET /image-tasks/{id}`；sidecar 与 C# 共用
`MIT_STORAGE_ROOT` 卷；网关间加内网 token。

- 工作量：**2–4 人时**代码 ＋ 持续的部署/运维成本（多一个服务、健康检查、
  日志、版本跟随）。
- 风险：多一跳延迟与故障点；Python 网关的部署形态（gunicorn/worker 共存）
  又带回半个旧后端；本质只是把方案 A 的 60 行序列化换成一个常驻进程，
  性价比低。

### 方案 C：保持 fail-closed（不支持 MIT）

- 工作量：**0**。`/v1/mit` 保持 404（`Program.cs:181-200` +
  `MitHttpTests.cs:131-207` 已锁定该行为）。
- 风险/代价：MIT 功能（漫画 tubo 检测/OCR/翻译预处理）不可用；
  若业务需要，该缺口迟早要填，届时仍要在 A/B 间二选一。

## 5. 推荐

- **产品近期需要 MIT → 做方案 A**：无新常驻服务，所有权收敛在 C# 仓库；
  wire 格式已由本 spike 实测锁定（golden 可直接入测试），最大不确定性只剩
  “真实 worker 镜像”的联调，而这是 B/C 都绕不开的。
- **产品近期不需要 MIT → 选方案 C**（现状已是 C，零动作），但不要选 B：
  B 的运维常驻成本超过它省下的 60 行序列化代码。
- 无论 A/C：`preprocess_mit` 编排语义（worker 内阻塞 `.get()`）意味着
  “C# 发单步任务、worker 内部编排”与“C# 发 preprocess 让 worker 全包”
  两用都通；建议 A 只实现单步任务（detect/ocr/translate），编排放在 C#
  侧按 Python `:109-128` 顺序调三次短轮询，避免把阻塞链埋进外部 worker。

## 6. 未验证项（做 A 之前必须关闭）

1. 真实 MIT worker 镜像：`manga_translator/moeflow_worker.py` 的实际消费
   签名（是否真读 `device`/`regions` 等 kwarg）、`inpaint` 参数形状、
   大图/长文本行为——本次只有发布端实测，无 worker 回放。
2. 鉴权：broker `RABBITMQ_USER/PASS/VHOST`（`.env.sample:23`）如何映射到
   C# 配置与 secret 管理；管理台 15672 不对外。
3. 大文件传输：`MIT_STORAGE_ROOT` 共享卷的挂载拓扑（API 与外部 worker 双挂载）、
   并发写/清理策略（Python 侧 `NamedTemporaryFile(delete=False)` 从不清理）。
4. `celery_taskmeta` 增长：Python 侧无过期配置（`result_expires` 未设），
   切到 C# 轮询后需定 TTL/索引策略。
5. Worker 侧 `preprocess_mit` 阻塞 `.get()` 要求 result backend 对 worker
   可达且 `ignore_result=false` 全链路保持——与推荐的“C# 侧编排”冲突点已在 §5 说明。
6. `basic_publish` 的 confirm/return 与队列声明幂等（durable + `arguments={}`）
   在生产 broker（含 policy/仲裁队列）下的表现。

## 7. 复现（无凭证）

```bash
# 1. 起 throwaway broker（结束务必删除）
docker run -d --name r2-mit-rmq-33070 \
  -p 127.0.0.1:33070:5672 -p 127.0.0.1:33071:15672 \
  rabbitmq:3-management
docker network create r2mitspike
docker network connect r2mitspike r2-mit-rmq-33070

# 2. 发布（broker URL 只有本地 throwaway 默认账号，可用 env 覆盖）
docker run --rm --network r2mitspike \
  -v $PWD/tools/spikes:/spikes \
  -e MIT_SPIKE_BROKER_URL=amqp://guest:guest@r2-mit-rmq-33070:5672// \
  shoreline-moeflow-backend:dev python /spikes/mit_publish.py
# -> {"detect_text_id": "<uuid>", "translate_id": "<uuid>"}

# 3. 看队列
docker exec r2-mit-rmq-33070 rabbitmqctl list_queues name durable messages messages_ready
curl -s -u guest:guest http://127.0.0.1:33071/api/queues/%2f/mit

# 4. 消费并打印原始 JSON（含 headers/properties/body）
docker run --rm --network r2mitspike \
  -v $PWD/tools/spikes:/spikes \
  -e MIT_SPIKE_BROKER_URL=amqp://guest:guest@r2-mit-rmq-33070:5672// \
  shoreline-moeflow-backend:dev python /spikes/mit_consume.py 2

# 5. 清理（必须）
docker rm -f r2-mit-rmq-33070
docker network rm r2mitspike
```

脚本：`tools/spikes/mit_publish.py`（`send_task` 复刻 `queue_task` 形状），
`tools/spikes/mit_consume.py`（kombu 原始 dump + ack）。
