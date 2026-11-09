using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Notifications;

namespace Shoreline.Api.Features;

public sealed partial class NotificationPublishService
{
    private static readonly string[] DueNoteStates = ["scheduled", "preparing", "dispatching"];
    private static readonly string[] RetryableDeliveryStates = ["pending", "retry_wait"];

    // ------------------------------------------------------------------
    // Scan (replacement for the Celery beat recovery task)
    // ------------------------------------------------------------------

    public async Task ScanAsync(CancellationToken ct)
    {
        if (!Read.Enabled) return;
        var now = Read.UtcNow;

        await _deliveries.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("state", "leased")
            & Builders<BsonDocument>.Filter.Lte("lease_until", now),
            Builders<BsonDocument>.Update.Set("state", "unknown").Set("reason", "lease_expired"),
            cancellationToken: ct);

        var started = Stopwatch.GetTimestamp();
        var notes = await _notifications.Find(
                Builders<BsonDocument>.Filter.In("state", DueNoteStates)
                & Builders<BsonDocument>.Filter.Eq("revoked_at", BsonNull.Value)
                & Builders<BsonDocument>.Filter.Lte("publish_at", now)
                & Builders<BsonDocument>.Filter.Lte("lease_until", now))
            .Sort(Builders<BsonDocument>.Sort.Ascending("publish_at"))
            .Limit(10)
            .ToListAsync(ct);
        foreach (var note in notes)
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 45) break;
            await DispatchAsync(note["_id"].AsObjectId, ct);
        }

        var deliveries = await _deliveries.Find(
                Builders<BsonDocument>.Filter.In("state", RetryableDeliveryStates)
                & Builders<BsonDocument>.Filter.Lte("next_attempt_at", now))
            .Sort(Builders<BsonDocument>.Sort.Ascending("next_attempt_at"))
            .Limit(50)
            .ToListAsync(ct);
        foreach (var delivery in deliveries)
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 75) break;
            await DeliverAsync(delivery["_id"].AsObjectId, ct);
        }

        var revoked = await _notifications.Find(
                Builders<BsonDocument>.Filter.Ne("revoked_at", BsonNull.Value)
                & Builders<BsonDocument>.Filter.Eq("moderation_audited", false))
            .Sort(Builders<BsonDocument>.Sort.Ascending("revoked_at"))
            .Limit(100)
            .ToListAsync(ct);
        foreach (var note in revoked)
        {
            var actorId = NullableId(note, "revoked_by") ?? ObjectId.Empty;
            var action = note.Text("moderation_action", "revoke");
            await Read.AuditAsync(actorId, action, note["_id"].AsObjectId,
                new BsonDocument("reason", note.Text("revoke_reason")), note.Text("moderation_key") + ":revoke", ct);
            await _notifications.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", note["_id"]),
                Builders<BsonDocument>.Update.Set("moderation_audited", true), cancellationToken: ct);
        }
    }

    // ------------------------------------------------------------------
    // Dispatch: prepare durable audience chunks, then fan out receipts
    // ------------------------------------------------------------------

    public async Task DispatchAsync(ObjectId notificationId, CancellationToken ct)
    {
        var now = Read.UtcNow;
        var token = Guid.NewGuid().ToString("D");
        var note = await _notifications.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", notificationId)
            & Builders<BsonDocument>.Filter.Eq("revoked_at", BsonNull.Value)
            & Builders<BsonDocument>.Filter.In("state", DueNoteStates)
            & Builders<BsonDocument>.Filter.Lte("publish_at", now)
            & Builders<BsonDocument>.Filter.Lte("lease_until", now),
            Builders<BsonDocument>.Update.Set("lease_token", token).Set("lease_until", now.AddSeconds(120)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
            ct);
        if (note is null) return;

        FilterDefinition<BsonDocument> Guard() =>
            Builders<BsonDocument>.Filter.Eq("_id", notificationId)
            & Builders<BsonDocument>.Filter.Eq("lease_token", token)
            & Builders<BsonDocument>.Filter.Eq("revoked_at", BsonNull.Value);

        try
        {
            if (!note.Flag("publish_audited"))
            {
                await Read.AuditAsync(note["actor_id"].AsObjectId, "publish", notificationId, null,
                    note.Text("key") + ":publish", ct);
                await _notifications.UpdateOneAsync(Guard(),
                    Builders<BsonDocument>.Update.Set("publish_audited", true), cancellationToken: ct);
            }

            if (note.TryGetValue("expires_at", out var expiry) && expiry.IsBsonDateTime &&
                expiry.AsBsonDateTime.ToUniversalTime() <= now)
            {
                await _notifications.UpdateOneAsync(Guard(),
                    Builders<BsonDocument>.Update.Set("state", "cancelled").Set("error", "expired"),
                    cancellationToken: ct);
                return;
            }

            if (note.Text("state") == "scheduled")
            {
                if (now - note["publish_at"].AsBsonDateTime.ToUniversalTime() > TimeSpan.FromHours(1))
                {
                    await _notifications.UpdateOneAsync(Guard(),
                        Builders<BsonDocument>.Update.Set("state", "draft").Set("error", "schedule_late"),
                        cancellationToken: ct);
                    return;
                }
                var prepared = await _notifications.UpdateOneAsync(Guard(),
                    Builders<BsonDocument>.Update.Set("state", "preparing"), cancellationToken: ct);
                if (prepared.MatchedCount == 0) return;
                note["state"] = "preparing";
            }

            if (!await AuthorAllowedAsync(note, ct))
            {
                await _notifications.UpdateOneAsync(Guard(),
                    Builders<BsonDocument>.Update.Set("state", "cancelled").Set("error", "sender_permission_lost"),
                    cancellationToken: ct);
                return;
            }

            var valid = await RecipientIdsAsync(note, ct);

            if (note.Text("state") == "preparing")
            {
                for (var index = 0; index < 4; index++)
                {
                    var number = note.Integer("next_chunk");
                    var chunk = await _chunks.Find(
                        Builders<BsonDocument>.Filter.Eq("notification_id", notificationId)
                        & Builders<BsonDocument>.Filter.Eq("number", number)).FirstOrDefaultAsync(ct);
                    if (chunk is null)
                    {
                        var cursor = note.Text("cursor");
                        var users = valid
                            .Where(id => cursor.Length == 0 ||
                                         string.CompareOrdinal(id.ToString(), cursor) > 0)
                            .Take(100)
                            .ToList();
                        if (users.Count == 0)
                        {
                            var changed = await _notifications.UpdateOneAsync(
                                Guard() & Builders<BsonDocument>.Filter.Eq("state", "preparing"),
                                Builders<BsonDocument>.Update.Set("state", "dispatching"),
                                cancellationToken: ct);
                            if (changed.MatchedCount == 0) return;
                            note["state"] = "dispatching";
                            break;
                        }

                        try
                        {
                            await _chunks.UpdateOneAsync(
                                Builders<BsonDocument>.Filter.Eq("notification_id", notificationId)
                                & Builders<BsonDocument>.Filter.Eq("number", number),
                                Builders<BsonDocument>.Update
                                    .SetOnInsert("users", new BsonArray(users.Select(id => (BsonValue)id)))
                                    .SetOnInsert("cursor", users[^1].ToString())
                                    .SetOnInsert("done", false),
                                new UpdateOptions { IsUpsert = true },
                                ct);
                        }
                        catch (Exception error) when (IsDuplicateKey(error))
                        {
                            // A concurrent worker created the same durable chunk; reuse it.
                        }

                        chunk = await _chunks.Find(
                            Builders<BsonDocument>.Filter.Eq("notification_id", notificationId)
                            & Builders<BsonDocument>.Filter.Eq("number", number)).FirstOrDefaultAsync(ct);
                        if (chunk is null) return;
                    }

                    var savedCursor = chunk.Text("cursor");
                    var savedUsers = chunk["users"].AsBsonArray.Count;
                    var advanced = await _notifications.UpdateOneAsync(
                        Guard()
                        & Builders<BsonDocument>.Filter.Eq("state", "preparing")
                        & Builders<BsonDocument>.Filter.Eq("next_chunk", number),
                        Builders<BsonDocument>.Update
                            .Set("cursor", savedCursor)
                            .Inc("next_chunk", 1)
                            .Inc("recipient_count", savedUsers),
                        cancellationToken: ct);
                    if (advanced.MatchedCount == 0) return;

                    note["cursor"] = savedCursor;
                    note["next_chunk"] = number + 1;
                    note["recipient_count"] = note.Integer("recipient_count") + savedUsers;
                }
            }

            if (note.Text("state") != "dispatching") return;

            var allowed = valid.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
            var chunks = await _chunks.Find(
                    Builders<BsonDocument>.Filter.Eq("notification_id", notificationId)
                    & Builders<BsonDocument>.Filter.Eq("done", false))
                .Sort(Builders<BsonDocument>.Sort.Ascending("number"))
                .Limit(4)
                .ToListAsync(ct);

            foreach (var chunk in chunks)
            {
                foreach (var value in chunk["users"].AsBsonArray)
                {
                    if (!value.IsObjectId) continue;
                    if (await _notifications.Find(
                            Guard() & Builders<BsonDocument>.Filter.Eq("state", "dispatching"))
                        .FirstOrDefaultAsync(ct) is null)
                        return;

                    var userId = value.AsObjectId;
                    if (!allowed.Contains(userId.ToString())) continue;
                    await CreateReceiptAsync(notificationId, userId, note.Flag("requested_email"), now, ct);
                }

                // The old worker marked chunks done without an owner check; keep the
                // lease check here so a preempted worker cannot resume fan-out.
                if (await _notifications.Find(
                        Guard() & Builders<BsonDocument>.Filter.Eq("state", "dispatching"))
                    .FirstOrDefaultAsync(ct) is null)
                    return;
                await _chunks.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", chunk["_id"])
                    & Builders<BsonDocument>.Filter.Eq("done", false),
                    Builders<BsonDocument>.Update.Set("done", true), cancellationToken: ct);
            }

            var remaining = await _chunks.Find(
                    Builders<BsonDocument>.Filter.Eq("notification_id", notificationId)
                    & Builders<BsonDocument>.Filter.Eq("done", false))
                .FirstOrDefaultAsync(ct);
            if (remaining is null)
            {
                var count = await _receipts.CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.Eq("notification_id", notificationId), cancellationToken: ct);
                await _notifications.UpdateOneAsync(
                    Guard() & Builders<BsonDocument>.Filter.Eq("state", "dispatching"),
                    Builders<BsonDocument>.Update.Set("state", "published").Set("recipient_count", count),
                    cancellationToken: ct);
            }
        }
        catch (ApiError error) when (error.Error == NotificationErrors.ErrorName)
        {
            await _notifications.UpdateOneAsync(Guard(),
                Builders<BsonDocument>.Update.Set("state", "cancelled").Set("error", "invalid_audience"),
                cancellationToken: ct);
        }
        finally
        {
            await _notifications.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", notificationId)
                & Builders<BsonDocument>.Filter.Eq("lease_token", token),
                Builders<BsonDocument>.Update.Set("lease_until", Read.UtcNow).Set("lease_token", ""),
                cancellationToken: ct);
        }
    }

    private async Task CreateReceiptAsync(
        ObjectId notificationId, ObjectId userId, bool requestedEmail, DateTime now, CancellationToken ct)
    {
        await _receipts.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("notification_id", notificationId)
            & Builders<BsonDocument>.Filter.Eq("user_id", userId),
            Builders<BsonDocument>.Update
                .SetOnInsert("created_at", now)
                .SetOnInsert("archived", false),
            new UpdateOptions { IsUpsert = true },
            ct);

        if (!requestedEmail) return;
        await _deliveries.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("notification_id", notificationId)
            & Builders<BsonDocument>.Filter.Eq("user_id", userId),
            Builders<BsonDocument>.Update
                .SetOnInsert("state", "pending")
                .SetOnInsert("attempts", 0)
                .SetOnInsert("next_attempt_at", now)
                .SetOnInsert("lease_until", now)
                .SetOnInsert("message_id", $"<notification.{notificationId}.{userId}@moeflow.invalid>"),
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    // ------------------------------------------------------------------
    // Deliver: lease a pending delivery, re-check live permission, send
    // ------------------------------------------------------------------

    public async Task DeliverAsync(ObjectId deliveryId, CancellationToken ct)
    {
        var now = Read.UtcNow;
        var token = Guid.NewGuid().ToString("D");
        var delivery = await _deliveries.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", deliveryId)
            & Builders<BsonDocument>.Filter.In("state", RetryableDeliveryStates)
            & Builders<BsonDocument>.Filter.Lte("next_attempt_at", now),
            Builders<BsonDocument>.Update
                .Set("state", "leased")
                .Set("lease_token", token)
                .Set("lease_until", now.AddSeconds(120))
                .Inc("attempts", 1),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
            ct);
        if (delivery is null) return;

        var state = "skipped";
        var reason = "recipient_ineligible";

        var noteId = NullableId(delivery, "notification_id");
        var userId = NullableId(delivery, "user_id");
        var note = noteId is null
            ? null
            : await _notifications.Find(Builders<BsonDocument>.Filter.Eq("_id", noteId.Value)).FirstOrDefaultAsync(ct);
        var user = userId is null
            ? null
            : await _users.Find(Builders<BsonDocument>.Filter.Eq("_id", userId.Value)).FirstOrDefaultAsync(ct);

        if (note is null || !Read.IsLive(note))
        {
            state = "cancelled";
            reason = "revoked_or_expired";
        }
        else if (!await AuthorAllowedAsync(note, ct))
        {
            state = "cancelled";
            reason = "sender_permission_lost";
        }
        else if (user is not null && await RecipientEligibleAsync(note, user, ct))
        {
            reason = await EmailReasonAsync(user, note.Text("category"), ct);
            if (string.IsNullOrEmpty(reason))
            {
                var stillLive = await _notifications.Find(
                    Builders<BsonDocument>.Filter.Eq("_id", note["_id"])
                    & Builders<BsonDocument>.Filter.Eq("revoked_at", BsonNull.Value)
                    & Builders<BsonDocument>.Filter.In("state", new[] { "dispatching", "published" }))
                    .FirstOrDefaultAsync(ct);
                if (stillLive is null)
                {
                    state = "cancelled";
                    reason = "revoked";
                }
                else
                {
                    var message = BuildMailMessage(note, user, delivery);
                    var result = await Mail.SendAsync(message, ct);
                    state = result.Status;
                    reason = result.Reason;
                }
            }
        }

        var update = Builders<BsonDocument>.Update
            .Set("state", state)
            .Set("reason", reason)
            .Set("lease_token", "");
        if (state == "retry_wait")
        {
            var attempts = delivery.Integer("attempts");
            if (attempts >= 5)
            {
                state = "failed";
                update = Builders<BsonDocument>.Update
                    .Set("state", state)
                    .Set("reason", reason)
                    .Set("lease_token", "");
            }
            else
            {
                var delay = Math.Min(3600, 30 * Math.Pow(2, attempts));
                update = update.Set("next_attempt_at", now.AddSeconds(delay));
            }
        }
        if (state == "accepted")
            update = update.Set("accepted_at", Read.UtcNow);

        await _deliveries.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", deliveryId)
            & Builders<BsonDocument>.Filter.Eq("lease_token", token)
            & Builders<BsonDocument>.Filter.Eq("state", "leased"),
            update,
            cancellationToken: ct);
    }

    private NotificationMailMessage BuildMailMessage(BsonDocument note, BsonDocument user, BsonDocument delivery)
    {
        var category = note.Text("category");
        var url = SiteOrigin.TrimEnd('/') + "/dashboard/notifications/" + note["_id"].AsObjectId;
        var nodes = note["nodes"].AsBsonArray;
        var subject = "萌翻通知";
        var text = "您收到一条通知，请登录查看：" + url;
        string? html = null;
        string? replyTo = null;

        if (category == "system")
        {
            subject = note.Text("title");
            text = NotificationContent.Plain(nodes) + "\n" + url;
            html = NotificationContent.RenderHtml(nodes) + "<p>" + url + "</p>";
        }
        if (note.Text("event_type") == "proofread_feedback")
        {
            var payload = note["payload"].AsBsonDocument;
            subject = note.Text("title");
            text = payload.Text("text");
            html = payload.TryGetValue("html", out var htmlValue) && htmlValue.IsString ? htmlValue.AsString : null;
            replyTo = payload.TryGetValue("reply_to", out var replyValue) && replyValue.IsString ? replyValue.AsString : null;
        }

        return new NotificationMailMessage(
            user.Text("e"), subject, text, html, delivery.Text("message_id"), replyTo);
    }
}
