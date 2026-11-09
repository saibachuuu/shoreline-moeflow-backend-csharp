# Partner Search 兼容说明

本文记录 C# 版 `Modules/PartnerSearch` 对 Python 版 `app/modules/partner_search` 的实现边界、行为对齐点和已知差异。模块代码只包含：

- `src/Shoreline.Api/Modules/PartnerSearch/PartnerSearchService.cs`
- `src/Shoreline.Api/Modules/PartnerSearch/PartnerSearchEndpoints.cs`
- `src/Shoreline.Api/Modules/PartnerSearch/PartnerSearchRegistration.cs`
- `tests/Shoreline.Tests/PartnerSearchTests.cs`（18 个直连服务 Fact）
- `tests/Shoreline.Tests/PartnerSearchHttpTests.cs`（6 个 HTTP Fact）

本轮没有修改 `Program.cs`、公共 `SiteSettings`、`Storage`、`Project`、其他模块文件、`csproj`、`packages.lock` 或 `SLN`，也没有在本轮把模块接入共享主程序。

## 模块注册与功能开关的区别

模块注册（`AddPartnerSearchServices()` + `MapPartnerSearch()`）和业务开关（站点设置中的 `partner_search_enabled`）是两层独立状态：

1. **注册**决定进程里是否存在这组路由和服务。注册是宿主启动时的显式动作；本轮宿主未接入，因此共享主程序里访问这些路由仍是 404。
2. **`partner_search_enabled`** 决定已注册的查询接口是否工作。未注册时该设置没有可观察效果；已注册但设置为 `false` 时，`POST /v1/partner-search-query-entry` 返回 403。
3. 测试环境的 HTTP 兼容快照会在临时副本里显式注册并映射模块，用于验证真实 `Program` 管道；共享工作树不做这种接线。

`PartnerSearchRegistration` 注释和本文都保留这个区别，避免把“配置打开”误当成“代码已上线”。

## 路由与鉴权

| 方法 | 路径 | 鉴权 |
| --- | --- | --- |
| `OPTIONS` | `/v1/partner-search-query-entry` | 无 |
| `POST` | `/v1/partner-search-query-entry` | 无 |
| `OPTIONS` | `/v1/partner-search-query-entry/settings` | 无 |
| `GET` | `/v1/partner-search-query-entry/settings` | `admin` |
| `PUT` | `/v1/partner-search-query-entry/settings` | `admin` |

- `OPTIONS` 由模块显式映射为 `200`，即使请求没有 `Origin` 也可用；带 `Origin` 的真实预检仍由全局 CORS 中间件处理，状态为 `204`。
- 管理接口复用 `CurrentUser.RequireAdminAsync`。公共 `ApiError.Forbidden()` 目前构造的是默认 `400`，而 Python `admin_required` 返回 `403`；模块在自己的端点层把 `NoPermissionError`（code 103）规范为 `403`，没有修改公共代码。
- 缺少令牌仍为 `401` + `NeedTokenError`/1003；无效令牌为 `401` + `BadTokenError`/1004。

错误信封沿用 C# 主程序：`{"error": "...", "code": n, "message": ...}`，`message` 可能是字符串或字段错误字典。

## 站点设置读写

设置保存在公共 `site_setting` 集合中 `n: "site"` 的同一个站点文档里：

| API 字段 | BSON 字段 | 类型 | 默认 |
| --- | --- | --- | --- |
| `partner_search_enabled` | `pse` | bool | `false` |
| `partner_search_team_ids` | `pst` | ObjectId[] | `[]` |
| `partner_search_rate_limit_seconds` | `psq` | int，>= 0 | `10` |
| `partner_search_max_limit` | `psm` | int，>= 1 | `20` |

实现约束：

- `GET` 只输出上述四个长字段名，队伍 ID 以字符串返回。
- `PUT` 是严格的部分更新：只处理出现的四个字段；未知键按 Python `DefaultSchema` 的排除方式忽略。`pse` 必须是布尔，`pst` 必须是字符串 ObjectId 数组，`psq >= 0`，`psm >= 1`；类型错误和越界在写库前拒绝。
- 写入先用 `n: "site"` 找到现有文档，再按该文档的 `_id` 只 `$set` 四个模块字段。核心标题、邮件、通知等未知字段原样保留，不会整文档替换。
- 文档不存在时，插入 `{n:"site", pse:false, pst:[], psq:10, psm:20}` 再合并本次字段。模块用进程内互斥 + 重复键重试（最多 3 次）保证并发首次插入只产生一个站点文档；本轮没有让模块创建 `site_setting.n` 的唯一索引，该索引属于核心初始化逻辑。
- 读取不迁移、不清理旧文档：`pst` 同时接受 BSON ObjectId 和可解析为 ObjectId 的字符串，其他元素跳过；`pse` 只认真正的布尔 `true`；缺失或不可解析的 `psq`/`psm` 回退默认值。旧文档中的负数或 `0` 原样读出：`psq <= 0` 因此不限流，`psm <= 0` 时查询 `limit` 兜底为 1。旧文档不会因为 GET 被改写。

## 查询接口

`POST /v1/partner-search-query-entry` 的顺序与 Python 保持一致：

1. 读取设置；未启用立即返回 `403`，`error` 为 `PartnerSearchDisabledError`，`code` 为 `1`，`message` 为英文 `partner search is disabled`。禁用优先于请求体解析。
2. 解析 JSON。空 body、`null`、非 JSON content-type 都按 `{}` 处理；格式错误的 JSON 也按 `{}` 处理（对应 Flask `get_json(silent=True)`）。
3. 解析 `keyword`、`limit`、`page`、`client_key`。
4. 执行限流。即使关键字为空或没有配置队伍，也会消耗一次限流机会。
5. 关键字为空或没有可用队伍时直接返回 `{total:0, page, limit, projects:[]}`。
6. 用 `SearchText.Normalize` 做 NFC、去首尾空白、完整 Unicode casefold（例如 `ß`→`ss`、`ς`/`Σ`→`σ`），不使用 NFKC，也不复用通知模块的归一化。
7. 以规范化关键字做正则字面量搜索（`Regex.Escape` + `i`），匹配 `project.ns`。
8. 只查 `project.t ∈ 配置队伍`、`st = 0`（WORKING）的文档；`st = 1`（CLEARED）和 `st = 5`（COMPLETED）自然被排除。
9. 总数由数据库 `count` 得出；排序 `m_e` 降序、`_id` 降序，与 Python `Project.objects(...).order_by("-edit_time", "-id")`（`app/modules/partner_search/api.py:153`）对齐；`skip = (page-1) * limit` 用 64 位安全计算，超出 Mongo 可接受范围时返回空页而不是溢出成负值。
10. 批量查询当页的 team 和每个项目的首张图片，避免逐项目 N+1。

**排序键修正（曾误排 `et`）**：本查询对象是 `Project`，不是 `ProjectSet`。Python 侧 `Project` 继承 `GroupMixin`，`edit_time` 的 `db_field` 是 `m_e`（`app/core/rbac.py:259`）；`et` 是 `ProjectSet.edit_time` 的字段（`app/models/project.py:294-296`），C# 也只在 `project_set` 视图里这样读（`src/Shoreline.Api/Features/ProjectService.cs:548`）。C# 写入 `Project` 时同样使用 `m_e`：创建见 `ProjectService.cs:227`，更新见 `ProjectService.cs:395`，项目视图 `edit_time = project.Timestamp("m_e")` 见 `ProjectService.cs:490`。修复前 `PartnerSearchService.cs:153` 误用了 `Descending("et")`，在真实项目文档上没有该字段，实际退化为仅按 `_id` 排序；现已改为 `Descending("m_e").Descending("_id")`。测试 fixture 也同步从 `et` 改为 `m_e`（`tests/Shoreline.Tests/PartnerSearchTests.cs` 的 `InsertProject`、`tests/Shoreline.Tests/PartnerSearchHttpTests.cs` 的 `InsertProjectAsync`），旧的“按 `et` 排序”期望随之修正。

`limit` / `page` 对齐 Python 的宽松转换：

- `limit` 缺失、`null`、`<= 0` 或无法转换时使用设置中的 `partner_search_max_limit`（再兜底至少 1）；有效值取 `min(输入, 配置上限)`，至少 1。
- 布尔按 Python `int(bool)` 处理；数字按截断；数字字符串可解析。
- `page` 缺失、`null`、`0`、`false`、空字符串或无法转换时取 1；负数取 1；浮点截断；布尔 `true` 为 1。
- 响应原样返回计算后的 `page`（长整型）与 `limit`。

本项目选择的安全差异：

- 顶层 JSON 不是对象（例如 legacy 列表）返回 `400 ValidateError`，而不是让服务端抛异常。
- `keyword` 是非空数组、非空对象或非零数字时返回 `400 keyword must be a string`。Python 会在 `.strip()` 处 500；这里明确改为可诊断的 400。
- 空数组、空对象、`false`、`0` 仍按 Python 的真值语义视为空关键字。

响应项目字段固定为：

```json
{
  "id": "ObjectId 字符串",
  "name": "项目名",
  "source_name": "sn 或 null",
  "target_name": "tn 或 null",
  "status": 0,
  "intro": "项目简介",
  "team_id": "队伍 ObjectId 或 null",
  "team_name": "队伍名或 null",
  "thumbnail_url": "签名 URL 或 null"
}
```

`source_name` 读 `sn`、`target_name` 读 `tn`、`intro` 读 `i`、`name` 读 `n`；不会返回成员、密钥或其他未列字段。

## 缩略图

缩略图只使用默认对象存储（与 Python `cover_url` 一致），不按项目 `csid` 改走项目存储：

- 文件必须 `ac = true`、`t = 2`（IMAGE）。
- 同一项目内按 Python `Project.files()` 的实际顺序选第一张：默认 `dn`、`t`、`sn`、`_id` 升序；如果该项目存在任一首图候选带非空 `mo`（manual_order），改为 `dn`、`t`、`mo`、`sn`、`_id` 升序。
- 只有该文件 `sa` 非空且 `th = 3`（SUCCEEDED）时才尝试封面：截取 `sa` 最后一个点之前的部分，拼成 `files/cover-{stem}.webp`，先 `HeadAsync` 确认对象存在，再 `SignGetAsync` 生成 1 小时有效的 URL。
- `th != 3`、`sa` 为空、对象不存在或签名异常都返回 `null`。不会为不存在的对象伪造成功 URL；Python 在 `SUCCEEDED`/部分 legacy `UNKNOWN` 情况下会直接签名，这里更保守，需要文档记录这一差异。
- 只处理当页项目；没有图片时返回 `null`。

## 限流

限流集合为 `partner_search_throttle`：

- 文档 `_id` 是限流 key，`lt` 是最后一次允许时间。
- key 为 `ip:{ip}`；请求体 `client_key` 非空时追加 `:key:{client_key}`。
- IP 取 `X-Forwarded-For` 逗号前的第一段并去空白；没有该头时取连接远端地址；仍为空时用 `unknown`。
- `partner_search_rate_limit_seconds <= 0` 时直接放行，不读写数据库。
- 先做一次原子条件更新：`_id = key && lt <= now - window` 时把 `lt` 更新为 `now`，匹配到即放行。
- 没有匹配时尝试插入 `{_id:key, lt:now}`；`_id` 唯一键冲突表示窗口内已有请求，返回限流。这样并发请求只有第一个插入者能通过，不会先读再盲目替换。
- 超限返回 `429`，`error = PartnerSearchRateLimitedError`，`code = 2`，`message = rate limited, please retry later`。
- 时钟通过 `TimeProvider` 注入，测试可精确推进窗口边界。
- key 长度有上限（超过 200 字符时截断并追加 SHA-256 前缀），避免异常客户端制造超长 `_id`；没有给该集合加 TTL，旧时间戳文档会保留，后续同 key 到达时由条件更新复用。若长期存在大量不同 IP/key，集合会随访问来源增长，这是当前保留的运维风险，而不是通过 TTL 静默改变限流语义。
- 这套协议沿用旧行为：调用方可以自报 `X-Forwarded-For` 和 `client_key` 来绕过限流。它不是安全边界，也不在本模块内擅自改动；是否收紧由主程序/网关后续决定。

## 测试边界

- 直连测试使用 `LocalServices` 的随机数据库，每个 Fact 自己创建、自己删除，不删除非本 Fact 拥有的数据库。
- HTTP 测试优先使用真实 `WebApplicationFactory<Program>`：临时兼容快照里会显式注册、映射本模块，从而覆盖真实中间件、CORS、鉴权和错误信封。共享工作树尚未接入模块时，测试会退回到用同一批服务、同一套 CORS/ApiError 管道和 `MapPartnerSearch` 构建的最小 HTTP 宿主，并在测试代码里明确标注；这不是共享主程序已经接线的证据。
- 依赖仅限本地一次性 Mongo（127.0.0.1:27128）和 S3Mock（127.0.0.1:29128）；不访问真实合作方网络、不调用真实 SMTP、不使用 R2、不使用 SSH 或生产配置。
- 覆盖点包括：默认值与部分更新保留核心字段、PUT 严格校验、旧文档不迁移、禁用优先于解析、空关键字/空队伍仍限流、limit/page 的布尔/浮点/字符串/溢出、NFC casefold 的 `ß`/`σ` 与正则字面量、队伍和状态过滤、`m_e`/`_id` 同时间排序以及 `_id` 顺序与时间顺序冲突时以 `m_e` 为准、忽略项目集 `et`、`th=3` 与对象存在性、手工排序、非激活图片、`csid` 不改变默认存储、限流窗口边界、24 路并发只放行一个、HTTP 的 IP/client_key 组合、并发首次写入站点设置、四个长字段精确输出。
