namespace Taverna.App.ViewModels;

public sealed record EditorLanguage(string TextMateId, string DisplayName);

public static class EditorLanguages
{
    public static EditorLanguage For(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".csx" => new("csharp", "C#"),
        ".py" => new("python", "Python"),
        ".js" or ".jsx" or ".mjs" => new("javascript", "JavaScript"),
        ".ts" or ".tsx" => new("typescript", "TypeScript"),
        ".json" or ".jsonc" => new("json", "JSON"),
        ".html" or ".htm" => new("html", "HTML"),
        ".css" => new("css", "CSS"),
        ".scss" => new("scss", "SCSS"),
        ".axaml" or ".xaml" => new("xml", "XAML"),
        ".xml" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".resx" or ".config" or ".svg" =>
            new("xml", "XML"),
        ".md" or ".markdown" => new("markdown", "Markdown"),
        ".sh" or ".bash" or ".zsh" => new("shellscript", "Shell"),
        ".ps1" or ".psm1" => new("powershell", "PowerShell"),
        ".bat" or ".cmd" => new("bat", "Batch"),
        ".yml" or ".yaml" => new("yaml", "YAML"),
        ".ini" or ".editorconfig" => new("ini", "INI"),
        ".sql" => new("sql", "SQL"),
        ".go" => new("go", "Go"),
        ".rs" => new("rust", "Rust"),
        ".java" => new("java", "Java"),
        ".c" or ".h" => new("c", "C"),
        ".cpp" or ".hpp" => new("cpp", "C++"),
        ".fs" => new("fsharp", "F#"),
        ".lua" => new("lua", "Lua"),
        ".rb" => new("ruby", "Ruby"),
        ".php" => new("php", "PHP"),
        _ => new("plaintext", "Plain Text")
    };
}
