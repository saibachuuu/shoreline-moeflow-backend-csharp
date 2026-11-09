# 管理员创建用户兼容说明（AdminUserListAPI.post）

本文记录 `POST /v1/admin/users` 的 Python → C# 契约。该路由此前只在 C#
注册了 `GET /v1/admin/users` 与 `PUT /v1/admin/users/{id}`。

## Python 对照

- 路由：`app/apis/urls.py:606` → `AdminUserListAPI`（`app/apis/user.py:134`）
- 方法：`AdminUserListAPI.post`（`app/apis/user.py:169-176`）
- Schema：`AdminRegisterSchema`（`app/validators/auth.py:167-184`）
- 用户创建：`User.create`（`app/models/user.py:143-155`）
- 鉴权：`@admin_required`（`app/decorators/auth.py:26-40`），要求
  `current_user.admin is True`；站点级封禁在 token 校验阶段已拒绝。

## 请求与响应

请求 JSON：

```json
{ "email": "user@example.com", "name": "用户名", "password": "secret12" }
```

成功：

```json
{ "message": "注册成功" }
```

Python 不返回 token；新用户 `admin` 固定为 `false`。

## 字段校验

与公开注册复用同一套 `UserValidate`（`app/validators/custom_validate.py:90-112`）：

- `email`：必须匹配 `EMAIL_REGEX`，且不存在同邮箱用户（大小写不敏感，落库小写）。
- `name`：Unicode 长度 2–18；匹配 `USER_NAME_REGEX`；同昵称大小写不敏感唯一。
- `password`：长度 6–60。
- 任一失败 → `ValidateError` / code `2`，字段级错误数组；不创建用户。

C# 在 `UserRegistrationService` 中抽取了公开注册与管理员创建共用的
`ValidateAccountAsync` / `InsertUserAsync` / `AutoJoinAsync`，保证文案、顺序、
name gate、DuplicateKey 兜底和 `site_setting.ajt` 自动入队行为一致。

## 与公开注册的差异

- 不需要 `v_code`，不消费邮箱验证码。
- 不检查 `site_setting.enable_whitelist` 白名单。
- 不返回 token。
- 仍执行 `User.create` 的自动加入默认团队逻辑（`site_setting.ajt`）；单个团队
  加入失败只记 warning，不回滚已创建账号，与 Python 先 `save()` 再 `join()` 一致。

## C# 改动

- `src/Shoreline.Api/Features/AdminUserEndpoints.cs`：新增
  `app.MapPost("/v1/admin/users", ...)`，调用 `current.RequireAdminAsync`。
- `src/Shoreline.Api/Features/UserRegistrationService.cs`：新增
  `AdminCreateAsync(JsonElement data, string requestId, CancellationToken ct)`，
  并抽取与 `RegisterAsync` 共用的校验/插入/自动加入逻辑。
- `Program.cs` 已有 `app.MapAdminUsers()` 接线，无需新增调用。
- 测试：
  - `tests/Shoreline.Tests/AdminUserTests.cs`
    `AdminCreateService_UsesAdminRegisterSchemaAndPersistsOrdinaryUser`（direct）
  - 同文件 `AdminCreateHttp_RequiresAdminAndReturnsLegacyMessage`（HTTP）

## 已知差异 / 未验证

- Python 的 `User.create` 会写入 mongoengine 默认字段；C# 显式写入当前注册路径
  使用的最小字段集（`e/n/as/ns/asrch/ddn/s/l/t/a/b/p/admin/c`）。未在本路由
  单独差分所有历史文档字段的默认值。
- 未验证真实邮件/邀请副作用：管理员创建本身不发邮件，与 Python 一致；自动加入
  团队若触发通知，由既有团队通知链路处理。
- 站点 `admin` 标志的授予仍只能走 `PUT /v1/admin/admin-status`（仅
  `Auth:AdminEmail` 指定账号可操作），本路由不会创建管理员。
