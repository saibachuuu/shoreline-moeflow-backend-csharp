using System.Text.Json;
using MongoDB.Bson;

namespace Shoreline.Api.Infrastructure;

public static class IdentityErrors
{
    private static ApiError Error(string name, int code, string identity, string message, int status = 400) =>
        new(name, code, message, status) { IdentityCode = identity };

    public static ApiError InvalidRequest(string detail = "") => Error("InvalidIdentityRequestError", 5100,
        "INVALID_REQUEST", "身份标签请求参数错误" + (detail.Length == 0 ? "" : ": " + detail));
    public static ApiError InvalidTag() => Error("InvalidIdentityTagError", 5101, "INVALID_IDENTITY_TAG", "项目身份标签无效或不可分配", 422);
    public static ApiError TeamMemberMissing() => Error("IdentityTeamMemberNotFoundError", 5104, "TEAM_MEMBER_NOT_FOUND", "团队成员不存在", 404);
    public static ApiError UserMissing() => Error("IdentityUserNotFoundError", 5105, "USER_NOT_FOUND", "用户不存在", 404);
    public static ApiError Capacity() => Error("MemberCapacityReachedError", 5107, "MEMBER_CAPACITY_REACHED", "成员数量已达到上限", 409);
    public static ApiError VersionConflict() => Error("IdentityVersionConflictError", 5115, "VERSION_CONFLICT", "版本已变化，请刷新后重试", 409);
    public static ApiError ProtectedTag() => Error("ProtectedIdentityTagError", 5116, "PROTECTED_IDENTITY_TAG", "系统身份标签受保护", 422);
    public static ApiError PolicyInUse() => Error("IdentityPolicyInUseError", 5117, "IDENTITY_POLICY_IN_USE", "仍有成员使用该标签，不能删除", 409);
    public static ApiError InProgress() => Error("IdentityOperationInProgressError", 5118, "IDENTITY_OPERATION_IN_PROGRESS", "同一成员操作正在处理中，请稍后重试", 409);

    public static void RequireObject(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw InvalidRequest();
    }

    public static JsonElement Value(JsonElement data, string field) => data.TryGetProperty(field, out var value) ? value : default;
    public static int ExpectedVersion(JsonElement data)
    {
        RequireObject(data);
        if (!data.TryGetProperty("expected_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var expected))
            throw InvalidRequest("expected_version is required");
        return expected;
    }

    // Legacy documents may omit a default-zero version. Still fence every write.
    public static BsonDocument VersionFilter(BsonDocument document, int expected)
    {
        var filter = new BsonDocument("_id", document["_id"]);
        if (expected == 0)
            filter["$or"] = new BsonArray { new BsonDocument("version", 0), new BsonDocument("version", new BsonDocument("$exists", false)) };
        else filter["version"] = expected;
        return filter;
    }

    public static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Number => !value.TryGetDecimal(out var number) || number != 0,
        JsonValueKind.Array => value.GetArrayLength() != 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        _ => true
    };
}
