using System.Text;
using System.Text.RegularExpressions;

namespace HhHomelab.Wizard.DocsCheck;

/// <summary>A relative or absolute link target found on a visible Markdown line.</summary>
public sealed record MarkdownLink(int Line, string Target);

/// <summary>
/// Visible structure of one Markdown file: GitHub-style heading anchors, links and text outside fenced code.
/// Fenced examples are ignored, so links and headings inside them are not checked.
/// </summary>
public sealed partial class MarkdownDocument
{
    private MarkdownDocument(IReadOnlySet<string> anchors, IReadOnlyList<MarkdownLink> links, string visibleText)
    {
        Anchors = anchors;
        Links = links;
        VisibleText = visibleText;
    }

    public IReadOnlySet<string> Anchors { get; }

    public IReadOnlyList<MarkdownLink> Links { get; }

    public string VisibleText { get; }

    /// <summary>Text before the first second-level heading, where type, status and provenance are declared.</summary>
    public string Preamble
    {
        get
        {
            var match = SecondLevelHeading().Match(VisibleText);
            if (!match.Success)
                return VisibleText;

            return VisibleText[..match.Index];
        }
    }

    public static MarkdownDocument Parse(string source)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var slugCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var links = new List<MarkdownLink>();
        var visible = new StringBuilder();
        var fence = (string?)null;
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var marker = FenceMarker().Match(line);
            if (marker.Success)
            {
                fence = NextFenceState(fence, marker.Groups[1].Value, line);
                continue;
            }

            if (fence is not null)
                continue;

            visible.Append(line).Append('\n');
            AddHeadingAnchor(line, anchors, slugCounts);
            AddExplicitAnchors(line, anchors);
            AddLinks(line, index + 1, links);
        }

        return new MarkdownDocument(anchors, links, visible.ToString());
    }

    /// <summary>GitHub anchor of a heading text: lowercase, punctuation removed, spaces as hyphens.</summary>
    public static string Slug(string heading)
    {
        var withoutCode = InlineCode().Replace(heading, "$1");
        var withoutLinks = InlineLink().Replace(withoutCode, "$1");
        var lower = withoutLinks.ToLowerInvariant();
        var letters = NotSlugCharacter().Replace(lower, string.Empty);

        return letters.Replace(' ', '-');
    }

    private static string? NextFenceState(string? openFence, string marker, string line)
    {
        if (openFence is null)
            return marker;

        var sameCharacter = marker[0] == openFence[0];
        var longEnough = marker.Length >= openFence.Length;
        var bareMarker = ClosingFence().IsMatch(line);
        if (sameCharacter && longEnough && bareMarker)
            return null;

        return openFence;
    }

    private static void AddHeadingAnchor(string line, HashSet<string> anchors, Dictionary<string, int> slugCounts)
    {
        var heading = Heading().Match(line);
        if (!heading.Success)
            return;

        var slug = Slug(heading.Groups[1].Value);
        var anchor = slug;
        slugCounts.TryGetValue(slug, out var suffix);
        while (anchors.Contains(anchor))
        {
            suffix++;
            anchor = slug + "-" + suffix;
        }

        anchors.Add(anchor);
        slugCounts[slug] = suffix;
    }

    private static void AddExplicitAnchors(string line, HashSet<string> anchors)
    {
        foreach (Match match in HtmlAnchor().Matches(line))
            anchors.Add(match.Groups[1].Value);
    }

    private static void AddLinks(string line, int lineNumber, List<MarkdownLink> links)
    {
        foreach (Match match in InlineTarget().Matches(line))
        {
            var bracketed = match.Groups[1];
            var target = bracketed.Success ? bracketed.Value : match.Groups[2].Value;
            links.Add(new MarkdownLink(lineNumber, target));
        }
    }

    [GeneratedRegex(@"^\s{0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceMarker();

    [GeneratedRegex(@"^\s{0,3}(?:`{3,}|~{3,})\s*$")]
    private static partial Regex ClosingFence();

    [GeneratedRegex(@"^#{1,6}\s+(.+?)(?:\s+#+)?\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^## ", RegexOptions.Multiline)]
    private static partial Regex SecondLevelHeading();

    [GeneratedRegex(@"<a\s+(?:id|name)=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAnchor();

    [GeneratedRegex(@"\[[^\]]*\]\(\s*(?:<([^>]+)>|([^\s)]+))(?:\s+[""'][^""']*[""'])?\s*\)")]
    private static partial Regex InlineTarget();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"[^\p{L}\p{N}\p{M}\s_-]")]
    private static partial Regex NotSlugCharacter();
}
