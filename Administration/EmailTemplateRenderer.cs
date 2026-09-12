// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Net;

namespace RustArchon.Api.Administration;

/// <summary>
/// Substitutes <c>{{Token}}</c> placeholders into a Subject and HtmlBody.
/// </summary>
/// <remarks>
/// Takes plain strings rather than an <see cref="Data.EmailTemplate"/> or
/// <see cref="Data.EmailTemplateTranslation"/> - substitution itself has no notion of which language or
/// which template the wording came from, and <see cref="Administration.CommunicationPublisher"/> is
/// already the one place that resolves a template/culture pair down to a Subject/HtmlBody pair before
/// calling this.
/// </remarks>
public static class EmailTemplateRenderer
{
    /// <summary>
    /// Renders <paramref name="subject"/>/<paramref name="htmlBody"/> with <paramref name="tokens"/>
    /// substituted in.
    /// </summary>
    /// <remarks>
    /// Values are HTML-encoded before substitution into <paramref name="htmlBody"/> - never into
    /// <paramref name="subject"/>, a plain-text email header - since most tokens carry content nobody
    /// reviewed for safe HTML (an Organization's own chosen name, say); an admin writing
    /// <c>{{OrganizationName}}</c> into a template shouldn't have to remember to encode it themselves.
    /// A token with no matching key in <paramref name="tokens"/> is left in the output verbatim rather
    /// than silently blanked - a caller that forgot to supply one sees it immediately in the sent email
    /// instead of a quietly wrong one.
    /// </remarks>
    public static (string Subject, string HtmlBody) Render(
        string subject, string htmlBody, IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(htmlBody);
        ArgumentNullException.ThrowIfNull(tokens);

        foreach (var (key, value) in tokens)
        {
            var placeholder = $"{{{{{key}}}}}";
            subject = subject.Replace(placeholder, value, StringComparison.Ordinal);
            htmlBody = htmlBody.Replace(placeholder, WebUtility.HtmlEncode(value), StringComparison.Ordinal);
        }

        return (subject, htmlBody);
    }
}
