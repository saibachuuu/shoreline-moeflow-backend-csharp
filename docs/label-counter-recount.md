# 标签计数器重算（translated / checked）

## 为什么要这个

`file.tsc` / `target.tsc` / `project.tsc` 不是查询时实时算的，而是**增量加减**：

```csharp
// src/Shoreline.Api/Features/LabelService.Internal.cs
if (otherCount == 0 && !source.Flag("b"))
    await AdjustCountAsync(file, session, "translated_source_count", +1, targetId, ct);
```

也就是说，只有「这条原文第一次被翻译」时才 +1。只要有一次操作没有执行到这行
（并发、请求中断、机器翻译被删、旧数据），计数器就会**永久少 1**，而系统里
没有任何东西会去纠正它。

Python 老版用的是同一套 `inc_cache` 逻辑（`app/models/file.py:1750`），所以两端都会漂移。

2026-11-13 的生产事故就是这种情况：两个项目显示 393 / 94，实际 399 / 95。

## 三种修法

### 1. 管理端点（单个项目，最常用）

```
POST /v1/admin/projects/{projectId}/recount
```

需要站点管理员令牌。返回：

```json
{
  "project_id": "6aeed79af54dd6b6f8b27c17",
  "revision": 0,
  "translated_source_count": 399,
  "checked_source_count": 0,
  "translated_delta": 6,
  "checked_delta": 0
}
```

`delta` 就是原来的错误值差了多少，方便确认修对了。

它调用的是 `LabelCounterReconciler.ReconcileAsync`——和带版本号的批量保存
走的是同一个重算器，规则与生产一致：

- `translated`：该项目下每个文件里，有至少一条翻译、且原文 `b != true` 的原文数
- `checked`：同上，但只算 `selected = true` 的
- 会同时修正 `file.tsc`、`target.tsc`、`project.tsc`、`file_target_cache.ts/cs`

### 2. 全库扫描脚本（批量排查）

`tools/recount-labels.js`，用生产 mongo shell 跑：

```bash
# 只读：列出所有漂移的项目
docker exec <mongo> mongo -u "$U" -p "$P" --authenticationDatabase admin moeflow \
  --quiet /path/to/recount-labels.js

# 写入修正
docker exec <mongo> mongo -u "$U" -p "$P" --authenticationDatabase admin moeflow \
  --quiet --eval 'var APPLY=true' /path/to/recount-labels.js

# 只修一个项目
... --eval 'var ONLY="6aeed79af54dd6b6f8b27c17"; var APPLY=true'
```

### 3. 定时重算（可选）

没有内置定时任务。需要的话用宿主 cron 调管理端点即可：

```cron
30 3 * * * curl -sS -X POST -H "Authorization: Bearer $ADMIN_TOKEN" \
  https://shoreline.akiusagi.cc/api/v1/admin/projects/<id>/recount
```

不建议对全部 1900+ 项目每天无差别重算：单项目重算是全表聚合，项目多时会明显吃 CPU。

## 什么时候该跑

- 用户报告「已翻译数对不上」
- 大批量删除 / 恢复快照 / 迁移之后
- 从 Python 切过来之后的一次性体检

## 已知限制

- 重算按 `file.p == project` 归属，跨项目移动文件后旧项目的历史计数不会被追溯修正
- 只修计数，不修数据本身：如果 translation 记录真的丢了，重算只会让数字变"对但更小"
