namespace Shoreline.Api.Features;

/// <summary>
/// Success messages returned by legacy views. Python wraps them in
/// <c>gettext</c>; with no/English Accept-Language Flask-Babel picks
/// <c>app/translations/en/LC_MESSAGES/messages.po</c>, so the wire value is
/// English unless the caller explicitly asks for Chinese.
/// </summary>
public static class LegacySuccessMessages
{
    public static string Get(HttpContext context, string chineseText) =>
        Get(LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage), chineseText);

    public static string Get(bool chinese, string chineseText) => chinese
        ? chineseText
        : chineseText switch
        {
            "创建成功" => "Created successfully.",
            "修改成功" => "Updated",
            "删除成功" => "Removed",
            "注册成功" => "Registration successful.",
            "移动成功" => "Moved",
            "改名成功" => "Renamed",
            "添加目标语言成功" => "Successfully added target language.",
            "创建导出任务成功" => "Export task created successfully.",
            "修改成功，请重新登陆" => "Updated. Please log in again.",
            "加入成功" => "Successfully joined.",
            "加入成功，管理员继承自团队" => "Joined successfully as administrator due to team settings.",
            "申请成功，请等待管理员审核" => "Application Sent. Please wait for administrator to review.",
            "邀请成功，请等待用户确认" => "Invited. Please wait for the user to confirm.",
            "此用户已有申请，已直接加入" => "This user already has an application and has been directly added.",
            "此用户是项目所在团队成员，已直接加入" => "This user is already a member of the project's team and has been directly added.",
            "管理员先前邀请了您，已直接加入" => "The administrator previously invited you, so you have been directly added.",
            "已接受" => "Approved",
            "归档导入任务已加入队列" => "Queued",
            // msgstr is empty in the legacy catalogue, so gettext falls back
            // to the Chinese source string.
            _ => chineseText
        };
}
