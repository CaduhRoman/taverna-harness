using Taverna.Core.Projects;
using Xunit;

namespace Taverna.Core.Tests;

public sealed class ProjectFilesTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("taverna-files-").FullName;
    private readonly ProjectFiles files = new();

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void CreateFile_CreatesEmptyFileInsideParent()
    {
        var folder = files.CreateDirectory(root, root, "src");
        var path = files.CreateFile(root, folder, "Program.cs");
        Assert.Equal(Path.Combine(root, "src", "Program.cs"), path);
        Assert.Equal("", File.ReadAllText(path));
    }

    [Fact]
    public void CreateFile_RejectsExistingEntry()
    {
        File.WriteAllText(Path.Combine(root, "keep.txt"), "original");
        Assert.Throws<IOException>(() => files.CreateFile(root, root, "keep.txt"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "keep.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("..")]
    [InlineData("../escape.txt")]
    [InlineData("nested/file.txt")]
    public void CreateFile_RejectsInvalidNames(string name) =>
        Assert.Throws<ArgumentException>(() => files.CreateFile(root, root, name));

    [Fact]
    public void CreateDirectory_RejectsParentOutsideProject()
    {
        var outside = Path.GetDirectoryName(root)!;
        Assert.Throws<InvalidOperationException>(() => files.CreateDirectory(root, outside, "x"));
        Assert.Throws<InvalidOperationException>(() => files.CreateDirectory(root, root + "-sibling", "x"));
    }

    [Theory]
    [InlineData("Program.cs", FileKind.CSharp)]
    [InlineData("Taverna.sln", FileKind.Solution)]
    [InlineData("Taverna.App.csproj", FileKind.Project)]
    [InlineData("appsettings.json", FileKind.Json)]
    [InlineData("MainWindow.axaml", FileKind.Markup)]
    [InlineData("README.md", FileKind.Markdown)]
    [InlineData("logo.PNG", FileKind.Image)]
    [InlineData("ci.yml", FileKind.Config)]
    [InlineData(".env.local", FileKind.Config)]
    [InlineData(".gitignore", FileKind.Git)]
    [InlineData("package-lock.json", FileKind.Lock)]
    [InlineData("run.ps1", FileKind.Script)]
    [InlineData("unknown.xyz", FileKind.File)]
    public void Classify_RecognizesCommonFiles(string name, FileKind expected) =>
        Assert.Equal(expected, FileKinds.Classify(name, isDirectory: false));

    [Fact]
    public void Classify_DirectoriesAreFolders() =>
        Assert.Equal(FileKind.Folder, FileKinds.Classify("Program.cs", isDirectory: true));
}
