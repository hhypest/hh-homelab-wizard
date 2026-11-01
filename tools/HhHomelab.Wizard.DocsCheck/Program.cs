using HhHomelab.Wizard.DocsCheck;

if (args.Length > 1)
{
    Console.Error.WriteLine("Usage: DocsCheck [repository-root]");
    return 2;
}

var root = args.Length == 1 ? Path.GetFullPath(args[0]) : RepositoryFiles.FindRoot(Directory.GetCurrentDirectory());
if (root is null)
{
    Console.Error.WriteLine("Repository root with HhHomelab.Wizard.slnx was not found.");
    return 2;
}

var files = RepositoryFiles.List(root);
var sources = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var file in files.Where(DocumentationChecker.IsCheckedDocument))
    sources[file] = await File.ReadAllTextAsync(Path.Combine(root, file));

var problems = DocumentationChecker.Check(sources, files);
foreach (var problem in problems)
    Console.Error.WriteLine(problem);

if (problems.Count > 0)
    return 1;

Console.WriteLine($"Documentation: {sources.Count} documents; links, anchors, catalog, types, ADR status, archive provenance and current paths checked.");
return 0;
