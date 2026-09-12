// Copyright ©2026 Scott Blomfield

using System.Net;

namespace RustArchon.Api.Administration;

/// <summary>
/// Wraps a template's rendered content in the platform's branded email shell - a dark header bar with
/// the wordmark, a light content card, and a small footer line. Applied in <see cref="CommunicationPublisher"/>
/// to every email that goes out, templated or not, so the branding is a property of "any email this app
/// sends" rather than something each template has to remember to include.
/// </summary>
/// <remarks>
/// <para>
/// Colors are the light-theme values from <c>RustArchon.Panel/wwwroot/app.css</c>'s <c>--ra-*</c>
/// palette, copied rather than referenced - an email renders in the recipient's mail client, which has
/// no idea what a CSS custom property is, let alone this app's dark-mode toggle. Email always renders
/// on a light background regardless of the app's own theme, the same way every other transactional
/// email in the wild does.
/// </para>
/// <para>
/// Table-based layout with inline styles throughout, not the modern CSS this app's own pages use -
/// email clients (Outlook chief among them) still need it; a <c>&lt;div&gt;</c>/flexbox layout here
/// would render broken in a meaningful share of inboxes.
/// </para>
/// <para>
/// <paramref name="siteName"/>/<paramref name="siteUrl"/> in <see cref="Wrap"/> come from
/// <see cref="Infrastructure.PlatformSettingsRegistry.SiteName"/>/<see cref="Infrastructure.PlatformSettingsRegistry.SiteUrl"/>
/// - resolved by the caller (see <c>CommunicationPublisher.ResolveSiteBrandingAsync</c>) rather than
/// read here, keeping this a pure formatting function with no dependency of its own. Both are
/// HTML-encoded before being written into the shell - unlike a template's own <c>Subject</c>/
/// <c>HtmlBody</c>, which are the admin's trusted content, these two are platform settings anyone
/// holding <c>Platform.ManageSettings</c> can edit, and free text ending up unescaped in every email
/// this app sends is exactly the kind of thing worth encoding defensively rather than trusting.
/// </para>
/// </remarks>
public static class EmailLayout
{
    private const string BackgroundColor = "#f2efe6";
    private const string CardColor = "#fbf9f3";
    private const string BorderColor = "#d3cdb8";
    private const string HeaderColor = "#211f18";
    private const string TextColor = "#211f18";
    private const string MutedTextColor = "#55523f";
    private const string AccentColor = "#ff6a1a";

    public static string Wrap(string contentHtml, string siteName, string siteUrl)
    {
        var encodedName = WebUtility.HtmlEncode(siteName);
        var encodedUrl = WebUtility.HtmlEncode(siteUrl);

        return $"""
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:{BackgroundColor};padding:32px 16px;">
            <tr><td align="center">
            <table role="presentation" width="100%" style="max-width:560px;background-color:{CardColor};border:1px solid {BorderColor};border-radius:8px;overflow:hidden;font-family:Arial,Helvetica,sans-serif;" cellpadding="0" cellspacing="0">
            <tr><td style="background-color:{HeaderColor};padding:20px 28px;">
            <a href="{encodedUrl}" style="font-size:20px;font-weight:bold;letter-spacing:2px;color:{AccentColor};text-transform:uppercase;text-decoration:none;">{encodedName}</a>
            </td></tr>
            <tr><td style="padding:28px;color:{TextColor};font-size:15px;line-height:1.6;">
            {contentHtml}
            </td></tr>
            <tr><td style="padding:16px 28px;border-top:1px solid {BorderColor};color:{MutedTextColor};font-size:12px;">
            This is an automated message from {encodedName}.
            </td></tr>
            </table>
            </td></tr>
            </table>
            """;
    }
}
