// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RustArchon.Api.Administration;

/// <summary>
/// Turns what a site admin wrote in the announcement editor into the HTML of an email - by building the HTML itself from the editor's structured content,
/// never by cleaning up HTML it was handed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not sanitize HTML:</b> the text ends up in other people's mail clients, so it must not be able to carry anything but formatting. Cleaning HTML means
/// parsing it and writing it back out, and that round trip is where sanitizers have failed (a value that parses one way and serializes another); the
/// available library also depends on a parser with a published vulnerability in exactly that step. So the editor sends its <b>Delta</b> (Quill's own JSON:
/// runs of text with attributes such as bold or a link), and this writes the email HTML from it. Every run of text is HTML-encoded; the only tags that can
/// appear are the ones written here (paragraphs, headings 1 to 3, bold, italic, underline, strike, lists, quotes, links); a link is kept only if its address
/// is an absolute http, https or mailto one; nothing else in the Delta - an embedded image, an unknown attribute, a colour, a style - is carried over.
/// Nothing the editor sends can put markup of its own into the email.
/// </para>
/// <para>
/// Also here: the checks on the words themselves - a subject that is one plain line, and the <c>{{Token}}</c>s an announcement may use.
/// </para>
/// </remarks>
public static partial class AnnouncementBody
{
    /// <summary>The largest Delta accepted, in characters of JSON.</summary>
    public const int MaxDeltaLength = 200_000;

    /// <summary>The most runs of text in one Delta - far more than a real message has; the guard is against a runaway one.</summary>
    public const int MaxOps = 5_000;

    public const int MaxSubjectLength = 200;

    /// <summary>The tokens an announcement's own subject and body may use; each is filled in for every recipient.</summary>
    public static readonly IReadOnlySet<string> AllowedTokens = new HashSet<string>(StringComparer.Ordinal) { "OrganizationName", "PlanName", "SiteName" };

    /// <summary>
    /// The email HTML for a Delta. Never null. An empty or absent Delta is an empty string. Throws <see cref="FormatException"/> if it is not a Delta at all
    /// (not JSON, or not the shape the editor sends) or is larger than the limits - which the editor cannot produce, so it means a request made by hand.
    /// </summary>
    public static string ToHtml(string? deltaJson)
    {
        if (string.IsNullOrWhiteSpace(deltaJson))
        {
            return string.Empty;
        }

        if (deltaJson.Length > MaxDeltaLength)
        {
            throw new FormatException("The message is too long.");
        }

        List<Run> runs;
        try
        {
            runs = ReadRuns(deltaJson);
        }
        catch (JsonException)
        {
            throw new FormatException("The message is not in the form the editor sends.");
        }

        return Render(runs);
    }

    /// <summary>Whether the HTML has any visible content: some text, or a rule, rather than the empty paragraphs an editor leaves behind.</summary>
    public static bool HasContent(string? html) =>
        !string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(TagPattern().Replace(html ?? string.Empty, " ")).Replace('?', ' '));

    /// <summary>The subject as one plain line. It is a mail header, not HTML, so it is not encoded - but line breaks are removed, or a header could be forged.</summary>
    public static string CleanSubject(string? subject) => LineBreaks().Replace(subject ?? string.Empty, " ").Trim();

    /// <summary>The <c>{{Token}}</c>s written in <paramref name="text"/> that an announcement may not use (there is no value to put in them).</summary>
    public static IReadOnlyList<string> UnknownTokens(string? text) =>
        TokenPattern().Matches(text ?? string.Empty).Select(m => m.Groups[1].Value).Where(t => !AllowedTokens.Contains(t)).Distinct().ToList();

    /// <summary>All the text of a Delta, for checks that need to read the words (which tokens they use) without the formatting.</summary>
    public static string PlainText(string? deltaJson)
    {
        if (string.IsNullOrWhiteSpace(deltaJson))
        {
            return string.Empty;
        }

        try
        {
            return string.Concat(ReadRuns(deltaJson).Select(r => r.Text));
        }
        catch (JsonException)
        {
            throw new FormatException("The message is not in the form the editor sends.");
        }
    }

    // ---- reading the Delta ----------------------------------------------------------------------------------------------------------

    /// <summary>One run of text and the formatting the editor put on it (or, for a newline, on the line it ends).</summary>
    private sealed record Run(string Text, bool Bold, bool Italic, bool Underline, bool Strike, string? Link, int Header, string? List, bool Quote);

    private static List<Run> ReadRuns(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        var ops = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ops", out var inner) ? inner : root;
        if (ops.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Not a list of operations.");
        }

        var runs = new List<Run>();
        var count = 0;
        foreach (var op in ops.EnumerateArray())
        {
            if (++count > MaxOps)
            {
                throw new FormatException("The message is too long.");
            }

            // Only text is carried over. An embed (an image, a video) has an object where the text would be, and is dropped.
            if (op.ValueKind != JsonValueKind.Object || !op.TryGetProperty("insert", out var insert) || insert.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var attributes = op.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            runs.Add(new Run(
                insert.GetString() ?? string.Empty,
                Flag(attributes, "bold"), Flag(attributes, "italic"), Flag(attributes, "underline"), Flag(attributes, "strike"),
                SafeLink(attributes),
                HeaderLevel(attributes), ListKind(attributes), Flag(attributes, "blockquote")));
        }

        return runs;
    }

    private static bool Flag(JsonElement attributes, string name) =>
        attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int HeaderLevel(JsonElement attributes) =>
        attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("header", out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var level) && level is >= 1 and <= 3 ? level : 0;

    private static string? ListKind(JsonElement attributes) =>
        attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("list", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() switch { "bullet" => "ul", "ordered" => "ol", _ => null }
            : null;

    /// <summary>The link, if it is an absolute http, https or mailto address that a person can safely follow; otherwise null (the text stays, unlinked).</summary>
    private static string? SafeLink(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object || !attributes.TryGetProperty("link", out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = v.GetString()?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 2000 || text.Any(char.IsControl) || text.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return null;
        }

        // Written back from the parsed address, not the text as typed: what is emitted is what was understood.
        return uri.Scheme == "mailto" ? uri.OriginalString : uri.AbsoluteUri;
    }

    // ---- writing the HTML -------------------------------------------------------------------------------------------------------------

    private static string Render(List<Run> runs)
    {
        var html = new StringBuilder();
        var line = new List<Run>();        // the inline runs of the line being read
        string? openList = null;
        var quoteOpen = false;

        void CloseList()
        {
            if (openList is not null)
            {
                html.Append("</").Append(openList).Append('>');
                openList = null;
            }
        }

        void CloseQuote()
        {
            if (quoteOpen)
            {
                html.Append("</blockquote>");
                quoteOpen = false;
            }
        }

        void EndLine(Run ending)
        {
            var inline = string.Concat(line.Select(Inline));
            line.Clear();

            if (ending.List is { } list)
            {
                CloseQuote();
                if (openList != list)
                {
                    CloseList();
                    html.Append('<').Append(list).Append('>');
                    openList = list;
                }

                html.Append("<li>").Append(inline.Length == 0 ? "<br>" : inline).Append("</li>");
                return;
            }

            CloseList();
            var tag = ending.Header is > 0 ? "h" + ending.Header : "p";
            if (ending.Quote)
            {
                if (!quoteOpen)
                {
                    html.Append("<blockquote>");
                    quoteOpen = true;
                }
            }
            else
            {
                CloseQuote();
            }

            html.Append('<').Append(tag).Append('>').Append(inline.Length == 0 ? "<br>" : inline).Append("</").Append(tag).Append('>');
        }

        foreach (var run in runs)
        {
            // A newline ends a line, and it is the newline that carries the line's own formatting (its heading level, list or quote). Text with newlines
            // inside one run is several lines; only the last newline of a run carries the run's line formatting, as in the editor's own model.
            var parts = run.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    line.Add(run with { Text = parts[i] });
                }

                if (i < parts.Length - 1)
                {
                    EndLine(i == parts.Length - 2 ? run : new Run(string.Empty, false, false, false, false, null, 0, null, false));
                }
            }
        }

        if (line.Count > 0)
        {
            EndLine(new Run(string.Empty, false, false, false, false, null, 0, null, false));
        }

        CloseList();
        CloseQuote();
        return html.ToString();
    }

    private static string Inline(Run run)
    {
        var text = WebUtility.HtmlEncode(run.Text);
        if (run.Strike)
        {
            text = $"<s>{text}</s>";
        }

        if (run.Underline)
        {
            text = $"<u>{text}</u>";
        }

        if (run.Italic)
        {
            text = $"<em>{text}</em>";
        }

        if (run.Bold)
        {
            text = $"<strong>{text}</strong>";
        }

        return run.Link is { } link ? $"""<a href="{WebUtility.HtmlEncode(link)}" rel="noopener noreferrer nofollow" target="_blank">{text}</a>""" : text;
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[\r\n??]+")]
    private static partial Regex LineBreaks();
}
