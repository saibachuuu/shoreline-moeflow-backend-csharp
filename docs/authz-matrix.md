# 全路由授权矩阵 (AuthZ Matrix)

> Flask oracle: `shoreline-moeflow-backend/app/{core/rbac.py, decorators/auth.py, services/identity_permission.py, apis/*.py}`.
> C# port: `src/Shoreline.Api/{Security/IdentityPermissions.cs, Security/CurrentUser.cs, Features/*Endpoints.cs, Features/*Service.cs}`.
> 状态: C# 行为已逐端点对照 Python 源码；`待确认` 表列出未定语义（不断言、不猜测）。
> 测试: `tests/Shoreline.Tests/Authz*Tests.cs`（`FullyQualifiedName~Authz`）。

## 0. 全局约定

HTTP 映射（两端一致）:

| 场景 | HTTP | `error` | `code` |
|---|---|---|---|
| 未带令牌 | 401 | NeedTokenError | 1003 |
| 令牌格式错 / 非 Bearer / 验签失败 / 用户不存在 / 改密后旧令牌 | 401 | BadTokenError | 1004 |
| 账号被封禁 (`b=true`)，且路由经 `token_required` / `RequireAsync` | 400 | UserBannedError | 1006 |
| 无权限（`NoPermissionError`） | 400 | NoPermissionError | 103 |
| 资源不存在（team/project/target/file/language/set… legacy `*NotExistError`） | 400 | 各 `*NotExistError` | 3xxx/4xxx |
| 成员/用户不存在（`Identity*NotFoundError`） | 404 | `Identity*NotFoundError` | 51xx |
| 请求体非法 | 400 | ValidateError / RequestDataEmptyError / … | 2 / 110 / … |

注意：

- 本矩阵把"无权限"记为 **403**，实际线上传输为 `400 + NoPermissionError/103`（Flask 与 C# 一致，
  `app/services/project_member.py:102` 甚至显式写 `403 if isinstance(error, NoPermissionError)`，
  但默认错误映射仍是 400；测试按实际状态码断言）。
- 同理"资源不存在"：legacy `*NotExistError`（team/project/file/target…）实际为 **400**，
  只有 `Identity*NotFoundError`（成员/用户）为真正的 404。两端一致，测试按实际断言。
- Python `admin_required` 只看 `admin_can()`（`admin is True`，**不检查 banned**）；
  C# `RequireAdminAsync` 先过 `RequireAsync`（banned 照样 1006）。C# 更严格，视为修复而非缺口。
- 站内管理员（`admin=true`）**不绕过**任何项目/团队权限检查（C# 注释与 Python 快照语义一致）；
  无成员关系的管理员在业务路由上等同陌生人。
- 项目状态 `st != 0`（cleared/completed）时，所有 `_NORMAL_ONLY_PERMISSIONS` 全部失效
  （Python `can_project` + C# `CanProject` 一致）：`ADD/MOVE/RENAME/DELETE_FILE, OUTPUT_TRA,
  ADD/MOVE/DELETE_LABEL, ADD_TRA, DELETE_TRA, PROOFREAD_TRA, CHECK_TRA,
  ADD/CHANGE/DELETE_TARGET`。`ACCESS/CHANGE/COMPLETE_PROJECT/成员管理类`不受影响。
- 鉴权与校验的先后顺序按路由而异（矩阵中"顺序"列标注 `auth→valid` 或 `valid→auth`）；
  无权限者发非法包体时，`valid→auth` 的路由返回校验错而非 103。

身份列缩写：`A` 匿名（无头） / `B` 坏 token / `X` 被封禁 / `S` 纯陌生人 /
`TM` 团队普通成员 / `TA` 团队管理员 / `TC` 团队创建者 /
`PT` 项目 translator / `PP` 项目 proofreader / `PY` 项目 typesetter /
`PC` 项目 owner（creator 且 `ou` 指向自己）/ `SA` 站内管理员（无成员关系）。

系统标签默认权限（`IdentityPermissions.ProjectDefaults/TeamDefaults`，与
Python `PROJECT_TAG_PERMISSIONS / TEAM_BASE_PERMISSIONS` 逐项核对一致）：

- project `creator`/`admin`：全部（含 `MANAGE_MEMBERS, COMPLETE_PROJECT, INVITE_USER, CHECK_USER` …）。
- `cropper/cleaner/scanner/raw_provider`：`ACCESS ADD_FILE MOVE_FILE RENAME_FILE DELETE_FILE`。
- `translator`：文件五项 + `OUTPUT_TRA ADD_LABEL MOVE_LABEL DELETE_LABEL ADD_TRA DELETE_TRA`。
- `proofreader`：translator + `PROOFREAD_TRA CHECK_TRA`。
- `typesetter`：文件五项 + `OUTPUT_TRA ADD_LABEL MOVE_LABEL DELETE_LABEL`。
- team `creator`：全部 team 权限；`admin`：除 `DELETE` 外全部；`member`：仅 `ACCESS`。

## 1. 认证原语（所有路由共享）

| 路由示例 | A | B | X | 备注 |
|---|---|---|---|---|
| 任意需登录路由 | 401/1003 | 401/1004 | 400/1006 | `RequireAsync` |
| 任意 `admin/*`、storage-pool、admin files | 401/1003 | 401/1004 | 400/1006（C#；Python oracle 不查 banned） | `RequireAdminAsync` |

## 2. 团队（Teams）

| 端点 | 允许（200/201） | 403 | 404 | 顺序 | Oracle |
|---|---|---|---|---|---|
| POST /v1/teams | SA（默认 `oacg=true` 时仅 SA）；`oacg=false` 时任意登录用户 | X（1006，先于 gate）；`oacg=true` 时非 SA → 3008 | — | gate→valid | `team.py:189` gate 两端一致 |
| GET /v1/teams/{id} | TM/TA/TC（team:ACCESS 任一来源） | S/PT（无 team 关系） | team 不存在 | auth→resource | `team.py:228` can(team, ACCESS) |
| PUT /v1/teams/{id} | TA/TC（team:CHANGE）；`proofread_marker_*` 仅 TC | TM/S | team 不存在 | auth→valid（`{}` 包体时先鉴权） | `team.py:258` + `:267` creator-only |
| DELETE /v1/teams/{id} | 仅 TC（team:DELETE） | TA/TM/S | team 不存在 | auth→gate | `team.py:324` can(team, DELETE) |
| GET /v1/user/teams, GET /v1/teams | 任意登录用户（只列出自己的） | — | — | — | `TeamListAPI.get` 无额外检查 |
| GET /v1/teams/{id}/members | TM/TA/TC（team:ACCESS） | S | team 不存在 | resource→auth | `TeamMemberService.ListAsync` |
| POST /v1/teams/{id}/members | TA/TC（manager）；`base_tag=admin` 仅 TC | TM/S | team/user 不存在 | auth→valid | Python `TeamMemberService.add`（待确认 T-1） |
| PATCH /v1/teams/{id}/members/{m} | 本人改自己 / manager 按 `CanManage`（admin 互改、creator 改 creator 均拒绝） | 越权修改 | member 不存在 | auth→valid | `role.py` level 规则移植 |
| DELETE /v1/teams/{id}/members/{m} | 同 PATCH | 越权移除 | member 不存在 | auth→valid | 同上 |
| GET/PATCH /v1/teams/{id}/identity-tag-policy | TA/TC（manager）；改 system team 标签仅 TC | TM/S | — | auth→valid | `identity.py:286` is_team_manager |
| PATCH /v1/teams/{id}/members/{m}/aliases（本人/他人两路） | 本人改自己 / manager 改他人 | 越权 | member 不存在 | — | 待确认 T-2（`MeAliases/UserAliases` 语义） |
| PATCH /v1/teams/{id}/members/{m}/default-display-name | 同 aliases | 越权 | — | — | 同 T-2 |

备注：POST /v1/teams 的 C# `CreateAsync:117` 有 `actor.Flag("b") → Forbidden` 分支，
但 endpoint 先经 `RequireAsync`，banned 已在入口得 `400/1006`（与 Python `token_required` 一致），
该分支不可达。见缺口表 G-1（已关闭）。

## 3. 项目（Projects）

| 端点 | 允许 | 403 | 404 | Oracle |
|---|---|---|---|---|
| GET /v1/projects/{id} | PC/PT/PP/PY + TM/TA/TC（team:ACCESS 兜底） | S/SA | project 不存在 | `project.py:56` project.ACCESS 或 team.ACCESS |
| PUT /v1/projects/{id} | PC（project:CHANGE）；`position_locked` 仅 TC | PT/PP/PY/TM/S | project 不存在 | `project.py:95` + `:106` creator-only |
| GET /v1/projects/{id}/targets | project:ACCESS 任一来源 | S | project 不存在 | `project.py:148` |
| POST /v1/projects/{id}/targets | ADD_TARGET（PC/PA；team creator/admin 继承） | PT/PP/PY/TM/S | project/language 不存在 | `project.py:172` |
| DELETE /v1/targets/{id} | DELETE_TARGET | 无权限者 | target 不存在 | 待确认 P-1（Python 侧删除目标权限位） |
| GET /v1/teams/{id}/projects | TM/TA/TC | S | team 不存在 | `team.py` project-list（ACCESS） |
| POST /v1/teams/{id}/projects | CREATE_PROJECT（TA/TC） | TM/S | team 不存在 | `team.py:472` |
| POST /v1/teams/{id}/import（分包导入） | CREATE_PROJECT（TA/TC） | TM/S | team 不存在 | `team.py:518` |
| GET /v1/teams/{id}/project-sets | TM/TA/TC | S | team 不存在 | `team.py:582` |
| POST /v1/teams/{id}/project-sets | CREATE_PROJECT_SET（TA/TC） | TM/S | team 不存在 | `team.py:615` |
| PUT /v1/project-sets/{id} | CHANGE_PROJECT_SET（TA/TC）；默认集拒绝 | TM/S | set 不存在 | 待确认 P-2 |
| DELETE /v1/project-sets/{id} | DELETE_PROJECT_SET（TA/TC）；默认集/— | TM/S | set 不存在 | 待确认 P-2 |
| POST /v1/teams/{id}/outputs | AUTO_BECOME_PROJECT_ADMIN（TC/TA） | TM/S/PT | team 不存在 | `team.py:502` 精确一致 |
| GET /v1/teams/{id}/insight/users…（4 条） | INSIGHT（TC/TA） | TM/S | — | `team.py:658…` |
| POST /v1/projects/{id}/presence/{heartbeat,leave} GET /v1/projects/{id}/presence GET /v1/teams/{id}/projects/active-presence | **任意登录用户**（仅验票 + 资源存在，**无成员检查**） | — | project/team 不存在 | `presence.py` 同语义（安全复审项，不视为通过） |

## 4. 项目成员与生命周期

| 端点 | 允许 | 403 | Oracle |
|---|---|---|---|
| GET /v1/projects/{id}/members | manager（MANAGE_MEMBERS/TC/TA）或 ACCESS+在职 | S；ACCESS 但非在职 | C# `ListMembersAsync` 用 `Has(ACCESS)`；Python `require_project_access` 同义 |
| POST /v1/projects/{id}/members/changes | manager（IsManager） | 非 manager（含普通 PT） | `project_member.py:1474 apply_changes` 服务内鉴权 |
| POST /v1/projects/{id}/members/{m}/bind | manager | 非 manager | `:702 bind` |
| POST /v1/projects/{id}/members/{m}/merge | manager | 非 manager | `:935 merge` |
| DELETE /v1/projects/{id}/members/{m}（hard-delete external） | manager（待确认 P-3：是否仅外部成员） | 非 manager | `:859 hard_delete_external` |
| POST /v1/projects/{id}/owner-transfer | PC（is_owner）/ MANAGE_MEMBERS / TC | PT/PP/TM/S | `project_lifecycle.py:330` 三条件精确一致 |
| POST /v1/projects/{id}/complete | COMPLETE_PROJECT（PC/PA） | PT/PP/PY/TM/S | `can_project_action` complete |
| POST /v1/projects/{id}/clear | **仅 TC**（team base_tag=creator） | PC（非 TC）/TA/TM/S | `can_project_action` clear 精确一致 |
| POST /v1/projects/{id}/reopen | COMPLETE_PROJECT | 无权限者 | `can_project_action` reopen |

## 5. 文件（Files）

| 端点 | 允许 | 403 | Oracle |
|---|---|---|---|
| GET /v1/projects/{id}/files | project:ACCESS 任一来源（含 team 兜底）；finished(`st!=0`) 拒绝 normal-only 但 ACCESS 仍可列 | S | `file.py:95` 双 ACCESS |
| POST /v1/projects/{id}/files（上传） | ADD_FILE（PT/PP/PY/PC + team creator/admin 继承） | 无权限者 | `file.py` add（待确认 F-1：Python 是否含 team 兜底） |
| GET /v1/files/{id} | ACCESS（team 兜底）；`st!=0` 时 normal-only 持有者也被 `EnsureWorking` 拒绝 | S | C# `GetFileAsync`；Python `file.py` get 仅 project.ACCESS（**差异？见 G-2**） |
| PUT /v1/files/{id} | 按字段 MOVE_FILE / RENAME_FILE | 无权限者 | `file.py` update |
| DELETE /v1/files/{id} | DELETE_FILE | 无权限者 | `file.py` delete |
| GET /v1/projects/{id}/files/order | CanOrder（ACCESS + 来源为 team creator/admin 或 project creator/admin） | 普通 PT/PP/S | `file_order.py can_order_files` 精确一致 |
| PUT /v1/projects/{id}/files/order | 同上 | 同上 | 同上；包体校验先于鉴权（空 `file_ids` → 400/2） |
| GET /v1/admin/files | SA | 非 SA（含 TC/PC） | `admin_required` |

## 6. 标注（Label：sources / translations / tips / snapshots）

| 端点 | 允许 | 403 | Oracle |
|---|---|---|---|
| GET /v1/files/{id}/sources | ACCESS | S | `source.py` list |
| POST /v1/files/{id}/sources | ADD_LABEL（PT/PY/PP/PC；team creator/admin 继承） | 无权限者 | `source.py` create |
| PUT /v1/sources/{id}/move | MOVE_LABEL | 无权限者 | `source.py` move |
| DELETE /v1/sources/{id} | DELETE_LABEL | 无权限者 | `source.py` delete |
| POST /v1/sources/{id}/translations | ADD_TRA（PT/PP/PC） | PY（无 ADD_TRA）/S | `translation.py:34` |
| PUT /v1/translations/{id} | `selected`→CHECK_TRA；`content`→仅作者本人；`proofread_content`→PROOFREAD_TRA | 他人 content / 无 CHECK_TRA 选定 / 无 PROOFREAD_TRA 校对 | `translation.py:75…99` 精确一致 |
| DELETE /v1/translations/{id} | 作者本人 或 DELETE_TRA | 非作者且无 DELETE_TRA | `translation.py:138` |
| POST /v1/sources/{id}/tips | 作者本人（owner）+ PROOFREAD_TRA | 非作者 / 无 PROOFREAD_TRA | `tip.py`（待确认 L-1） |
| GET /v1/projects/{id}/label-state | ACCESS | S | — |
| GET /v1/projects/{id}/snapshots | manager/proofreader/output 任一（ capabilities() ） | PT/PY/S | `label_snapshot.py capabilities()` 精确一致 |
| POST /v1/projects/{id}/snapshots | 同上（create） | 同上 | 同上 |
| POST /v1/projects/{id}/snapshots/{id}/restore | 仅 manager | PP（能建不能恢）/S | 同上 |
| POST /v1/projects/{id}/label-clear | manager/proofreader | PT/S | 同上 |
| GET/POST /v1/projects/{id}/label-merge | GET: manager/proofreader/output；POST: manager 或 (proofreader&check&add) | PT/S | 同上 |
| POST /v1/projects/{id}/label-recover | 仅 manager | PP/PT/S | 同上（L-2 已关闭：`label_snapshot.py:180` 按 `restore` 门） |

顺序：label 写入在 `label_revision>0` 时须带 `X-Label-Revision`，revision 守卫先于
capabilities 门（Python `label_lock.py:345-365`，两端一致；revision 为 0 或已带正确
版本号时守卫通过，无权限者仍得 400/103）。

## 7. 输出（Outputs）

| 端点 | 允许 | 403 | Oracle |
|---|---|---|---|
| POST /v1/projects/{id}/outputs | OUTPUT_TRA（PT/PY/PP/PC；team creator/admin 继承） | 无权限者 | `project.py:183` |
| GET /v1/projects/{id}/targets/{t}/outputs | OUTPUT_TRA | 无权限者；target 跨项目 → 404/4024 | `project.py:232` |
| POST /v1/projects/{id}/targets/{t}/outputs | OUTPUT_TRA；`type` 非法 → 400 | 同上 | `project.py:257` |
| GET /v1/projects/{id}/labelplus | OUTPUT_TRA | 无权限者 | `project.py:310` |
| POST /v1/teams/{id}/outputs | 仅 TC/TA（AUTO_BECOME_PROJECT_ADMIN） | TM/PT/S | `team.py:502` |

## 8. 邀请（Invitations）

组邀请 `INVITE_USER`（project scope：PC/PA/PP？——PP 默认无 INVITE_USER，
实际仅 PC/PA；team scope：TA/TC）。层级规则：`selfLevel > roleLevel` 才能授角色。

| 端点 | 允许 | 403 | Oracle |
|---|---|---|---|
| GET /v1/{project,team}/{id}/invitations | CanInvite | 无 INVITE_USER 者 | `invitation.py` list |
| POST /v1/{project,team}/{id}/invitations | CanInvite + 层级 | 无权限/层级不足 | `invitation.py` post |
| PUT /v1/invitations/{id} | CanInvite（同组） + 层级 | 受邀人本人（非 manager）/ 跨组 | 同上 |
| PATCH /v1/invitations/{id}（accept/reject） | 仅受邀人本人 | manager 代点 / 已终态 → 5010 | `invitation.py` patch |
| DELETE /v1/invitations/{id} | CanInvite 或受邀人（待确认 I-1） | 第三方 | 同上 |
| GET /v1/user/invitations | 任意登录用户（仅自己） | — | me-list |

## 9. 申请（Applications）

| 端点 | 允许 | 403 | Oracle |
|---|---|---|---|
| GET /v1/{project,team}/{id}/applications | CHECK_USER（PC/PA/TA/TC） | 无 CHECK_USER 者 | `application.py` list |
| POST /v1/{project,team}/{id}/applications | 组开放（project: allow=2 任意人 / =3 需团队成员；team: 仅 allow=2） | 组未开放；已是成员 | `ApplicationService.ApplyAsync:188` |
| PATCH /v1/applications/{id}（allow/reject） | CHECK_USER | 申请人自批 / 无权限者 | patch |
| DELETE /v1/applications/{id} | 仅申请人本人 | 他人（含 manager？待确认 A-1） | delete |
| GET /v1/user/applications/related, public-info | 任意登录用户 | — | related/public |

## 10. 管理员（Admin）

全部 `RequireAdminAsync`（Python `admin_required`）。非 SA → 403（400/103）；
banned SA → C# 400/1006（Python 不查 banned，见 §0）。

| 端点 | SA | 非 SA |
|---|---|---|
| PUT /v1/admin/admin-status（需 `ADMIN_EMAIL` 配置的 owner，banned owner 拒绝） | 200（仅配置 owner）/ 其他 admin 403 | 403 |
| GET /v1/admin/users；PUT /v1/admin/users/{id}（改密会撤销其 token） | 200 | 403 |
| POST /v1/admin/users（代建普通用户） | 200 | 403 |
| GET /v1/admin/v-codes | 200 | 403 |
| PUT /v1/admin/storage/default（存储池切换） | 200 | 403 |
| GET /v1/storage-pools/*（sources/targets/files） | 200 | 403 |
| GET /v1/admin/languages？（待确认 M-1） | — | — |

## 11. 可选模块

| 端点 | 允许 | 403 | Oracle |
|---|---|---|---|
| POST /v1/projects/{id}/ocr | USE_OCR_QUOTA（TC/TA） | TM/PT/S | `project.py:337` |
| POST /v1/projects/{id}/thumbnail | project:CHANGE（PC） | PT/S | `project.py:375` |
| POST /v1/projects/{id}/targets/{t}/send-proofread-draft | PROOFREAD_TRA（PP/PC） | PT/S | `project.py:459` |
| POST /v1/projects/{id}/import-task（archive） | ADD_FILE | 无权限者 | archive import（待确认 M-2） |
| GET /v1/projects/{id}/import-task | ACCESS | S | 同上 |
| term-banks GET / POST /v1/teams/{id}/term-banks | ACCESS_TERM_BANK / CREATE_TERM_BANK（TC/TA） | TM/S | `term.py` |
| PUT/DELETE /v1/term-banks/{id} | CHANGE/DELETE_TERM_BANK（TC；admin？TA 有 CHANGE 无 DELETE…见备注） | 无权限者 | `term.py` |
| GET /v1/term-banks/{id}/terms；POST terms | ACCESS_TERM_BANK / CREATE_TERM（TC/TA） | TM/S | `term.py` |
| PUT/DELETE /v1/terms/{id} | CHANGE/DELETE_TERM（TC/TA） | TM/S | `term.py` |

备注：team `admin` 默认拥有 `CHANGE_TERM_BANK` 等全部非 DELETE 权限，可改 bank 但不可删 bank；
team `member` 只有 ACCESS，连 term-bank 列表都不可见。

## 12. 已发现缺口（Gaps）

| 编号 | 位置 | 现象 | 判定 |
|---|---|---|---|
| G-1 | `TeamService.CreateAsync` (`TeamService.cs:117`) `if (actor.Flag("b")) throw Forbidden()` | **经核查不构成缺口**：`POST /v1/teams` 先经 `RequireAsync`，banned 已在 endpoint 层得 `400/1006`，该分支不可达（纵深防御死代码）。Python `token_required` 同样先查 banned。行为一致，无需修复。 | 已关闭 |
| G-2 | `FileService.GetFileAsync` team 兜底 | **经核查不构成缺口**：Python `file.py` get 只查 `can(project, ACCESS)`，而 `can_project` 即快照 `has("project:ACCESS")`，快照的 `team_access` 分支给一切在职团队成员 ACCESS——与 C# `RequireProjectAccessAsync` 的 team 兜底等价。行为一致，无需修复。 | 已关闭 |

## 13. 待确认（Open questions — 不断言）

- T-1：`POST /v1/teams/{id}/members` 的 Python 层级校验细节（`TeamMemberService.add` vs C# `AddAsync` 的 admin-仅-creator）。
- T-2：aliases / default-display-name 两路 PATCH 的 Python 权限语义。
- P-1：`DELETE /v1/targets/{id}` 的 Python 权限位。
- P-2：project-set PUT/DELETE 的 Python 语义（默认集保护、权限位）。
- P-3：hard-delete 是否仅限外部成员（Python `:859` 条件）。
- F-1：已关闭（见 G-2）：快照 `team_access` 使 team 兜底两端等价。
- L-1：tips 创建的 owner 判定细节（`tip.py`）。
- L-2：已关闭：label-recover 按 `restore`（仅 manager）门（`label_snapshot.py:176-182`），
  `AuthzTailGateTests.LabelClear_ProofreadVsFullGates_RecoverRequiresManager` 按此断言。
- I-1：邀请 DELETE 是否允许受邀人自删。
- A-1：申请 DELETE 是否允许 manager 删除。
- M-1：`GET /v1/admin/languages`（或语言目录管理路由）是否存在及权限。
- M-2：archive import 两条路由的 Python 权限位。
- M-3：`GET /v1/user/teams` 等列表路由在 Python 是否有隐藏检查（目前判定为无）。

## 14. 测试覆盖映射

| 测试类 | 覆盖领域 | 测试方法 |
|---|---|---|
| `AuthzMatrixTests.cs` | world 夹具 + §1 + §2（团队 CRUD/成员/策略） | `AuthPrimitives_*`, `Teams_CreateGate_*`, `Teams_Create_*`, `Teams_Update_*`, `Teams_Delete_*`, `Teams_MembersList_*`, `Teams_MembersAdd_*`, `Teams_MemberUpdateRemove_*`, `Teams_TagPolicy_*` |
| `AuthzReadGateTests.cs` | §7 输出读取、§8/§9 列表 | `ProjectInvitations_RequireInviteUser`, `ProjectApplications_RequireCheckUser`, `TargetOutputs_RequireOutputPermission`, `BannedIdentity_IsRejectedBeforePermissionChecks` |
| `AuthzWriteGateTests.cs` | §4 批量成员变更/转让/完结、§7 团队导出 | `ProjectMemberChanges_RequireManagerForMutations`, `ProjectOwnerTransfer_RequiresOwnerOrManager`, `ProjectComplete_RequiresCompletePermission`, `TeamOutputs_RequireTeamManagement`, `BannedIdentity_WritesAreRejectedBeforeValidation` |
| `AuthzMemberFileGateTests.cs` | §4 bind/merge/hard-delete；§5 文件改名/移动/删除；§7 输出创建 | `ProjectMemberBind_RequiresManager`, `ProjectMemberMerge_RequiresManager`, `ProjectMemberHardDelete_RequiresOwnerOrTeamCreator`, `FileRenameMoveDelete_RequireFilePermissions`, `ProjectOutputsPost_RequiresOutputPermission` |
| `AuthzJoinAdminGateTests.cs` | §8 邀请、§9 申请、§11 terms、§10 管理员 | `ProjectInvitations_MutationGates`, `TeamInvitations_ListAndCreateGates`, `ProjectApplications_FlowGates`, `ProjectApplications_DeleteOnlyByApplicant`, `TeamApplications_ListRequiresCheckUser`, `Terms_MutationGates`, `AdminEndpoints_RequireSiteAdmin` |
| `AuthzTailGateTests.cs` | §8 PUT 邀请角色层级、§6 快照恢复/合并/clear/recover 门、§10 admin-status 配置 owner 门 | `InvitationsPut_TeamRoleHierarchy_OwnerRaises_AdminCannotRaisePeer_TranslatorDenied`, `InvitationsPut_ProjectTagsRequireInviteUser`, `Snapshots_RestoreRequiresManager_MergeRequiresProofreadTriple`, `LabelClear_ProofreadVsFullGates_RecoverRequiresManager`, `AdminStatus_OnlyConfiguredOwnerMayGrant_BannedOwnerDenied` |
| `AuthzCapabilityMatrixTests.cs` | §6 三动作 × 七角色完整矩阵（owner / project-admin / proofreader / translator / teamMember / stranger / banned，另加 anon / siteAdmin 参照行） | `LabelClear_FullCapabilityMatrix`, `LabelMerge_FullCapabilityMatrix`, `LabelRecover_FullCapabilityMatrix` |
| `AuthzConcurrencyTests.cs` | §4 成员信封并发窗口（`Task.WhenAll` 双打） | `ConcurrentMemberUpdate_OneWinner_NoLostUpdateOrDuplicates`, `ConcurrentAddSameStranger_OnlyOneWins_LoserGetsBusinessError` |

## 15. 已知差异（Known differences — C# 现行行为，测试按此断言）

- D-1（已记录，见 §0）：`GET /v1/admin/*`、`storage-pool` 等 `RequireAdminAsync`
  路由上，被封禁的 siteAdmin 在 C# 得 `400/1006`（`RequireAsync` 先于
  admin 检查），而 Python `admin_required` 不查 banned（`admin_can()` 只看
  `admin is True`）。C# 更严格，视为修复而非缺口；`AdminEndpoints_RequireSiteAdmin`
  按 C# 行为断言。
- D-2（文案）：`POST /v1/projects/{id}/applications` 在需审核组（`application_check_type=2`）
  的成功文案为「申请成功，请等待管理员审核」（与 Python `user.py:701` 一致）；
  `ProjectApplications_FlowGates` 按此断言。
- D-3（校验）：项目邀请 `tags` 分支要求目标用户具备相应团队工作人员资格
  （`TeamQualificationRequiredError/5102`，见 `identity_permission.py:499…615`）；
  无资格目标的邀请创建两端一致拒绝。`ProjectInvitations_MutationGates` 使用
  `admin` 标签（管理标签，不受资格门限制，见 `InvitationTests` 同例）。
- 本轮扩展未发现新的越权放行或错误码形状差异：bind/merge 的
  `RequireOperator → IsManager` 顺序、hard-delete 的 owner-or-team-creator 门、
  文件 `MOVE/RENAME/DELETE_FILE` 分字段门、输出 `OUTPUT_TRA` 先于 `type`
  校验、邀请/申请的 `INVITE_USER/CHECK_USER`/仅本人门、terms 的
  `ACCESS/CHANGE/DELETE_TERM*` 门，均与 Python oracle 一致，无需 `TODO(parity)`。
- D-4（并发观察，非缺陷）：`ConcurrentMemberUpdate` 双打下恰好一个 200、
  另一个 409/5108（`ProjectMemberVersionConflictError`），终态单行、版本号 +1、
  无合并写入；`ConcurrentAddSameStranger` 双打下恰好一个 200、另一个
  409/5106（`MemberAlreadyExistsError`），终态单行 active。进程内
  `InvitationMutationGate` 把同项目 add 串行化（loser 走重读后 `AlreadyExists`）；
  唯一索引 `(project, user)` / `ik` 是跨进程 backstop。若多进程真并发同时穿过
  `existing is null` 检查，loser 的 `InsertOneAsync` 抛 `MongoWriteException`
  （DuplicateKey）会被 `ApplyChangesAsync` 的通用 `catch (Exception)` 以
  400/`INVALID_REQUEST`（`error_code` 为 null）吞掉，而非 409/5106——单后端测试
  环境无法触发，未修（修了也验证不了）；多写进程部署前值得加一个
  DuplicateKey → AlreadyExists 的最小映射。
- D-5（用例修正记录，非服务端缺陷）：初版 `ConcurrentMemberUpdate` 用 tags
  双打， bilateral 422/5102（`TeamQualificationRequiredError`）——目标用户无团队
  工作人员资格时，manager 指派 worker tag 同样拒绝（Python
  `identity_permission.py:599-617`，两端一致）。改测 display_name 后 CAS 窗口
  行为符合预期。
