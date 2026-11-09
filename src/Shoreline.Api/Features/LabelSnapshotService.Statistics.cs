using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public sealed partial class LabelSnapshotService
{
    private sealed class FileCounts
    {
        public long Files { get; set; }
        public long Folders { get; set; }
        public long FileSize { get; set; }
        public long Sources { get; set; }
        public long Translated { get; set; }
        public long Checked { get; set; }

        public BsonDocument ToBson() => new()
        {
            ["fo"] = Folders,
            ["fc"] = Files,
            ["fs"] = FileSize,
            ["sc"] = Sources,
            ["tsc"] = Translated,
            ["csc"] = Checked
        };
    }

    /// <summary>
    /// Port of project_statistics.rebuild_project_statistics. Counts are rebuilt
    /// from primary rows, never from previous rollups. The final KB value uses
    /// banker's rounding on integers, so large files do not lose precision
    /// through a double.
    /// </summary>
    private async Task RebuildProjectStatisticsAsync(ObjectId projectId, CancellationToken ct)
    {
        var project = await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct)
                      ?? throw ProjectNotExist();
        var revision = project.Integer("label_revision", 0);

        var files = await FindAllAsync(Files, new BsonDocument("p", projectId), ct);
        var byId = files.ToDictionary(IdOf);
        var fileIds = byId.Keys.ToList();
        var targets = await FindAllAsync(Targets, new BsonDocument("t", projectId), ct);
        var targetIds = targets.Select(IdOf).ToList();
        var targetSet = targetIds.ToHashSet();

        var sources = fileIds.Count == 0
            ? new List<BsonDocument>()
            : await ReadAllInAsync(Sources, "f", fileIds, ct);
        var bySource = sources.ToDictionary(IdOf);
        var sourceIds = bySource.Keys.ToList();

        var translated = new Dictionary<(ObjectId File, ObjectId Target), HashSet<ObjectId>>();
        var checkedSources = new Dictionary<(ObjectId File, ObjectId Target), HashSet<ObjectId>>();
        if (sourceIds.Count > 0)
        {
            var translations = await ReadAllInAsync(Translations, "o", sourceIds, ct);
            foreach (var row in translations)
            {
                if (!TryObjectId(row, "o", out var sourceId) ||
                    !bySource.TryGetValue(sourceId, out var source)) continue;
                if (source.Flag("b") || !TryObjectId(row, "t", out var targetId) ||
                    !targetSet.Contains(targetId)) continue;
                if (!TryObjectId(source, "f", out var fileId)) continue;

                var key = (fileId, targetId);
                if (!translated.TryGetValue(key, out var rows))
                    translated[key] = rows = [];
                rows.Add(sourceId);
                if (row.Flag("s"))
                {
                    if (!checkedSources.TryGetValue(key, out var checkedRows))
                        checkedSources[key] = checkedRows = [];
                    checkedRows.Add(sourceId);
                }
            }
        }

        var counts = byId.Keys.ToDictionary(id => id, _ => new FileCounts());
        var sourceCounts = new Dictionary<ObjectId, long>();
        foreach (var row in sources)
        {
            if (row.Flag("b") || !TryObjectId(row, "f", out var fileId)) continue;
            sourceCounts[fileId] = sourceCounts.GetValueOrDefault(fileId) + 1;
        }

        var cacheCounts = new Dictionary<(ObjectId File, ObjectId Target), (long Translated, long Checked)>();
        var totals = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["fo"] = 0,
            ["fc"] = 0,
            ["fs"] = 0,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["tc"] = targets.Count
        };
        var targetTotals = targetIds.ToDictionary(id => id, _ => (Translated: 0L, Checked: 0L));

        foreach (var row in files)
        {
            var fileId = IdOf(row);
            if (row.Integer("t", 0) == 1)
            {
                if (Activated(row))
                {
                    totals["fo"]++;
                    foreach (var ancestor in Ancestors(row))
                    {
                        if (counts.TryGetValue(ancestor, out var ancestorCounts))
                            ancestorCounts.Folders++;
                    }
                }
                continue;
            }

            var own = counts[fileId];
            own.FileSize = row.Integer("fs", 0);
            own.Sources = sourceCounts.GetValueOrDefault(fileId);
            foreach (var targetId in targetIds)
            {
                var key = (fileId, targetId);
                var values = (
                    Convert.ToInt64(translated.GetValueOrDefault(key)?.Count ?? 0),
                    Convert.ToInt64(checkedSources.GetValueOrDefault(key)?.Count ?? 0));
                cacheCounts[key] = values;
                own.Translated += values.Item1;
                own.Checked += values.Item2;
                if (Activated(row))
                {
                    var targetTotal = targetTotals[targetId];
                    targetTotals[targetId] = (
                        targetTotal.Translated + values.Item1,
                        targetTotal.Checked + values.Item2);
                    foreach (var ancestor in Ancestors(row))
                    {
                        if (!byId.ContainsKey(ancestor)) continue;
                        var ancestorKey = (ancestor, targetId);
                        var rollup = cacheCounts.GetValueOrDefault(ancestorKey);
                        cacheCounts[ancestorKey] = (
                            rollup.Translated + values.Item1,
                            rollup.Checked + values.Item2);
                    }
                }
            }

            if (!Activated(row)) continue;
            totals["fc"]++;
            totals["sc"] += own.Sources;
            totals["tsc"] += own.Translated;
            totals["csc"] += own.Checked;
            foreach (var ancestor in Ancestors(row))
            {
                if (!counts.TryGetValue(ancestor, out var ancestorCounts))
                    throw new InvalidOperationException("文件引用了不存在的祖先目录");
                ancestorCounts.Files++;
                ancestorCounts.FileSize += own.FileSize;
                ancestorCounts.Sources += own.Sources;
                ancestorCounts.Translated += own.Translated;
                ancestorCounts.Checked += own.Checked;
            }
        }

        var stored = files
            .Where(row => row.Integer("t", 0) != 1 && Truthy(row.GetValue("sa", BsonNull.Value)))
            .ToList();
        var coverBytes = stored.Sum(row => row.Integer("cover_bytes", 0));
        var resampleBytes = stored.Sum(row => row.Integer("resample_bytes", 0));
        var outputBytes = await SumOutputBytesAsync(projectId, ct);
        var originalBytes = stored.Sum(row => row.Integer("fs", 0) * 1024L);
        var totalBytes = originalBytes + coverBytes + resampleBytes + outputBytes;

        totals["cover_bytes"] = coverBytes;
        totals["resample_bytes"] = resampleBytes;
        totals["ob"] = outputBytes;
        totals["fs"] = DivideRoundToEven(totalBytes, 1024);

        await ApplyStatisticsAsync(files, counts, byId, targetIds, cacheCounts,
            targetTotals, totals, revision, projectId, ct);
    }

    private async Task ApplyStatisticsAsync(
        IReadOnlyList<BsonDocument> files,
        IReadOnlyDictionary<ObjectId, FileCounts> counts,
        IReadOnlyDictionary<ObjectId, BsonDocument> byId,
        IReadOnlyList<ObjectId> targetIds,
        IReadOnlyDictionary<(ObjectId File, ObjectId Target), (long Translated, long Checked)> cacheCounts,
        IReadOnlyDictionary<ObjectId, (long Translated, long Checked)> targetTotals,
        IReadOnlyDictionary<string, long> totals,
        long revision,
        ObjectId projectId,
        CancellationToken ct)
    {
        var fileUpdates = new List<WriteModel<BsonDocument>>();
        foreach (var (fileId, values) in counts)
        {
            fileUpdates.Add(new UpdateOneModel<BsonDocument>(
                new BsonDocument("_id", fileId),
                new BsonDocument("$set", values.ToBson())));
            if (fileUpdates.Count >= MutationBatch)
            {
                await Files.BulkWriteAsync(fileUpdates, cancellationToken: ct);
                fileUpdates.Clear();
            }
        }
        if (fileUpdates.Count > 0) await Files.BulkWriteAsync(fileUpdates, cancellationToken: ct);

        var cacheUpdates = new List<WriteModel<BsonDocument>>();
        foreach (var fileId in byId.Keys)
        {
            foreach (var targetId in targetIds)
            {
                var values = cacheCounts.GetValueOrDefault((fileId, targetId));
                cacheUpdates.Add(new UpdateOneModel<BsonDocument>(
                    new BsonDocument { ["f"] = fileId, ["t"] = targetId },
                    new BsonDocument
                    {
                        ["$set"] = new BsonDocument
                        {
                            ["ts"] = values.Translated,
                            ["cs"] = values.Checked
                        },
                        ["$setOnInsert"] = new BsonDocument("label_epoch", revision)
                    }) { IsUpsert = true });
                if (cacheUpdates.Count >= MutationBatch)
                {
                    await FileTargetCaches.BulkWriteAsync(cacheUpdates, cancellationToken: ct);
                    cacheUpdates.Clear();
                }
            }
        }
        if (cacheUpdates.Count > 0)
            await FileTargetCaches.BulkWriteAsync(cacheUpdates, cancellationToken: ct);

        var targetUpdates = new List<WriteModel<BsonDocument>>();
        foreach (var targetId in targetIds)
        {
            var values = targetTotals.GetValueOrDefault(targetId);
            targetUpdates.Add(new UpdateOneModel<BsonDocument>(
                new BsonDocument("_id", targetId),
                new BsonDocument("$set", new BsonDocument
                {
                    ["tsc"] = values.Translated,
                    ["csc"] = values.Checked
                })));
        }
        if (targetUpdates.Count > 0)
            await Targets.BulkWriteAsync(targetUpdates, cancellationToken: ct);

        await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
            new BsonDocument("$set", new BsonDocument(totals.Select(pair =>
                new BsonElement(pair.Key, pair.Value)))), cancellationToken: ct);
    }

    private async Task<long> SumOutputBytesAsync(ObjectId projectId, CancellationToken ct)
    {
        var rows = await store.Collection("output").Find(new BsonDocument
        {
            ["p"] = projectId,
            ["file_name"] = new BsonDocument("$gt", "")
        }).Project(new BsonDocument("sb", 1)).ToListAsync(ct);
        return rows.Sum(row => row.Integer("sb", 0));
    }

    private static async Task<List<BsonDocument>> FindAllAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument filter,
        CancellationToken ct)
    {
        using var cursor = await collection.FindAsync(filter,
            new FindOptions<BsonDocument> { BatchSize = 1000 }, ct);
        var result = new List<BsonDocument>();
        while (await cursor.MoveNextAsync(ct)) result.AddRange(cursor.Current);
        result.Sort((left, right) => IdOf(left).CompareTo(IdOf(right)));
        return result;
    }

    private static bool Activated(BsonDocument row) =>
        !row.TryGetValue("ac", out var value) || Truthy(value);

    private static IEnumerable<ObjectId> Ancestors(BsonDocument row)
    {
        if (!row.TryGetValue("a", out var value) || !value.IsBsonArray) yield break;
        foreach (var item in value.AsBsonArray)
        {
            if (item.IsObjectId) yield return item.AsObjectId;
        }
    }

    private static bool TryObjectId(BsonDocument row, string field, out ObjectId id)
    {
        if (row.TryGetValue(field, out var value) && value.IsObjectId)
        {
            id = value.AsObjectId;
            return true;
        }
        id = ObjectId.Empty;
        return false;
    }

    private static bool Truthy(BsonValue value) => value switch
    {
        null => false,
        BsonNull => false,
        BsonBoolean boolean => boolean.Value,
        BsonString text => text.Value.Length > 0,
        BsonObjectId => true,
        BsonArray array => array.Count > 0,
        BsonDocument document => document.ElementCount > 0,
        BsonInt32 number => number.Value != 0,
        BsonInt64 number => number.Value != 0,
        BsonDouble number => number.Value != 0,
        _ => true
    };

    /// <summary>
    /// Python's round(total_bytes / 1024) is banker's rounding. Integer math
    /// avoids both the default AwayFromZero mode and double precision loss.
    /// </summary>
    private static long DivideRoundToEven(long dividend, long divisor)
    {
        if (divisor <= 0) throw new ArgumentOutOfRangeException(nameof(divisor));
        var quotient = Math.DivRem(dividend, divisor, out var remainder);
        if (remainder == 0) return quotient;
        var twice = remainder * 2;
        if (twice < divisor) return quotient;
        if (twice > divisor) return quotient + 1;
        return quotient % 2 == 0 ? quotient : quotient + 1;
    }
}
