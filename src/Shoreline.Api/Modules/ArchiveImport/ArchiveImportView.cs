using MongoDB.Bson;

namespace Shoreline.Api.Modules.ArchiveImport;

/// <summary>API projection. zip_url (zu) and any API-key material are deliberately never exposed.</summary>
public static class ArchiveImportView
{
    public static Dictionary<string, object?> ToApi(BsonDocument task)
    {
        var status = (int)task.Integer("s");
        var id = ArchiveImportIds.IdValue(task.GetValue("_id", BsonNull.Value));
        var projectId = ArchiveImportIds.IdValue(task.GetValue("p", BsonNull.Value));
        return new Dictionary<string, object?>
        {
            ["id"] = id?.ToString() ?? "",
            ["project_id"] = projectId?.ToString() ?? "",
            ["gid"] = Field(task, "g"),
            ["token"] = Field(task, "t"),
            ["gallery_url"] = Field(task, "gu"),
            ["status"] = status,
            ["status_name"] = ArchiveImportText.Name(status),
            ["stage"] = task.Text("st"),
            ["total_pages"] = (int)task.Integer("n"),
            ["completed_pages"] = (int)task.Integer("c"),
            ["error"] = task.Text("e"),
            ["dismissed"] = task.Flag("d"),
            ["create_time"] = ArchiveImportIds.TimeText(task, "ct"),
            ["update_time"] = ArchiveImportIds.TimeText(task, "ut"),
        };
    }

    private static string Field(BsonDocument task, string key) =>
        task.TryGetValue(key, out var value) ? ArchiveImportIds.StringValue(value) : "";
}
