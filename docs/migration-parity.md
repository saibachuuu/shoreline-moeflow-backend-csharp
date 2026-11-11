# Migration parity: Python oracle vs C# port

Scope: `shoreline-moeflow-backend/app/migrations/` (oracle, read-only) vs
`src/Shoreline.Api/Infrastructure/Migrations/` (`LegacyMigrationRunner` + `M0001`..`M0009`).
Oracle revision compared: Python backend worktree state at time of review
(`runner.py` 368 lines; `m0004` 2021 lines). C# base: worktree branch
`agents/r2-migration-parity-20261111` off main `733e239`.

Conventions: "Python file:line" uses `app/migrations/...` paths;
"C# file:line" uses `src/Shoreline.Api/Infrastructure/Migrations/...` paths.
"一致性" = 等价 / 等价(报告差异) / 接受差异(有意) / 差异(已修复).

## 1. Runner 对拍 (`runner.py` ↔ `LegacyMigrationRunner.cs`)

| 行为 | Python `runner.py:行` | C# `LegacyMigrationRunner.cs:行` | 一致性 | 说明 |
|---|---|---|---|---|
| 版本表/锁表名 | `MIGRATION_RECORDS`/`MIGRATION_LOCK`/`LOCK_ID` 23–26 | `RecordCollection`/`LockCollection`/`LockId` 17–19 | 等价 | `migration_record` / `migration_lock` / `migrate` 一致 |
| 租约时长 | `LOCK_LEASE` 30min 26 | `LockLease` 30min 23 | 等价 | |
| 幂等键/失败语义 | 唯一索引 `v` 143–146；失败不写行 307–313 | 唯一索引 `v` 62–77；失败不写行 210–214 | 等价 | 先 `up` → `verify` → 写行；抛错即停，下次重试 |
| 并发写冲突 | `_record_applied` 243：重读比对 `c`，不同则抛 | `RecordAppliedAsync` 268：重读比对 `c`，不同则抛 | 等价 | 字段均为 `v/n/at/d/c`；C# 多写 `runner: "csharp"` |
| 锁获取/续租/释放 | 192/216/237（含 `DuplicateKeyError` → false） | 299/333/350（含 `IsDuplicateKey` → false） | 等价 | 同一 filter（`_id` + 过期/`holder`）+ upsert + `ReturnDocument.After` |
| 乱序保护 | `blocking` 检查 274–284 | `blocking` 检查 181–191 | 等价 | 同版本比较、同引用比较 |
| 回滚 | `rollback` 336：校验目标版本、逆序 `down`、删行、续租 | `RollbackAsync` 231：同上 | 等价 | |
| dry-run 只读 | `dry_run` 不建索引不加锁 267–269 | `DryRunAsync` 162：只 `Status`+`Pending` | 等价 | 不建 ledger 索引、不写锁/数据（有测试锁定） |
| 租约超 60% 告警 | `logger.warning` 321–330 | 无（只续租 217） | 差异(已修复) | 补 `LogWarning`，行为与 Python 一致 |
| 篡改检测 | `status` 165：所有含 `c` 的行都比对（<0004 宽松）75–107 | `StatusAsync` 106：仅 `runner == "csharp"` 的行比对 | 接受差异(有意) | checksum 体系不同（Python AST 哈希 vs C# 规范串 SHA-256）；Python 行被无条件信任，见 §3 R1 |

## 2. 迁移逐个对拍

| 号 | Python 文件:行 | C# 文件:行 | 一致性 | 差异 / 风险 |
|---|---|---|---|---|
| 0000 | `versions/m0000_baseline.py:10,14,17` | `IndexMigrations.cs:13` (`Up` 19/`Down` 22 均 no-op) | 等价 | 无数据操作 |
| 0001 | `m0001_file_list_index.py:12,16,20` 键 `(p,ac,f,dn,t,sn)` 名 `file_list_v2` | `IndexMigrations.cs:27` 键 `IndexKeys` 31–32，`Up` 39–41/`Down` 43–44/`Verify` 46–51 | 等价 | C# 经 `EnsureNamedIndexAsync`（`LegacyMigration.cs:112`）：同名/同键冲突时先删后建，Python `create_index` 同规同名是 no-op、异规则抛错。生产旧库若索引被人手动改过，C# 会重建（仅索引重建代价，无数据风险） |
| 0002 | `m0002_thumbnail_status.py:13,20,24`：`{t:2,th不存在}` → `$set{th:0,the:""}`；`down` 全表 `$unset th/the/tht` | `FieldBackfillMigrations.cs:7`：`Up` 14–23/`Down` 25–32/`Verify` 34–41 | 等价 | 查询、更新、verify 逐字段一致；已存在目标文档不受影响（filter 排除） |
| 0003 | `m0003_admin_file_edit_time_index.py:12,16,20` 键 `(et,-1)` 名 `file_admin_edit_time_v1` | `IndexMigrations.cs:55` 键 59，`Up` 66/`Down` 70/`Verify` 73 | 等价 | 同 0001 的索引调和说明 |
| 0004 | `m0004_identity_members.py:1334(up),1832(down),1838(verify)` | `M0004IdentityMembers.cs:108(Up),401(Verify)`；`Down` 继承基类抛错（`LegacyMigration.cs:50`，对应 Python `IrreversibleMigration`） | 等价(报告差异,已修复) | 数据语义逐分支一致（关系→成员、join 流程、owner 修复、m_uc、`w/pft/pdt/ft/ctm` 清理、7 个索引 `$exists` 形、报告+summary）。报告类差异 4 处已修复：`mapped_tags` 取当条关系（原取累计）、`m_t` 按 truthiness 计数（原按存在性）、补 `duplicate_identity_member` issue、非字符串 tag/alias 按 Python 只保留字符串。接受差异：casefold→`ToLowerInvariant`（拉丁/CJK 等价，ß 等极端字符影响外部 ID，见 §3 R2）、不写文件 evidence bundle（verify 不检查，见 §3 R3）、summary 里 `batch_id` 为同级字段而非 summary 内键（verify 只看 `owner_issue_count`） |
| 0005 | `m0005_identity_member_index_options.py:65(up),82(down抛),88(verify)`：`{external_id:None}` unset；两索引换 `$type` partial | `FieldBackfillMigrations.cs:78`：`Up` 100–111/`Verify` 113–119；无 `Down`（基类抛错，等价 `Irreversible`） | 等价 | `Eq(null)` 与 `{external_id:None}` 同匹配 null/缺失；partial `{$type:objectId/string}` 一致；`verify` 同为 名+键+unique+partial 精确比对。Python 的 mongomock sparse fallback 与 C# 无关（C# 只跑真实 mongo） |
| 0006 | `m0006_search_projections.py:90(up),143(down抛),153(verify)`；batch 500，`ordered=False` | `M0006SearchProjections.cs:67(Up),114(Verify)`；`WriteBatchesAsync` 180（batch 500，`IsOrdered=false`）；无 `Down`（基类抛错） | 等价(接受 1 处) | 投影公式 `NFC+strip+小写` 一致（casefold 注记同 0004）；索引 7 个名+键一致（C# 逐集合校验，比 Python 的合并字典更严，结论一致）；`aliases` 非数组时 Python 会按字符拆分（wart），C# 视为 `[]`——只影响脏数据，C# 行为更合理，不复刻 |
| 0007 | `m0007_project_member_tags.py:11(up),18(down抛),22(verify)`：`$or[缺失, null]` → `[]` | `FieldBackfillMigrations.cs:45`：`Up` 57–64/`Verify` 65–70；无 `Down` | 等价 | 查询与更新逐字段一致 |
| 0008 | `m0008_file_manual_order.py:20,24,27` 键 `(p,ac,f,dn,t,mo,sn,_id)` 名 `file_manual_order_v1` | `IndexMigrations.cs:82` 键 85–87，`Up` 94/`Down` 98/`Verify` 101 | 等价 | 两边都不回填 `mo`（老文件保持缺失），verify 只查索引名 |
| 0009 | `m0009_storage_usage_backfill.py:34(up),63(verify),85(down抛)`：`fn→file_name`（目标存在则只删 `fn`）；`fs>0` 按 `p` 求和，只回填缺失/0/null | `M0009StorageUsageBackfill.cs:18(Up),75(Verify)`；无 `Down`（基类抛错） | 等价 | 逐文档 upsert 语义、`$gt:0`、`_id` 空/非数值/`total<=0` 跳过、回填 filter（缺失/0/null）逐字段一致；`sha` 无；`worker` 无（0009 不涉及）。`raw_workers_length` 式微差（emoji 计量，见 §4 F5，已对齐为 code-point 计数）——注：该条实为 0004 `MigrateWorkers` 的 `raw_workers_length`，用 `EnumerateRunes` 与 Python `len()` 对齐 |

## 3. 生产旧库首次 apply 结论

- **0000/0001/0002/0003/0005/0006/0007/0008/0009：安全。** 查询条件、`$set`/`$unset`、批量策略（0006 batch 500 unordered；其余单 `update_many` 或逐文档 `update_one`，与 Python 相同量级）、幂等（重跑 no-op 或收敛）、verify 门槛均与 oracle 一致。
- **0004：安全（数据等价），附 4 条需人工核对（R1–R4）。**
  - **R1（需人工核对）：Python 已写 ledger 行被无条件信任。** 若生产库 `migration_record` 已有 Python 0004+ 行，C# 直接跳过而不校验内容。等价性成立的前提是生产 Python 代码与本报告比对的 oracle 修订语义一致；若生产 Python 曾被 hotfix 改过迁移语义，需人工核对数据（抽查 `project_member.ik`、`m_uc`、`ou`、索引 partial 形态）。
  - **R2（需人工核对，低风险）：`casefold` vs `ToLowerInvariant`。** 仅 ß/İ 等极端字符会使 0004 外部 ID（UUID5 输入）或 0006 搜索投影与 Python 跑出的结果不同；拉丁/CJK（含中文标签 `图源/扫图/修图/翻译/校对/嵌字`）完全等价。且同一份数据只会被其中一种 runner 跑一次（ledger 互斥），不存在混合行。
  - **R3（接受）：无文件 evidence bundle。** Python 默认还写 `artifacts/identity-migration/<batch>/`（`m0004:329`）；C# 只写 `identity_migration_report`。verify 不检查 bundle；若运维审计依赖该目录，需改从 Mongo 读。
  - **R4（接受，需知）：C# 写 `int64`，Python 写 `int32`。** `stv/ouv/m_uc` 等计数在 C# 为 `long`（`NonNegativeInt` 返回 `long`）。运行时读写透明；但旧 Python 工具链 `--check/verify`（`$not:{$type:int}` 拒 int64）会对 C# 跑过的库误报——回退到 Python 工具链是单向门（另见 R5）。
- **Runner：安全，附 2 条需知。**
  - **R5：回退单向门。** C# 行的 `c` 是自有体系哈希，旧 Python `status()` 会判“被篡改”而拒绝（0004+ 严格）。C# 切主后不要再用 Python runner 做 `--check`。
  - **R6：锁语义一致。** 30min 租约、逐迁移续租、失败不写行； double-run 互斥。若 `apply` 进程被 kill，租约最长残留 30min（与 Python 相同），按报错提示清理 `migration_lock` 的 `migrate` 文档或等待。

## 4. 修复记录（本分支内已修的 parity bug，均为最小改动）

- **F1** `M0004IdentityMembers.cs`：`mapped_tags` 报告改为当条关系的映射 tag（`sorted(tags)`），原实现误记累计值（Python `m0004:1466`）。
- **F2** `M0004IdentityMembers.cs`（2 处）：`legacy_relation_tag_count` 按 truthiness 计（空串/空数组/null 不计），与 Python `m0004:1408,1694` 的 `if relation.get("m_t")` 对齐。
- **F3** `M0004IdentityMembers.cs` `MergeExistingDocumentsAsync`：补 `duplicate_identity_member` issue（`{count}`），与 Python `m0004:764` 对齐（counter 已有）。
- **F4** `M0004IdentityMembers.cs`：`NormalizedLegacyTags`/alias 收集只保留字符串并 trim，与 Python `m0004:153`（非字符串丢弃）对齐；`legacy_tags` 报告保留原始 BSON 值（原实现转字符串），与 Python `m0004:175` 对齐；`invalid_worker_name` detail 对齐 Python `repr`（字符串加引号、其余原文）。
- **F5** `M0004IdentityMembers.cs` `MigrateWorkers`：`raw_workers_length` 按 code-point 计数（`EnumerateRunes`），与 Python `len()` 对齐（含 emoji 输入）。
- **F6** `LegacyMigrationRunner.cs` `ApplyAsync`：租约使用超 60% 时记 warning，与 Python `runner.py:321` 对齐（原只续租不告警）。
- **F7（真 bug，测试发现）** `M0004IdentityMembers.cs` `MergeExistingDocumentsAsync`：合并预存 `project_member`/`team_member` 时传 `null` projection，被 `ListAsync` 默认收敛为仅 `_id`，导致所有现存成员文档 key 为空、全部误标 `invalid_identity_member`，重复文档永不合并，且后续 `project_member_project_user_v1` 等唯一索引在有重复时直接建索引失败。改为 `Exclude("_cls")` 全文档读取（与 relation 扫描一致）。生产影响：0004 首次 apply 若集合非空（崩溃重试/脏库）必现；纯空库首次跑不受影响。回归测试 `M0004_MergeDuplicates_ReportedWithIssue_AndInvalidCounted` 锁定。

接受的微小差异（不修）：非 string 的项目 `w` 字段，Python 取 `repr(raw)` 仍记录报告并继续（`workers_projects+1`），C# 记 `workers_parse_error` 后直接返回（不计该项目）。`w` 按 schema恒为 string，且 Python 的 `repr` 语义无法从 BSON 精确复刻，故接受现状。

## 5. 测试

`tests/Shoreline.Tests/MigrationsParityTests.cs`（相对 `MigrationsTests.cs` 的 happy-path 的补充，均为 focused `Migrations` filter 可跑）：覆盖空集合、目标已存在、重复运行、缺字段老文档、Python 风格 ledger 行（无 `runner`）、>100（650）文档分批、锁争用、`verify` 失败路径、0004 脏数据分支（dangling/duplicate/missing-role/unmapped-role/owner 修复/join 流程/重跑收敛），以及 F1–F5 回归（per-relation `mapped_tags`、falsy `m_t`、merge dup issue+invalid 计数、非字符串 tag 过滤与 raw 保留、emoji code-point 长度与 `repr` detail）。
结果：`--filter Migrations` 37/37 通过（既有 7 + 本文件 30）。

## 6. 未能验证项

- 生产量级全链路（~1825 项目）耗时与内存：只在合成种子上验证语义，批量策略与 Python 同量级（0006 bulk 500 / 0009 逐文档），但未做大规模压测。
- Python AST checksum 真值：本机无 Python+mongo 驱动环境可交叉计算；C# 采用自有 checksum 体系，跨 runner 比对本就不可比，已用“命名空间隔离 + 单向门”处理。
- 同 fixture 下 Python vs C# 双跑 diff：未执行（无合规的 Python 运行環境）；语义为逐行静态对拍 + C# 侧行为测试覆盖。
- Python 的 mongomock 专用分支（m0005/m0006 的 sparse/逐条 fallback）：C# 仅支持真实 Mongo（测试/生产一致），无需对拍。
