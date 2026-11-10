# Notification user-mail gate readiness

Scope: the C# notification mail gate only. This change fixes the user-mail
enable/disable OR bug and does not modify the security-code handler or the
code-issuing service; their behaviour is reviewed separately.

## Python oracle

`shoreline-moeflow-backend/app/services/notification_mail.py:25-51`
(`send_protected_mail`) selects exactly one switch:

```python
if not config.get("ENABLE_USER_EMAIL" if user_mail else "ENABLE_LOG_EMAIL"):
    return "skipped", "channel_disabled"
```

`ENABLE_USER_EMAIL` and `ENABLE_LOG_EMAIL` are independent channels. Log mail
is sent separately by `app/utils/logging.py:43-49` with `user_mail=False`.
They must never OR together.

## Bug and fix

Before the fix,
`src/Shoreline.Api/Notifications/ProtectedNotificationMail.cs:103-104` was:

```csharp
if (!settings.UserEmailEnabled && !settings.LogEmailEnabled)
    return ("skipped", "channel_disabled", "");
```

That allowed user notification mail whenever log mail was enabled, even if
`ENABLE_USER_EMAIL`/`Notifications:UserEmailEnabled` was false.

The fixed gate takes an explicit channel
(`src/Shoreline.Api/Notifications/ProtectedNotificationMail.cs:111-125`):

```csharp
public static (string Status, string Reason, string Address) Check(
    NotificationMailMessage message,
    NotificationMailSettings settings,
    NotificationMailChannel channel = NotificationMailChannel.User)
{
    var channelEnabled = channel switch
    {
        NotificationMailChannel.User => settings.UserEmailEnabled,
        NotificationMailChannel.Log => settings.LogEmailEnabled,
        _ => false
    };
    if (!channelEnabled)
        return ("skipped", "channel_disabled", "");
    ...
}
```

`MailKitNotificationTransport.SendAsync` passes
`NotificationMailChannel.User` explicitly. The existing allowlist, address,
SMTP-configuration, and header-injection checks are unchanged.

## Call-site audit

Production call sites of `ProtectedNotificationMail.Check`:

| Call site | Channel | Why |
| --- | --- | --- |
| `src/Shoreline.Api/Notifications/ProtectedNotificationMail.cs:177-178` | User | MailKit transport used by notification dispatch and proofread send; no log caller exists in C#. |
| `src/Shoreline.Api/Features/ProofreadFeedbackService.cs:187-190` | User | Proofread draft mail is sent to translators; Python uses the default `user_mail=True` here. |

`NotificationPublishService.EmailReasonAsync` already checks
`UserEmailEnabled` before queueing user delivery, so notification dispatch
does not expose a log-channel bypass. The enforcement point that was broken
was the protected transport/proofread gate itself.

## Configuration compatibility

`NotificationMailSettings.FromConfiguration` keeps both the new nested keys and
the legacy environment-compatible keys:

- User: `Notifications:UserEmailEnabled` fallback `ENABLE_USER_EMAIL`.
- Log: `Notifications:LogEmailEnabled` fallback `ENABLE_LOG_EMAIL`.

Both use the existing bool parsing (`True`/`true`/`1`). No key was renamed or
removed, and log mail remains a separate switch. C# currently has no log-mail
sender; the log channel is represented by the gate/config contract and is
exercised by tests. Runtime log email remains the Python logging path until a
C# log sender is added and reviewed.

## Switch matrix

Status is always `skipped` with reason `channel_disabled` when the selected
channel is off.

| User switch | Log switch | User gate | Log gate |
| --- | --- | --- | --- |
| on | on | ok | ok |
| on | off | ok | skipped / channel_disabled |
| off | on | skipped / channel_disabled | ok |
| off | off | skipped / channel_disabled | skipped / channel_disabled |

## Test coverage

Added or extended in `tests/Shoreline.Tests/NotificationPublishTests.cs`:

- `MailGate_SelectsUserAndLogSwitchesIndependently` covers all four user/log
  switch combinations for both channels.
- `MailSettings_ReadsUserAndLogSwitchesFromLegacyAndNewKeys` verifies legacy
  and nested configuration precedence.
- `ProtectedTransport_DoesNotSendUserMailWhenOnlyLogChannelIsEnabled` calls
  the real `MailKitNotificationTransport` with user off/log on and asserts
  `skipped/channel_disabled` before any SMTP connect.
- `ProtectedTransport_RejectsRecipientOutsideAllowlistWithoutSending` calls
  the real transport with an unauthorized recipient and asserts
  `skipped/recipient_not_allowed` before any SMTP connect.
- Existing `MailGate_EnforcesAllowlistHeadersAndTls` assertions are preserved
  and extended with an explicit log-channel disabled assertion.

Added in `tests/Shoreline.Tests/ProofreadFeedbackTests.cs`:

- `Send_UserMailDisabledWithLogMailEnabledDoesNotSendProofreadDraft` proves the
  proofread direct-send path is gated as user mail and never reaches the fake
  transport when only log mail is enabled.

Protected delivery through the dispatch path is covered by the existing
fake-transport delivery tests (`Deliver_RetriesThenAcceptsAndRecordsBoundary`,
`Deliver_RevokedBeforeSmtpDoesNotSend`, etc.). The accepted-delivery tests use
the fake transport rather than Mailpit because the production MailKit transport
always requires STARTTLS and the disposable Mailpit SMTP listener does not
offer STARTTLS; a real Mailpit accepted mail test would need either TLS
termination or a test-only TLS bypass, and no production security option was
weakened for this change.

## Verification evidence

- Worktree: `.readiness-worktrees/moeflow-readiness-mailgate`, branch
  `fix/readiness-notification-mail-gate-20261110`, based on `2aaaa85`.
- Disposable services (Compose project
  `shoreline-readiness-mailgate-20261110`): Mongo `27271`, S3 `29271`, SMTP
  `21271`, Mailpit UI `28271`, API `25271`, explicit non-dotenv env file.
- Build: 0 warnings, 0 errors.
- Focused tests (`NotificationPublishTests` + `ProofreadFeedbackTests`):
  74 passed, 0 failed, 0 skipped.
- LSP: `documentSymbol`, `hover`, and `findReferences` succeeded in the
  worktree after restore.

## Limits

- No C# log-mail sender exists; the log gate is tested but not wired to a
  runtime log channel.
- Security-code handler and code-issuing service were not changed.
- The accepted MailKit path was not exercised against Mailpit for the TLS
  reason above; accepted delivery through the notification service uses the
  fake transport.
