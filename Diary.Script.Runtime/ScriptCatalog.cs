using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public interface IScriptCatalog
{
    ScriptRegistrationResult Register(IScriptProgramV1 program);
    ScriptRegistrationResult RegisterOrReplace(IScriptProgramV1 program);
    IReadOnlyList<IScriptProgramV1> GetAll();
    bool Remove(string id);
    bool TryGet(string id, out IScriptProgramV1? program);
}

public sealed class ScriptCatalog : IScriptCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IScriptProgramV1> _programs = new(StringComparer.Ordinal);

    public ScriptRegistrationResult Register(IScriptProgramV1 program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var id = program.Descriptor.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return ScriptRegistrationResult.Failure(new ScriptDiagnostic(
                "SCRIPT_ID_INVALID",
                "The script descriptor ID must not be empty.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation));
        }

        lock (_gate)
        {
            if (!_programs.TryAdd(id, program))
            {
                return ScriptRegistrationResult.Failure(new ScriptDiagnostic(
                    "SCRIPT_ID_DUPLICATE",
                    $"A script with ID '{id}' is already registered.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation));
            }
        }

        return ScriptRegistrationResult.Success();
    }

    public ScriptRegistrationResult RegisterOrReplace(IScriptProgramV1 program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var id = program.Descriptor.Id;
        if (string.IsNullOrWhiteSpace(id))
            return Register(program);

        IScriptProgramV1? previous;
        lock (_gate)
        {
            _programs.TryGetValue(id, out previous);
            _programs[id] = program;
        }

        DisposeProgram(previous);
        return ScriptRegistrationResult.Success();
    }

    public IReadOnlyList<IScriptProgramV1> GetAll()
    {
        lock (_gate)
            return _programs.Values.ToArray();
    }

    public bool Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        IScriptProgramV1? program;
        lock (_gate)
        {
            if (!_programs.Remove(id, out program))
                return false;
        }

        DisposeProgram(program);
        return true;
    }

    public bool TryGet(string id, out IScriptProgramV1? program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            return _programs.TryGetValue(id, out program);
    }

    private static void DisposeProgram(IScriptProgramV1? program)
    {
        if (program is IDisposable disposable)
            disposable.Dispose();
    }
}
