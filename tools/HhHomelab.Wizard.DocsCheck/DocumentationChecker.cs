using System.Text.RegularExpressions;

namespace HhHomelab.Wizard.DocsCheck;

/// <summary>One violated documentation rule, reported against a repository-relative path and line.</summary>
public sealed record DocumentationProblem(string Path, int Line, string Rule, string Message)
{
    public override string ToString() => $"{Path}:{Line} [{Rule}] {Message}";
}

/// <summary>
/// Checks the documentation organisation described in docs/catalog.md: resolvable links and anchors,
/// a complete catalog, declared document types, ADR status, archive provenance and current code paths.
/// It proves structure only; it does not judge whether a description is still true.
/// </summary>
public static partial class DocumentationChecker
{
    public const string CatalogPath = "docs/catalog.md";

    private const string OldPathsHeading = "## Карта прежних путей";

    public static IReadOnlyList<DocumentationProblem> Check(
        IReadOnlyDictionary<string, string> markdownSources,
        IReadOnlySet<string> repositoryFiles
    )
    {
        var problems = new List<DocumentationProblem>();
        var paths = WithDirectories(repositoryFiles);
        var documents = markdownSources
            .Where(source => IsCheckedDocument(source.Key))
            .ToDictionary(source => source.Key, source => MarkdownDocument.Parse(source.Value), StringComparer.Ordinal);

        foreach (var (path, document) in documents)
        {
            CheckLinks(path, document, documents, paths, problems);
            CheckDeclaration(path, document, problems);
            if (!IsHistorical(path))
                CheckCurrentReferences(path, markdownSources[path], paths, problems);
        }

        CheckCatalog(documents, problems);
        return problems;
    }

    /// <summary>Imported licence texts keep their upstream form and are outside the catalog.</summary>
    public static bool IsCheckedDocument(string path)
    {
        var markdown = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var importedLicence = path.StartsWith("LICENSES/", StringComparison.Ordinal);

        return markdown && !importedLicence;
    }

    private static bool IsHistorical(string path)
    {
        var archive = path.StartsWith("docs/archive/", StringComparison.Ordinal);
        var decision = path.StartsWith("docs/adr/", StringComparison.Ordinal);
        var report = path.StartsWith("docs/verification/reports/", StringComparison.Ordinal);
        var changelog = path == "CHANGELOG.md";

        return archive || decision || report || changelog;
    }

    private static HashSet<string> WithDirectories(IReadOnlySet<string> files)
    {
        var paths = new HashSet<string>(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var directory = ParentOf(file);
            while (directory.Length > 0)
            {
                paths.Add(directory);
                directory = ParentOf(directory);
            }
        }

        return paths;
    }

    private static void CheckLinks(
        string path,
        MarkdownDocument document,
        Dictionary<string, MarkdownDocument> documents,
        HashSet<string> paths,
        List<DocumentationProblem> problems
    )
    {
        foreach (var link in document.Links)
        {
            if (ExternalTarget().IsMatch(link.Target))
                continue;

            var resolved = Resolve(path, link.Target);
            if (resolved is null)
            {
                problems.Add(new(path, link.Line, "link", $"Invalid link target: {link.Target}"));
                continue;
            }

            var (target, anchor) = resolved.Value;
            if (!paths.Contains(target))
            {
                problems.Add(new(path, link.Line, "link", $"Missing target: {target}"));
                continue;
            }

            var anchorMissing = anchor is not null
                && documents.TryGetValue(target, out var linked)
                && !linked.Anchors.Contains(anchor);
            if (anchorMissing)
                problems.Add(new(path, link.Line, "anchor", $"Missing anchor: {target}#{anchor}"));
        }
    }

    /// <summary>Resolves a relative link against the linking file; null when the target cannot be decoded.</summary>
    public static (string Target, string? Anchor)? Resolve(string linkingFile, string target)
    {
        var hashIndex = target.IndexOf('#', StringComparison.Ordinal);
        var name = hashIndex < 0 ? target : target[..hashIndex];
        var fragment = hashIndex < 0 ? null : target[(hashIndex + 1)..];
        var queryIndex = name.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
            name = name[..queryIndex];

        try
        {
            var decodedName = Uri.UnescapeDataString(name);
            var anchor = fragment is null ? null : Uri.UnescapeDataString(fragment);
            if (decodedName.Length == 0)
                return (linkingFile, anchor);

            var directory = ParentOf(linkingFile);
            var combined = directory.Length == 0 ? decodedName : directory + "/" + decodedName;
            return (Normalize(combined), anchor);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;

            if (part == ".." && parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash < 0)
            return string.Empty;

        return path[..slash];
    }

    private static void CheckDeclaration(string path, MarkdownDocument document, List<DocumentationProblem> problems)
    {
        var preamble = document.Preamble;
        var archive = path.StartsWith("docs/archive/", StringComparison.Ordinal);
        var decision = path.StartsWith("docs/adr/0", StringComparison.Ordinal);
        var typed = path.StartsWith("docs/", StringComparison.Ordinal) || path == "TESTING.md";

        if (archive)
        {
            var notice = ArchiveNotice().IsMatch(preamble);
            var dated = Date().IsMatch(preamble);
            var pinned = ExactSourceCommit().IsMatch(preamble);
            if (!notice || !dated || !pinned)
                problems.Add(new(path, 1, "provenance", "Archive must name a date and an exact source commit"));

            return;
        }

        if (decision)
        {
            if (!Status().IsMatch(preamble))
                problems.Add(new(path, 1, "status", "ADR must declare its status"));

            return;
        }

        if (typed && !DocumentType().IsMatch(preamble))
            problems.Add(new(path, 1, "type", "Declare the document type near its beginning (Тип: …)"));
    }

    /// <summary>Current documents must not name code paths or projects that no longer exist, fenced commands included.</summary>
    private static void CheckCurrentReferences(
        string path,
        string source,
        HashSet<string> paths,
        List<DocumentationProblem> problems
    )
    {
        foreach (Match code in CodePath().Matches(source))
        {
            var mentioned = code.Groups[1].Value.TrimEnd('/');
            if (!paths.Contains(mentioned))
                problems.Add(new(path, LineOf(source, code.Index), "code-path", $"Missing current path: {mentioned}"));
        }

        foreach (Match command in ProjectCommand().Matches(source))
        {
            var project = command.Groups[1].Value.TrimEnd('/');
            if (!paths.Contains(project))
                problems.Add(new(path, LineOf(source, command.Index), "command", $"Missing project: {project}"));
        }
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var position = 0; position < index; position++)
        {
            if (text[position] == '\n')
                line++;
        }

        return line;
    }

    private static void CheckCatalog(Dictionary<string, MarkdownDocument> documents, List<DocumentationProblem> problems)
    {
        if (!documents.TryGetValue(CatalogPath, out var catalog))
        {
            problems.Add(new(CatalogPath, 1, "catalog", "The documentation catalog is missing"));
            return;
        }

        var catalogued = CataloguedTargets(catalog);
        foreach (var path in documents.Keys.Order(StringComparer.Ordinal))
        {
            if (!catalogued.Contains(path))
                problems.Add(new(CatalogPath, 1, "catalog", $"Uncatalogued document: {path}"));
        }

        var stated = CatalogCount().Match(catalog.VisibleText);
        var expected = documents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var matches = stated.Success && stated.Groups[1].Value == expected;
        if (!matches)
        {
            var statedText = stated.Success ? stated.Groups[1].Value : "none";
            problems.Add(new(CatalogPath, 1, "count", $"Expected {expected} Markdown documents, stated {statedText}"));
        }
    }

    /// <summary>Targets linked from catalog table rows before the map of old paths.</summary>
    private static HashSet<string> CataloguedTargets(MarkdownDocument catalog)
    {
        var lines = catalog.SourceLines;
        var mapStart = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].StartsWith(OldPathsHeading, StringComparison.Ordinal))
            {
                mapStart = index;
                break;
            }
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in catalog.Links)
        {
            var beforeMap = mapStart < 0 || link.Line <= mapStart;
            var tableRow = lines[link.Line - 1].StartsWith('|');
            var resolved = Resolve(CatalogPath, link.Target);
            if (beforeMap && tableRow && resolved is not null)
                targets.Add(resolved.Value.Target);
        }

        return targets;
    }

    [GeneratedRegex(@"^(?:[a-z][a-z\d+.-]*:|//|/)", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalTarget();

    [GeneratedRegex(@"^> \*\*(?:Архив исходного документа|Исторический материал)\.", RegexOptions.Multiline)]
    private static partial Regex ArchiveNotice();

    [GeneratedRegex(@"\b\d{2}\.\d{2}\.\d{4}\b")]
    private static partial Regex Date();

    [GeneratedRegex(@"/blob/[a-f\d]{40}/")]
    private static partial Regex ExactSourceCommit();

    [GeneratedRegex(@"Статус:", RegexOptions.IgnoreCase)]
    private static partial Regex Status();

    [GeneratedRegex(
        @"^Тип:\s*(?:действующ|текущ|план|протокол|реестр|навигатор|страница|стабильн)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex DocumentType();

    [GeneratedRegex(@"`((?:src|tests|tools|profiles|\.github)/[A-Za-z\d_./-]+)`")]
    private static partial Regex CodePath();

    [GeneratedRegex(@"dotnet run --project ((?:src|tests|tools)/[A-Za-z\d_./-]+)")]
    private static partial Regex ProjectCommand();

    [GeneratedRegex(@"Каталог охватывает (\d+) Markdown")]
    private static partial Regex CatalogCount();
}
