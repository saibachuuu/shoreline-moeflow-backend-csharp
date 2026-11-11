# Environment/config compatibility audit (2026-11-11)

Scope: isolated worktree `moeflow-repl-env-compat-beam-20261111`; source and synthetic tests only.

## Mapped and parity-covered

- `SECRET_KEY`, `ADMIN_EMAIL`, `ADMIN_INITIAL_PASSWORD` -> `Auth:*`.
- `EMAIL_SMTP_HOST/PORT/USERNAME/PASSWORD/ADDRESS/USE_SSL`, `ENABLE_USER_EMAIL` -> `Mail:*`.
- `SITE_NAME`, `SITE_ORIGIN` -> `Site:*`.
- `MONGODB_URI` / `MONGODB_USER/PASS/DB_NAME` -> `Mongo:Uri`.
- Storage legacy variables -> `ObjectStorage` (`STORAGE_TYPE`, `OSS_BUCKET_STYLE`, `OSS_ENDPOINT`, `OSS_BUCKET_NAME`, access keys, region, addressing style, key prefix, domain, via CDN).
- `ZITENG_PARTNER_*`, `ARCHIVE_*`, notification toggles, `MIT_STORAGE_ROOT`, `TESTING`, `DEBUG`.

## Actionable gaps fixed

- `SecurityEmailHandler` now honors `Mail:UseSsl` (mapped from `EMAIL_USE_SSL`) instead of only treating port 465 as SMTPS.
- `ArchiveImportOptions` now treats legacy `ARCHIVE_MAX_ZIP_BYTES` and `ARCHIVE_MAX_ENTRY_BYTES` as megabytes, matching Python; explicit C# `Archive:*` byte values still win.

## Changed/unsafe legacy compatibility (intentionally not relaxed)

- `OSS_S3_VERIFY_SSL=False` is not honored; TLS certificate validation remains enabled.
- Aliyun native storage (`OSS_BUCKET_STYLE=ALI`) remains unsupported and fails fast.
- `CDN_URL_KEY_A/B` remain unused in the S3 branch, matching Python.

## Intentionally unmigrated

- `APP_ROLE`, `BABEL_DEFAULT_LOCALE`, `CELERY_*`, `GOOGLE_*`, `LOG_LEVEL/LOG_PATH`,
  `MAX_CONTENT_LENGTH_MB`, `MOEFLOW_BUILD_ID`, `POOL_TRANSFER_TEMP_DIR`,
  `THUMBNAIL_SYNC_FALLBACK`, `OSS_PROCESS_COVER_NAME/RESAMPLE_NAME`,
  `EMAIL_ERROR_ADDRESS`, `EMAIL_REPLY_ADDRESS`.

## Unresolved deployment configuration

- `Security:TrustedProxies` must be set explicitly for nginx/X-Forwarded-* handling; Python had no equivalent.
- `Storage:Root` is required for local storage.
- `Bootstrap:Enabled`, `StoragePool:WorkerEnabled`, and `Notifications:WorkerEnabled` remain deployment-controlled.
- Real R2 authorization and SMTP delivery still need live validation with production-shaped non-secret config.