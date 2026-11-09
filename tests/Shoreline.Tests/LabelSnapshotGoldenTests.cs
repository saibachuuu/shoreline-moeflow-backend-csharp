using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Xunit;

namespace Shoreline.Tests;

/// <summary>
/// Golden evidence from the AST-extracted Python reference in
/// tools/legacy-reference/fixtures/goldens.json. Expected bytes, field order,
/// numeric types and the global digest are never reconstructed in C# here.
/// </summary>
[Collection("local-services")]
public sealed class LabelSnapshotGoldenTests(LocalServices services)
{
    [Fact]
    public void Python_golden_rows_match_writer_bytes_field_order_and_digest()
    {
        var snapshot = SnapshotFixture();
        var rows = SnapshotRows(snapshot);
        foreach (var kind in LabelSnapshotService.Kinds)
        {
            var expected = snapshot["row_bson"]![kind]!.AsArray();
            Assert.Equal(expected.Count, rows[kind].Count);
            for (var index = 0; index < expected.Count; index++)
            {
                var item = expected[index]!;
                var expectedBytes = Convert.FromBase64String(item["b64"]!.GetValue<string>());
                var document = rows[kind][index];
                Assert.Equal(expectedBytes, document.ToBson());
                Assert.Equal(item["byte_length"]!.GetValue<int>(), expectedBytes.Length);
                Assert.Equal(item["sha256"]!.GetValue<string>(), Sha256(expectedBytes));
                Assert.Equal(
                    item["field_order"]!.AsArray().Select(field => field!.GetValue<string>()).ToArray(),
                    document.Names.ToArray());
                foreach (var property in item["field_types"]!.AsObject())
                {
                    Assert.Equal(
                        ExpectedBsonType(property.Value!.GetValue<string>()),
                        document[property.Key].BsonType);
                }
            }
        }

        Assert.Equal(snapshot["metadata"]!["digest"]!.GetValue<string>(),
            LabelSnapshotService.DataDigestOf(rows));
    }

    [Fact]
    public void Python_golden_chunks_match_stored_hash_and_reconstruct_same_digest()
    {
        var snapshot = SnapshotFixture();
        var rows = LabelSnapshotService.Kinds.ToDictionary(
            kind => kind, _ => (IReadOnlyList<BsonDocument>)new List<BsonDocument>(),
            StringComparer.Ordinal);
        var chunks = snapshot["chunks"]!.AsArray();
        Assert.Equal(snapshot["chunk_count"]!.GetValue<int>(), chunks.Count);

        // The oracle used a 200-byte override to force multiple chunks. This is
        // fixture input, not the production 4 MiB constant.
        Assert.Equal(200, snapshot["chunk_bytes_override"]!.GetValue<int>());
        Assert.Equal(4 * 1024 * 1024, LabelSnapshotService.ChunkBytes);

        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            var chunk = chunks[chunkIndex]!;
            Assert.Equal(chunkIndex, chunk["index"]!.GetValue<int>());
            var bytes = Convert.FromBase64String(chunk["b64"]!.GetValue<string>());
            Assert.Equal(chunk["byte_length"]!.GetValue<int>(), bytes.Length);
            Assert.Equal(chunk["sha256"]!.GetValue<string>(), Sha256(bytes));
            Assert.Equal(chunk["stored_hash"]!.GetValue<string>(), Sha256(bytes));

            var payload = BsonSerializer.Deserialize<BsonDocument>(bytes);
            Assert.Single(payload.Names);
            Assert.Equal("rows", payload.Names.Single());
            var kind = chunk["kind"]!.GetValue<string>();
            var destination = (List<BsonDocument>)rows[kind];
            foreach (var row in payload["rows"].AsBsonArray)
                destination.Add(row.AsBsonDocument);
        }

        var expectedRows = SnapshotRows(snapshot);
        foreach (var kind in LabelSnapshotService.Kinds)
        {
            Assert.Equal(expectedRows[kind].Count, rows[kind].Count);
            for (var index = 0; index < expectedRows[kind].Count; index++)
                Assert.Equal(expectedRows[kind][index].ToBson(), rows[kind][index].ToBson());
        }
        Assert.Equal(snapshot["metadata"]!["digest"]!.GetValue<string>(),
            LabelSnapshotService.DataDigestOf(rows));
    }

    [Fact]
    public void Python_bson_typed_cases_preserve_types_and_byte_order()
    {
        var cases = Fixtures()["bson_typed"]!["cases"]!.AsArray();
        Assert.NotEmpty(cases);
        foreach (var item in cases)
        {
            var caseNode = item!;
            var expectedBytes = Convert.FromBase64String(caseNode["b64"]!.GetValue<string>());
            Assert.Equal(caseNode["hex"]!.GetValue<string>(), Convert.ToHexString(expectedBytes).ToLowerInvariant());
            Assert.Equal(caseNode["sha256"]!.GetValue<string>(), Sha256(expectedBytes));
            var document = BsonSerializer.Deserialize<BsonDocument>(expectedBytes);
            Assert.Equal(expectedBytes, document.ToBson());
            Assert.Equal(
                caseNode["field_order"]!.AsArray().Select(field => field!.GetValue<string>()).ToArray(),
                document.Names.ToArray());
            foreach (var property in caseNode["field_types"]!.AsObject())
            {
                Assert.Equal(
                    ExpectedBsonType(property.Value!.GetValue<string>()),
                    document[property.Key].BsonType);
            }
        }
    }

    [Fact]
    public async Task LoadLabelData_matches_python_golden_and_strips_label_epoch()
    {
        var snapshot = SnapshotFixture();
        var expectedRows = SnapshotRows(snapshot);
        var projectId = ObjectId.GenerateNewId();
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "golden-project"
        });

        foreach (var file in expectedRows["files"])
            await services.Store.Collection("file").InsertOneAsync(WithLink(file, "p", projectId));
        foreach (var target in expectedRows["targets"])
            await services.Store.Collection("target").InsertOneAsync(WithLink(target, "t", projectId));
        foreach (var source in expectedRows["sources"])
            await services.Store.Collection("source").InsertOneAsync(WithEpoch(source));
        foreach (var translation in expectedRows["translations"])
            await services.Store.Collection("translation").InsertOneAsync(WithEpoch(translation));
        foreach (var tip in expectedRows["tips"])
            await services.Store.Collection("tip").InsertOneAsync(WithEpoch(tip));

        var clock = TimeProvider.System;
        var service = new LabelSnapshotService(
            services.Store,
            clock,
            new IdentityPermissions(services.Store, clock),
            new ProjectLabelLock(services.Store, clock),
            new LabelRequestContext());
        var loaded = await service.LoadLabelDataAsync(projectId);

        foreach (var kind in LabelSnapshotService.Kinds)
        {
            Assert.Equal(expectedRows[kind].Count, loaded[kind].Count);
            for (var index = 0; index < expectedRows[kind].Count; index++)
                Assert.Equal(expectedRows[kind][index].ToBson(), loaded[kind][index].ToBson());
        }
        Assert.DoesNotContain(loaded["sources"], row => row.Contains("label_epoch"));
        Assert.DoesNotContain(loaded["translations"], row => row.Contains("label_epoch"));
        Assert.DoesNotContain(loaded["tips"], row => row.Contains("label_epoch"));
        Assert.Equal(snapshot["metadata"]!["digest"]!.GetValue<string>(),
            LabelSnapshotService.DataDigest(loaded));
    }

    private static BsonDocument WithLink(BsonDocument source, string field, ObjectId projectId)
    {
        var copy = new BsonDocument(source);
        copy[field] = projectId;
        return copy;
    }

    private static BsonDocument WithEpoch(BsonDocument source)
    {
        var copy = new BsonDocument(source);
        copy["label_epoch"] = 99L;
        return copy;
    }

    private static Dictionary<string, IReadOnlyList<BsonDocument>> SnapshotRows(JsonNode snapshot)
    {
        var result = new Dictionary<string, IReadOnlyList<BsonDocument>>(StringComparer.Ordinal);
        foreach (var kind in LabelSnapshotService.Kinds)
        {
            result[kind] = snapshot["row_bson"]![kind]!.AsArray()
                .Select(item => BsonSerializer.Deserialize<BsonDocument>(
                    Convert.FromBase64String(item!["b64"]!.GetValue<string>())))
                .ToList();
        }
        return result;
    }

    private static BsonType ExpectedBsonType(string expected) => expected.ToLowerInvariant() switch
    {
        "objectid" => BsonType.ObjectId,
        "string" => BsonType.String,
        "int32" => BsonType.Int32,
        "int64" => BsonType.Int64,
        "double" => BsonType.Double,
        "decimal128" => BsonType.Decimal128,
        "null" => BsonType.Null,
        "date" => BsonType.DateTime,
        "bool" => BsonType.Boolean,
        "array" => BsonType.Array,
        "object" => BsonType.Document,
        _ => throw new InvalidOperationException("Unknown golden BSON type " + expected)
    };

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonNode SnapshotFixture() => Fixtures()["label_snapshot"]!;

    private static JsonNode Fixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName,
                "tools", "legacy-reference", "fixtures", "goldens.json");
            if (File.Exists(candidate))
                return JsonNode.Parse(File.ReadAllText(candidate))!["fixtures"]!;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("goldens.json not found above " + AppContext.BaseDirectory);
    }
}
