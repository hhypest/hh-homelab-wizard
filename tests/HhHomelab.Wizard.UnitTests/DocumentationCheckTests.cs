using HhHomelab.Wizard.DocsCheck;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class DocumentationCheckTests
{
    private const string Catalog = """
        # Каталог

        Тип: реестр документов и статусов.

        Каталог охватывает 3 Markdown-документов.

        | Документ | Тип |
        | --- | --- |
        | [Каталог](catalog.md) | Реестр |
        | [Руководство](guide/start.md) | Действующее описание |
        | [Главная](../README.md) | Описание |

        ## Карта прежних путей

        | Прежний путь | Новый |
        | --- | --- |
        | `docs/OLD.md` | [Руководство](guide/start.md) |
        """;

    private const string Guide = """
        # Начало работы

        Тип: действующая инструкция.

        ## Запуск программы

        См. [каталог](../catalog.md) и [запуск](#запуск-программы).
        """;

    [Fact]
    public void ConsistentDocumentationHasNoProblems()
    {
        var problems = Check(Documentation());

        Assert.Empty(problems);
    }

    [Fact]
    public void MissingLinkTargetIsReported()
    {
        var sources = Documentation();
        sources["docs/guide/start.md"] = Guide + "\n[Нет](missing.md)\n";

        var problem = Assert.Single(Check(sources));

        Assert.Equal("link", problem.Rule);
        Assert.Equal("docs/guide/start.md", problem.Path);
    }

    [Fact]
    public void MissingAnchorIsReported()
    {
        var sources = Documentation();
        sources["README.md"] = "# Главная\n\n[Руководство](docs/guide/start.md#нет-такого-раздела)\n";

        var problem = Assert.Single(Check(sources));

        Assert.Equal("anchor", problem.Rule);
    }

    [Fact]
    public void LinksInsideFencedExamplesAreIgnored()
    {
        var sources = Documentation();
        sources["README.md"] = "# Главная\n\n```text\n[Нет](missing.md)\n```\n";

        Assert.Empty(Check(sources));
    }

    [Fact]
    public void UncataloguedDocumentAndStaleCountAreReported()
    {
        var sources = Documentation();
        sources["docs/extra.md"] = "# Лишний\n\nТип: действующее описание.\n";

        var rules = Check(sources).Select(problem => problem.Rule).Order(StringComparer.Ordinal);

        Assert.Equal(["catalog", "count"], rules);
    }

    [Fact]
    public void LinkOnlyInOldPathMapDoesNotCatalogDocument()
    {
        var sources = Documentation();
        sources["docs/catalog.md"] = Catalog.Replace("| [Руководство](guide/start.md) | Действующее описание |\n", string.Empty, StringComparison.Ordinal);

        var problem = Assert.Single(Check(sources));

        Assert.Equal("catalog", problem.Rule);
        Assert.Contains("docs/guide/start.md", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FencedExampleBeforeCatalogTableDoesNotShiftRows()
    {
        var sources = Documentation();
        var example = "```text\nпример\nещё строка\n```\n\n| Документ | Тип |";
        sources["docs/catalog.md"] = Catalog.Replace("| Документ | Тип |", example, StringComparison.Ordinal);

        Assert.Empty(Check(sources));
    }

    [Fact]
    public void DocumentWithoutTypeIsReported()
    {
        var sources = Documentation();
        sources["docs/guide/start.md"] = Guide.Replace("Тип: действующая инструкция.", "Просто текст.", StringComparison.Ordinal);

        var problem = Assert.Single(Check(sources));

        Assert.Equal("type", problem.Rule);
    }

    [Fact]
    public void AdrWithoutStatusAndArchiveWithoutSourceCommitAreReported()
    {
        var sources = Documentation();
        sources["docs/adr/0001-first.md"] = "# ADR 0001\n\nТекст без статуса.\n";
        sources["docs/archive/old.md"] = "# Старое\n\n> **Архив исходного документа.** Сохранён 01.11.2026 без ссылки на commit.\n";
        sources["docs/catalog.md"] = Catalog
            .Replace("Каталог охватывает 3", "Каталог охватывает 5", StringComparison.Ordinal)
            .Replace("| [Главная]", "| [ADR](adr/0001-first.md) | ADR |\n| [Архив](archive/old.md) | Архив |\n| [Главная]", StringComparison.Ordinal);

        var rules = Check(sources).Select(problem => problem.Rule).Order(StringComparer.Ordinal);

        Assert.Equal(["provenance", "status"], rules);
    }

    [Fact]
    public void CurrentDocumentMustNotNameMissingProject()
    {
        var sources = Documentation();
        sources["docs/guide/start.md"] = Guide + "\n```text\ndotnet run --project tools/Missing.Tool\n```\n";

        var problem = Assert.Single(Check(sources));

        Assert.Equal("command", problem.Rule);
    }

    [Fact]
    public void HistoricalProtocolMayNameRemovedCodePath()
    {
        var sources = Documentation();
        sources["docs/verification/reports/2026-10-19-old.md"] = "# Протокол\n\nТип: протокол проверки.\n\nУдалён `src/Removed/File.cs`.\n";
        sources["docs/catalog.md"] = Catalog
            .Replace("Каталог охватывает 3", "Каталог охватывает 4", StringComparison.Ordinal)
            .Replace("| [Главная]", "| [Протокол](verification/reports/2026-10-19-old.md) | Протокол |\n| [Главная]", StringComparison.Ordinal);

        Assert.Empty(Check(sources));
    }

    [Theory]
    [InlineData("12. Версия 2.0.0: настраиваемый стек", "12-версия-200-настраиваемый-стек")]
    [InlineData("Сборка, тесты и запуск", "сборка-тесты-и-запуск")]
    [InlineData("`core` и [CI](ci.md)", "core-и-ci")]
    public void HeadingSlugMatchesGitHub(string heading, string expected)
    {
        Assert.Equal(expected, MarkdownDocument.Slug(heading));
    }

    [Fact]
    public void RepeatedHeadingsReceiveNumberedAnchors()
    {
        var document = MarkdownDocument.Parse("# Итоги\n\n## Итоги\n\n## Итоги\n");

        Assert.Contains("итоги", document.Anchors);
        Assert.Contains("итоги-1", document.Anchors);
        Assert.Contains("итоги-2", document.Anchors);
    }

    [Fact]
    public void ImportedLicenceTextsAreNotChecked()
    {
        Assert.False(DocumentationChecker.IsCheckedDocument("LICENSES/third-party/Avalonia-LICENSE.md"));
        Assert.True(DocumentationChecker.IsCheckedDocument("docs/README.md"));
    }

    private static Dictionary<string, string> Documentation() => new(StringComparer.Ordinal)
    {
        ["docs/catalog.md"] = Catalog,
        ["docs/guide/start.md"] = Guide,
        ["README.md"] = "# Главная\n\n[Документация](docs/catalog.md)\n",
    };

    private static IReadOnlyList<DocumentationProblem> Check(Dictionary<string, string> sources)
    {
        var files = new HashSet<string>(sources.Keys, StringComparer.Ordinal) { "tools/Existing.Tool/Existing.Tool.csproj" };

        return DocumentationChecker.Check(sources, files);
    }
}
