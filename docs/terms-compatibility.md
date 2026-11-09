# F1 术语库 / 术语兼容性说明

本切片只交付 `TermService`、`TermEndpoints`、`TermRegistration`、direct-service 测试和本说明。
shared `Program.cs` 已加入最小接线 `AddTermServices()` / `MapTerms()`（仅 2 行，保留既有 Presence/Application 注册与映射）。构建/测试证据来自独立 clean snapshot：`33c93ea` 临时副本 + shared 的 F1 五文件 + Program + `TermHttpTests`，未混入其他 WIP。当前已完成 F1 路由 port，但项目术语库选择与 `find_terms` 仍缺，整体不代表“已可替代 Python”。

## 文件

| 文件 | 作用 |
| --- | --- |
| `src/Shoreline.Api/Features/TermService.cs` | 直接服务实现：CRUD、权限、校验、分页、视图、副作用 |
| `src/Shoreline.Api/Features/TermEndpoints.cs` | 8 条 legacy 路由；shared `Program.cs` 已 `MapTerms()` |
| `src/Shoreline.Api/Features/TermRegistration.cs` | `AddTermServices()` / `MapTerms()` 接线入口 |
| `tests/Shoreline.Tests/TermTests.cs` | 12 个 direct-service 兼容事实，不依赖路由接线 |
| `tests/Shoreline.Tests/TermHttpTests.cs` | 3 个 HTTP 事实：鉴权、真实路由 CRUD/分页、团队隔离与 owner 分支 |
| `docs/terms-compatibility.md` | 本说明 |

主代理接线方式：

```csharp
builder.Services.AddTermServices();
// after building:
app.MapTerms();
```

## Snapshot 验证

隔离快照来自 `git archive 33c93ea`（Storage 已提交基线，原 **226** 个测试），目录
`C:\Users\88888888\AppData\Local\Temp\shoreline-term-commit-20261109-214900`。快照只复制
shared 的 F1 三份 source、两份 tests、`docs/terms-compatibility.md` 和 shared `Program.cs`；
不含其他 agent 的 WIP。

- `dotnet restore`：通过。
- `dotnet build`：**0 warnings / 0 errors**。
- `TermTests`：**12 passed / 0 failed**。
- `TermHttpTests`：**3 passed / 0 failed**（missing token 1003、真实路由 CRUD + 分页头、跨团队列表隔离 + owner 分支）。
- 全量 snapshot 测试：**241 passed / 0 failed**（226 基线 + 12 direct + 3 HTTP）。

以上证据覆盖快照中的 F1 七文件；shared 仍含其他切片 WIP，未直接在 shared 混跑构建。F1 路由已接线，但整体 port 不是完整替代。

## 路由

严格按 `app/apis/urls.py`，不采用文档中可能出现的 `team-banks` 拼写：

| Method | Path | Legacy source |
| --- | --- | --- |
| GET | `/v1/teams/{teamId}/term-banks` | `urls.py:347-351` |
| POST | `/v1/teams/{teamId}/term-banks` | 同上 |
| PUT | `/v1/term-banks/{termBankId}` | `urls.py:385-390` |
| DELETE | `/v1/term-banks/{termBankId}` | 同上 |
| GET | `/v1/term-banks/{termBankId}/terms` | `urls.py:391-395` |
| POST | `/v1/term-banks/{termBankId}/terms` | 同上 |
| PUT | `/v1/terms/{termId}` | `urls.py:396-401` |
| DELETE | `/v1/terms/{termId}` | 同上 |

不存在单条 `GET /v1/term-banks/{id}`，也没有 `GET /v1/terms/{id}`。`OPTIONS` 由框架处理，不计业务路由。

## 数据映射

### `term_bank`

| Legacy 字段 | BSON | 语义 |
| --- | --- | --- |
| `team` | `t` | ObjectId ref；缺失表示系统库 |
| `name` | `n` | 1..40 字符 |
| `source_language` | `sl` | 必需 Language ref |
| `target_language` | `tl` | 必需 Language ref |
| `tip` | `ti` | 0..140，默认 `""` |
| `user` | `u` | ObjectId ref |
| `create_time` | `c` | UTC |
| `edit_time` | `e` | UTC，默认排序键 |

### `term`

| Legacy 字段 | BSON | 语义 |
| --- | --- | --- |
| `term_bank` | `tb` | 必需 TermBank ref |
| `source` | `o` | 1..40 |
| `target` | `t` | 1..40 |
| `tip` | `ti` | 0..140 |
| `user` | `u` | ObjectId ref |
| `create_time` | `c` | UTC |
| `edit_time` | `e` | UTC，默认排序键 |

长度使用 .NET rune 计数，对应 Python `len()` 对 code point 的计数；不是 UTF-16 code unit 长度。未知 JSON 字段忽略，与 legacy `DefaultSchema Meta.unknown = EXCLUDE` 一致。

## 权限矩阵

`site admin` 不是团队成员，本实现不会绕过 membership（与 `IdentityPermissions` 的既有规则一致）。

| 操作 | 允许条件 |
| --- | --- |
| GET team bank list | `team:ACCESS` |
| POST team bank | `team:CREATE_TERM_BANK` |
| PUT bank | `team:CHANGE_TERM_BANK` **或** bank owner |
| DELETE bank | `team:DELETE_TERM_BANK` **或** bank owner |
| GET terms | `team:ACCESS_TERM_BANK` **或** bank owner |
| POST term | `team:CREATE_TERM` **或** bank owner |
| PUT term | `team:CHANGE_TERM` **或** bank owner **或** term owner |
| DELETE term | `team:DELETE_TERM` **或** bank owner **或** term owner |

没有隐式把 `ACCESS` 加到其他权限判断上；owner 分支也不能被“拥有团队其他权限”替代。

## 兼容语义

- 所有 POST/PUT 字段必填；PUT 也要求完整字段，不按 patch 处理。错误 code 为 `2`，字段错误形如 `{"name":["必填"]}`。
- 语言 id 不是合法 ObjectId：`101 错误的ObjectId`；语言不存在：`6001 语言不存在`；source 与 target 相同：`6002 原语言和目标语言不能相同`。
- TermBank 不存在：`7001 术语库不存在`；Term 不存在：`7002 术语不存在`；非法 ObjectId 同样映射到 7001/7002。
- 响应：创建/修改/删除只返回 `{"message":"创建成功/修改成功/删除成功"}`，不返回新对象。
- 列表：返回 JSON 数组；使用 legacy 分页默认值 **30**（共享 `Pagination` 默认 20，因此 endpoint 单独解析），最大 100，`page>=1`。响应头：`X-Pagination-Page`、`X-Pagination-Limit`、`X-Pagination-Count`、`X-Pagination-Page-Count`。
- 搜索：bank 按 `n`；term 按 `o`/`t`/`ti` 的 `$or`；均使用 `Regex.Escape` 后的不区分大小写 Mongo regex，避免把用户输入当正则。
- 排序：bank/term 默认 `e:-1`（`-edit_time`）。
- 视图：bank 输出 `id/name/source_language/target_language/tip/user/create_time/edit_time`；term 输出 `id/source/target/tip/user/create_time/edit_time`。user 使用现有 `UserView`（admin viewer 带 email；本人带 `can_manage_site_admins`）。语言字段与 `ProjectService.LanguageView` 保持一致（`e/c/co/gt/go/n/s`）。
- 批量视图通过 `$in` 一次性加载 user/language，避免逐行 N+1。
- 系统术语库 `term_bank.t` 缺失或不是 ObjectId 时返回“无团队”状态，不会转成 `ObjectId.Empty`，也不会因此崩溃；非 owner 无法通过团队权限访问系统库。

## 副作用

- `Term.create` 后：对所有 `project.tb` 包含该 bank 的项目 `$set nft=true`（`need_find_terms`）。
- `Term.edit`：只有 `source` 真正变化时才设置 `nft=true`；只改 target/tip 不设置。
- `TermBank` 修改不触发 `nft`。
- `Term.delete`：从 `source.pt` (`possible_terms`) `$pull` 该 term。
- `TermBank.delete`：legacy 依赖 MongoEngine reverse delete rule：
  - `Term.term_bank` CASCADE → 删除该 bank 的全部 terms；
  - `Source.possible_terms` PULL → 从 source.pt 移除这些 terms；
  - `Project.term_banks` PULL → 从 `project.tb` 移除该 bank。
  本实现按相同顺序手工完成；term 清理使用 200 条一批的 bounded cursor，每批先 PULL `source.pt` 再删除 terms，最后 PULL `project.tb` 并删 bank，避免一次性构造超大 `$in`。**没有事务**，与 legacy 一样不是原子操作；测试覆盖了 205 条 bulk terms 跨两批的情况。
- 不实现 `find_terms` 后台 job。创建/修改后只写入正确的 `nft` 标记；实际术语索引刷新由后续阶段补齐。本切片不会返回“job 已完成”的假状态。

## 已接线 / 待验证

1. shared `Program.cs` 已最小接线 `AddTermServices()` / `MapTerms()`，既有 Presence/Application 代码未改。
2. shared 仍含其他切片 WIP，未直接在 shared 混跑构建；clean snapshot 已 build 0/0，并跑通 12 direct + 3 HTTP + **241** 全量。
3. `ProjectService.UpdateProjectAsync` 目前不处理项目术语库选择；它不属于本切片，不能修改。主代理后续集成项目 `tb` 关联。
4. `find_terms` job、术语实际匹配/替换流程未 port。
5. 缺失 user/language 引用的视图会返回 `null` 而不是抛错；这是防御性行为，正常数据不应出现。该项属于对 legacy 的反向引用健壮性增强，需在集成验收时确认。
6. 没有新增唯一索引；legacy 同名术语库/术语允许重复。查询索引是否追加需主代理结合整体 Mongo 索引策略决定。
7. `app/models/term.py` 没有声明 MongoEngine `meta.indexes`/`index`，因此本切片不提供 `InitializeIndexes`；没有需要复刻的旧索引，也绝不新增 unique。
8. 语言视图沿用现有 `ProjectService.LanguageView`，`lo_name`/`i18n_name` 使用存储的 `c`；legacy `Language.to_api` 的 Flask-Babel `gettext` 请求语言本地化不在本切片单独实现，属于既有 C# 语言视图的共享边界。

## 测试事实（snapshot 已通过）

`tests/Shoreline.Tests/TermTests.cs` 覆盖：

1. 创建 bank 的 legacy BSON 字段与完整视图。
2. PUT 全字段必填、字符串类型、rune 40/41 边界、同语言 6002、缺失语言 6001。
3. team ACCESS 与 owner 分支分离；site admin 不绕过 membership。
4. 跨团队 actor 读取/创建被拒绝。
5. 分页默认 30、最大 100、搜索 literal regex 转义、批量视图。
6. term 搜索覆盖 source/target/tip 且转义。
7. create term 设置 nft；target/tip 修改不设置；source 修改设置；bank 修改不设置。
8. 删除 bank 级联 terms、project.tb、source.pt，并覆盖 205 条 terms 的两批清理。
9. 删除单个 term 只 PULL 对应 source.pt。
10. 系统 bank 缺少 `t` 时仅 owner 分支可用，且不崩溃。
11. 7001/7002 映射与 dangling refs 视图容错。
12. `ListBanksAsync` 强制 `t` 团队过滤；两个团队都有 `Alpha` 时列表/搜索/total 只统计当前团队。

`TermHttpTests.cs` 另有 3 条 HTTP 事实：

1. 未带 token 返回 401 + legacy code 1003。
2. 真实 legacy 路由完成 bank/term CRUD，并验证 `X-Pagination-*` 与默认 30/max 100。
3. HTTP 层验证团队列表隔离与“非成员 bank owner”可编辑、非 owner 被 103 拒绝。

snapshot 执行结果：

```powershell
dotnet build
dotnet test --filter FullyQualifiedName~TermTests
dotnet test --filter FullyQualifiedName~TermHttpTests
dotnet test
```

结果分别为 build 0/0、12 passed、3 passed、**241 passed**。这是 `33c93ea` + F1 七文件的 clean snapshot 证据。

F1 路由已接线；在完成权威比对、项目 `tb` 选择与 `find_terms` 集成前，不应把整体后端标记为“已替代 Python”。
