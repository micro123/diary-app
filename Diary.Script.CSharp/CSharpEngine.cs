using System.Diagnostics.CodeAnalysis;
using Diary.ScriptBase;

namespace Diary.Script.CSharp;

public class CSharpEngine: IScriptEngine
{
    public string Name { get; } = "csharp";
    public bool Cacheable { get; } = true;
    public bool Match(string sourcePath)
    {
        return sourcePath.EndsWith(".cs");
    }

    public bool Build(string source, [NotNullWhen(true)] out IScript? script)
    {
        script = null;
        return false;
    }
}