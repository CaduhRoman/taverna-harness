namespace Taverna.Core.Projects;

public enum FileKind
{
    Folder, File, Text, CSharp, Json, Markup, Markdown, Image, Config, Solution, Project,
    Script, Code, Style, Git, Lock, Database
}

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [".gitignore"] = FileKind.Git, [".gitattributes"] = FileKind.Git, [".gitmodules"] = FileKind.Git,
        [".editorconfig"] = FileKind.Config, ["Dockerfile"] = FileKind.Config, ["Makefile"] = FileKind.Config,
        ["package-lock.json"] = FileKind.Lock, ["packages.lock.json"] = FileKind.Lock,
        ["LICENSE"] = FileKind.Text, ["README"] = FileKind.Text
    };

    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = FileKind.CSharp, [".csx"] = FileKind.CSharp,
        [".json"] = FileKind.Json, [".jsonc"] = FileKind.Json,
        [".xml"] = FileKind.Markup, [".xaml"] = FileKind.Markup, [".axaml"] = FileKind.Markup,
        [".html"] = FileKind.Markup, [".htm"] = FileKind.Markup, [".resx"] = FileKind.Markup,
        [".props"] = FileKind.Markup, [".targets"] = FileKind.Markup, [".xsd"] = FileKind.Markup,
        [".md"] = FileKind.Markdown, [".markdown"] = FileKind.Markdown, [".mdx"] = FileKind.Markdown,
        [".png"] = FileKind.Image, [".jpg"] = FileKind.Image, [".jpeg"] = FileKind.Image, [".gif"] = FileKind.Image,
        [".bmp"] = FileKind.Image, [".ico"] = FileKind.Image, [".svg"] = FileKind.Image, [".webp"] = FileKind.Image,
        [".yml"] = FileKind.Config, [".yaml"] = FileKind.Config, [".toml"] = FileKind.Config, [".ini"] = FileKind.Config,
        [".cfg"] = FileKind.Config, [".conf"] = FileKind.Config, [".config"] = FileKind.Config, [".env"] = FileKind.Config,
        [".runsettings"] = FileKind.Config,
        [".sln"] = FileKind.Solution, [".slnx"] = FileKind.Solution,
        [".csproj"] = FileKind.Project, [".fsproj"] = FileKind.Project, [".vbproj"] = FileKind.Project,
        [".vcxproj"] = FileKind.Project, [".proj"] = FileKind.Project,
        [".ps1"] = FileKind.Script, [".psm1"] = FileKind.Script, [".sh"] = FileKind.Script, [".bash"] = FileKind.Script,
        [".zsh"] = FileKind.Script, [".bat"] = FileKind.Script, [".cmd"] = FileKind.Script,
        [".js"] = FileKind.Code, [".jsx"] = FileKind.Code, [".mjs"] = FileKind.Code, [".ts"] = FileKind.Code,
        [".tsx"] = FileKind.Code, [".py"] = FileKind.Code, [".go"] = FileKind.Code, [".rs"] = FileKind.Code,
        [".java"] = FileKind.Code, [".kt"] = FileKind.Code, [".c"] = FileKind.Code, [".h"] = FileKind.Code,
        [".cpp"] = FileKind.Code, [".hpp"] = FileKind.Code, [".rb"] = FileKind.Code, [".php"] = FileKind.Code,
        [".swift"] = FileKind.Code, [".lua"] = FileKind.Code, [".fs"] = FileKind.Code, [".vb"] = FileKind.Code,
        [".css"] = FileKind.Style, [".scss"] = FileKind.Style, [".less"] = FileKind.Style,
        [".lock"] = FileKind.Lock,
        [".db"] = FileKind.Database, [".sqlite"] = FileKind.Database, [".sql"] = FileKind.Database,
        [".txt"] = FileKind.Text, [".log"] = FileKind.Text, [".csv"] = FileKind.Text
    };

    public static FileKind Classify(string name, bool isDirectory)
    {
        if (isDirectory) return FileKind.Folder;
        if (ByName.TryGetValue(name, out var kind)) return kind;
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) return FileKind.Config;
        return ByExtension.TryGetValue(Path.GetExtension(name), out kind) ? kind : FileKind.File;
    }
}
