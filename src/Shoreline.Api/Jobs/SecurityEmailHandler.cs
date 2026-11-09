using System.Net;
using System.Net.Mail;
using System.Text;
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
        using var smtp = new SmtpClient(configuration["Mail:Host"] ?? throw new InvalidOperationException("Mail:Host is required"),
            configuration.GetValue("Mail:Port", 587)) { EnableSsl = configuration.GetValue("Mail:StartTls", true) };
        if (configuration["Mail:Username"] is { Length: > 0 } username)
            smtp.Credentials = new NetworkCredential(username, configuration["Mail:Password"]);
        var subject = code["t"].AsInt32 switch { 2 => "确认您的安全邮箱", 3 => "重置您的安全邮箱", 4 => "重置您的密码", _ => throw new InvalidOperationException("Unknown mail code type") };
        using var message = new MailMessage(configuration["Mail:From"] ?? "noreply@localhost", code["i"].AsString, subject,
            $"{configuration["Site:Name"] ?? "MoeFlow"}\n\n{subject}的验证码：{code["c"].AsString}\n\n有效期为48小时。若非本人操作，请忽略此邮件。")
        { BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(message, timeout.Token);
    }
}
