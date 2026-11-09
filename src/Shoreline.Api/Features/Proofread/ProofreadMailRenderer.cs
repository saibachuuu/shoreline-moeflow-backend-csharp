using System.Globalization;
using System.Net;
using System.Text;

namespace Shoreline.Api.Features;

/// <summary>
/// Static proofread feedback mail renderer. Ports app/templates/email/
/// proofread_draft.html and .txt without a templating dependency.
///
/// All user-controlled strings are HTML encoded. Only DiffHtml is emitted as
/// markup, and it is generated locally from escaped diff fragments.
/// </summary>
public static class ProofreadMailRenderer
{
    public const string InMarker = "#FF657C";
    public const string OutMarker = "#FFD583";

    public static (string Subject, string Html, string Text) Render(
        string siteName,
        string projectName,
        string targetLanguageName,
        string senderName,
        string senderEmail,
        string sendTime,
        int totalChangedLabels,
        int totalTips,
        IReadOnlyList<ProofreadPage> pages,
        string projectUrl,
        string replyAddress)
    {
        var subject = $"[{siteName}] 校对稿修改反馈 - {targetLanguageName}";
        return (subject, RenderHtml(siteName, projectName, targetLanguageName, senderName, senderEmail,
            sendTime, totalChangedLabels, totalTips, pages, projectUrl, replyAddress),
            RenderText(siteName, projectName, targetLanguageName, senderName, senderEmail,
            sendTime, totalChangedLabels, totalTips, pages, projectUrl, replyAddress));
    }

    public static string RenderHtml(
        string siteName,
        string projectName,
        string targetLanguageName,
        string senderName,
        string senderEmail,
        string sendTime,
        int totalChangedLabels,
        int totalTips,
        IReadOnlyList<ProofreadPage> pages,
        string projectUrl,
        string replyAddress)
    {
        string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        var html = new StringBuilder(16 * 1024);
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>").Append(E(siteName)).Append(" · 校对反馈</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:#f6f7f9;color:#1f2328;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','Noto Sans SC',sans-serif;\">")
            .Append("<div style=\"max-width:720px;margin:0 auto;padding:24px 16px;\">")
            .Append("<div style=\"background:#fff;border:1px solid #d0d7de;border-radius:10px;padding:24px;\">")
            .Append("<div style=\"font-size:13px;color:").Append(InMarker).Append(";font-weight:600;\">")
            .Append(E(siteName)).Append(" · 校对反馈</div>")
            .Append("<h1 style=\"font-size:22px;margin:8px 0 16px;\">").Append(E(projectName)).Append(" · 校对修改稿</h1>")
            .Append("<table style=\"font-size:13px;color:#57606a;border-collapse:collapse;\">")
            .Append("<tr><td style=\"padding:2px 12px 2px 0;\">目标语言</td><td>").Append(E(targetLanguageName)).Append("</td></tr>")
            .Append("<tr><td style=\"padding:2px 12px 2px 0;\">反馈人</td><td>").Append(E(senderName));
        if (!string.IsNullOrWhiteSpace(senderEmail))
            html.Append(" &lt;").Append(E(senderEmail)).Append("&gt;");
        html.Append("</td></tr><tr><td style=\"padding:2px 12px 2px 0;\">发送时间</td><td>").Append(E(sendTime)).Append("</td></tr></table>")
            .Append("<p style=\"font-size:14px;line-height:1.7;margin:16px 0;padding:12px 14px;background:#f6f8fa;border-radius:8px;\">")
            .Append("共挑出 ").Append(pages.Count.ToString(CultureInfo.InvariantCulture)).Append(" 个页面存在校对修改，共计 ")
            .Append(totalChangedLabels.ToString(CultureInfo.InvariantCulture)).Append(" 处标签变更");
        if (totalTips > 0) html.Append("，附 ").Append(totalTips.ToString(CultureInfo.InvariantCulture)).Append(" 条批注");
        html.Append("。请查阅以下页面及高亮变更详情。</p>");

        foreach (var page in pages)
        {
            html.Append("<div style=\"margin:24px 0;border-top:1px solid #d0d7de;padding-top:16px;\">")
                .Append("<h2 style=\"font-size:16px;margin:0 0 8px;\">第 ").Append(page.PageNumber.ToString(CultureInfo.InvariantCulture))
                .Append(" 页 <span style=\"font-weight:400;color:#57606a;font-size:13px;\">(").Append(E(page.FileName)).Append(")</span></h2>")
                .Append("<div style=\"font-size:12px;margin-bottom:10px;\">");
            if (page.ChangedCount > 0)
            {
                html.Append("<span style=\"display:inline-block;background:#ffeff1;color:#af1b28;border-radius:999px;padding:2px 10px;margin-right:6px;\">本页 ")
                    .Append(page.ChangedCount.ToString(CultureInfo.InvariantCulture)).Append(" 处修改");
                if (page.ChangedLabelNums.Count > 0)
                    html.Append(" (标签：").Append(string.Join(", ", page.ChangedLabelNums.Select(n => "#" + n.ToString(CultureInfo.InvariantCulture)))).Append(")");
                html.Append("</span>");
            }
            if (page.TipCount > 0)
            {
                html.Append("<span style=\"display:inline-block;background:#fff8c5;color:#9a6700;border-radius:999px;padding:2px 10px;\">本页 ")
                    .Append(page.TipCount.ToString(CultureInfo.InvariantCulture)).Append(" 条批注</span>");
            }
            html.Append("</div>");

            var image = string.IsNullOrWhiteSpace(page.MarkedImageUrl) ? page.ImageUrl : page.MarkedImageUrl;
            if (!string.IsNullOrWhiteSpace(image))
            {
                html.Append("<a href=\"").Append(E(image)).Append("\" style=\"display:block;\">")
                    .Append("<img src=\"").Append(E(image)).Append("\" alt=\"第 ")
                    .Append(page.PageNumber.ToString(CultureInfo.InvariantCulture))
                    .Append(" 页\" style=\"max-width:100%;max-height:540px;border:1px solid #d0d7de;border-radius:6px;\"></a>");
            }
            html.Append("<div style=\"font-size:12px;color:#57606a;margin:6px 0 14px;\">")
                .Append("<span style=\"color:").Append(InMarker).Append(";\">●</span> 框内 ")
                .Append("<span style=\"color:").Append(OutMarker).Append(";\">●</span> 框外 · 点击查看大图</div>");

            foreach (var label in page.Labels)
            {
                var background = label.IsChanged ? "#fffbe6" : "#ffffff";
                var border = label.IsChanged ? "#ffe58f" : "#eaeef2";
                html.Append("<div style=\"background:").Append(background).Append(";border-bottom:1px solid ").Append(border)
                    .Append(";padding:8px 0;font-size:13px;line-height:1.65;\">")
                    .Append("<span style=\"color:#57606a;margin-right:6px;\">#").Append(label.LabelNum.ToString(CultureInfo.InvariantCulture)).Append("</span>");
                if (label.IsChanged)
                {
                    html.Append("<span style=\"color:#af1b28;text-decoration:line-through;\">").Append(E(label.OrigTranslation)).Append("</span> ")
                        .Append("<span style=\"display:inline-block;background:").Append(InMarker)
                        .Append(";color:#fff;border-radius:4px;padding:0 5px;font-size:11px;margin:0 4px;\">校</span>")
                        .Append("<span>").Append(label.DiffHtml).Append("</span>");
                }
                else
                {
                    html.Append("<span>").Append(E(string.IsNullOrWhiteSpace(label.ProofreadTranslation) ? label.OrigTranslation : label.ProofreadTranslation)).Append("</span>");
                }

                foreach (var tip in label.Tips)
                {
                    html.Append("<div style=\"margin:6px 0 0 18px;background:#fff8c5;border-radius:6px;padding:6px 10px;font-size:12px;color:#57606a;\">")
                        .Append("<span style=\"display:inline-block;background:#fff8c5;color:#9a6700;font-weight:600;margin-right:6px;\">注</span>")
                        .Append(E(string.IsNullOrWhiteSpace(tip.Author) ? "已注销用户" : tip.Author))
                        .Append("：").Append(E(tip.Content))
                        .Append(" <span style=\"color:#8c959f;\">").Append(E(tip.EditTime)).Append("</span></div>");
                }
                html.Append("</div>");
            }
            html.Append("</div>");
        }

        html.Append("<div style=\"margin-top:24px;padding-top:16px;border-top:1px solid #d0d7de;font-size:13px;color:#57606a;\">");
        if (!string.IsNullOrWhiteSpace(replyAddress))
        {
            html.Append("如有疑问，请回复 <a href=\"mailto:").Append(E(replyAddress)).Append("\" style=\"color:")
                .Append(InMarker).Append(";\">").Append(E(replyAddress)).Append("</a>。<br>");
        }
        if (!string.IsNullOrWhiteSpace(projectUrl))
        {
            html.Append("<a href=\"").Append(E(projectUrl)).Append("\" style=\"display:inline-block;margin-top:10px;background:")
                .Append(InMarker).Append(";color:#fff;text-decoration:none;border-radius:6px;padding:8px 14px;\">前往项目</a><br>");
        }
        html.Append("<span style=\"display:inline-block;margin-top:12px;color:#8c959f;\">").Append(E(siteName)).Append(" · 在线翻译协作平台</span>")
            .Append("</div></div></div></body></html>");
        return html.ToString();
    }

    public static string RenderText(
        string siteName,
        string projectName,
        string targetLanguageName,
        string senderName,
        string senderEmail,
        string sendTime,
        int totalChangedLabels,
        int totalTips,
        IReadOnlyList<ProofreadPage> pages,
        string projectUrl,
        string replyAddress)
    {
        var text = new StringBuilder(8 * 1024);
        text.Append('[').Append(siteName).Append("] ").Append(projectName).Append(" · 校对修改稿反馈\n")
            .Append("----------------------------------------\n")
            .Append("目标语言：").Append(targetLanguageName).Append('\n')
            .Append("反馈人：").Append(senderName);
        if (!string.IsNullOrWhiteSpace(senderEmail)) text.Append(" <").Append(senderEmail).Append('>');
        text.Append('\n').Append("发送时间：").Append(sendTime).Append('\n')
            .Append("共挑出 ").Append(pages.Count.ToString(CultureInfo.InvariantCulture)).Append(" 个页面存在校对修改，共计 ")
            .Append(totalChangedLabels.ToString(CultureInfo.InvariantCulture)).Append(" 处标签变更");
        if (totalTips > 0) text.Append("，附 ").Append(totalTips.ToString(CultureInfo.InvariantCulture)).Append(" 条批注");
        text.Append("。\n");

        foreach (var page in pages)
        {
            text.Append('\n').Append("【第 ").Append(page.PageNumber.ToString(CultureInfo.InvariantCulture)).Append(" 页】")
                .Append(page.FileName).Append(" (").Append(page.ChangedCount.ToString(CultureInfo.InvariantCulture))
                .Append(" 处修改，").Append(page.TipCount.ToString(CultureInfo.InvariantCulture)).Append(" 条批注)\n");
            var image = string.IsNullOrWhiteSpace(page.MarkedImageUrl) ? page.ImageUrl : page.MarkedImageUrl;
            if (!string.IsNullOrWhiteSpace(image)) text.Append("页面图片：").Append(image).Append('\n');
            foreach (var label in page.Labels)
            {
                text.Append(label.IsChanged ? "* " : "  ").Append("[#")
                    .Append(label.LabelNum.ToString(CultureInfo.InvariantCulture)).Append("] ").Append(label.OrigTranslation).Append('\n');
                if (label.IsChanged)
                {
                    text.Append("  [校] ").Append(string.IsNullOrWhiteSpace(label.ProofreadTranslation)
                        ? label.OrigTranslation : label.ProofreadTranslation).Append('\n');
                }
                foreach (var tip in label.Tips)
                {
                    text.Append("  [注] ").Append(string.IsNullOrWhiteSpace(tip.Author) ? "已注销用户" : tip.Author)
                        .Append(": ").Append(tip.Content).Append(" (").Append(tip.EditTime).Append(")\n");
                }
            }
        }

        text.Append('\n');
        if (!string.IsNullOrWhiteSpace(replyAddress)) text.Append("如有疑问，请回复：").Append(replyAddress).Append('\n');
        if (!string.IsNullOrWhiteSpace(projectUrl)) text.Append("项目地址：").Append(projectUrl).Append('\n');
        text.Append(siteName).Append(" · 在线翻译协作平台\n");
        return text.ToString();
    }
}
