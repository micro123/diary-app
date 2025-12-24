using System.Diagnostics.CodeAnalysis;

namespace Diary.ScriptBase;

public interface IScriptEngine
{
    string Name { get; }      // 引擎的名字
    bool   Cacheable { get; } // 是否可以缓存，如可以编译为字节码保存起来 
    
    bool Match(string sourcePath); // 是否匹配脚本源
    bool Build(string source, [NotNullWhen(true)]out IScript? script); // 生成脚本，这里可以处理缓存，可以从缓存加载而不需要重新编译
}
