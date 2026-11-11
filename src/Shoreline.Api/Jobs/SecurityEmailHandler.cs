using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Jobs;

public sealed class SecurityEmailHandler(MongoStore store, IConfiguration configuration, TimeProvider clock) : IJobHandler
{
    public string Kind => "security-email";

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        var row = await store.Collection("csharp_job").Find(new BsonDocument("_id", payload["code_job_id"])).FirstOrDefaultAsync(ct);
        if (row is null) return;
        var code = row["verification"].AsBsonDocument;
        if (code["consumed"].AsBoolean || code["w"].AsInt32 >= 5 || code["e"].ToUniversalTime() <= clock.GetUtcNow().UtcDateTime) return;
        if (!configuration.GetValue("Mail:Enabled", false)) throw new InvalidOperationException("Mail service disabled");

        var host = configuration["Mail:Host"] ?? throw new InvalidOperationException("Mail:Host is required");
        var port = configuration.GetValue("Mail:Port", 587);
        // Python selects implicit SMTPS solely from EMAIL_USE_SSL. Keep the 465
        // port heuristic for deployments that omit the toggle but expect SMTPS.
        var useSsl = configuration.GetValue("Mail:UseSsl", false) || port == 465;
        var startTls = configuration.GetValue<bool?>("Mail:StartTls", null);
        var socketOptions = useSsl
            ? SecureSocketOptions.SslOnConnect
            : startTls switch
            {
                true => SecureSocketOptions.StartTls,
                false => SecureSocketOptions.None,
                null => SecureSocketOptions.Auto
            };

        var subject = code["t"].AsInt32 switch
        {
            2 => "确认您的安全邮箱",
            3 => "重置您的安全邮箱",
            4 => "重置您的密码",
            _ => throw new InvalidOperationException("Unknown mail code type")
        };

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(configuration["Mail:From"] ?? "noreply@localhost"));
        message.To.Add(MailboxAddress.Parse(code["i"].AsString));
        message.Subject = subject;
        message.Body = new TextPart("plain")
        {
            Text = $"{configuration["Site:Name"] ?? "MoeFlow"}\n\n{subject}的验证码：{code["c"].AsString}\n\n有效期为48小时。若非本人操作，请忽略此邮件。"
        };

        // Never deliver a code the legacy `v_code` schema cannot see: a rollback to
        // the Python backend would otherwise reject a code we already emailed.
        // IssueAsync writes the job first and mirrors afterwards, so a crash in
        // between leaves a durable job with no mirror. Recreate it from the job's
        // own verification (same code, same expiry, no rotation); if a matching
        // mirror already exists it is left untouched. A failed write throws, which
        // makes the queue retry the same job and keeps the mail unsent.
        var mirror = await store.Collection("v_code")
            .Find(new BsonDocument { ["t"] = code["t"], ["i"] = code["i"] })
            .Sort(new BsonDocument("_id", 1))
            .FirstOrDefaultAsync(ct);
        var mirrorCode = mirror is not null && mirror.TryGetValue("c", out var existing) && existing.IsString
            ? existing.AsString
            : null;
        if (mirrorCode != code["c"].AsString)
        {
            await store.Collection("v_code").UpdateOneAsync(
                new BsonDocument { ["t"] = code["t"], ["i"] = code["i"] },
                Builders<BsonDocument>.Update.Set("c", code["c"]).Set("e", code["e"])
                    .Set("w", code["w"]).Set("s", code["s"]),
                new UpdateOptions { IsUpsert = true }, ct);
        }

        // Exactly-once send fence: claim the send on the job's own verification
        // document BEFORE touching SMTP. A redelivered job observes
        // sent == true and skips, so a crash between Send and queue Complete
        // never resends (the code stays valid for 48h). Pre-existing rows have
        // no sent field and match $ne: true, so they send exactly once.
        var sent = await store.Collection("csharp_job").UpdateOneAsync(
            new BsonDocument { ["_id"] = row["_id"], ["verification.sent"] = new BsonDocument("$ne", true) },
            Builders<BsonDocument>.Update.Set("verification.sent", true), cancellationToken: ct);
        if (sent.MatchedCount != 1) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(host, port, socketOptions, timeout.Token);
            if (configuration["Mail:Username"] is { Length: > 0 } username)
            {
                await client.AuthenticateAsync(username, configuration["Mail:Password"] ?? "", timeout.Token);
            }
            await client.SendAsync(message, timeout.Token);
            await client.DisconnectAsync(true, timeout.Token);
        }
        catch (Exception)
        {
            // The send did not go out on this attempt: release the claim so a
            // queue retry may send it. Best effort — if the reset itself fails,
            // the original send error still propagates and the job retries.
            try
            {
                await store.Collection("csharp_job").UpdateOneAsync(
                    new BsonDocument { ["_id"] = row["_id"], ["verification.sent"] = true },
                    Builders<BsonDocument>.Update.Set("verification.sent", false),
                    cancellationToken: CancellationToken.None);
            }
            catch { }
            throw;
        }
    }
}
