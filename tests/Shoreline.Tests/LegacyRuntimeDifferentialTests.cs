using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

// This test consumes the fixture produced by the Python Flask reference runtime.
// It seeds the fixture's own raw BSON into a randomly named LocalServices database,
// then runs the same HTTP cases against the C# API. It deliberately collects every
// difference instead of stopping at the first assertion failure.
public sealed class LegacyRuntimeDifferentialTests
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task CSharpRuntimeMatchesPythonReferenceBusinessPayloads()
    {
        var repoRoot = FindRepoRoot();
        var fixturePath = Path.Combine(repoRoot, "tools", "legacy-reference", "fixtures", "runtime_flask_reference.json");
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        var root = fixture.RootElement;
        var services = new LocalServices();
        await services.InitializeAsync();

        var referenceConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Storage:DefaultUserAvatar"] = "",
            ["Site:DefaultTeamAvatar"] = "",
            ["Pagination:DefaultLimit"] = "10",
            ["Pagination:MaxLimit"] = "100"
        };
        var factory = services.App.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(referenceConfiguration)));
        using var http = factory.CreateClient();

        var report = new DifferentialReport
        {
            Fixture = fixturePath,
            FixtureDatabase = root.GetProperty("database").GetProperty("name").GetString() ?? "",
            TestDatabase = services.DatabaseName,
            Configuration = referenceConfiguration,
            ConfigurationProvenance =
                "runtime_reference.py:203-204 synthetic DEFAULT_USER_AVATAR/DEFAULT_TEAM_AVATAR=''; " +
                "runtime_reference.py:224 APIKIT_DEFAULT_LIMIT=10/MAX=100; " +
                "mapped to C# Storage:DefaultUserAvatar, Site:DefaultTeamAvatar, Pagination:DefaultLimit/MaxLimit."
        };

        try
        {
            var seedCollections = root.GetProperty("seed_collections");
            var seedNames = seedCollections.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            await ClearAndSeedAsync(services, seedCollections, seedNames);

            report.Seed = await DatabaseDigestAsync(services, seedNames);
            var firstCase = root.GetProperty("cases")[0];
            CompareDigest(report, "seed", "$seed", firstCase.GetProperty("database_before"), report.Seed,
                "bson_seed", "The C# database does not match the Python fixture seed byte-for-byte.");

            var owner = await services.Store.Collection("user")
                .Find(new BsonDocument("_id", ObjectId.Parse("650000000000000000000101")))
                .FirstOrDefaultAsync() ?? throw new InvalidOperationException("fixture owner was not seeded");
            var ownerId = owner["_id"].AsObjectId.ToString();
            var token = factory.Services.GetRequiredService<LegacyToken>()
                .Issue(ownerId, owner.Text("p"));

            foreach (var item in root.GetProperty("cases").EnumerateArray())
            {
                await RunCaseAsync(report, services, http, item, token, seedNames);
            }
        }
        finally
        {
            await factory.DisposeAsync();
            await services.DisposeAsync();
        }

        report.Conclusion = report.BusinessDifferences.Count == 0
            ? "C# responses match the Python reference business payloads for the covered cases."
            : $"{report.BusinessDifferences.Count} business difference(s) require an owner decision; no production code was changed.";
        report.ReportPath = WriteReport(report, repoRoot);

        Assert.True(report.BusinessDifferences.Count == 0, FormatFailure(report));
    }

    private static async Task ClearAndSeedAsync(LocalServices services,
        JsonElement seedCollections, IReadOnlyList<string> seedNames)
    {
        // Startup initializes indexes and legacy language/team-role seed rows. Drop only
        // this test's own random database collections before installing the exact fixture.
        foreach (var name in seedNames)
        {
            await services.Store.Database.DropCollectionAsync(name);
        }

        foreach (var name in seedNames)
        {
            foreach (var document in seedCollections.GetProperty(name).GetProperty("documents").EnumerateArray())
            {
                var base64 = document.GetProperty("bson_base64").GetString()
                    ?? throw new InvalidOperationException($"{name}: bson_base64 missing");
                var raw = Convert.FromBase64String(base64);
                var parsed = BsonSerializer.Deserialize<BsonDocument>(raw);
                await services.Store.Collection(name).InsertOneAsync(parsed);
            }
        }
    }

    private static async Task RunCaseAsync(DifferentialReport report, LocalServices services, HttpClient http,
        JsonElement item, string token, IReadOnlyList<string> seedNames)
    {
        var name = item.GetProperty("name").GetString() ?? $"case-{item.GetProperty("index").GetInt32()}";
        var request = item.GetProperty("request");
        var method = request.GetProperty("method").GetString() ?? "GET";
        var path = request.GetProperty("path").GetString() ?? "/";
        var query = request.GetProperty("query");
        var url = BuildUrl(path, query);

        var caseReport = new CaseReport
        {
            Index = item.GetProperty("index").GetInt32(),
            Name = name,
            Method = method,
            Path = url
        };

        var before = await DatabaseDigestAsync(services, seedNames);
        var expectedBefore = item.GetProperty("database_before");
        var beforeEqual = CompareDigest(report, name, "$database_before", expectedBefore, before,
            "bson_before", "GET request baseline differs from the Python fixture seed.");

        using var message = new HttpRequestMessage(new HttpMethod(method), url);
        if (request.TryGetProperty("json", out var requestJson))
            message.Content = new StringContent(requestJson.GetRawText(), Encoding.UTF8, "application/json");
        if (request.TryGetProperty("accept_language", out var acceptLanguage) &&
            acceptLanguage.GetString() is { Length: > 0 } language)
            message.Headers.AcceptLanguage.ParseAdd(language);
        if (request.GetProperty("authorization").GetString() == "redacted")
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(message);
        var actualBody = await response.Content.ReadAsStringAsync();
        var actualHeaders = ReadHeaders(response);
        var status = (int)response.StatusCode;
        var expectedStatus = item.GetProperty("response").GetProperty("status").GetInt32();
        caseReport.StatusExpected = expectedStatus;
        caseReport.StatusActual = status;
        caseReport.ActualHeaders = actualHeaders;
        caseReport.ActualBody = actualBody;

        if (status != expectedStatus)
        {
            Add(report, name, "status", "$status", JsonSerializer.Serialize(expectedStatus, ReportJson),
                JsonSerializer.Serialize(status, ReportJson), "business",
                SourceFor(name, "$status"), "HTTP status differs from the Python reference.");
        }

        var expectedBody = item.GetProperty("response").GetProperty("body");
        var expectedKind = expectedBody.GetProperty("kind").GetString();
        var expectedValue = expectedBody.GetProperty("value");
        var bodyDifferencesBefore = report.BusinessDifferences.Count(d =>
            d.Case == name && d.Kind is "json" or "body");
        caseReport.BodyKind = expectedKind ?? "";

        if (expectedKind == "json")
        {
            try
            {
                using var actualJson = JsonDocument.Parse(actualBody);
                CompareJson(report, name, "$", expectedValue, actualJson.RootElement);
                caseReport.BodyEqual = report.BusinessDifferences.Count(d =>
                    d.Case == name && d.Kind is "json" or "body") == bodyDifferencesBefore;
            }
            catch (JsonException e)
            {
                caseReport.BodyEqual = false;
                Add(report, name, "body", "$", "valid JSON", actualBody, "business",
                    SourceFor(name, "$"), $"C# response was not valid JSON: {e.Message}");
            }
        }
        else
        {
            var equal = string.Equals(expectedValue.GetString(), actualBody, StringComparison.Ordinal);
            caseReport.BodyEqual = equal;
            if (!equal)
            {
                if (name == "method_not_allowed_ping_post")
                {
                    Add(report, name, "body", "$", expectedValue.GetString() ?? "", actualBody,
                        "expected-framework", SourceFor(name, "$"),
                        "ASP.NET Core emits its own 405 HTML body; status is still checked.");
                }
                else
                {
                    Add(report, name, "body", "$", expectedValue.GetString() ?? "", actualBody,
                        "business", SourceFor(name, "$"),
                        "Text response body differs from the Python reference.");
                }
            }
        }

        CompareHeaders(report, name, expectedBody, item.GetProperty("response").GetProperty("headers"), actualHeaders);

        var after = await DatabaseDigestAsync(services, seedNames);
        var expectedAfter = item.GetProperty("database_after");
        var afterEqual = CompareDigest(report, name, "$database_after", expectedAfter, after,
            "bson_after", "Request changed fixture data, or the C# database does not match the Python fixture after the request.");
        caseReport.DatabaseBeforeEqual = beforeEqual;
        caseReport.DatabaseAfterEqual = afterEqual;
        report.Cases.Add(caseReport);
    }

    private static void CompareHeaders(DifferentialReport report, string name,
        JsonElement expectedBody, JsonElement expectedHeaders, Dictionary<string, string> actual)
    {
        foreach (var header in expectedHeaders.EnumerateObject())
        {
            var key = header.Name;
            if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            actual.TryGetValue(key, out var actualValue);
            var expected = header.Value.GetString() ?? "";
            actualValue ??= "<missing>";
            if (string.Equals(expected, actualValue, StringComparison.Ordinal)) continue;

            if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                var expectedMedia = expected.Split(';')[0].Trim();
                var actualMedia = actualValue.Split(';')[0].Trim();
                if (!string.Equals(expectedMedia, actualMedia, StringComparison.OrdinalIgnoreCase))
                {
                    var classification = name is "ping" or "method_not_allowed_ping_post"
                        ? "expected-framework"
                        : "business";
                    Add(report, name, "header", $"$.headers.{key}", expected, actualValue, classification,
                        SourceFor(name, key),
                        "Legacy and ASP.NET Core selected different media types for this response.");
                }
                else
                {
                    Add(report, name, "header", $"$.headers.{key}", expected, actualValue, "wire-format",
                        SourceFor(name, key),
                        "Same media type; only the charset parameter or formatting differs.");
                }
                continue;
            }

            Add(report, name, "header", $"$.headers.{key}", expected, actualValue, "business",
                SourceFor(name, key), "Legacy pagination/header contract differs.");
        }
    }

    private static void CompareJson(DifferentialReport report, string name, string path,
        JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            Add(report, name, "json", path, Kind(expected), Kind(actual), "business",
                SourceFor(name, path), "JSON value kinds differ.");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProperties = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var actualProperties = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                foreach (var property in expectedProperties)
                {
                    var childPath = path + "." + property.Key;
                    if (!actualProperties.TryGetValue(property.Key, out var actualValue))
                    {
                        Add(report, name, "json", childPath, ToJson(property.Value), "<missing>", "business",
                            SourceFor(name, childPath), "Field present in the Python response is missing in C#.");
                        continue;
                    }
                    CompareJson(report, name, childPath, property.Value, actualValue);
                }
                foreach (var property in actualProperties)
                {
                    if (expectedProperties.ContainsKey(property.Key)) continue;
                    Add(report, name, "json", path + "." + property.Key, "<missing>", ToJson(property.Value),
                        "business", SourceFor(name, path + "." + property.Key),
                        "Field present in the C# response is missing in the Python reference.");
                }
                break;

            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                if (expectedItems.Length != actualItems.Length)
                {
                    Add(report, name, "json", path, expectedItems.Length.ToString(CultureInfo.InvariantCulture),
                        actualItems.Length.ToString(CultureInfo.InvariantCulture), "business",
                        SourceFor(name, path), "Array length differs.");
                }
                for (var i = 0; i < Math.Min(expectedItems.Length, actualItems.Length); i++)
                    CompareJson(report, name, path + "[" + i + "]", expectedItems[i], actualItems[i]);
                break;

            case JsonValueKind.String:
                var expectedText = expected.GetString() ?? "";
                var actualText = actual.GetString() ?? "";
                if (string.Equals(expectedText, actualText, StringComparison.Ordinal)) break;
                if (IsDateField(path) && TryParseInstant(expectedText, out var expectedInstant) &&
                    TryParseInstant(actualText, out var actualInstant) && expectedInstant == actualInstant)
                {
                    Add(report, name, "json", path, expectedText, actualText, "wire-format",
                        SourceFor(name, path), "Same instant; ISO-8601 wire formatting differs.");
                }
                else
                {
                    Add(report, name, "json", path, expectedText, actualText, "business",
                        SourceFor(name, path), "JSON string differs.");
                }
                break;

            case JsonValueKind.Number:
                var expectedRaw = expected.GetRawText();
                var actualRaw = actual.GetRawText();
                if (string.Equals(expectedRaw, actualRaw, StringComparison.Ordinal)) break;
                if (decimal.TryParse(expectedRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var expectedNumber) &&
                    decimal.TryParse(actualRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var actualNumber) &&
                    expectedNumber == actualNumber)
                {
                    Add(report, name, "json", path, expectedRaw, actualRaw, "wire-format",
                        SourceFor(name, path), "Same numeric value; JSON numeric formatting differs.");
                }
                else
                {
                    Add(report, name, "json", path, expectedRaw, actualRaw, "business",
                        SourceFor(name, path), "JSON number differs.");
                }
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                if (expected.ValueKind != actual.ValueKind)
                {
                    Add(report, name, "json", path, Kind(expected), Kind(actual), "business",
                        SourceFor(name, path), "JSON literal differs.");
                }
                break;
        }
    }

    private static bool CompareDigest(DifferentialReport report, string name, string path,
        JsonElement expected, object actual, string kind, string description)
    {
        var actualElement = JsonSerializer.SerializeToElement(actual, ReportJson);
        var equal = JsonElementEquals(expected, actualElement);
        if (!equal)
        {
            Add(report, name, kind, path, ToJson(expected), ToJson(actualElement), "business",
                "tests/Shoreline.Tests/LocalServices.cs:20-47 + tools/legacy-reference/runtime_reference.py:372-399",
                description);
        }
        return equal;
    }

    private static bool JsonElementEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().All(p =>
                right.TryGetProperty(p.Name, out var r) && JsonElementEquals(p.Value, r)) &&
                right.EnumerateObject().All(p => left.TryGetProperty(p.Name, out _)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() &&
                left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonElementEquals(pair.First, pair.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
            _ => left.ValueKind == right.ValueKind
        };
    }

    private static async Task<Dictionary<string, CollectionDigest>> DatabaseDigestAsync(
        LocalServices services, IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, CollectionDigest>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var docs = await services.Store.Collection(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(new BsonDocument("_id", 1))
                .ToListAsync();
            using var stream = new MemoryStream();
            foreach (var document in docs)
            {
                var raw = document.ToBson();
                stream.Write(raw, 0, raw.Length);
            }
            var bytes = stream.ToArray();
            result[name] = new CollectionDigest
            {
                Count = docs.Count,
                Bytes = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            };
        }
        return result;
    }

    private static string BuildUrl(string path, JsonElement query)
    {
        var values = query.EnumerateObject()
            .Select(property => Uri.EscapeDataString(property.Name) + "=" +
                Uri.EscapeDataString(property.Value.GetString() ?? ""))
            .ToArray();
        return values.Length == 0 ? path : path + "?" + string.Join("&", values);
    }

    private static Dictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
            result[header.Key] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers)
            result[header.Key] = string.Join(", ", header.Value);
        return result;
    }

    private static bool IsDateField(string path)
    {
        var leaf = path.Split('.').Last().Split('[')[0];
        return leaf is "create_time" or "edit_time" or "last_heartbeat" or "expire_at" or
            "removed_time" or "created" or "updated_at";
    }

    private static bool TryParseInstant(string value, out DateTime valueUtc)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            valueUtc = parsed;
            return true;
        }
        valueUtc = default;
        return false;
    }

    private static string Kind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => element.ValueKind.ToString()
    };

    private static string ToJson(JsonElement element) => element.GetRawText();

    private static string SourceFor(string caseName, string path) => caseName switch
    {
        "ping" => "src/Shoreline.Api/Program.cs:94",
        "missing_token_team_list" => "src/Shoreline.Api/Security/CurrentUser.cs:9; src/Shoreline.Api/Features/TeamEndpoints.cs:44-50",
        "user_info" => "src/Shoreline.Api/Features/UserEndpoints.cs:36-37,101-124",
        "team_list_missing_word" => "src/Shoreline.Api/Features/TeamEndpoints.cs:44-50",
        "team_list" => "src/Shoreline.Api/Features/TeamEndpoints.cs:44-61; src/Shoreline.Api/Features/TeamService.cs ViewAsync",
        "term_bank_list" => "src/Shoreline.Api/Features/TermEndpoints.cs:13-20; src/Shoreline.Api/Features/TermService.cs:262-290",
        "application_list" => "src/Shoreline.Api/Features/ApplicationEndpoints.cs:52-65; src/Shoreline.Api/Features/ApplicationService.cs ToApiListAsync",
        "method_not_allowed_ping_post" => "src/Shoreline.Api/Program.cs:94",
        _ => "unknown"
    };

    private static void Add(DifferentialReport report, string caseName, string kind, string path,
        string expected, string actual, string classification, string source, string description)
    {
        var difference = new Difference
        {
            Case = caseName,
            Kind = kind,
            Path = path,
            Expected = expected,
            Actual = actual,
            Classification = classification,
            Source = source,
            Description = description
        };
        switch (classification)
        {
            case "expected-framework":
                report.ExpectedFrameworkDifferences.Add(difference);
                break;
            case "wire-format":
                report.WireFormatDifferences.Add(difference);
                break;
            default:
                report.BusinessDifferences.Add(difference);
                break;
        }
    }

    private static string WriteReport(DifferentialReport report, string repoRoot)
    {
        var configured = Environment.GetEnvironmentVariable("SHORELINE_DIFFERENTIAL_REPORT");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "shoreline-runtime-differential", "runtime_csharp_differential_after_fixes.json")
            : Path.GetFullPath(configured);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, ReportJson) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string FormatFailure(DifferentialReport report)
    {
        var shown = report.BusinessDifferences.Take(20).Select(d =>
            $"  [{d.Case}] {d.Kind} {d.Path}: expected={d.Expected} actual={d.Actual} ({d.Source})");
        var suffix = report.BusinessDifferences.Count > 20
            ? $"\n  ... and {report.BusinessDifferences.Count - 20} more"
            : "";
        return $"C#/Python runtime differential found {report.BusinessDifferences.Count} business difference(s). " +
            $"Report: {report.ReportPath}\n{string.Join('\n', shown)}{suffix}";
    }

    private static string FindRepoRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Shoreline.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate Shoreline.sln from the test source path.");
    }

    private sealed class DifferentialReport
    {
        public string Schema { get; set; } = "shoreline.csharp-runtime-differential/v1";
        public string Fixture { get; set; } = "";
        public string FixtureDatabase { get; set; } = "";
        public string TestDatabase { get; set; } = "";
        public Dictionary<string, string?> Configuration { get; set; } = new();
        public string ConfigurationProvenance { get; set; } = "";
        public Dictionary<string, CollectionDigest> Seed { get; set; } = new();
        public List<CaseReport> Cases { get; set; } = new();
        public List<Difference> BusinessDifferences { get; set; } = new();
        public List<Difference> ExpectedFrameworkDifferences { get; set; } = new();
        public List<Difference> WireFormatDifferences { get; set; } = new();
        public string Conclusion { get; set; } = "";
        public string ReportPath { get; set; } = "";

        public List<Difference> CaseDifferences(string caseName) =>
            BusinessDifferences.Where(d => d.Case == caseName).ToList();
    }

    private sealed class CaseReport
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public int StatusExpected { get; set; }
        public int StatusActual { get; set; }
        public string BodyKind { get; set; } = "";
        public bool BodyEqual { get; set; }
        public bool DatabaseBeforeEqual { get; set; }
        public bool DatabaseAfterEqual { get; set; }
        public string ActualBody { get; set; } = "";
        public Dictionary<string, string> ActualHeaders { get; set; } = new();
    }

    private sealed class CollectionDigest
    {
        public int Count { get; set; }
        public int Bytes { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private sealed class Difference
    {
        public string Case { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Path { get; set; } = "";
        public string Expected { get; set; } = "";
        public string Actual { get; set; } = "";
        public string Classification { get; set; } = "";
        public string Source { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
