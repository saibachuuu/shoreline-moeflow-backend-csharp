# Legacy route vs C# route coverage

Generated: `2026-11-09T12:59:05.576169+00:00` (UTC) by `Audit-LegacyRoutes.py`.

This is an **offline static audit**. It does not import or run the legacy Python app, does not run `dotnet build`, and does not read deployment configuration values. It reports route registration presence only, not business compatibility.

## Inputs and normalization

- Legacy directory: `C:\Users\88888888\shoreline-moeflow\shoreline-moeflow-backend`
- C# directory: `C:\Users\88888888\shoreline-moeflow\shoreline-moeflow-backend-csharp`
- Placeholder normalization: Flask <name>/<converter:name> and C# {name}/{name:constraint}/{*name} are collapsed to one '{}' token; path structure is unchanged.
- OPTIONS is recorded but excluded from business coverage.

## Summary

| Metric | Count |
| --- | ---: |
| Legacy method entries (incl. OPTIONS) | 325 |
| Legacy business registrations (non-OPTIONS) | 194 |
| Core business registrations | 162 |
| Core effective methods (handler exists) | 160 |
| Core effective methods present in C# | 73 |
| Core effective methods missing in C# | 87 |
| Core registered-only methods (405) | 2 |
| Core methods with unresolved handler status | 0 |
| Conditional (MIT) effective methods | 5 |
| Conditional (MIT) present in C# | 0 |
| Optional-module effective methods | 27 |
| Optional-module methods present in C# | 0 |
| Commented-out method entries | 6 |
| C# route method entries | 76 |
| C# entries not in legacy | 3 |
| Legacy unresolved warnings | 0 |
| C# unresolved warnings | 0 |

> `core_effective_methods` intentionally excludes OPTIONS and methods that Flask registered without a real view handler. Those are reported below as registered-only/405 and are **not** counted as missing business.

## Registered-only routes (runtime 405)

| Method | Path | Handler | Source |
| --- | --- | --- | --- |
| PUT | `/v1/applications/<application_id>` | `ApplicationAPI` | `app/apis/urls.py:558` |
| GET | `/v1/teams/<team_id>/project-sets/<project_set_id>/project-zips` | `TeamProjectImportAPI` | `app/apis/urls.py:337` |

## Missing by module (core effective routes)

| Module | Missing | Methods and paths |
| --- | ---: | --- |
| admin | 4 | `GET /v1/admin/files` (AdminFileListAPI @ app/apis/urls.py:596)<br>`GET /v1/admin/storage/default` (AdminStorageDefaultAPI @ app/apis/urls.py:626)<br>`POST /v1/admin/users` (AdminUserListAPI @ app/apis/urls.py:606)<br>`GET /v1/admin/v-codes` (AdminVCodeListAPI @ app/apis/urls.py:621) |
| avatar | 1 | `PUT /v1/avatar` (AvatarAPI @ app/apis/urls.py:588) |
| file | 8 | `DELETE /v1/files/<file_id>` (FileAPI @ app/apis/urls.py:480)<br>`GET /v1/files/<file_id>` (FileAPI @ app/apis/urls.py:480)<br>`PUT /v1/files/<file_id>` (FileAPI @ app/apis/urls.py:480)<br>`POST /v1/files/<file_id>/ocr` (FileOCRAPI @ app/apis/urls.py:490)<br>`GET /v1/files/<file_id>/sources` (FileSourceListAPI @ app/apis/urls.py:485)<br>`PATCH /v1/files/<file_id>/sources` (FileSourceListAPI @ app/apis/urls.py:485)<br>`POST /v1/files/<file_id>/sources` (FileSourceListAPI @ app/apis/urls.py:485)<br>`POST /v1/files/<file_id>/thumbnail` (FileThumbnailAPI @ app/apis/urls.py:495) |
| index | 4 | `GET /docs/<path:path>` (DocsAPI @ app/apis/urls.py:121)<br>`GET /error` (ErrorAPI @ app/apis/urls.py:134)<br>`GET /urls` (UrlListAPI @ app/apis/urls.py:126)<br>`GET /warning` (WarningAPI @ app/apis/urls.py:129) |
| me | 1 | `GET /v1/user/projects` (MeProjectListAPI @ app/apis/urls.py:197) |
| notification | 26 | `GET /v1/admin/notification-recipients` (CandidatesAPI @ app/apis/notification.py:716)<br>`GET /v1/admin/notifications` (ManageListAPI @ app/apis/notification.py:707)<br>`POST /v1/admin/notifications` (PublishAPI @ app/apis/notification.py:708)<br>`POST /v1/admin/notifications/preview` (PreviewAPI @ app/apis/notification.py:709)<br>`GET /v1/admin/notifications/<notification_id>` (ManageDetailAPI @ app/apis/notification.py:723)<br>`POST /v1/admin/notifications/<notification_id>/revoke` (RevokeAPI @ app/apis/notification.py:730)<br>`GET /v1/me/notification-capabilities` (NotificationCapabilitiesAPI @ app/apis/notification.py:663)<br>`GET /v1/me/notification-preferences` (PreferenceAPI @ app/apis/notification.py:678)<br>`PUT /v1/me/notification-preferences` (PreferenceAPI @ app/apis/notification.py:678)<br>`GET /v1/me/notification-sync` (NotificationSyncAPI @ app/apis/notification.py:740)<br>`GET /v1/me/notifications` (InboxAPI @ app/apis/notification.py:669)<br>`POST /v1/me/notifications/mark-read` (MarkReadAPI @ app/apis/notification.py:671)<br>`GET /v1/me/notifications/unread-counts` (UnreadAPI @ app/apis/notification.py:670)<br>`GET /v1/me/notifications/<notification_id>` (ReceiptAPI @ app/apis/notification.py:672)<br>`PATCH /v1/me/notifications/<notification_id>` (ReceiptAPI @ app/apis/notification.py:672)<br>`GET /v1/notification-project-cards` (CardSearchAPI @ app/apis/notification.py:738)<br>`POST /v1/notification-project-cards/resolve` (CardAPI @ app/apis/notification.py:706)<br>`GET /v1/notification-recipients` (CandidatesAPI @ app/apis/notification.py:692)<br>`POST /v1/notifications` (PublishAPI @ app/apis/notification.py:690)<br>`POST /v1/notifications/preview` (PreviewAPI @ app/apis/notification.py:691)<br>`GET /v1/notifications/sent` (ManageListAPI @ app/apis/notification.py:693)<br>`GET /v1/notifications/<notification_id>` (ManageDetailAPI @ app/apis/notification.py:694)<br>`PATCH /v1/notifications/<notification_id>` (ManageDetailAPI @ app/apis/notification.py:694)<br>`POST /v1/notifications/<notification_id>/revoke` (RevokeAPI @ app/apis/notification.py:700)<br>`GET /v1/teams/<team_id>/notification-policy` (PolicyAPI @ app/apis/notification.py:684)<br>`PUT /v1/teams/<team_id>/notification-policy` (PolicyAPI @ app/apis/notification.py:684) |
| project | 18 | `GET /v1/projects/<project_id>/files` (ProjectFileListAPI @ app/apis/urls.py:433)<br>`POST /v1/projects/<project_id>/files` (ProjectFileListAPI @ app/apis/urls.py:433)<br>`GET /v1/projects/<project_id>/files/order` (ProjectFileOrderAPI @ app/apis/urls.py:428)<br>`PUT /v1/projects/<project_id>/files/order` (ProjectFileOrderAPI @ app/apis/urls.py:428)<br>`POST /v1/projects/<project_id>/label-clear` (ProjectLabelClearAPI @ app/apis/urls.py:412)<br>`GET /v1/projects/<project_id>/label-merge` (ProjectLabelMergeAPI @ app/apis/urls.py:412)<br>`POST /v1/projects/<project_id>/label-merge` (ProjectLabelMergeAPI @ app/apis/urls.py:412)<br>`POST /v1/projects/<project_id>/label-recover` (ProjectLabelRecoverAPI @ app/apis/urls.py:412)<br>`GET /v1/projects/<project_id>/label-state` (ProjectLabelStateAPI @ app/apis/urls.py:412)<br>`POST /v1/projects/<project_id>/ocr` (ProjectOCRAPI @ app/apis/urls.py:458)<br>`POST /v1/projects/<project_id>/outputs` (ProjectOutputListAPI @ app/apis/urls.py:443)<br>`GET /v1/projects/<project_id>/snapshots` (ProjectSnapshotListAPI @ app/apis/urls.py:412)<br>`POST /v1/projects/<project_id>/snapshots` (ProjectSnapshotListAPI @ app/apis/urls.py:412)<br>`POST /v1/projects/<project_id>/snapshots/<snapshot_id>/restore` (ProjectSnapshotRestoreAPI @ app/apis/urls.py:412)<br>`GET /v1/projects/<project_id>/targets/<target_id>/outputs` (ProjectTargetOutputListAPI @ app/apis/urls.py:448)<br>`POST /v1/projects/<project_id>/targets/<target_id>/outputs` (ProjectTargetOutputListAPI @ app/apis/urls.py:448)<br>`POST /v1/projects/<project_id>/targets/<target_id>/send-proofread-draft` (ProjectSendProofreadDraftAPI @ app/apis/urls.py:453)<br>`POST /v1/projects/<project_id>/thumbnails` (ProjectThumbnailAPI @ app/apis/urls.py:423) |
| site | 1 | `GET /v1/site/homepage` (HomepageAPI @ app/apis/urls.py:139) |
| source | 5 | `DELETE /v1/sources/<source_id>` (SourceAPI @ app/apis/urls.py:502)<br>`PUT /v1/sources/<source_id>` (SourceAPI @ app/apis/urls.py:502)<br>`PUT /v1/sources/<source_id>/rank` (SourceRankAPI @ app/apis/urls.py:507)<br>`POST /v1/sources/<source_id>/tips` (SourceTipListAPI @ app/apis/urls.py:517)<br>`POST /v1/sources/<source_id>/translations` (SourceTranslationListAPI @ app/apis/urls.py:512) |
| team | 9 | `DELETE /v1/teams/<team_id>` (TeamAPI @ app/apis/urls.py:321)<br>`GET /v1/teams/<team_id>/insight/projects` (TeamInsightProjectListAPI @ app/apis/urls.py:357)<br>`GET /v1/teams/<team_id>/insight/projects/<project_id>/users` (TeamInsightProjectUserListAPI @ app/apis/urls.py:372)<br>`GET /v1/teams/<team_id>/insight/users` (TeamInsightUserListAPI @ app/apis/urls.py:352)<br>`GET /v1/teams/<team_id>/insight/users/<user_id>/projects` (TeamInsightUserProjectListAPI @ app/apis/urls.py:367)<br>`POST /v1/teams/<team_id>/outputs` (TeamProjectOutputListAPI @ app/apis/urls.py:332)<br>`POST /v1/teams/<team_id>/project-sets/<project_set_id>/project-zips` (TeamProjectImportAPI @ app/apis/urls.py:337)<br>`GET /v1/teams/<team_id>/term-banks` (TermBankAPI @ app/apis/urls.py:347)<br>`POST /v1/teams/<team_id>/term-banks` (TermBankAPI @ app/apis/urls.py:347) |
| term | 2 | `DELETE /v1/terms/<term_id>` (TermAPI @ app/apis/urls.py:397)<br>`PUT /v1/terms/<term_id>` (TermAPI @ app/apis/urls.py:397) |
| term_bank | 4 | `DELETE /v1/term-banks/<term_bank_id>` (TermBankAPI @ app/apis/urls.py:386)<br>`PUT /v1/term-banks/<term_bank_id>` (TermBankAPI @ app/apis/urls.py:386)<br>`GET /v1/term-banks/<term_bank_id>/terms` (TermListAPI @ app/apis/urls.py:391)<br>`POST /v1/term-banks/<term_bank_id>/terms` (TermListAPI @ app/apis/urls.py:391) |
| tip | 2 | `DELETE /v1/tips/<tip_id>` (TipAPI @ app/apis/urls.py:531)<br>`PUT /v1/tips/<tip_id>` (TipAPI @ app/apis/urls.py:531) |
| translation | 2 | `DELETE /v1/translations/<translation_id>` (TranslationAPI @ app/apis/urls.py:524)<br>`PUT /v1/translations/<translation_id>` (TranslationAPI @ app/apis/urls.py:524) |

## Optional modules and enable conditions

| Module | Enable condition | Routes | Effective | In C# | Missing |
| --- | --- | ---: | ---: | ---: | ---: |
| archive_import | optional module: directory app/modules/archive_import exists and __init__.py exports MODULE (factory.discover()); no environment-variable switch | 3 | 3 | 0 | 3 |
| partner_search | optional module: directory app/modules/partner_search exists and __init__.py exports MODULE (factory.discover()); no environment-variable switch | 3 | 3 | 0 | 3 |
| storage_pool | optional module: directory app/modules/storage_pool exists and __init__.py exports MODULE (factory.discover()); no environment-variable switch | 18 | 18 | 0 | 18 |
| ziteng_partner | optional module: directory app/modules/ziteng_partner exists and __init__.py exports MODULE (factory.discover()); no environment-variable switch | 3 | 3 | 0 | 3 |

### Optional-module missing routes

- **archive_import**: `POST /v1/projects/<project_id>/import-from-archive`, `GET /v1/projects/<project_id>/import-task`, `POST /v1/projects/<project_id>/import-task/dismiss`
- **partner_search**: `POST /v1/partner-search-query-entry`, `GET /v1/partner-search-query-entry/settings`, `PUT /v1/partner-search-query-entry/settings`
- **storage_pool**: `POST /v1/admin/storage-pool/projects/<project_id>/lock`, `POST /v1/admin/storage-pool/projects/<project_id>/migrate`, `POST /v1/admin/storage-pool/projects/<project_id>/move`, `POST /v1/admin/storage-pool/projects/<project_id>/restore`, `POST /v1/admin/storage-pool/scan`, `GET /v1/admin/storage-pool/settings`, `PUT /v1/admin/storage-pool/settings`, `GET /v1/admin/storage-pool/sources`, `POST /v1/admin/storage-pool/sources`, `DELETE /v1/admin/storage-pool/sources/<source_id>`, `PUT /v1/admin/storage-pool/sources/<source_id>`, `POST /v1/admin/storage-pool/sources/<source_id>/test`, `GET /v1/admin/storage-pool/stats`, `GET /v1/admin/storage-pool/transfers`, `DELETE /v1/admin/storage-pool/transfers/<transfer_id>`, `POST /v1/admin/storage-pool/transfers/<transfer_id>/cancel`, `POST /v1/admin/storage-pool/transfers/<transfer_id>/retry`, `GET /v1/admin/storage-pool/usage`
- **ziteng_partner**: `GET /v1/projects/<project_id>/ziteng-check`, `POST /v1/projects/<project_id>/ziteng-check`, `GET /v1/ziteng-partner/config`

## Conditional MIT routes

| Method | Path | Handler | Condition | In C# |
| --- | --- | --- | --- | --- |
| OPTIONS | `/v1/mit/image-tasks` | `MitImageTaskApi` | `app_config['MIT_STORAGE_ROOT']` | no |
| POST | `/v1/mit/image-tasks` | `MitImageTaskApi` | `app_config['MIT_STORAGE_ROOT']` | no |
| GET | `/v1/mit/image-tasks/<task_id>` | `MitImageTaskApi` | `app_config['MIT_STORAGE_ROOT']` | no |
| OPTIONS | `/v1/mit/image-tasks/<task_id>` | `MitImageTaskApi` | `app_config['MIT_STORAGE_ROOT']` | no |
| OPTIONS | `/v1/mit/images` | `MitImageApi` | `app_config['MIT_STORAGE_ROOT']` | no |
| POST | `/v1/mit/images` | `MitImageApi` | `app_config['MIT_STORAGE_ROOT']` | no |
| OPTIONS | `/v1/mit/translate-tasks` | `MitTranslateTaskApi` | `app_config['MIT_STORAGE_ROOT']` | no |
| POST | `/v1/mit/translate-tasks` | `MitTranslateTaskApi` | `app_config['MIT_STORAGE_ROOT']` | no |
| GET | `/v1/mit/translate-tasks/<task_id>` | `MitTranslateTaskApi` | `app_config['MIT_STORAGE_ROOT']` | no |
| OPTIONS | `/v1/mit/translate-tasks/<task_id>` | `MitTranslateTaskApi` | `app_config['MIT_STORAGE_ROOT']` | no |

## Commented-out (disabled) routes

| Method | Path | Handler | Source |
| --- | --- | --- | --- |
| GET | `/<group_type>/<group_id>/roles` | `RoleListAPI` | `app/apis/urls.py:564` |
| OPTIONS | `/<group_type>/<group_id>/roles` | `RoleListAPI` | `app/apis/urls.py:564` |
| POST | `/<group_type>/<group_id>/roles` | `RoleListAPI` | `app/apis/urls.py:564` |
| DELETE | `/<group_type>/<group_id>/roles/<role_id>` | `RoleAPI` | `app/apis/urls.py:569` |
| OPTIONS | `/<group_type>/<group_id>/roles/<role_id>` | `RoleAPI` | `app/apis/urls.py:569` |
| PUT | `/<group_type>/<group_id>/roles/<role_id>` | `RoleAPI` | `app/apis/urls.py:569` |

## Unresolved

### Legacy Python

None.

### C#

None.

## Full legacy route inventory

| Category | Module | Method | Path | Handler | Handler status | C# | Source |
| --- | --- | --- | --- | --- | --- | --- | --- |
| conditional | manga-image-translator | OPTIONS | `/v1/mit/image-tasks` | `MitImageTaskApi` | options_not_business | - | `app/apis/urls.py:639` |
| conditional | manga-image-translator | POST | `/v1/mit/image-tasks` | `MitImageTaskApi` | handler | no | `app/apis/urls.py:639` |
| conditional | manga-image-translator | GET | `/v1/mit/image-tasks/<task_id>` | `MitImageTaskApi` | handler | no | `app/apis/urls.py:644` |
| conditional | manga-image-translator | OPTIONS | `/v1/mit/image-tasks/<task_id>` | `MitImageTaskApi` | options_not_business | - | `app/apis/urls.py:644` |
| conditional | manga-image-translator | OPTIONS | `/v1/mit/images` | `MitImageApi` | options_not_business | - | `app/apis/urls.py:634` |
| conditional | manga-image-translator | POST | `/v1/mit/images` | `MitImageApi` | handler | no | `app/apis/urls.py:634` |
| conditional | manga-image-translator | OPTIONS | `/v1/mit/translate-tasks` | `MitTranslateTaskApi` | options_not_business | - | `app/apis/urls.py:649` |
| conditional | manga-image-translator | POST | `/v1/mit/translate-tasks` | `MitTranslateTaskApi` | handler | no | `app/apis/urls.py:649` |
| conditional | manga-image-translator | GET | `/v1/mit/translate-tasks/<task_id>` | `MitTranslateTaskApi` | handler | no | `app/apis/urls.py:654` |
| conditional | manga-image-translator | OPTIONS | `/v1/mit/translate-tasks/<task_id>` | `MitTranslateTaskApi` | options_not_business | - | `app/apis/urls.py:654` |
| core | admin | OPTIONS | `/v1/admin/admin-status` | `AdminUserAdminStatusAPI` | options_not_business | - | `app/apis/urls.py:601` |
| core | admin | PUT | `/v1/admin/admin-status` | `AdminUserAdminStatusAPI` | handler | yes | `app/apis/urls.py:601` |
| core | admin | GET | `/v1/admin/files` | `AdminFileListAPI` | handler | no | `app/apis/urls.py:596` |
| core | admin | OPTIONS | `/v1/admin/files` | `AdminFileListAPI` | options_not_business | - | `app/apis/urls.py:596` |
| core | admin | GET | `/v1/admin/site-setting` | `SiteSettingAPI` | handler | yes | `app/apis/urls.py:611` |
| core | admin | OPTIONS | `/v1/admin/site-setting` | `SiteSettingAPI` | options_not_business | - | `app/apis/urls.py:611` |
| core | admin | PUT | `/v1/admin/site-setting` | `SiteSettingAPI` | handler | yes | `app/apis/urls.py:611` |
| core | admin | GET | `/v1/admin/storage/default` | `AdminStorageDefaultAPI` | handler | no | `app/apis/urls.py:626` |
| core | admin | OPTIONS | `/v1/admin/storage/default` | `AdminStorageDefaultAPI` | options_not_business | - | `app/apis/urls.py:626` |
| core | admin | GET | `/v1/admin/users` | `AdminUserListAPI` | handler | yes | `app/apis/urls.py:606` |
| core | admin | OPTIONS | `/v1/admin/users` | `AdminUserListAPI` | options_not_business | - | `app/apis/urls.py:606` |
| core | admin | POST | `/v1/admin/users` | `AdminUserListAPI` | handler | no | `app/apis/urls.py:606` |
| core | admin | OPTIONS | `/v1/admin/users/<user_id>` | `AdminUserAPI` | options_not_business | - | `app/apis/urls.py:616` |
| core | admin | PUT | `/v1/admin/users/<user_id>` | `AdminUserAPI` | handler | yes | `app/apis/urls.py:616` |
| core | admin | GET | `/v1/admin/v-codes` | `AdminVCodeListAPI` | handler | no | `app/apis/urls.py:621` |
| core | admin | OPTIONS | `/v1/admin/v-codes` | `AdminVCodeListAPI` | options_not_business | - | `app/apis/urls.py:621` |
| core | avatar | OPTIONS | `/v1/avatar` | `AvatarAPI` | options_not_business | - | `app/apis/urls.py:588` |
| core | avatar | PUT | `/v1/avatar` | `AvatarAPI` | handler | no | `app/apis/urls.py:588` |
| core | file | DELETE | `/v1/files/<file_id>` | `FileAPI` | handler | no | `app/apis/urls.py:480` |
| core | file | GET | `/v1/files/<file_id>` | `FileAPI` | handler | no | `app/apis/urls.py:480` |
| core | file | OPTIONS | `/v1/files/<file_id>` | `FileAPI` | options_not_business | - | `app/apis/urls.py:480` |
| core | file | PUT | `/v1/files/<file_id>` | `FileAPI` | handler | no | `app/apis/urls.py:480` |
| core | file | OPTIONS | `/v1/files/<file_id>/ocr` | `FileOCRAPI` | options_not_business | - | `app/apis/urls.py:490` |
| core | file | POST | `/v1/files/<file_id>/ocr` | `FileOCRAPI` | handler | no | `app/apis/urls.py:490` |
| core | file | GET | `/v1/files/<file_id>/sources` | `FileSourceListAPI` | handler | no | `app/apis/urls.py:485` |
| core | file | OPTIONS | `/v1/files/<file_id>/sources` | `FileSourceListAPI` | options_not_business | - | `app/apis/urls.py:485` |
| core | file | PATCH | `/v1/files/<file_id>/sources` | `FileSourceListAPI` | handler | no | `app/apis/urls.py:485` |
| core | file | POST | `/v1/files/<file_id>/sources` | `FileSourceListAPI` | handler | no | `app/apis/urls.py:485` |
| core | file | OPTIONS | `/v1/files/<file_id>/thumbnail` | `FileThumbnailAPI` | options_not_business | - | `app/apis/urls.py:495` |
| core | file | POST | `/v1/files/<file_id>/thumbnail` | `FileThumbnailAPI` | handler | no | `app/apis/urls.py:495` |
| core | group | DELETE | `/v1/applications/<application_id>` | `ApplicationAPI` | handler | yes | `app/apis/urls.py:558` |
| core | group | OPTIONS | `/v1/applications/<application_id>` | `ApplicationAPI` | options_not_business | - | `app/apis/urls.py:558` |
| core | group | PATCH | `/v1/applications/<application_id>` | `ApplicationAPI` | handler | yes | `app/apis/urls.py:558` |
| core | group | PUT | `/v1/applications/<application_id>` | `ApplicationAPI` | registered_only_405 | no | `app/apis/urls.py:558` |
| core | group | DELETE | `/v1/invitations/<invitation_id>` | `InvitationAPI` | handler | yes | `app/apis/urls.py:548` |
| core | group | OPTIONS | `/v1/invitations/<invitation_id>` | `InvitationAPI` | options_not_business | - | `app/apis/urls.py:548` |
| core | group | PATCH | `/v1/invitations/<invitation_id>` | `InvitationAPI` | handler | yes | `app/apis/urls.py:548` |
| core | group | PUT | `/v1/invitations/<invitation_id>` | `InvitationAPI` | handler | yes | `app/apis/urls.py:548` |
| core | group | GET | `/v1/<group_type>/<group_id>/applications` | `ApplicationListAPI` | handler | yes | `app/apis/urls.py:553` |
| core | group | OPTIONS | `/v1/<group_type>/<group_id>/applications` | `ApplicationListAPI` | options_not_business | - | `app/apis/urls.py:553` |
| core | group | POST | `/v1/<group_type>/<group_id>/applications` | `ApplicationListAPI` | handler | yes | `app/apis/urls.py:553` |
| core | group | GET | `/v1/<group_type>/<group_id>/invitations` | `InvitationListAPI` | handler | yes | `app/apis/urls.py:543` |
| core | group | OPTIONS | `/v1/<group_type>/<group_id>/invitations` | `InvitationListAPI` | options_not_business | - | `app/apis/urls.py:543` |
| core | group | POST | `/v1/<group_type>/<group_id>/invitations` | `InvitationListAPI` | handler | yes | `app/apis/urls.py:543` |
| core | group | GET | `/v1/<group_type>/<group_id>/public-info` | `GroupPublicInfoAPI` | handler | yes | `app/apis/urls.py:538` |
| core | group | OPTIONS | `/v1/<group_type>/<group_id>/public-info` | `GroupPublicInfoAPI` | options_not_business | - | `app/apis/urls.py:538` |
| core | identity | OPTIONS | `/v1/me/aliases` | `MeAliasesAPI` | options_not_business | - | `app/apis/urls.py:211` |
| core | identity | PATCH | `/v1/me/aliases` | `MeAliasesAPI` | handler | yes | `app/apis/urls.py:211` |
| core | identity | OPTIONS | `/v1/projects/<project_id>/clear` | `ProjectClearAPI` | options_not_business | - | `app/apis/urls.py:256` |
| core | identity | POST | `/v1/projects/<project_id>/clear` | `ProjectClearAPI` | handler | yes | `app/apis/urls.py:256` |
| core | identity | OPTIONS | `/v1/projects/<project_id>/complete` | `ProjectCompleteAPI` | options_not_business | - | `app/apis/urls.py:251` |
| core | identity | POST | `/v1/projects/<project_id>/complete` | `ProjectCompleteAPI` | handler | yes | `app/apis/urls.py:251` |
| core | identity | GET | `/v1/projects/<project_id>/members` | `ProjectMemberListAPI` | handler | yes | `app/apis/urls.py:221` |
| core | identity | OPTIONS | `/v1/projects/<project_id>/members` | `ProjectMemberListAPI` | options_not_business | - | `app/apis/urls.py:221` |
| core | identity | OPTIONS | `/v1/projects/<project_id>/members/changes` | `ProjectMemberChangesAPI` | options_not_business | - | `app/apis/urls.py:226` |
| core | identity | POST | `/v1/projects/<project_id>/members/changes` | `ProjectMemberChangesAPI` | handler | yes | `app/apis/urls.py:226` |
| core | identity | DELETE | `/v1/projects/<project_id>/members/<member_id>` | `ProjectMemberHardDeleteAPI` | handler | yes | `app/apis/urls.py:241` |
| core | identity | OPTIONS | `/v1/projects/<project_id>/members/<member_id>` | `ProjectMemberHardDeleteAPI` | options_not_business | - | `app/apis/urls.py:241` |
| core | identity | OPTIONS | `/v1/projects/<project_id>/members/<member_id>/bind` | `ProjectMemberBindAPI` | options_not_business | - | `app/apis/urls.py:231` |
| core | identity | POST | `/v1/projects/<project_id>/members/<member_id>/bind` | `ProjectMemberBindAPI` | handler | yes | `app/apis/urls.py:231` |
| core | identity | OPTIONS | `/v1/projects/<project_id>/members/<member_id>/merge` | `ProjectMemberMergeAPI` | options_not_business | - | `app/apis/urls.py:236` |
| core | identity | POST | `/v1/projects/<project_id>/members/<member_id>/merge` | `ProjectMemberMergeAPI` | handler | yes | `app/apis/urls.py:236` |
| core | identity | OPTIONS | `/v1/projects/<project_id>/owner/transfer` | `ProjectOwnerTransferAPI` | options_not_business | - | `app/apis/urls.py:246` |
| core | identity | POST | `/v1/projects/<project_id>/owner/transfer` | `ProjectOwnerTransferAPI` | handler | yes | `app/apis/urls.py:246` |
| core | identity | OPTIONS | `/v1/projects/<project_id>/reopen` | `ProjectReopenAPI` | options_not_business | - | `app/apis/urls.py:261` |
| core | identity | POST | `/v1/projects/<project_id>/reopen` | `ProjectReopenAPI` | handler | yes | `app/apis/urls.py:261` |
| core | identity | GET | `/v1/teams/<team_id>/identity-tag-policy` | `IdentityTagPolicyAPI` | handler | yes | `app/apis/urls.py:288` |
| core | identity | OPTIONS | `/v1/teams/<team_id>/identity-tag-policy` | `IdentityTagPolicyAPI` | options_not_business | - | `app/apis/urls.py:288` |
| core | identity | PATCH | `/v1/teams/<team_id>/identity-tag-policy` | `IdentityTagPolicyAPI` | handler | yes | `app/apis/urls.py:288` |
| core | identity | GET | `/v1/teams/<team_id>/members` | `TeamMemberListAPI` | handler | yes | `app/apis/urls.py:266` |
| core | identity | OPTIONS | `/v1/teams/<team_id>/members` | `TeamMemberListAPI` | options_not_business | - | `app/apis/urls.py:266` |
| core | identity | POST | `/v1/teams/<team_id>/members` | `TeamMemberListAPI` | handler | yes | `app/apis/urls.py:266` |
| core | identity | DELETE | `/v1/teams/<team_id>/members/<member_id>` | `TeamMemberAPI` | handler | yes | `app/apis/urls.py:271` |
| core | identity | OPTIONS | `/v1/teams/<team_id>/members/<member_id>` | `TeamMemberAPI` | options_not_business | - | `app/apis/urls.py:271` |
| core | identity | PATCH | `/v1/teams/<team_id>/members/<member_id>` | `TeamMemberAPI` | handler | yes | `app/apis/urls.py:271` |
| core | identity | OPTIONS | `/v1/teams/<team_id>/members/<member_id>/aliases` | `TeamMemberAliasesAPI` | options_not_business | - | `app/apis/urls.py:276` |
| core | identity | PATCH | `/v1/teams/<team_id>/members/<member_id>/aliases` | `TeamMemberAliasesAPI` | handler | yes | `app/apis/urls.py:276` |
| core | identity | OPTIONS | `/v1/teams/<team_id>/members/<member_id>/default-display-name` | `TeamMemberDefaultDisplayNameAPI` | options_not_business | - | `app/apis/urls.py:281` |
| core | identity | PATCH | `/v1/teams/<team_id>/members/<member_id>/default-display-name` | `TeamMemberDefaultDisplayNameAPI` | handler | yes | `app/apis/urls.py:281` |
| core | identity | OPTIONS | `/v1/users/<user_id>/aliases` | `UserAliasesAPI` | options_not_business | - | `app/apis/urls.py:216` |
| core | identity | PATCH | `/v1/users/<user_id>/aliases` | `UserAliasesAPI` | handler | yes | `app/apis/urls.py:216` |
| core | index | GET | `/docs/<path:path>` | `DocsAPI` | handler | no | `app/apis/urls.py:121` |
| core | index | OPTIONS | `/docs/<path:path>` | `DocsAPI` | options_not_business | - | `app/apis/urls.py:121` |
| core | index | GET | `/error` | `ErrorAPI` | handler | no | `app/apis/urls.py:134` |
| core | index | OPTIONS | `/error` | `ErrorAPI` | options_not_business | - | `app/apis/urls.py:134` |
| core | index | GET | `/ping` | `PingAPI` | handler | yes | `app/apis/urls.py:118` |
| core | index | OPTIONS | `/ping` | `PingAPI` | options_not_business | - | `app/apis/urls.py:118` |
| core | index | GET | `/urls` | `UrlListAPI` | handler | no | `app/apis/urls.py:126` |
| core | index | OPTIONS | `/urls` | `UrlListAPI` | options_not_business | - | `app/apis/urls.py:126` |
| core | index | GET | `/warning` | `WarningAPI` | handler | no | `app/apis/urls.py:129` |
| core | index | OPTIONS | `/warning` | `WarningAPI` | options_not_business | - | `app/apis/urls.py:129` |
| core | language | GET | `/v1/languages` | `LanguageListAPI` | handler | yes | `app/apis/urls.py:583` |
| core | language | OPTIONS | `/v1/languages` | `LanguageListAPI` | options_not_business | - | `app/apis/urls.py:583` |
| core | me | OPTIONS | `/v1/user/email` | `MeEmailAPI` | options_not_business | - | `app/apis/urls.py:167` |
| core | me | PUT | `/v1/user/email` | `MeEmailAPI` | handler | yes | `app/apis/urls.py:167` |
| core | me | GET | `/v1/user/info` | `MeInfoAPI` | handler | yes | `app/apis/urls.py:162` |
| core | me | OPTIONS | `/v1/user/info` | `MeInfoAPI` | options_not_business | - | `app/apis/urls.py:162` |
| core | me | PUT | `/v1/user/info` | `MeInfoAPI` | handler | yes | `app/apis/urls.py:162` |
| core | me | GET | `/v1/user/invitations` | `MeInvitationListAPI` | handler | yes | `app/apis/urls.py:177` |
| core | me | OPTIONS | `/v1/user/invitations` | `MeInvitationListAPI` | options_not_business | - | `app/apis/urls.py:177` |
| core | me | DELETE | `/v1/user/password` | `MePasswordAPI` | handler | yes | `app/apis/urls.py:192` |
| core | me | OPTIONS | `/v1/user/password` | `MePasswordAPI` | options_not_business | - | `app/apis/urls.py:172` |
| core | me | OPTIONS | `/v1/user/password` | `MePasswordAPI` | options_not_business | - | `app/apis/urls.py:192` |
| core | me | PUT | `/v1/user/password` | `MePasswordAPI` | handler | yes | `app/apis/urls.py:172` |
| core | me | GET | `/v1/user/projects` | `MeProjectListAPI` | handler | no | `app/apis/urls.py:197` |
| core | me | OPTIONS | `/v1/user/projects` | `MeProjectListAPI` | options_not_business | - | `app/apis/urls.py:197` |
| core | me | GET | `/v1/user/related-applications` | `MeRelatedApplicationListAPI` | handler | yes | `app/apis/urls.py:182` |
| core | me | OPTIONS | `/v1/user/related-applications` | `MeRelatedApplicationListAPI` | options_not_business | - | `app/apis/urls.py:182` |
| core | me | GET | `/v1/user/teams` | `MeTeamListAPI` | handler | yes | `app/apis/urls.py:202` |
| core | me | OPTIONS | `/v1/user/teams` | `MeTeamListAPI` | options_not_business | - | `app/apis/urls.py:202` |
| core | me | OPTIONS | `/v1/user/token` | `MeTokenAPI` | options_not_business | - | `app/apis/urls.py:187` |
| core | me | POST | `/v1/user/token` | `MeTokenAPI` | handler | yes | `app/apis/urls.py:187` |
| core | notification | GET | `/v1/admin/notification-recipients` | `CandidatesAPI` | handler | no | `app/apis/notification.py:716` |
| core | notification | GET | `/v1/admin/notifications` | `ManageListAPI` | handler | no | `app/apis/notification.py:707` |
| core | notification | POST | `/v1/admin/notifications` | `PublishAPI` | handler | no | `app/apis/notification.py:708` |
| core | notification | POST | `/v1/admin/notifications/preview` | `PreviewAPI` | handler | no | `app/apis/notification.py:709` |
| core | notification | GET | `/v1/admin/notifications/<notification_id>` | `ManageDetailAPI` | handler | no | `app/apis/notification.py:723` |
| core | notification | POST | `/v1/admin/notifications/<notification_id>/revoke` | `RevokeAPI` | handler | no | `app/apis/notification.py:730` |
| core | notification | GET | `/v1/me/notification-capabilities` | `NotificationCapabilitiesAPI` | handler | no | `app/apis/notification.py:663` |
| core | notification | GET | `/v1/me/notification-preferences` | `PreferenceAPI` | handler | no | `app/apis/notification.py:678` |
| core | notification | PUT | `/v1/me/notification-preferences` | `PreferenceAPI` | handler | no | `app/apis/notification.py:678` |
| core | notification | GET | `/v1/me/notification-sync` | `NotificationSyncAPI` | handler | no | `app/apis/notification.py:740` |
| core | notification | GET | `/v1/me/notifications` | `InboxAPI` | handler | no | `app/apis/notification.py:669` |
| core | notification | POST | `/v1/me/notifications/mark-read` | `MarkReadAPI` | handler | no | `app/apis/notification.py:671` |
| core | notification | GET | `/v1/me/notifications/unread-counts` | `UnreadAPI` | handler | no | `app/apis/notification.py:670` |
| core | notification | GET | `/v1/me/notifications/<notification_id>` | `ReceiptAPI` | handler | no | `app/apis/notification.py:672` |
| core | notification | PATCH | `/v1/me/notifications/<notification_id>` | `ReceiptAPI` | handler | no | `app/apis/notification.py:672` |
| core | notification | GET | `/v1/notification-project-cards` | `CardSearchAPI` | handler | no | `app/apis/notification.py:738` |
| core | notification | POST | `/v1/notification-project-cards/resolve` | `CardAPI` | handler | no | `app/apis/notification.py:706` |
| core | notification | GET | `/v1/notification-recipients` | `CandidatesAPI` | handler | no | `app/apis/notification.py:692` |
| core | notification | POST | `/v1/notifications` | `PublishAPI` | handler | no | `app/apis/notification.py:690` |
| core | notification | POST | `/v1/notifications/preview` | `PreviewAPI` | handler | no | `app/apis/notification.py:691` |
| core | notification | GET | `/v1/notifications/sent` | `ManageListAPI` | handler | no | `app/apis/notification.py:693` |
| core | notification | GET | `/v1/notifications/<notification_id>` | `ManageDetailAPI` | handler | no | `app/apis/notification.py:694` |
| core | notification | PATCH | `/v1/notifications/<notification_id>` | `ManageDetailAPI` | handler | no | `app/apis/notification.py:694` |
| core | notification | POST | `/v1/notifications/<notification_id>/revoke` | `RevokeAPI` | handler | no | `app/apis/notification.py:700` |
| core | notification | GET | `/v1/teams/<team_id>/notification-policy` | `PolicyAPI` | handler | no | `app/apis/notification.py:684` |
| core | notification | PUT | `/v1/teams/<team_id>/notification-policy` | `PolicyAPI` | handler | no | `app/apis/notification.py:684` |
| core | project | GET | `/v1/projects/<project_id>` | `ProjectAPI` | handler | yes | `app/apis/urls.py:417` |
| core | project | OPTIONS | `/v1/projects/<project_id>` | `ProjectAPI` | options_not_business | - | `app/apis/urls.py:417` |
| core | project | PUT | `/v1/projects/<project_id>` | `ProjectAPI` | handler | yes | `app/apis/urls.py:417` |
| core | project | GET | `/v1/projects/<project_id>/files` | `ProjectFileListAPI` | handler | no | `app/apis/urls.py:433` |
| core | project | OPTIONS | `/v1/projects/<project_id>/files` | `ProjectFileListAPI` | options_not_business | - | `app/apis/urls.py:433` |
| core | project | POST | `/v1/projects/<project_id>/files` | `ProjectFileListAPI` | handler | no | `app/apis/urls.py:433` |
| core | project | GET | `/v1/projects/<project_id>/files/order` | `ProjectFileOrderAPI` | handler | no | `app/apis/urls.py:428` |
| core | project | OPTIONS | `/v1/projects/<project_id>/files/order` | `ProjectFileOrderAPI` | options_not_business | - | `app/apis/urls.py:428` |
| core | project | PUT | `/v1/projects/<project_id>/files/order` | `ProjectFileOrderAPI` | handler | no | `app/apis/urls.py:428` |
| core | project | OPTIONS | `/v1/projects/<project_id>/label-clear` | `ProjectLabelClearAPI` | options_not_business | - | `app/apis/urls.py:412` |
| core | project | POST | `/v1/projects/<project_id>/label-clear` | `ProjectLabelClearAPI` | handler | no | `app/apis/urls.py:412` |
| core | project | GET | `/v1/projects/<project_id>/label-merge` | `ProjectLabelMergeAPI` | handler | no | `app/apis/urls.py:412` |
| core | project | OPTIONS | `/v1/projects/<project_id>/label-merge` | `ProjectLabelMergeAPI` | options_not_business | - | `app/apis/urls.py:412` |
| core | project | POST | `/v1/projects/<project_id>/label-merge` | `ProjectLabelMergeAPI` | handler | no | `app/apis/urls.py:412` |
| core | project | OPTIONS | `/v1/projects/<project_id>/label-recover` | `ProjectLabelRecoverAPI` | options_not_business | - | `app/apis/urls.py:412` |
| core | project | POST | `/v1/projects/<project_id>/label-recover` | `ProjectLabelRecoverAPI` | handler | no | `app/apis/urls.py:412` |
| core | project | GET | `/v1/projects/<project_id>/label-state` | `ProjectLabelStateAPI` | handler | no | `app/apis/urls.py:412` |
| core | project | OPTIONS | `/v1/projects/<project_id>/label-state` | `ProjectLabelStateAPI` | options_not_business | - | `app/apis/urls.py:412` |
| core | project | OPTIONS | `/v1/projects/<project_id>/ocr` | `ProjectOCRAPI` | options_not_business | - | `app/apis/urls.py:458` |
| core | project | POST | `/v1/projects/<project_id>/ocr` | `ProjectOCRAPI` | handler | no | `app/apis/urls.py:458` |
| core | project | OPTIONS | `/v1/projects/<project_id>/outputs` | `ProjectOutputListAPI` | options_not_business | - | `app/apis/urls.py:443` |
| core | project | POST | `/v1/projects/<project_id>/outputs` | `ProjectOutputListAPI` | handler | no | `app/apis/urls.py:443` |
| core | project | GET | `/v1/projects/<project_id>/presence` | `ProjectPresenceAPI` | handler | yes | `app/apis/urls.py:473` |
| core | project | OPTIONS | `/v1/projects/<project_id>/presence` | `ProjectPresenceAPI` | options_not_business | - | `app/apis/urls.py:473` |
| core | project | OPTIONS | `/v1/projects/<project_id>/presence/heartbeat` | `ProjectPresenceHeartbeatAPI` | options_not_business | - | `app/apis/urls.py:463` |
| core | project | POST | `/v1/projects/<project_id>/presence/heartbeat` | `ProjectPresenceHeartbeatAPI` | handler | yes | `app/apis/urls.py:463` |
| core | project | OPTIONS | `/v1/projects/<project_id>/presence/leave` | `ProjectPresenceLeaveAPI` | options_not_business | - | `app/apis/urls.py:468` |
| core | project | POST | `/v1/projects/<project_id>/presence/leave` | `ProjectPresenceLeaveAPI` | handler | yes | `app/apis/urls.py:468` |
| core | project | GET | `/v1/projects/<project_id>/snapshots` | `ProjectSnapshotListAPI` | handler | no | `app/apis/urls.py:412` |
| core | project | OPTIONS | `/v1/projects/<project_id>/snapshots` | `ProjectSnapshotListAPI` | options_not_business | - | `app/apis/urls.py:412` |
| core | project | POST | `/v1/projects/<project_id>/snapshots` | `ProjectSnapshotListAPI` | handler | no | `app/apis/urls.py:412` |
| core | project | OPTIONS | `/v1/projects/<project_id>/snapshots/<snapshot_id>/restore` | `ProjectSnapshotRestoreAPI` | options_not_business | - | `app/apis/urls.py:412` |
| core | project | POST | `/v1/projects/<project_id>/snapshots/<snapshot_id>/restore` | `ProjectSnapshotRestoreAPI` | handler | no | `app/apis/urls.py:412` |
| core | project | GET | `/v1/projects/<project_id>/targets` | `ProjectTargetListAPI` | handler | yes | `app/apis/urls.py:438` |
| core | project | OPTIONS | `/v1/projects/<project_id>/targets` | `ProjectTargetListAPI` | options_not_business | - | `app/apis/urls.py:438` |
| core | project | POST | `/v1/projects/<project_id>/targets` | `ProjectTargetListAPI` | handler | yes | `app/apis/urls.py:438` |
| core | project | GET | `/v1/projects/<project_id>/targets/<target_id>/outputs` | `ProjectTargetOutputListAPI` | handler | no | `app/apis/urls.py:448` |
| core | project | OPTIONS | `/v1/projects/<project_id>/targets/<target_id>/outputs` | `ProjectTargetOutputListAPI` | options_not_business | - | `app/apis/urls.py:448` |
| core | project | POST | `/v1/projects/<project_id>/targets/<target_id>/outputs` | `ProjectTargetOutputListAPI` | handler | no | `app/apis/urls.py:448` |
| core | project | OPTIONS | `/v1/projects/<project_id>/targets/<target_id>/send-proofread-draft` | `ProjectSendProofreadDraftAPI` | options_not_business | - | `app/apis/urls.py:453` |
| core | project | POST | `/v1/projects/<project_id>/targets/<target_id>/send-proofread-draft` | `ProjectSendProofreadDraftAPI` | handler | no | `app/apis/urls.py:453` |
| core | project | OPTIONS | `/v1/projects/<project_id>/thumbnails` | `ProjectThumbnailAPI` | options_not_business | - | `app/apis/urls.py:423` |
| core | project | POST | `/v1/projects/<project_id>/thumbnails` | `ProjectThumbnailAPI` | handler | no | `app/apis/urls.py:423` |
| core | project_set | DELETE | `/v1/project-sets/<project_set_id>` | `ProjectSetAPI` | handler | yes | `app/apis/urls.py:379` |
| core | project_set | GET | `/v1/project-sets/<project_set_id>` | `ProjectSetAPI` | handler | yes | `app/apis/urls.py:379` |
| core | project_set | OPTIONS | `/v1/project-sets/<project_set_id>` | `ProjectSetAPI` | options_not_business | - | `app/apis/urls.py:379` |
| core | project_set | PUT | `/v1/project-sets/<project_set_id>` | `ProjectSetAPI` | handler | yes | `app/apis/urls.py:379` |
| core | site | GET | `/v1/site/homepage` | `HomepageAPI` | handler | no | `app/apis/urls.py:139` |
| core | site | OPTIONS | `/v1/site/homepage` | `HomepageAPI` | options_not_business | - | `app/apis/urls.py:139` |
| core | source | DELETE | `/v1/sources/<source_id>` | `SourceAPI` | handler | no | `app/apis/urls.py:502` |
| core | source | OPTIONS | `/v1/sources/<source_id>` | `SourceAPI` | options_not_business | - | `app/apis/urls.py:502` |
| core | source | PUT | `/v1/sources/<source_id>` | `SourceAPI` | handler | no | `app/apis/urls.py:502` |
| core | source | OPTIONS | `/v1/sources/<source_id>/rank` | `SourceRankAPI` | options_not_business | - | `app/apis/urls.py:507` |
| core | source | PUT | `/v1/sources/<source_id>/rank` | `SourceRankAPI` | handler | no | `app/apis/urls.py:507` |
| core | source | OPTIONS | `/v1/sources/<source_id>/tips` | `SourceTipListAPI` | options_not_business | - | `app/apis/urls.py:517` |
| core | source | POST | `/v1/sources/<source_id>/tips` | `SourceTipListAPI` | handler | no | `app/apis/urls.py:517` |
| core | source | OPTIONS | `/v1/sources/<source_id>/translations` | `SourceTranslationListAPI` | options_not_business | - | `app/apis/urls.py:512` |
| core | source | POST | `/v1/sources/<source_id>/translations` | `SourceTranslationListAPI` | handler | no | `app/apis/urls.py:512` |
| core | target | DELETE | `/v1/targets/<target_id>` | `TargetAPI` | handler | yes | `app/apis/urls.py:576` |
| core | target | OPTIONS | `/v1/targets/<target_id>` | `TargetAPI` | options_not_business | - | `app/apis/urls.py:576` |
| core | team | GET | `/v1/teams` | `TeamListAPI` | handler | yes | `app/apis/urls.py:318` |
| core | team | OPTIONS | `/v1/teams` | `TeamListAPI` | options_not_business | - | `app/apis/urls.py:318` |
| core | team | POST | `/v1/teams` | `TeamListAPI` | handler | yes | `app/apis/urls.py:318` |
| core | team | DELETE | `/v1/teams/<team_id>` | `TeamAPI` | handler | no | `app/apis/urls.py:321` |
| core | team | GET | `/v1/teams/<team_id>` | `TeamAPI` | handler | yes | `app/apis/urls.py:321` |
| core | team | OPTIONS | `/v1/teams/<team_id>` | `TeamAPI` | options_not_business | - | `app/apis/urls.py:321` |
| core | team | PUT | `/v1/teams/<team_id>` | `TeamAPI` | handler | yes | `app/apis/urls.py:321` |
| core | team | GET | `/v1/teams/<team_id>/insight/projects` | `TeamInsightProjectListAPI` | handler | no | `app/apis/urls.py:357` |
| core | team | OPTIONS | `/v1/teams/<team_id>/insight/projects` | `TeamInsightProjectListAPI` | options_not_business | - | `app/apis/urls.py:357` |
| core | team | GET | `/v1/teams/<team_id>/insight/projects/<project_id>/users` | `TeamInsightProjectUserListAPI` | handler | no | `app/apis/urls.py:372` |
| core | team | OPTIONS | `/v1/teams/<team_id>/insight/projects/<project_id>/users` | `TeamInsightProjectUserListAPI` | options_not_business | - | `app/apis/urls.py:372` |
| core | team | GET | `/v1/teams/<team_id>/insight/users` | `TeamInsightUserListAPI` | handler | no | `app/apis/urls.py:352` |
| core | team | OPTIONS | `/v1/teams/<team_id>/insight/users` | `TeamInsightUserListAPI` | options_not_business | - | `app/apis/urls.py:352` |
| core | team | GET | `/v1/teams/<team_id>/insight/users/<user_id>/projects` | `TeamInsightUserProjectListAPI` | handler | no | `app/apis/urls.py:367` |
| core | team | OPTIONS | `/v1/teams/<team_id>/insight/users/<user_id>/projects` | `TeamInsightUserProjectListAPI` | options_not_business | - | `app/apis/urls.py:367` |
| core | team | OPTIONS | `/v1/teams/<team_id>/outputs` | `TeamProjectOutputListAPI` | options_not_business | - | `app/apis/urls.py:332` |
| core | team | POST | `/v1/teams/<team_id>/outputs` | `TeamProjectOutputListAPI` | handler | no | `app/apis/urls.py:332` |
| core | team | GET | `/v1/teams/<team_id>/project-sets` | `TeamProjectSetListAPI` | handler | yes | `app/apis/urls.py:342` |
| core | team | OPTIONS | `/v1/teams/<team_id>/project-sets` | `TeamProjectSetListAPI` | options_not_business | - | `app/apis/urls.py:342` |
| core | team | POST | `/v1/teams/<team_id>/project-sets` | `TeamProjectSetListAPI` | handler | yes | `app/apis/urls.py:342` |
| core | team | GET | `/v1/teams/<team_id>/project-sets/<project_set_id>/project-zips` | `TeamProjectImportAPI` | registered_only_405 | no | `app/apis/urls.py:337` |
| core | team | OPTIONS | `/v1/teams/<team_id>/project-sets/<project_set_id>/project-zips` | `TeamProjectImportAPI` | options_not_business | - | `app/apis/urls.py:337` |
| core | team | POST | `/v1/teams/<team_id>/project-sets/<project_set_id>/project-zips` | `TeamProjectImportAPI` | handler | no | `app/apis/urls.py:337` |
| core | team | GET | `/v1/teams/<team_id>/projects` | `TeamProjectListAPI` | handler | yes | `app/apis/urls.py:327` |
| core | team | OPTIONS | `/v1/teams/<team_id>/projects` | `TeamProjectListAPI` | options_not_business | - | `app/apis/urls.py:327` |
| core | team | POST | `/v1/teams/<team_id>/projects` | `TeamProjectListAPI` | handler | yes | `app/apis/urls.py:327` |
| core | team | GET | `/v1/teams/<team_id>/projects/active-presence` | `TeamProjectsActivePresenceAPI` | handler | yes | `app/apis/urls.py:362` |
| core | team | OPTIONS | `/v1/teams/<team_id>/projects/active-presence` | `TeamProjectsActivePresenceAPI` | options_not_business | - | `app/apis/urls.py:362` |
| core | team | GET | `/v1/teams/<team_id>/term-banks` | `TermBankAPI` | handler | no | `app/apis/urls.py:347` |
| core | team | OPTIONS | `/v1/teams/<team_id>/term-banks` | `TermBankAPI` | options_not_business | - | `app/apis/urls.py:347` |
| core | team | POST | `/v1/teams/<team_id>/term-banks` | `TermBankAPI` | handler | no | `app/apis/urls.py:347` |
| core | term | DELETE | `/v1/terms/<term_id>` | `TermAPI` | handler | no | `app/apis/urls.py:397` |
| core | term | OPTIONS | `/v1/terms/<term_id>` | `TermAPI` | options_not_business | - | `app/apis/urls.py:397` |
| core | term | PUT | `/v1/terms/<term_id>` | `TermAPI` | handler | no | `app/apis/urls.py:397` |
| core | term_bank | DELETE | `/v1/term-banks/<term_bank_id>` | `TermBankAPI` | handler | no | `app/apis/urls.py:386` |
| core | term_bank | OPTIONS | `/v1/term-banks/<term_bank_id>` | `TermBankAPI` | options_not_business | - | `app/apis/urls.py:386` |
| core | term_bank | PUT | `/v1/term-banks/<term_bank_id>` | `TermBankAPI` | handler | no | `app/apis/urls.py:386` |
| core | term_bank | GET | `/v1/term-banks/<term_bank_id>/terms` | `TermListAPI` | handler | no | `app/apis/urls.py:391` |
| core | term_bank | OPTIONS | `/v1/term-banks/<term_bank_id>/terms` | `TermListAPI` | options_not_business | - | `app/apis/urls.py:391` |
| core | term_bank | POST | `/v1/term-banks/<term_bank_id>/terms` | `TermListAPI` | handler | no | `app/apis/urls.py:391` |
| core | tip | DELETE | `/v1/tips/<tip_id>` | `TipAPI` | handler | no | `app/apis/urls.py:531` |
| core | tip | OPTIONS | `/v1/tips/<tip_id>` | `TipAPI` | options_not_business | - | `app/apis/urls.py:531` |
| core | tip | PUT | `/v1/tips/<tip_id>` | `TipAPI` | handler | no | `app/apis/urls.py:531` |
| core | translation | DELETE | `/v1/translations/<translation_id>` | `TranslationAPI` | handler | no | `app/apis/urls.py:524` |
| core | translation | OPTIONS | `/v1/translations/<translation_id>` | `TranslationAPI` | options_not_business | - | `app/apis/urls.py:524` |
| core | translation | PUT | `/v1/translations/<translation_id>` | `TranslationAPI` | handler | no | `app/apis/urls.py:524` |
| core | type | GET | `/v1/types/<type_name>` | `TypeAPI` | handler | yes | `app/apis/urls.py:144` |
| core | type | OPTIONS | `/v1/types/<type_name>` | `TypeAPI` | options_not_business | - | `app/apis/urls.py:144` |
| core | user | GET | `/v1/users` | `UserListAPI` | handler | yes | `app/apis/urls.py:156` |
| core | user | OPTIONS | `/v1/users` | `UserListAPI` | options_not_business | - | `app/apis/urls.py:156` |
| core | user | OPTIONS | `/v1/users` | `UserAPI` | options_not_business | - | `app/apis/urls.py:159` |
| core | user | POST | `/v1/users` | `UserAPI` | handler | yes | `app/apis/urls.py:159` |
| core | user | GET | `/v1/users/<name>` | `UserAPI` | handler | yes | `app/apis/urls.py:151` |
| core | user | OPTIONS | `/v1/users/<name>` | `UserAPI` | options_not_business | - | `app/apis/urls.py:151` |
| core | v_code | OPTIONS | `/v1/captchas` | `CaptchaAPI` | options_not_business | - | `app/apis/urls.py:296` |
| core | v_code | POST | `/v1/captchas` | `CaptchaAPI` | handler | yes | `app/apis/urls.py:296` |
| core | v_code | OPTIONS | `/v1/confirm-email-codes` | `ConfirmEmailVCodeAPI` | options_not_business | - | `app/apis/urls.py:301` |
| core | v_code | POST | `/v1/confirm-email-codes` | `ConfirmEmailVCodeAPI` | handler | yes | `app/apis/urls.py:301` |
| core | v_code | OPTIONS | `/v1/reset-email-codes` | `ResetEmailVCodeAPI` | options_not_business | - | `app/apis/urls.py:306` |
| core | v_code | POST | `/v1/reset-email-codes` | `ResetEmailVCodeAPI` | handler | yes | `app/apis/urls.py:306` |
| core | v_code | OPTIONS | `/v1/reset-password-codes` | `ResetPasswordVCodeAPI` | options_not_business | - | `app/apis/urls.py:311` |
| core | v_code | POST | `/v1/reset-password-codes` | `ResetPasswordVCodeAPI` | handler | yes | `app/apis/urls.py:311` |
| disabled_comment | group | GET | `/<group_type>/<group_id>/roles` | `RoleListAPI` | handler | no | `app/apis/urls.py:564` |
| disabled_comment | group | OPTIONS | `/<group_type>/<group_id>/roles` | `RoleListAPI` | options_not_business | - | `app/apis/urls.py:564` |
| disabled_comment | group | POST | `/<group_type>/<group_id>/roles` | `RoleListAPI` | handler | no | `app/apis/urls.py:564` |
| disabled_comment | group | DELETE | `/<group_type>/<group_id>/roles/<role_id>` | `RoleAPI` | handler | no | `app/apis/urls.py:569` |
| disabled_comment | group | OPTIONS | `/<group_type>/<group_id>/roles/<role_id>` | `RoleAPI` | options_not_business | - | `app/apis/urls.py:569` |
| disabled_comment | group | PUT | `/<group_type>/<group_id>/roles/<role_id>` | `RoleAPI` | handler | no | `app/apis/urls.py:569` |
| optional | archive_import | OPTIONS | `/v1/projects/<project_id>/import-from-archive` | `ArchiveImportAPI` | options_not_business | - | `app/modules/archive_import/__init__.py:51` |
| optional | archive_import | POST | `/v1/projects/<project_id>/import-from-archive` | `ArchiveImportAPI` | handler | no | `app/modules/archive_import/__init__.py:51` |
| optional | archive_import | GET | `/v1/projects/<project_id>/import-task` | `ArchiveImportTaskAPI` | handler | no | `app/modules/archive_import/__init__.py:56` |
| optional | archive_import | OPTIONS | `/v1/projects/<project_id>/import-task` | `ArchiveImportTaskAPI` | options_not_business | - | `app/modules/archive_import/__init__.py:56` |
| optional | archive_import | OPTIONS | `/v1/projects/<project_id>/import-task/dismiss` | `ArchiveImportTaskAPI` | options_not_business | - | `app/modules/archive_import/__init__.py:61` |
| optional | archive_import | POST | `/v1/projects/<project_id>/import-task/dismiss` | `ArchiveImportTaskAPI` | handler | no | `app/modules/archive_import/__init__.py:61` |
| optional | partner_search | OPTIONS | `/v1/partner-search-query-entry` | `PartnerSearchEntryAPI` | options_not_business | - | `app/modules/partner_search/api.py:214` |
| optional | partner_search | POST | `/v1/partner-search-query-entry` | `PartnerSearchEntryAPI` | handler | no | `app/modules/partner_search/api.py:214` |
| optional | partner_search | GET | `/v1/partner-search-query-entry/settings` | `PartnerSearchSettingsAPI` | handler | no | `app/modules/partner_search/api.py:219` |
| optional | partner_search | OPTIONS | `/v1/partner-search-query-entry/settings` | `PartnerSearchSettingsAPI` | options_not_business | - | `app/modules/partner_search/api.py:219` |
| optional | partner_search | PUT | `/v1/partner-search-query-entry/settings` | `PartnerSearchSettingsAPI` | handler | no | `app/modules/partner_search/api.py:219` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/projects/<project_id>/lock` | `ProjectLockAPI` | options_not_business | - | `app/modules/storage_pool/api.py:865` |
| optional | storage_pool | POST | `/v1/admin/storage-pool/projects/<project_id>/lock` | `ProjectLockAPI` | handler | no | `app/modules/storage_pool/api.py:865` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/projects/<project_id>/migrate` | `ProjectMigrateAPI` | options_not_business | - | `app/modules/storage_pool/api.py:850` |
| optional | storage_pool | POST | `/v1/admin/storage-pool/projects/<project_id>/migrate` | `ProjectMigrateAPI` | handler | no | `app/modules/storage_pool/api.py:850` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/projects/<project_id>/move` | `ProjectMoveAPI` | options_not_business | - | `app/modules/storage_pool/api.py:860` |
| optional | storage_pool | POST | `/v1/admin/storage-pool/projects/<project_id>/move` | `ProjectMoveAPI` | handler | no | `app/modules/storage_pool/api.py:860` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/projects/<project_id>/restore` | `ProjectRestoreAPI` | options_not_business | - | `app/modules/storage_pool/api.py:855` |
| optional | storage_pool | POST | `/v1/admin/storage-pool/projects/<project_id>/restore` | `ProjectRestoreAPI` | handler | no | `app/modules/storage_pool/api.py:855` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/scan` | `ScanAPI` | options_not_business | - | `app/modules/storage_pool/api.py:885` |
| optional | storage_pool | POST | `/v1/admin/storage-pool/scan` | `ScanAPI` | handler | no | `app/modules/storage_pool/api.py:885` |
| optional | storage_pool | GET | `/v1/admin/storage-pool/settings` | `SettingsAPI` | handler | no | `app/modules/storage_pool/api.py:880` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/settings` | `SettingsAPI` | options_not_business | - | `app/modules/storage_pool/api.py:880` |
| optional | storage_pool | PUT | `/v1/admin/storage-pool/settings` | `SettingsAPI` | handler | no | `app/modules/storage_pool/api.py:880` |
| optional | storage_pool | GET | `/v1/admin/storage-pool/sources` | `SourceListAPI` | handler | no | `app/modules/storage_pool/api.py:815` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/sources` | `SourceListAPI` | options_not_business | - | `app/modules/storage_pool/api.py:815` |
| optional | storage_pool | POST | `/v1/admin/storage-pool/sources` | `SourceListAPI` | handler | no | `app/modules/storage_pool/api.py:815` |
| optional | storage_pool | DELETE | `/v1/admin/storage-pool/sources/<source_id>` | `SourceAPI` | handler | no | `app/modules/storage_pool/api.py:820` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/sources/<source_id>` | `SourceAPI` | options_not_business | - | `app/modules/storage_pool/api.py:820` |
| optional | storage_pool | PUT | `/v1/admin/storage-pool/sources/<source_id>` | `SourceAPI` | handler | no | `app/modules/storage_pool/api.py:820` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/sources/<source_id>/test` | `SourceTestAPI` | options_not_business | - | `app/modules/storage_pool/api.py:825` |
| optional | storage_pool | POST | `/v1/admin/storage-pool/sources/<source_id>/test` | `SourceTestAPI` | handler | no | `app/modules/storage_pool/api.py:825` |
| optional | storage_pool | GET | `/v1/admin/storage-pool/stats` | `StatsAPI` | handler | no | `app/modules/storage_pool/api.py:870` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/stats` | `StatsAPI` | options_not_business | - | `app/modules/storage_pool/api.py:870` |
| optional | storage_pool | GET | `/v1/admin/storage-pool/transfers` | `TransferListAPI` | handler | no | `app/modules/storage_pool/api.py:830` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/transfers` | `TransferListAPI` | options_not_business | - | `app/modules/storage_pool/api.py:830` |
| optional | storage_pool | DELETE | `/v1/admin/storage-pool/transfers/<transfer_id>` | `TransferDeleteAPI` | handler | no | `app/modules/storage_pool/api.py:845` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/transfers/<transfer_id>` | `TransferDeleteAPI` | options_not_business | - | `app/modules/storage_pool/api.py:845` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/transfers/<transfer_id>/cancel` | `TransferCancelAPI` | options_not_business | - | `app/modules/storage_pool/api.py:840` |
| optional | storage_pool | POST | `/v1/admin/storage-pool/transfers/<transfer_id>/cancel` | `TransferCancelAPI` | handler | no | `app/modules/storage_pool/api.py:840` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/transfers/<transfer_id>/retry` | `TransferRetryAPI` | options_not_business | - | `app/modules/storage_pool/api.py:835` |
| optional | storage_pool | POST | `/v1/admin/storage-pool/transfers/<transfer_id>/retry` | `TransferRetryAPI` | handler | no | `app/modules/storage_pool/api.py:835` |
| optional | storage_pool | GET | `/v1/admin/storage-pool/usage` | `UsageAPI` | handler | no | `app/modules/storage_pool/api.py:875` |
| optional | storage_pool | OPTIONS | `/v1/admin/storage-pool/usage` | `UsageAPI` | options_not_business | - | `app/modules/storage_pool/api.py:875` |
| optional | ziteng_partner | GET | `/v1/projects/<project_id>/ziteng-check` | `ZitengCheckAPI` | handler | no | `app/modules/ziteng_partner/api.py:96` |
| optional | ziteng_partner | OPTIONS | `/v1/projects/<project_id>/ziteng-check` | `ZitengCheckAPI` | options_not_business | - | `app/modules/ziteng_partner/api.py:96` |
| optional | ziteng_partner | POST | `/v1/projects/<project_id>/ziteng-check` | `ZitengCheckAPI` | handler | no | `app/modules/ziteng_partner/api.py:96` |
| optional | ziteng_partner | GET | `/v1/ziteng-partner/config` | `ZitengConfigAPI` | handler | no | `app/modules/ziteng_partner/api.py:101` |
| optional | ziteng_partner | OPTIONS | `/v1/ziteng-partner/config` | `ZitengConfigAPI` | options_not_business | - | `app/modules/ziteng_partner/api.py:101` |

## C# routes not present in legacy

| Method | Path | Source |
| --- | --- | --- |
| GET | `/health/live` | `src/Shoreline.Api/Program.cs:102` |
| GET | `/health/ready` | `src/Shoreline.Api/Program.cs:103` |
| GET | `/v1/homepage` | `src/Shoreline.Api/Features/SiteSettings.cs:67` |

## Usage

```powershell
# from the C# repository root
python tools/Audit-LegacyRoutes.py `
  --legacy-directory ../shoreline-moeflow-backend `
  --csharp-directory . `
  --output docs/route-coverage.json
```

Writing `.json` also writes the companion `.md` (and the reverse). The tool has no third-party dependencies and performs no network, database, or application startup work.

```powershell
python -m unittest discover -s tools -p "test_audit_legacy_routes.py" -v
```

## Limitations

- Static analysis only. Neither the Flask app nor the C# app is run.
- Flask auto-added OPTIONS/HEAD are not synthesized; only explicit registrations in source are reported. OPTIONS is not business coverage.
- Handler support is resolved from local class definitions and their local bases. Unknown bases are reported as unresolved, never as 405.
- Optional modules are enabled by directory presence in this repository; deployment-specific configuration values are never read.
- C# route matching proves route registration only, not business compatibility.
- Dynamic route strings or unresolved receivers in C# are listed under csharp_unresolved and are not counted as matches.
