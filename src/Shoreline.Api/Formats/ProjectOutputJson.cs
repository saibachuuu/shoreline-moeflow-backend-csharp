using MongoDB.Bson;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Formats;

/// <summary>Python <c>Project.to_output_json</c> (app/models/project.py:1153-1169).</summary>
public static class ProjectOutputJson
{
    public static IReadOnlyList<KeyValuePair<string, object?>> Fields(
        BsonDocument project,
        string defaultRoleCode,
        string sourceLanguageCode,
        IEnumerable<string> targetLanguageCodes)
    {
        var checkType = project.Integer("m_t", 2);
        var staffPage = project.GetValue("slp", BsonNull.Value).IsNumeric
            ? (object?)project.Integer("slp")
            : null;
        var lockTags = project.GetValue("plt", BsonNull.Value) is BsonArray tags
            ? tags.Select(tag => (object?)tag.ToString()).ToList()
            : new List<object?>();

        return
        [
            new("name", project.Text("n")),
            new("intro", project.Text("i")),
            new("default_role", defaultRoleCode),
            new("allow_apply_type", project.Integer("m_a", 1)),
            new("application_check_type", checkType),
            new("is_need_check_application", checkType != 1),
            new("create_time", Iso(project.GetValue("ct", BsonNull.Value))),
            new("edit_time", Iso(project.GetValue("et", BsonNull.Value))),
            new("source_language", sourceLanguageCode),
            new("target_languages", targetLanguageCodes.Cast<object?>().ToList()),
            new("staff_list_page", staffPage),
            new("position_lock_enabled", project.Flag("ple")),
            new("position_lock_tags", lockTags)
        ];
    }

    public static string Serialize(
        BsonDocument project,
        string defaultRoleCode,
        string sourceLanguageCode,
        IEnumerable<string> targetLanguageCodes) =>
        PythonJson.Serialize(Fields(project, defaultRoleCode, sourceLanguageCode, targetLanguageCodes));

    private static string Iso(BsonValue value) =>
        value is BsonDateTime date ? PythonValue.Iso(date) : "";
}
