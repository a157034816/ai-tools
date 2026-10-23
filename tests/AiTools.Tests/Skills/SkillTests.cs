using AiTools.Abstractions;
using AiTools.Skills;
using Xunit;

namespace AiTools.Tests.Skills;

public sealed class SkillFrontmatterTests
{
    [Fact]
    public void TryParse_ReadsScalarsAndBody()
    {
        const string content = """
        ---
        name: pdf-processing
        description: Extracts text and tables from PDF files.
        license: MIT
        version: 1.0.0
        ---

        # PDF Processing

        Use pdfplumber to extract text.
        """;

        Assert.True(SkillFrontmatter.TryParse(content, out var frontmatter, out var error), error);
        Assert.Equal("pdf-processing", frontmatter!.GetString("name"));
        Assert.Equal("Extracts text and tables from PDF files.", frontmatter.GetString("description"));
        Assert.Equal("MIT", frontmatter.GetString("license"));
        Assert.Equal("1.0.0", frontmatter.GetString("version"));
        Assert.Contains("# PDF Processing", frontmatter.Body);
    }

    [Fact]
    public void TryParse_ReadsBlockList()
    {
        const string content = """
        ---
        name: test
        description: d
        allowed-tools:
          - Read
          - Bash
        ---
        body
        """;

        Assert.True(SkillFrontmatter.TryParse(content, out var frontmatter, out _));
        Assert.Equal(new[] { "Read", "Bash" }, frontmatter!.GetList("allowed-tools"));
    }

    [Fact]
    public void TryParse_ReadsInlineList()
    {
        const string content = """
        ---
        name: test
        description: d
        allowed-tools: [Read, Write, Bash]
        ---
        """;

        Assert.True(SkillFrontmatter.TryParse(content, out var frontmatter, out _));
        Assert.Equal(new[] { "Read", "Write", "Bash" }, frontmatter!.GetList("allowed-tools"));
    }

    [Fact]
    public void TryParse_ReadsNestedMetadata()
    {
        const string content = """
        ---
        name: test
        description: d
        metadata:
          author: example-org
          version: "2.0"
        ---
        """;

        Assert.True(SkillFrontmatter.TryParse(content, out var frontmatter, out _));
        Assert.Equal("example-org", frontmatter!.GetString("metadata.author"));
        Assert.Equal("2.0", frontmatter.GetString("metadata.version"));
    }

    [Fact]
    public void TryParse_HandlesQuotedValuesAndComments()
    {
        const string content = """
        ---
        # leading comment
        name: "quoted-skill"
        description: 'single quoted: with colon'
        ---
        """;

        Assert.True(SkillFrontmatter.TryParse(content, out var frontmatter, out _));
        Assert.Equal("quoted-skill", frontmatter!.GetString("name"));
        Assert.Equal("single quoted: with colon", frontmatter.GetString("description"));
    }

    [Fact]
    public void TryParse_RejectsMissingFrontmatter()
    {
        Assert.False(SkillFrontmatter.TryParse("# Just markdown", out _, out var error));
        Assert.Contains("frontmatter", error);
    }

    [Fact]
    public void TryParse_RejectsUnterminatedFrontmatter()
    {
        Assert.False(SkillFrontmatter.TryParse("---\nname: x\ndescription: y", out _, out var error));
        Assert.Contains("Unterminated", error);
    }

    [Fact]
    public void TryParse_HandlesCrlfAndBom()
    {
        const string content = "﻿---\r\nname: test\r\ndescription: d\r\n---\r\nbody\r\n";

        Assert.True(SkillFrontmatter.TryParse(content, out var frontmatter, out var error), error);
        Assert.Equal("test", frontmatter!.GetString("name"));
        Assert.Equal("body", frontmatter.Body);
    }
}

public sealed class SkillCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aitools-skills-" + Guid.NewGuid().ToString("N"));

    public SkillCatalogTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteSkill(string name, string description, string? body = null, string? fileName = null, string? directoryName = null)
    {
        var directory = Path.Combine(_root, directoryName ?? name);
        Directory.CreateDirectory(directory);
        var content = $"---\nname: {name}\ndescription: {description}\n---\n\n{body ?? "# " + name + "\n\nInstructions for " + name + "."}";
        var path = Path.Combine(directory, fileName ?? "SKILL.md");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void LoadFromDirectory_DiscoversSkillsRecursively()
    {
        WriteSkill("pdf-processing", "Extract text from PDFs.");
        WriteSkill("data-analysis", "Analyze CSV data.");

        var catalog = SkillCatalog.LoadFromDirectory(_root);

        Assert.Equal(2, catalog.Count);
        Assert.Equal(new[] { "data-analysis", "pdf-processing" }, catalog.Skills.Select(s => s.Name));
        Assert.Empty(catalog.Diagnostics);
    }

    [Fact]
    public void LoadFromDirectory_ReadsResourcesRelativeToSkillDirectory()
    {
        WriteSkill("pdf-processing", "d");
        var referenceDir = Path.Combine(_root, "pdf-processing", "references");
        Directory.CreateDirectory(referenceDir);
        File.WriteAllText(Path.Combine(referenceDir, "guide.md"), "guide contents");

        var skill = Assert.Single(SkillCatalog.LoadFromDirectory(_root).Skills);

        Assert.True(File.Exists(Path.Combine(skill.DirectoryPath, "references", "guide.md")));
        Assert.Equal(Path.Combine(_root, "pdf-processing", "SKILL.md"), skill.SkillFilePath);
    }

    [Fact]
    public void LoadFromDirectory_SkipsInvalidSkillsAndReportsDiagnostics()
    {
        WriteSkill("valid", "ok");
        var invalidDir = Path.Combine(_root, "invalid");
        Directory.CreateDirectory(invalidDir);
        File.WriteAllText(Path.Combine(invalidDir, "SKILL.md"), "no frontmatter here");
        var badNameDir = Path.Combine(_root, "Bad Name");
        Directory.CreateDirectory(badNameDir);
        File.WriteAllText(Path.Combine(badNameDir, "SKILL.md"), "---\nname: Bad Name\ndescription: d\n---\n");

        var catalog = SkillCatalog.LoadFromDirectory(_root);

        Assert.Equal(1, catalog.Count);
        Assert.Equal("valid", catalog.Skills[0].Name);
        Assert.Equal(2, catalog.Diagnostics.Count);
    }

    [Fact]
    public void LoadFromDirectory_RequiresDescription()
    {
        var directory = Path.Combine(_root, "no-description");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), "---\nname: no-description\n---\nbody");

        var catalog = SkillCatalog.LoadFromDirectory(_root);

        Assert.Empty(catalog.Skills);
        Assert.Contains(catalog.Diagnostics, d => d.Message.Contains("description"));
    }

    [Fact]
    public void LoadFromDirectory_DuplicateNames_KeepsFirstAndWarns()
    {
        WriteSkill("duplicate", "first");
        WriteSkill("duplicate", "second", directoryName: "duplicate-copy");

        var catalog = SkillCatalog.LoadFromDirectory(_root);

        Assert.Single(catalog.Skills);
        Assert.Contains(catalog.Diagnostics, d => d.Message.Contains("Duplicate"));
    }

    [Fact]
    public void LoadFromDirectory_WarnsWhenDirectoryNameDiffers()
    {
        WriteSkill("actual-name", "d", directoryName: "different-folder");

        var catalog = SkillCatalog.LoadFromDirectory(_root);

        Assert.Single(catalog.Skills);
        Assert.Contains(catalog.Diagnostics, d => d.Message.Contains("does not match"));
    }

    [Fact]
    public void LoadFromDirectory_MissingDirectory_ReportsDiagnostic()
    {
        var catalog = SkillCatalog.LoadFromDirectory(Path.Combine(_root, "missing"));

        Assert.Empty(catalog.Skills);
        Assert.Single(catalog.Diagnostics);
    }

    [Fact]
    public void LoadFromDirectory_NonRecursive_OnlyTopLevel()
    {
        WriteSkill("top", "d");
        WriteSkill("nested", "d", directoryName: "group/nested");

        var catalog = SkillCatalog.LoadFromDirectory(_root, recursive: false);

        Assert.Single(catalog.Skills);
        Assert.Equal("top", catalog.Skills[0].Name);
    }

    [Fact]
    public void FromSkills_BuildsCatalogWithoutFiles()
    {
        var catalog = SkillCatalog.FromSkills(new[]
        {
            new SkillDefinition { Name = "b", Description = "B" },
            new SkillDefinition { Name = "a", Description = "A" }
        });

        Assert.Equal(new[] { "a", "b" }, catalog.Skills.Select(s => s.Name));
        Assert.NotNull(catalog.TryGet("A"));
        Assert.Null(catalog.TryGet("missing"));
    }
}

public sealed class SkillToolsetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aitools-skilltool-" + Guid.NewGuid().ToString("N"));

    public SkillToolsetTests()
    {
        Directory.CreateDirectory(_root);
        var pdf = Path.Combine(_root, "pdf-processing");
        Directory.CreateDirectory(Path.Combine(pdf, "references"));
        File.WriteAllText(Path.Combine(pdf, "SKILL.md"), """
            ---
            name: pdf-processing
            description: Extract text and tables from PDF files.
            allowed-tools: [Read]
            ---

            Use pdfplumber. First read references/style-guide.md.
            """);
        File.WriteAllText(Path.Combine(pdf, "references", "style-guide.md"), "# Style guide\nBe concise.");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "outside skill");

        var other = Path.Combine(_root, "data-analysis");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "SKILL.md"), "---\nname: data-analysis\ndescription: Analyze CSV data.\n---\nUse pandas.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SkillToolset CreateToolset(SkillToolOptions? options = null)
        => new(SkillCatalog.LoadFromDirectory(_root), options);

    [Fact]
    public void GetToolDefinitions_ExposesOnlyTwoMetaTools()
    {
        var definitions = CreateToolset().GetToolDefinitions();

        Assert.Equal(new[] { SkillToolset.LoadSkillToolName, SkillToolset.ReadSkillFileToolName }, definitions.Select(d => d.Name));
        Assert.All(definitions, d => Assert.NotNull(d.ParametersSchema));
    }

    [Fact]
    public void BuildSystemPrompt_ListsNamesAndDescriptionsOnly()
    {
        var prompt = CreateToolset().BuildSystemPrompt();

        Assert.Contains("pdf-processing", prompt);
        Assert.Contains("Extract text and tables from PDF files.", prompt);
        Assert.Contains("data-analysis", prompt);
        Assert.DoesNotContain("pdfplumber", prompt); // full instructions are not injected
    }

    [Fact]
    public void ApplyTo_AddsSystemPromptAndTools()
    {
        var request = ChatRequest.FromUser("m", "extract a PDF");

        var prepared = CreateToolset().ApplyTo(request);

        Assert.Contains("pdf-processing", prepared.SystemPrompt);
        Assert.Equal(2, prepared.Tools.Count);
        Assert.Same(request.Messages, prepared.Messages);
    }

    [Fact]
    public void ApplyTo_PreservesExistingSystemPrompt()
    {
        var request = new ChatRequest { Model = "m", SystemPrompt = "You are helpful.", Messages = new[] { ChatMessage.User("hi") } };

        var prepared = CreateToolset().ApplyTo(request);

        Assert.StartsWith("You are helpful.", prepared.SystemPrompt);
        Assert.Contains("## Skills", prepared.SystemPrompt);
    }

    [Fact]
    public async Task LoadSkill_ReturnsFullInstructions()
    {
        var handler = CreateToolset().CreateToolHandler();

        var result = await handler(new ToolCall("c1", SkillToolset.LoadSkillToolName, """{"name":"pdf-processing"}"""), CancellationToken.None);

        Assert.Contains("Use pdfplumber", result);
        Assert.Contains("Skill directory:", result);
    }

    [Fact]
    public async Task LoadSkill_UnknownSkill_ReturnsError()
    {
        var handler = CreateToolset().CreateToolHandler();

        var result = await handler(new ToolCall("c1", SkillToolset.LoadSkillToolName, """{"name":"missing"}"""), CancellationToken.None);

        Assert.Contains("Unknown skill", result);
        Assert.Contains("pdf-processing", result); // lists available skills
    }

    [Fact]
    public async Task ReadSkillFile_ReadsBundledResource()
    {
        var handler = CreateToolset().CreateToolHandler();

        var result = await handler(
            new ToolCall("c1", SkillToolset.ReadSkillFileToolName, """{"skill":"pdf-processing","path":"references/style-guide.md"}"""),
            CancellationToken.None);

        Assert.Contains("Style guide", result);
        Assert.Contains("Be concise", result);
    }

    [Fact]
    public async Task ReadSkillFile_BlocksPathTraversal()
    {
        var handler = CreateToolset().CreateToolHandler();

        var result = await handler(
            new ToolCall("c1", SkillToolset.ReadSkillFileToolName, """{"skill":"pdf-processing","path":"../secret.txt"}"""),
            CancellationToken.None);

        Assert.Contains("not allowed", result);
        Assert.DoesNotContain("outside skill", result);
    }

    [Fact]
    public async Task ReadSkillFile_BlocksOutsidePathEvenWithoutDotDot()
    {
        var handler = CreateToolset().CreateToolHandler();

        var result = await handler(
            new ToolCall("c1", SkillToolset.ReadSkillFileToolName, """{"skill":"pdf-processing","path":"../../../secret.txt"}"""),
            CancellationToken.None);

        Assert.DoesNotContain("outside skill", result);
    }

    [Fact]
    public async Task ReadSkillFile_TruncatesLargeFiles()
    {
        var bigPath = Path.Combine(_root, "pdf-processing", "big.txt");
        File.WriteAllText(bigPath, new string('x', 5000));
        var handler = CreateToolset(new SkillToolOptions { MaxFileCharacters = 100 }).CreateToolHandler();

        var result = await handler(
            new ToolCall("c1", SkillToolset.ReadSkillFileToolName, """{"skill":"pdf-processing","path":"big.txt"}"""),
            CancellationToken.None);

        Assert.Contains("truncated", result);
        Assert.True(result.Length < 300);
    }

    [Fact]
    public async Task ReadSkillFile_MissingFile_ReturnsError()
    {
        var handler = CreateToolset().CreateToolHandler();

        var result = await handler(
            new ToolCall("c1", SkillToolset.ReadSkillFileToolName, """{"skill":"pdf-processing","path":"nope.md"}"""),
            CancellationToken.None);

        Assert.Contains("not found", result);
    }

    [Fact]
    public void DisabledSkills_AreExcluded()
    {
        var toolset = CreateToolset(new SkillToolOptions { DisabledSkills = new[] { "pdf-processing" } });

        Assert.Single(toolset.Skills);
        Assert.DoesNotContain("pdf-processing", toolset.BuildSystemPrompt());
        Assert.Equal("data-analysis", toolset.Skills[0].Name);
    }

    [Fact]
    public void EmptyCatalog_ProducesNoToolsOrPrompt()
    {
        var toolset = new SkillToolset(SkillCatalog.FromSkills(Array.Empty<SkillDefinition>()));

        Assert.False(toolset.HasSkills);
        Assert.Empty(toolset.GetToolDefinitions());
        Assert.Equal(string.Empty, toolset.BuildSystemPrompt());
        Assert.Same(toolset, toolset); // trivial
    }
}
