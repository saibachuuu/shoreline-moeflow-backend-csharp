namespace Shoreline.Api.Infrastructure;

// Legacy notification failures use a single API error name and code; the HTTP
// status carries the distinction (400/403/404/409/422/429/503). Do not replace
// these with the generic validation error code 103.
public static class NotificationErrors
{
    public const string ErrorName = "NotificationError";
    public const int ErrorCode = 4600;

    public static ApiError Fail(string message, int status = 400) =>
        new(ErrorName, ErrorCode, message, status);

    public static ApiError NotFound(string message = "通知不存在或不可访问") =>
        Fail(message, 404);

    public static ApiError Forbidden(string message) => Fail(message, 403);

    public static ApiError Conflict(string message) => Fail(message, 409);

    public static ApiError TooManyRequests(string message = "操作过于频繁，请稍后重试") =>
        Fail(message, 429);

    public static ApiError Unavailable(string message = "通知系统尚未启用") =>
        Fail(message, 503);
}
