namespace Shoreline.Api.Infrastructure;

public sealed class ApiError(string error, int code, string message, int status = 400) : Exception(message)
{
    public string Error { get; } = error;
    public int Code { get; } = code;
    public int Status { get; } = status;
    public string? IdentityCode { get; init; }
    public object ResponseMessage { get; init; } = message;
    public static ApiError Invalid(string message) => new("ValidateError", 2, message);

    // Python's ValidateError subclasses APIError, whose __init__ prefixes the
    // class message: `raise ValidateError("...")` renders as
    // "Validate Error: ...". verify_data uses replace=True so field errors keep
    // the bare dict; only the string form gets the prefix.
    public static ApiError Validate(string message) => new("ValidateError", 2, "Validate Error: " + message);
    public static ApiError MissingToken() => new("NeedTokenError", 1003, "需要令牌", 401);
    public static ApiError BadToken() => new("BadTokenError", 1004, "无效的令牌", 401);
    public static ApiError Forbidden() => new("NoPermissionError", 103, "没有权限");
    public static ApiError Field(string field, string message) => new("ValidateError", 2, "Validate Error")
    { ResponseMessage = new Dictionary<string, string[]> { [field] = [message] } };
}
