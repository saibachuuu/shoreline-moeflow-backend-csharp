# Python 环境变量兼容（切换期）

C# 进程启动时会调用 `LegacyEnvironmentConfiguration.Apply`。它只在现代
`Auth:*`、`Mail:*`、`Mongo:*`、`Site:*` 键缺失时，把 Python `.env-backend`
中的变量映射过来；显式设置的 C# 键始终优先。这样现有部署可以先复用原
`.env-backend` 做灰度，而不必一次性重写全部变量名。

## 映射表

| Python 变量 | C# 配置键 | 说明 |
|---|---|---|
| `SECRET_KEY` | `Auth:Secret` | itsdangerous token 签名；C# 要求至少 32 字符 |
| `ADMIN_EMAIL` | `Auth:AdminEmail` | 管理员邮箱 |
| `ADMIN_INITIAL_PASSWORD` | `Auth:AdminInitialPassword` | 仅在管理员不存在时用于 bootstrap |
| `EMAIL_SMTP_HOST` | `Mail:Host` | |
| `EMAIL_SMTP_PORT` | `Mail:Port` | |
| `EMAIL_USERNAME` | `Mail:Username` | |
| `EMAIL_PASSWORD` | `Mail:Password` | |
| `EMAIL_ADDRESS` | `Mail:From` | |
| `EMAIL_USE_SSL` | `Mail:UseSsl` | 目前 465 端口直接走 SMTPS |
| `ENABLE_USER_EMAIL=True` | `Mail:Enabled=true` | 仅在 `Mail:Enabled` 未设置时 |
| `SITE_NAME` | `Site:Name` | |
| `SITE_ORIGIN` | `Site:Origin` | |
| `MONGODB_URI` | `Mongo:Uri` | 优先于分项 |
| `MONGODB_USER` / `MONGODB_PASS` / `MONGODB_DB_NAME` | `Mongo:Uri` | 按 Python 旧默认拼成 `mongodb://…@moeflow-mongodb:27017/<db>?authSource=admin` |

对象存储的映射在 `ObjectStorage` 内完成：

| Python 变量 | C# 行为 |
|---|---|
| `STORAGE_TYPE=LOCAL_STORAGE` | 选择 Local；仍需 `Storage:Root` |
| `STORAGE_TYPE=OSS` + `OSS_BUCKET_STYLE=S3`（或 `R2`/`MINIO`/`GARAGE`/`AWS`） | 选择标准 S3 客户端 |
| `STORAGE_TYPE=OSS` + `OSS_BUCKET_STYLE=ALI` | 明确拒绝：Aliyun 原生签名尚未移植 |
| `OSS_ENDPOINT` | `Storage:Endpoint` |
| `OSS_BUCKET_NAME` | `Storage:Bucket` |
| `OSS_ACCESS_KEY_ID` / `OSS_ACCESS_KEY_SECRET` | `Storage:AccessKey` / `Storage:SecretKey` |
| `OSS_S3_REGION` | `Storage:Region` |
| `OSS_S3_ADDRESSING_STYLE=path/virtual` | `Storage:ForcePathStyle` |
| `OSS_KEY_PREFIX` | `Storage:KeyPrefix`；`STORAGE_TYPE` 存在且未显式设置时默认 `storage/` |
| `STORAGE_DOMAIN` | `Storage:Domain` |
| `OSS_VIA_CDN=True` | `Storage:ViaCdn=true` |

## CDN 说明

Python 的 CDN 鉴权方式 C（`aliyun_cdn_url_auth_c`）只在 Aliyun 原生分支
（`OSS_BUCKET_STYLE=ALI`）使用。标准 S3 分支在 `OSS_VIA_CDN=True` 时同样
直接返回 `STORAGE_DOMAIN + key`，不生成签名 URL；C# 的 `viaCdn` 行为与该
分支一致。生产当前是 R2 + `OSS_BUCKET_STYLE=S3`，因此 `CDN_URL_KEY_A/B`
不会参与 URL 生成；回源与鉴权由 CDN/反向代理负责。

## 仍需显式设置的 C# 键

- `Storage:Root`：Local 存储根目录（Python 没有对应环境变量）。
- `StoragePool:*`：存储池 worker 和临时目录。
- `Bootstrap:Enabled`：测试或自有种子数据场景关闭 bootstrap。
- `Notifications:WorkerEnabled`：通知 worker 是否在本进程运行。

## 验证

- `tests/Shoreline.Tests/LegacyEnvironmentConfigurationTests.cs`：映射、优先级、Mongo URI 拼接。
- `tests/Shoreline.Tests/StorageLegacyEnvironmentTests.cs`：S3/Local 选择、`storage/` 前缀、CDN URL、Aliyun 原生拒绝。
- 全量 suite 与测试机部署见 `production-readiness-checklist.md`。
