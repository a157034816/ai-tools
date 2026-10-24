using System;
using System.IO;
using System.Text.Json;

namespace AiTools.Tests.Extensions;

internal sealed class TempScriptPackage : IDisposable
{
    public TempScriptPackage(string name)
    {
        Root = Path.Combine(Path.GetTempPath(), "ai-tools-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "extension.json"), JsonSerializer.Serialize(new { name, kind = "javascript", entry = "index.js" }));
    }

    public string Root { get; }

    public string Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
