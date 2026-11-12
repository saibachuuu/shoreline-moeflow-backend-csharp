using System.Text.Json;
using MongoDB.Bson;

namespace Shoreline.Api.Infrastructure;

public static class IdentityErrors
{
    private static ApiError Error(string name, int code, string identity, string message, int status = 400) =>
        new(name, code, message, status) { IdentityCode = identity };

    // Legacy zh translation maps "身份标签请求参数错误" to "请求参数错误"
    // (app/translations/zh/LC_MESSAGES/messages.po); emit the translated form.
    public static ApiError InvalidRequest(string detail = "") => Error("InvalidIdentityRequestError", 5100,
        "INVALID_REQUEST", "请求参数错误" + (detail.Length == 0 ? "" : ": " + detail));
    public static ApiError InvalidTag() => Error("InvalidIdentityTagError", 5101, "INVALID_IDENTITY_TAG", "项目身份标签无效或不可分配", 422);
    public static ApiError TeamMemberMissing() => Error("IdentityTeamMemberNotFoundError", 5104, "TEAM_MEMBER_NOT_FOUND", "团队成员不存在", 404);
    public static ApiError UserMissing() => Error("IdentityUserNotFoundError", 5105, "USER_NOT_FOUND", "用户不存在", 404);
    public static ApiError MemberMissing() => Error("IdentityMemberNotFoundError", 5103, "MEMBER_NOT_FOUND", "项目成员不存在", 404);
    public static ApiError AlreadyExists() => Error("MemberAlreadyExistsError", 5106, "MEMBERS_ALREADY_EXISTS", "成员已存在", 409);
    public static ApiError MemberVersionConflict() => Error("ProjectMemberVersionConflictError", 5108, "PROJECT_MEMBER_VERSION_CONFLICT", "项目成员版本已变化，请刷新后重试", 409);
    public static ApiError MergeRequired() => Error("MemberMergeRequiredError", 5109, "MEMBER_MERGE_REQUIRED", "项目中已存在该用户成员，需要显式合并", 409);
    public static ApiError OwnerConflict() => Error("OwnerConflictError", 5110, "OWNER_CONFLICT", "项目 owner 条件已变化", 409);
    public static ApiError AlreadyOwner() => Error("MemberAlreadyOwnerError", 5111, "MEMBER_ALREADY_OWNER", "项目 owner 不能被移除或降权", 409);
    public static ApiError StateConflict() => Error("ProjectStateConflictError", 5112, "PROJECT_STATE_CONFLICT", "项目状态不允许执行该操作", 409);
    public static ApiError ClearRetryable() => Error("ClearOperationRetryableError", 5113, "CLEAR_OPERATION_RETRYABLE", "项目内容清理未完成，请重试", 503);
    public static ApiError Capacity() => Error("MemberCapacityReachedError", 5107, "MEMBER_CAPACITY_REACHED", "成员数量已达到上限", 409);
    public static ApiError VersionConflict() => Error("IdentityVersionConflictError", 5115, "VERSION_CONFLICT", "版本已变化，请刷新后重试", 409);
    public static ApiError ProtectedTag() => Error("ProtectedIdentityTagError", 5116, "PROTECTED_IDENTITY_TAG", "系统身份标签受保护", 422);
    public static ApiError PolicyInUse() => Error("IdentityPolicyInUseError", 5117, "IDENTITY_POLICY_IN_USE", "仍有成员使用该标签，不能删除", 409);
    public static ApiError InProgress() => Error("IdentityOperationInProgressError", 5118, "IDENTITY_OPERATION_IN_PROGRESS", "同一成员操作正在处理中，请稍后重试", 409);
    public static ApiError GroupTypeNotSupport() => Error("GroupTypeNotSupportError", 5001, "GROUP_TYPE_NOT_SUPPORT", "不支持此团体类型");
    public static ApiError UserAlreadyJoined(string groupName) => Error("UserAlreadyJoinedError", 5003, "USER_ALREADY_JOINED", $"用户已经在 “{groupName}” 中");
    public static ApiError InvitationAlreadyExist() => Error("InvitationAlreadyExistError", 5004, "INVITATION_ALREADY_EXIST", "已邀请此用户，请等待用户确认");
    public static ApiError ApplicationAlreadyExist() => Error("ApplicationAlreadyExistError", 5005, "APPLICATION_ALREADY_EXIST", "您已经申请，请等待管理员确认");
    public static ApiError InvitationNotExist() => Error("InvitationNotExistError", 5006, "INVITATION_NOT_EXIST", "邀请不存在");
    public static ApiError ApplicationNotExist() => Error("ApplicationNotExistError", 5007, "APPLICATION_NOT_EXIST", "申请不存在");
    public static ApiError InvitationFinished() => Error("InvitationFinishedError", 5010, "INVITATION_FINISHED", "无法修改此邀请");
    public static ApiError ApplicationFinished() => Error("ApplicationFinishedError", 5011, "APPLICATION_FINISHED", "无法修改此申请");
    public static ApiError TargetIsFull() => Error("TargetIsFullError", 5012, "TARGET_IS_FULL", "已满员，不可申请加入或邀请新成员");
    public static ApiError GroupNotOpen() => Error("GroupNotOpenError", 5014, "GROUP_NOT_OPEN", "此团体不向公众开放");

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
