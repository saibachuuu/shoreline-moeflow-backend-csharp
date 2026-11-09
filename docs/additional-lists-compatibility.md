# 附加列表接口兼容性说明（I1 独立实现）

> 状态：**本地联合验证完成**。I1 实现通过 `AdditionalListRegistration` 接入共享
> `Program.cs`，且仅在 `Program.cs` 增加
> `builder.Services.AddAdditionalListServices()` 与 `app.MapAdditionalLists()`
> 两处接线；`/v1/admin/users` 继续复用既有 `MapAdminUsers()`，没有重复映射。
> 本文档记录的是本地提交前的验证证据，不代表已推送远程或 production-ready。

## 范围与新增文件

新增以下 8 个文件，另对 `Program.cs` 增加上述两行接线：

- `src/Shoreline.Api/Features/UserProjectListService.cs`
- `src/Shoreline.Api/Features/UserProjectListEndpoints.cs`
- `src/Shoreline.Api/Features/AdminListService.cs`
- `src/Shoreline.Api/Features/AdminListEndpoints.cs`
- `src/Shoreline.Api/Features/AdditionalListRegistration.cs`
- `tests/Shoreline.Tests/AdditionalListTests.cs`
- `tests/Shoreline.Tests/AdditionalListHttpTests.cs`
- `docs/additional-lists-compatibility.md`（本文件）

除 `Program.cs` 的两行接线外，未修改 `ProjectService`、`UserService`、`MongoStore`、
VCode 相关共享代码、其他 agent 文件或 `.csproj`。

## 接口一：GET /v1/user/projects

Legacy 参考：`app/apis/me.py:350-430`（`MeProjectListAPI`），路由注册在
`app/apis/urls.py:161` 和 `app/apis/urls.py:197-201`。

实现：

- `UserProjectListEndpoints.MapUserProjectLists` 注册 `GET /v1/user/projects`。
- 认证：`CurrentUser.RequireAsync`，缺失/无效令牌分别返回 legacy
  `NeedTokenError`/`BadTokenError`（1003/1004，HTTP 401）。
- 仅 `project_member` 中 `user=当前用户` 且 `status="active"` 的直接成员关系构成
  列表来源。团队管理员身份**不会**让未直接加入的项目出现在列表中。
- `status` 参数支持重复键和逗号分隔，逐项 trim 并忽略空串；大小写敏感：
  - `NORMAL` → `{0}`
  - `CLEARED` → `{1}`
  - `COMPLETED` → `{5, 1}`
  - 其他（含小写 `normal`）抛出 `InvalidIdentityRequestError`，code `5100`，
    identity_code `INVALID_REQUEST`，HTTP 400，message 精确为
    `身份标签请求参数错误: invalid project status`。
- `word` 仅匹配 `project.name_search`（BSON 字段 `ns`），使用
  `SearchText.Normalize` + `Regex.Escape` 的 literal 大小写不敏感正则；不搜索
  项目简介、别名等其他字段。Unicode 经 NFC + casefold 风格归一化。
- 排序 `edit_time` 降序；Project 继承 `GroupMixin`，其 BSON 字段是 **`m_e`**，
  不是 `ProjectSet` 使用的 `et`。分页通过现有 `Pagination`（默认 limit 20，最大
  100），并写入 `X-Pagination-Page/Limit/Count/Page-Count`。
- 查询使用 Mongo aggregation：先 match 直接 active 成员关系，再 `$lookup` 项目、
  按状态/名称过滤、排序，最后 `$facet` 同时计算 total 和当前页。不会先把 actor
  的所有项目 ID 拉到内存再分页。
- 卡片复用 `ProjectService.ListCardAsync`，字段与 legacy `batch_to_list_api` 一致：
  `group_type`, `id`, `name`, `status`, `effective_permissions`（与
  `project:ACCESS`/`project:MANAGE_MEMBERS`/`project:COMPLETE_PROJECT` 求交集并
  排序）, `source_count`, `target_count`, `translated_source_count`,
  `checked_source_count`, `file_size`, `team`（compact：`group_type`,`id`,`name`,
  `worker_qualification_mode`）, `project_set`（`id`,`name`,`default`）。
- 每页一次性查询 `project_member`（`status` 为 `active` 或 `invited`），按
  `display_name`、`_id` 升序构造 compact `member_summary`：
  `{id, display_name, tags, status}`，并附加到对应卡片；无成员时为 `[]`。查询带
  projection，只读取 `project/display_name/tags/status`，避免把成员原文档的无关
  payload 拉进内存。没有使用 `ProjectService.MemberSummariesAsync` 的逐项目 N+1 路径。
- 每页通过 `IdentityPermissions.ProjectSnapshotsAsync` 批量获取权限快照，并批量
  读取 team/project_set；不会逐卡片查询。

## 接口二：GET /v1/admin/users

Legacy 参考：`app/apis/user.py:134-172`（`AdminUserListAPI`）。

该路由在稳定基线的 `src/Shoreline.Api/Features/AdminUserEndpoints.cs:14-29` 中**已经
存在**并由 `Program.cs:95` 的 `MapAdminUsers()` 接线。本次没有重复注册，否则会
产生 ambiguous route。隔离 HTTP 测试通过共享映射验证其兼容性：

- 管理员保护：`RequireAdminAsync`；非管理员返回 `NoPermissionError`（103，
  HTTP 400），缺失/无效令牌返回 1003/1004（HTTP 401）。
- `word` 允许空；只搜索 `name_search`（`ns`）和 `aliases_search`（`asrch`），
  literal、大小写不敏感、归一化；不搜索邮箱、密码等字段。
- 排序 `create_time`（`c`）降序，使用现有分页和 `X-Pagination-*`。
- 响应复用 `UserView.ToApiAsync`：不会泄露密码或令牌；`email` 仅对管理员
  viewer 返回；`can_manage_site_admins` 仅 viewer 等于目标用户时返回。

## 接口三：GET /v1/admin/v-codes

Legacy 参考：`app/apis/v_code.py:135-153`（`AdminVCodeListAPI`），路由
`app/apis/urls.py:621-624`；模型 `app/models/v_code.py:32-40`，常量
`app/constants/v_code.py:8-24`。

实现：

- `AdminListEndpoints.MapAdminLists` 注册 `GET /v1/admin/v-codes`，使用
  `RequireAdminAsync`。
- 读取共享 Mongo 集合 `v_code`（不是 C# 的 captcha 集合，也不是只存新邮箱验证码
  的集合），查询 `{t: {$ne: 1}}` 排除 `CAPTCHA`，按 `s`（send_time）降序，
  limit 100；返回裸数组，无分页包装。
- 每项字段与 legacy 一致：`id`（字符串）、`content`（`c`）、`intro`（类型中文名）、
  `info`（`i`）、`expires`（`e`）、`wrong_count`（`w`，缺省 0）、`send_time`（`s`）。
- 类型中文名：1 人机验证码、2 验证邮箱、3 重设邮箱、4 重置密码、5 验证手机、
  6 重设手机；未知类型安全回退为 `未知类型`，不抛异常。
- 时间格式模拟 Python `datetime.isoformat()`：UTC、无尾随 `Z`，微秒非零时固定
  6 位小数，微秒为零时不输出小数部分。
- 不向日志输出 `content`、`info` 或其他验证码内容。

## 隔离验证

验证分为两个阶段，302 是早期独立快照的历史证据，323 是当前联合验证分母。

### I1 独立验证历史（302）

早期将稳定点 `e910e3399b99ec095a7ab56c49866cf865ef6ebb` 用 `git archive`
导出到 `C:\Users\88888888\AppData\Local\Temp\opencode` 下的独立目录，只复制本任务
允许的新文件，并仅在临时快照 `Program.cs` 中加入两处：

```csharp
builder.Services.AddAdditionalListServices();
...
app.MapAdditionalLists();
```

该阶段共享工作副本尚未接线，结果为：稳定基线完整测试 **283 通过 / 0 失败**；
复制 I1 文件并临时接线后，聚焦测试 **19 通过 / 0 失败**，完整测试
**302 通过 / 0 失败**（283 + 19）。这是历史证据，不是当前联合分母。

### 联合验证当前（323）

- 以最新稳定点 `0eddb2e98712369435b531dcb4f78eeee352e966` 独立导出，稳定基线
  `dotnet build` 0 警告 0 错误，完整测试 **304 通过 / 0 失败**。
- 共享 `Program.cs` 现已加入且仅加入两处接线：
  `builder.Services.AddAdditionalListServices()` 与 `app.MapAdditionalLists()`；
  `/v1/admin/users` 继续复用既有 `MapAdminUsers()`，没有重复映射。
- 将本任务 8 个文件、最终文档与共享 `Program.cs` 复制到该临时快照后：
  `dotnet build` 0 警告 0 错误；聚焦测试 **19 通过 / 0 失败**；完整测试
  **323 通过 / 0 失败**（304 + 19）。文档不参与编译，但会与代码一起校验 SHA。
- 共享工作副本与临时快照中的 8 个本任务文件及 `Program.cs` SHA-256 一致；
  本地提交仅包含这 9 个路径。

测试覆盖的重点兼容事实包括：

- 状态参数重复/逗号拆分/空 token 忽略/大小写敏感错误码文本；
- `COMPLETED` 只含 5 和 1，不含 0/2/3；
- 仅直接 active 成员；removed/invited 与“仅团队管理员”项目不出现；
- 名称搜索 Unicode、literal 元字符、大小写不敏感，且不搜索 intro/alias；
- `edit_time`（`m_e`，不是 `et`）降序、分页 total/headers；旧 `et` 与 `m_e` 相反
  顺序以及项目缺少 `et` 时仍按 `m_e` 排序；
- 卡片与 compact member_summary 的精确字段形状、批量分页不串项目；
- `effective_permissions` 交集排序；
- v-code 排除 CAPTCHA、100 条上限、send_time 降序、Python 式日期、已知/未知
  intro、`wrong_count` 缺省值；
- HTTP：缺失令牌 401/1003、普通用户 400/103、admin 200，admin users 的
  name/alias-only 搜索、排序/分页、无密码/令牌泄露、邮箱仅 admin 可见。

## 已知边界

- 本实现依赖 legacy 已存在的数据形状（`project.t`/`project.ps` 指向存在的
  team/project_set）。缺失关联文档时使用空占位，不会改变正常数据下的输出。
- `GET /v1/admin/users` 属于既有共享实现，不在本任务的“新增路由”范围内；本任务
  只补充其兼容性 HTTP 覆盖。
- 共享工作副本已完成联合验证与接线；本文件仍不声称已推送远程或生产环境已经就绪。
