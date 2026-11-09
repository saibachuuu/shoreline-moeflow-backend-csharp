using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public sealed partial class LabelSnapshotService
{
    public sealed record MergeVersion(string Id, string Content);

    public sealed record MergeCandidate(
        string Key,
        string SourceId,
        long Rank,
        string SourceContent,
        string UserId,
        string UserName,
        IReadOnlyList<MergeVersion> Versions);

    public sealed record MergeConflict(string SourceId, IReadOnlyList<string> Users);

    public sealed record MergePreview(
        string Token,
        IReadOnlyList<MergeCandidate> Candidates,
        IReadOnlyList<MergeConflict> SelectionConflicts,
        int ProofreadCount,
        bool HasSourceContent)
    {
        public Dictionary<string, object?> ToApi() => new()
        {
            ["token"] = Token,
            ["candidates"] = Candidates.Select(candidate => new Dictionary<string, object?>
            {
                ["key"] = candidate.Key,
                ["source_id"] = candidate.SourceId,
                ["rank"] = candidate.Rank,
                ["source_content"] = candidate.SourceContent,
                ["user_id"] = candidate.UserId,
                ["user_name"] = candidate.UserName,
                ["versions"] = candidate.Versions.Select(version => new Dictionary<string, object?>
                {
                    ["id"] = version.Id,
                    ["content"] = version.Content
                }).ToList()
            }).ToList(),
            ["selection_conflicts"] = SelectionConflicts.Select(conflict =>
                new Dictionary<string, object?>
                {
                    ["source_id"] = conflict.SourceId,
                    ["users"] = conflict.Users
                }).ToList(),
            ["proofread_count"] = ProofreadCount,
            ["has_source_content"] = HasSourceContent
        };
    }

    public async Task<MergePreview> PreviewMergeAsync(
        BsonDocument actor,
        ObjectId projectId,
        ObjectId targetId,
        CancellationToken ct = default)
    {
        await using var session = await EnterAsync(projectId, recovery: false, expectedRevision: null, ct);
        await RequireCapabilityAsync(actor, session.Project, "merge", ct);
        return await PreviewMergeCoreAsync(projectId, targetId, ct);
    }

    public async Task<ObjectId> MergeLabelsAsync(
        BsonDocument actor,
        ObjectId projectId,
        ObjectId targetId,
        string? token,
        IReadOnlyDictionary<string, string?>? versions,
        IReadOnlyDictionary<string, string?>? selections,
        CancellationToken ct = default)
    {
        await using var session = await EnterAsync(projectId, recovery: false, expectedRevision: null, ct);
        await RequireCapabilityAsync(actor, session.Project, "merge", ct);

        var preview = await PreviewMergeCoreAsync(projectId, targetId, ct);
        if (!string.Equals(token, preview.Token, StringComparison.Ordinal))
            throw OperationError("预览后数据已改变，请重新确认冲突");

        var chosen = new List<(MergeCandidate Candidate, MergeVersion Option)>();
        var usersBySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var candidate in preview.Candidates)
        {
            string? selected = null;
            if (versions is not null && versions.TryGetValue(candidate.Key, out var requested))
                selected = requested;
            else if (candidate.Versions.Count == 1)
                selected = candidate.Versions[0].Id;

            var option = candidate.Versions.FirstOrDefault(version =>
                string.Equals(version.Id, selected, StringComparison.Ordinal));
            if (option is null) throw OperationError("请解决所有校对内容冲突");
            chosen.Add((candidate, option));
            if (!usersBySource.TryGetValue(candidate.SourceId, out var users))
                usersBySource[candidate.SourceId] = users = [];
            users.Add(candidate.UserId);
        }

        var winners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sourceId, users) in usersBySource)
        {
            string? winner = null;
            if (selections is not null && selections.TryGetValue(sourceId, out var requested))
                winner = requested;
            else if (users.Count == 1)
                winner = users[0];
            if (winner is null || !users.Contains(winner, StringComparer.Ordinal))
                throw OperationError("请选择每个标签最终选定的翻译");
            winners[sourceId] = winner;
        }

        var current = await LoadLabelDataAsync(projectId, ct);
        var sourceIds = current["sources"].Select(IdOf).ToList();
        var actorId = IdOf(actor);

        return await ProtectedOperationAsync(session, actor, "merge", async () =>
        {
            var now = Now();
            foreach (var (candidate, option) in chosen)
            {
                await Translations.UpdateOneAsync(
                    new BsonDocument
                    {
                        ["o"] = ObjectId.Parse(candidate.SourceId),
                        ["t"] = targetId,
                        ["u"] = ObjectId.Parse(candidate.UserId)
                    },
                    new BsonDocument
                    {
                        ["$set"] = new BsonDocument
                        {
                            ["c"] = option.Content,
                            ["mt"] = false,
                            ["e"] = now
                        },
                        ["$setOnInsert"] = new BsonDocument
                        {
                            ["ct"] = now,
                            ["s"] = false,
                            ["p"] = ""
                        }
                    },
                    new UpdateOptions { IsUpsert = true }, ct);
            }

            foreach (var (sourceId, winner) in winners)
            {
                var sourceObjectId = ObjectId.Parse(sourceId);
                var winnerObjectId = ObjectId.Parse(winner);
                await Translations.UpdateManyAsync(
                    new BsonDocument { ["o"] = sourceObjectId, ["t"] = targetId },
                    new BsonDocument
                    {
                        ["$set"] = new BsonDocument("s", false),
                        ["$unset"] = new BsonDocument("sr", "")
                    }, cancellationToken: ct);
                await Translations.UpdateOneAsync(
                    new BsonDocument
                    {
                        ["o"] = sourceObjectId,
                        ["t"] = targetId,
                        ["u"] = winnerObjectId
                    },
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["s"] = true,
                        ["sr"] = actorId
                    }), cancellationToken: ct);
            }

            await UpdateInBatchesAsync(sourceIds, async batch =>
            {
                var sources = new BsonDocument("$in", new BsonArray(batch));
                await Translations.UpdateManyAsync(
                    new BsonDocument { ["o"] = sources, ["t"] = targetId },
                    new BsonDocument
                    {
                        ["$set"] = new BsonDocument("p", ""),
                        ["$unset"] = new BsonDocument("pr", "")
                    }, cancellationToken: ct);
                await Translations.DeleteManyAsync(new BsonDocument
                {
                    ["o"] = sources,
                    ["t"] = targetId,
                    ["c"] = "",
                    ["s"] = new BsonDocument("$ne", true)
                }, ct);
            }, ct);
        }, ct);
    }

    private async Task<MergePreview> PreviewMergeCoreAsync(
        ObjectId projectId,
        ObjectId targetId,
        CancellationToken ct)
    {
        var target = await Targets.Find(new BsonDocument
        {
            ["_id"] = targetId,
            ["t"] = projectId
        }).FirstOrDefaultAsync(ct) ?? throw TargetNotExist();
        _ = target;

        var data = await LoadLabelDataAsync(projectId, ct);
        var rows = data["translations"]
            .Where(row => TryObjectId(row, "t", out var rowTarget) && rowTarget == targetId)
            .ToList();

        var groups = new Dictionary<(ObjectId Source, ObjectId User), List<BsonDocument>>();
        foreach (var row in rows)
        {
            var proofread = row.Text("p");
            var content = row.Text("c");
            if (proofread.Length == 0 || string.Equals(proofread, content, StringComparison.Ordinal))
                continue;
            if (!TryObjectId(row, "pr", out var userId))
                throw OperationError("校对者账号缺失，不能合并");
            if (!TryObjectId(row, "o", out var sourceId))
                throw OperationError("校对者账号缺失，不能合并");
            var key = (sourceId, userId);
            if (!groups.TryGetValue(key, out var revisions)) groups[key] = revisions = [];
            revisions.Add(row);
        }

        var allUsers = groups.Keys.Select(key => key.User).Distinct().ToList();
        var users = allUsers.Count == 0
            ? new List<BsonDocument>()
            : await ReadAllInAsync(store.Collection("user"), "_id", allUsers, ct);
        var userNameById = users.ToDictionary(IdOf, row => row.Text("n"));

        // Validate in encounter order, matching Python's per-row User lookup.
        foreach (var row in rows)
        {
            var proofread = row.Text("p");
            if (proofread.Length == 0 || string.Equals(proofread, row.Text("c"), StringComparison.Ordinal))
                continue;
            var userId = row["pr"].AsObjectId;
            if (!userNameById.ContainsKey(userId))
                throw OperationError("校对者账号缺失，不能合并");
        }

        var sourceById = data["sources"].ToDictionary(IdOf);
        var candidates = new List<MergeCandidate>();
        foreach (var ((sourceId, userId), revisions) in groups)
        {
            var unique = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
            foreach (var row in revisions)
            {
                var content = row.Text("p");
                if (!unique.ContainsKey(content)) unique[content] = row;
            }
            var source = sourceById[sourceId];
            candidates.Add(new MergeCandidate(
                $"{sourceId}:{userId}",
                sourceId.ToString(),
                source.Integer("r", 0),
                source.Text("c"),
                userId.ToString(),
                userNameById[userId],
                unique.Values.Select(row => new MergeVersion(
                    IdOf(row).ToString(), row.Text("p"))).ToList()));
        }

        var usersBySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!usersBySource.TryGetValue(candidate.SourceId, out var userIds))
                usersBySource[candidate.SourceId] = userIds = [];
            userIds.Add(candidate.UserId);
        }
        var conflicts = usersBySource
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => new MergeConflict(pair.Key, pair.Value))
            .ToList();

        return new MergePreview(
            DataDigest(data),
            candidates,
            conflicts,
            rows.Count(row => row.Text("p").Length > 0),
            data["sources"].Any(HasNonEmptyContent));
    }
}
