using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Port of app/services/notification_content.py. This is intentionally a small,
// bounded BBCode subset, not an HTML parser: text nodes are always treated as
// plain text and escaped when rendered.
public sealed class NotificationContent(MongoStore store, IdentityPermissions permissions, IConfiguration configuration)
{
    private static readonly Regex Splitter = new(
        @"(\[(?:/?(?:b|i|u|s|quote|list|url|project)|\*)(?:=[^\]\r\n]*)?\])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagPattern = new(
        @"\A\[(/?)(b|i|u|s|quote|list|url|project|\*)(?:=([^\]]*))?\]\z",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProjectPath = new(
        @"/(?:projects?|project)/([a-fA-F0-9]{24})(?:/|$)",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> HtmlTags = new(StringComparer.Ordinal)
    {
        ["b"] = "strong",
        ["i"] = "em",
        ["u"] = "u",
        ["s"] = "s",
        ["quote"] = "blockquote",
        ["list"] = "ul",
        ["li"] = "li"
    };

    public static BsonArray ParseBody(string? text)
    {
        // Python counts code points, so a string of surrogate pairs must not be
        // rejected because of its UTF-16 length.
        if (text is null || text.EnumerateRunes().Count() > 10000)
            throw NotificationErrors.Fail("通知正文不能超过 10000 字符");

        var root = new BsonArray();
        var stack = new List<(string Tag, BsonArray Children)> { ("root", root) };
        var count = 0;
        foreach (var token in Splitter.Split(text))
        {
            if (token.Length == 0) continue;
            if (++count > 2000) throw NotificationErrors.Fail("通知格式节点过多");

            var match = TagPattern.Match(token);
            if (!match.Success)
            {
                stack[^1].Children.Add(new BsonDocument { ["type"] = "text", ["text"] = token });
                continue;
            }

            var closing = match.Groups[1].Value.Length > 0;
            var tag = match.Groups[2].Value.ToLowerInvariant();
            var arg = match.Groups[3].Value;

            if (tag == "*")
            {
                if (stack[^1].Tag == "li") stack.RemoveAt(stack.Count - 1);
                if (stack[^1].Tag != "list") throw NotificationErrors.Fail("列表项必须位于 list 内");
                var item = new BsonDocument { ["type"] = "li", ["children"] = new BsonArray() };
                stack[^1].Children.Add(item);
                stack.Add(("li", item["children"].AsBsonArray));
            }
            else if (closing)
            {
                if (tag == "list" && stack[^1].Tag == "li") stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 1 || stack[^1].Tag != tag)
                    throw NotificationErrors.Fail("BBCode 标签未正确闭合");
                stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                if (stack.Count >= 12) throw NotificationErrors.Fail("BBCode 嵌套过深");
                if (arg.Length > 0 && tag != "url")
                    throw NotificationErrors.Fail("不支持的标签参数");
                var node = new BsonDocument
                {
                    ["type"] = tag,
                    ["children"] = new BsonArray()
                };
                if (tag == "url" && arg.Length > 0)
                {
                    if (!SafeUrl(arg)) throw NotificationErrors.Fail("链接只支持安全的 HTTP/HTTPS 地址");
                    node["url"] = arg.Trim();
                }
                stack[^1].Children.Add(node);
                stack.Add((tag, node["children"].AsBsonArray));
            }
        }

        if (stack.Count != 1) throw NotificationErrors.Fail("BBCode 标签未正确闭合");
        return root;
    }

    public static string Plain(BsonArray nodes)
    {
        var output = new StringBuilder();
        foreach (var value in nodes)
            if (value.IsBsonDocument) PlainNode(value.AsBsonDocument, output);
        return output.ToString();
    }

    private static void PlainNode(BsonDocument node, StringBuilder output)
    {
        if (Text(node, "type") == "text")
        {
            output.Append(Text(node, "text"));
            return;
        }
        if (Children(node) is { } children) Plain(children, output);
    }

    private static void Plain(BsonArray nodes, StringBuilder output)
    {
        foreach (var value in nodes)
            if (value.IsBsonDocument) PlainNode(value.AsBsonDocument, output);
    }

    public static IEnumerable<BsonDocument> Walk(BsonArray nodes)
    {
        foreach (var value in nodes)
        {
            if (!value.IsBsonDocument) continue;
            var node = value.AsBsonDocument;
            yield return node;
            if (Children(node) is { } children)
                foreach (var child in Walk(children)) yield return child;
        }
    }

    // Python normalizes NFKC, then casefolds, then collapses whitespace. The
    // repository's SearchText helper already implements the case-fold mapping
    // (including ß => ss and final sigma => σ); apply it after NFKC so the
    // notification search projection matches Python's normalize().
    public static string Normalize(string text)
    {
        var folded = SearchText.Normalize(text.Normalize(NormalizationForm.FormKC));
        return CollapseWhitespace(folded);
    }

    private static string CollapseWhitespace(string value)
    {
        var output = new StringBuilder(value.Length);
        var pending = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var white = Rune.IsWhiteSpace(rune) || rune.Value is >= 0x1C and <= 0x1F;
            if (white)
            {
                if (output.Length > 0) pending = true;
                continue;
            }
            if (pending)
            {
                output.Append(' ');
                pending = false;
            }
            output.Append(rune.ToString());
        }
        return output.ToString();
    }

    // Tokens are two Unicode code points, never two UTF-16 code units: an emoji
    // must stay whole and must not be split into lone surrogates.
    public static string[] SearchTokens(string text)
    {
        var runes = Normalize(text).EnumerateRunes().ToArray();
        var tokens = new SortedSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < runes.Length; index++)
            tokens.Add(runes[index].ToString() + runes[index + 1]);
        return tokens.ToArray();
    }

    public static bool SafeUrl(string? value)
    {
        if (value is null || value.EnumerateRunes().Count() > 2048) return false;
        if (value.EnumerateRunes().Any(rune => rune.Value < 32)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    }

    public async Task<BsonArray> PrepareAsync(string? text, BsonDocument actor, CancellationToken ct)
    {
        var nodes = ParseBody(text);
        var projects = new HashSet<string>(StringComparer.Ordinal);
        var accessible = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var node in Walk(nodes))
        {
            var type = Text(node, "type");
            if (type == "url")
            {
                var url = Text(node, "url");
                if (url.Length == 0) url = Plain(Children(node) ?? new BsonArray());
                if (!SafeUrl(url)) throw NotificationErrors.Fail("链接只支持安全的 HTTP/HTTPS 地址");
                node["url"] = url.Trim();
            }
            if (type != "project") continue;

            var value = Plain(Children(node) ?? new BsonArray()).Trim();
            if (!ObjectId.TryParse(value, out _))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
                    parsed.Scheme is not ("http" or "https") ||
                    !SameSite(parsed))
                    throw NotificationErrors.Fail("项目名片只接受本站项目链接或 ID");
                var match = ProjectPath.Match(parsed.AbsolutePath);
                value = match.Success ? match.Groups[1].Value : "";
            }
            if (!ObjectId.TryParse(value, out var projectId))
                throw NotificationErrors.Fail("项目名片无效");

            if (!accessible.TryGetValue(value, out var allowed))
            {
                var project = await store.Collection("project")
                    .Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct);
                allowed = await ProjectAccessibleAsync(project, actor, ct);
                accessible[value] = allowed;
            }
            if (!allowed) throw NotificationErrors.Fail("关联项目不可访问", 403);

            projects.Add(value);
            if (projects.Count > 5) throw NotificationErrors.Fail("最多插入 5 个项目名片");
            node.Clear();
            node["type"] = "project";
            node["project_id"] = value;
        }
        return nodes;
    }

    public async Task<BsonArray> VisibleNodesAsync(BsonArray nodes, BsonDocument? user, CancellationToken ct)
    {
        var ids = new HashSet<ObjectId>();
        foreach (var node in Walk(nodes))
        {
            if (Text(node, "type") == "project" && ObjectId.TryParse(Text(node, "project_id"), out var id))
                ids.Add(id);
        }

        var projects = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Count > 0)
        {
            var rows = await store.Collection("project")
                .Find(new BsonDocument("_id", new BsonDocument("$in",
                    new BsonArray(ids.Select(id => (BsonValue)id)))))
                .ToListAsync(ct);
            foreach (var row in rows) projects[row["_id"].AsObjectId.ToString()] = row;

            if (user is not null && rows.Count > 0 && !user.Flag("b"))
            {
                var snapshots = await permissions.ProjectSnapshotsAsync(user, rows, ct);
                foreach (var row in rows)
                {
                    if (snapshots.TryGetValue(row["_id"].AsObjectId, out var snapshot) &&
                        IdentityPermissions.CanProject(snapshot, row, "project:ACCESS"))
                        allowed.Add(row["_id"].AsObjectId.ToString());
                }
            }
        }

        return Convert(nodes);

        BsonArray Convert(BsonArray source)
        {
            var result = new BsonArray();
            foreach (var value in source)
            {
                if (!value.IsBsonDocument)
                {
                    result.Add(value);
                    continue;
                }
                var node = value.AsBsonDocument;
                if (Text(node, "type") == "project")
                {
                    var id = Text(node, "project_id");
                    if (allowed.Contains(id))
                        result.Add(new BsonDocument
                        {
                            ["type"] = "project",
                            ["project_id"] = id,
                            ["name"] = Text(projects[id], "n")
                        });
                    else
                        result.Add(new BsonDocument { ["type"] = "unavailable" });
                    continue;
                }

                var copy = new BsonDocument();
                foreach (var element in node)
                    if (element.Name != "children") copy[element.Name] = element.Value;
                if (Children(node) is { } children) copy["children"] = Convert(children);
                result.Add(copy);
            }
            return result;
        }
    }

    public static string RenderHtml(BsonArray nodes)
    {
        var output = new StringBuilder();
        Render(nodes, output);
        return output.ToString();
    }

    private static void Render(BsonArray nodes, StringBuilder output)
    {
        foreach (var value in nodes)
        {
            if (!value.IsBsonDocument) continue;
            var node = value.AsBsonDocument;
            var kind = Text(node, "type");
            if (kind == "text")
            {
                output.Append(EscapeHtml(Text(node, "text")).Replace("\n", "<br>", StringComparison.Ordinal));
            }
            else if (kind is "project" or "unavailable")
            {
                output.Append("登录后查看关联项目");
            }
            else if (kind == "url")
            {
                output.Append("<a rel=\"noopener noreferrer\" href=\"")
                    .Append(EscapeHtml(Text(node, "url")))
                    .Append("\">");
                Render(Children(node) ?? new BsonArray(), output);
                output.Append("</a>");
            }
            else if (HtmlTags.TryGetValue(kind, out var tag))
            {
                output.Append('<').Append(tag).Append('>');
                Render(Children(node) ?? new BsonArray(), output);
                output.Append("</").Append(tag).Append('>');
            }
        }
    }

    private static string EscapeHtml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#x27;", StringComparison.Ordinal);

    private async Task<bool> ProjectAccessibleAsync(BsonDocument? project, BsonDocument? user, CancellationToken ct)
    {
        if (project is null || user is null || user.Flag("b")) return false;
        var snapshot = await permissions.ProjectSnapshotAsync(user, project, ct);
        return IdentityPermissions.CanProject(snapshot, project, "project:ACCESS");
    }

    private bool SameSite(Uri parsed)
    {
        var origin = configuration["Notifications:SiteOrigin"] ?? configuration["Site:Origin"] ?? "";
        return Uri.TryCreate(origin, UriKind.Absolute, out var site) &&
            string.Equals(parsed.Authority, site.Authority, StringComparison.OrdinalIgnoreCase);
    }

    private static BsonArray? Children(BsonDocument node) =>
        node.TryGetValue("children", out var value) && value.IsBsonArray ? value.AsBsonArray : null;

    private static string Text(BsonDocument? node, string key) =>
        node is not null && node.TryGetValue(key, out var value) && value.IsString ? value.AsString : "";
}
