# Job 幂等排队（EnqueueOnceAsync）

本文描述 `JobQueue` 新增的幂等入队接口：调用方（后续的 G2 / Archive producer）可以在持久化自己的 intent 之前预分配 job id，之后用同一个 id 重复排队，而不会产生重复 job、重复副作用或错误地把已有 job 重置。

实现只改动 `src/Shoreline.Api/Jobs/JobQueue.cs`。既有的 `EnqueueAsync`、`ClaimAsync`、`RenewAsync`、`CompleteAsync`、`FailAsync`、`CancelPendingAsync` 行为与租约/重试语义不变；既有的无 journal producer 本轮不迁移到新接口。

## API

```csharp
public Task<ObjectId> EnqueueOnceAsync(
    ObjectId jobId,
    string kind,
    BsonDocument payload,
    CancellationToken ct)
```

返回值为传入的 `jobId`。相同 `jobId`、相同 `kind`、相同 typed BSON `payload` 的重复调用幂等；不同 `kind` 或不同 `payload` 抛出 `JobEnqueueConflictException`。

```csharp
public sealed class JobEnqueueConflictException : InvalidOperationException
{
    public ObjectId JobId { get; }
}
```

异常消息只包含 job id 与「already exists with a different kind or payload」，不包含 payload 内容，也不回显 kind 值，避免日志泄漏。调用方可以按类型捕获该冲突，并把「同一个 generation id 对应不同 generation payload」视为严重状态不一致。

## 原子写入

写入是一条 MongoDB 原子 upsert：

```csharp
Jobs.UpdateOneAsync(
    new BsonDocument("_id", jobId),
    new BsonDocument("$setOnInsert", insert),
    new UpdateOptions { IsUpsert = true },
    ct);
```

- 过滤器只有 `_id == jobId`，依赖 MongoDB 已有的 `_id` 唯一性；**不需要新增全局唯一索引**。
- 所有业务字段都放在 `$setOnInsert` 中：`kind`、`payload`、`state = "pending"`、`attempts = 0`、`available_at`、`created_at`。
- 命中已有行时 upsert 不写任何字段，因此既有 `state`、`attempts`、`owner`、`lease_until`、`finished_at`、`available_at`、`created_at` 全部保持不变。
- 相同 id 的并发调用只会插入一行；其余调用命中该行后走严格校验并返回同一个 id。

`created_at` 与 `available_at` 在单次调用中取同一个 `clock.GetUtcNow()` 值，重试不会刷新它们。

## 严格 typed BSON 相等

重复调用是否一致的判定使用**严格 typed BSON 比较**（`StrictBsonEquals`），不是 JSON 比较，也不是宽松的 `BsonValue.Equals`：

- 每个节点的 `BsonType` 必须相同：`Int32 != Int64 != Double`，即使数值相同也视为不同 payload（例如 `{"generation": 7}` 与 `{"generation": 7L}` 冲突）。
- document 的字段顺序参与比较；数组的元素顺序与长度参与比较。
- 通过 `BsonDocument.DeepClone()` 在写入前快照 payload，调用方在方法返回后继续修改原 `BsonDocument` 不会影响已写入数据。
- 后续 producer（G2 / Archive）必须从持久化的 BSON intent 中取出同一个 payload 重新调用，不能经过 JSON 序列化/反序列化，否则字段顺序、数值类型（Int32/Int64/Double）可能改变并触发误冲突。

字段顺序之所以严格，是因为 BSON document 的顺序本身是语义的一部分，也是 MongoDB 原样保存的内容；保持「持久化的原 payload 原样回放」比引入规范化更可预测。若未来需要宽松比较，应新增显式的 canonical 规则和版本，而不是修改本接口的默认语义。

## 冲突与状态

- 相同 `jobId` 但 `kind` 不同：冲突。
- 相同 `jobId`、相同 `kind` 但 typed payload 不同：冲突，**即使已有行处于 `failed` 状态也不复位**，不重置 `attempts`，不重新排队。
- 已有行处于 `done`、`running`、`failed`、`cancelled` 任一状态时，只要输入完全一致，直接返回同一 id，行内容（包括时间戳与 attempts）逐字节不变。
- 同一 generation 的 payload 变化一定意味着调用方 id 复用错误，应该暴露而不是覆盖。

## 失败与重试语义

- **未知写异常原样传播**：写入过程中出现非「duplicate key」的异常时，本方法不会假定「一定没写」，也不做猜测性的成功返回；异常原样抛给调用方。调用方应使用同一个 `jobId` 重试。下一次调用会走 upsert：若上一次实际已提交，则命中并校验后返回同一 id；若未提交，则插入一行。
- **duplicate key 竞态**：并发 upsert 可能在过滤器求值与插入之间被其他调用抢先插入，MongoDB 会返回 duplicate key。这是已知竞态，本方法读取并严格校验已提交行；读不到时做最长 5 秒的有界回读，仍无法确认则抛 `InvalidOperationException`（消息不含 payload），绝不吞掉不确定性。
- **调用方取消**：数据库写入已提交后如果 `ct` 被取消，方法会通过 `ct.ThrowIfCancellationRequested()` 抛出 `OperationCanceledException`，不做后续写入或状态修改；调用方用同一 id 重试即可解析到已提交行。
- 该模型成立的前提是 producer 自己有可恢复的 journal：先持久化 intent，再调用本接口；异常/取消后按同一 id 重试即可。本轮只提供队列侧保证，producer 侧接线由后续 G2 / Archive 任务完成。

## 参数校验

在触碰数据库之前完成校验，异常消息不含 payload：

- `jobId == ObjectId.Empty`：`ArgumentException`。
- `kind` 为 `null`、空串或纯空白：`ArgumentException`。
- `payload` 为 `null`：`ArgumentNullException`。

## 测试 seam

为验证「数据库已提交但调用方收到异常」这类真实故障，`JobQueue` 增加了一个可选构造参数：

```csharp
public delegate Task JobQueueWriteObserver(JobQueueWritePhase phase, ObjectId jobId, CancellationToken ct);
public enum JobQueueWritePhase { BeforeWrite, AfterWrite }

public JobQueue(MongoStore store, TimeProvider clock, JobQueueWriteObserver? writeObserver = null)
```

- 默认 `null`，生产 DI 与既有 `new JobQueue(store, clock)` 调用不变；`BeforeWrite` 在真实 upsert 前调用，`AfterWrite` 在 upsert 返回后调用，因此测试可以在真实数据库写入已提交后再注入异常。
- 该 seam 只用于测试，不替代任何过滤器逻辑；测试不会用假 filter 来模拟幂等。

## 测试覆盖

`tests/Shoreline.Tests/JobQueueIdempotencyTests.cs` 覆盖：

1. 20 个并发相同 id 请求 → 只有一行、全部返回同一 id、`attempts == 0`。
2. 相同 id 不同 kind → 冲突，原行逐字节不变。
3. 相同 id 的 `Int32` 与 `Int64` payload → 冲突（typed 相等证明）；相同字段不同 document 字段顺序 → 冲突（顺序严格证明）。
4. `done` / `failed` / `running` / `cancelled` 四种状态下重复排队 → 同一 id 且整行逐字节不变。
5. `failed` 行配不同 payload → 冲突且保持 `failed` / `attempts` / 时间戳。
6. 真实写入已提交后注入异常，再重试两次 → 无重复行、时间戳与 `attempts` 保持首次值。
7. 写入前注入异常 → 库中无行；重试后只创建一次。
8. 提交后调用方取消 → 抛取消异常；重试返回同一行。
9. 调用方在返回后修改 payload（含嵌套 document / array）→ 存储内容不变。
10. `ObjectId.Empty`、空/空白 kind、null payload → 在数据库写入前拒绝。

## G2 导入 producer 接线

`ProjectImportService.InitializeAfterCreateAsync` 是第一个使用
`EnqueueOnceAsync` 的 producer：

- project 的 durable `ij` 字段在队列写入之前保存本次 generation 的 job id
  （intent，而不是入队结果回填）。
- 同一 project + revision 且队列行仍为 `pending/running` 时复用 `ij`；队列行缺失
  （崩溃在 upsert 前）也复用；终态或不同 generation 分配新 id。
- payload 每次从 `{ project_id: ObjectId, label_revision: Int64 }` 原样重建，
  字段顺序和数值类型固定，不经过 JSON。
- 入队异常后按 `ij` 精确读取该 job id：存在任意状态即保持 `is=Pending`，确认不存在
  才标记 `is=Error, ie=ErrorUnknown`。不再按 kind+project+revision 近似查找。
- `JobEnqueueConflictException` 表示同一 id 绑定了不同 typed payload，属于
  generation 错配，导入标记 blocked 并向调用方抛出。

`X-Request-ID` / `Idempotency-Key` 不参与导入去重（Python 导入路径没有该语义）。

## 限制与未做事项

- 不新增唯一索引，不修改 `MongoStore`、`Program.cs`、worker、既有测试或依赖锁定。
- 不改变 `ClaimAsync` / `RenewAsync` 的租约与过期语义；本任务只处理幂等入队。
- 只迁移了 G2 导入 producer；outputs / archive / thumbnails / proofread 等仍使用
  `EnqueueAsync`。
- 冲突检测比较的是当前行内容，不做 generation 编号推断；调用方必须保证同一 id 只对应一个 generation payload。
- 有界回读使用真实时间 5 秒上限，不依赖可注入时钟；这是故障确认路径，不影响正常的 upsert 快路径。
