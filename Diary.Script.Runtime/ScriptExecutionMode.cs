namespace Diary.Script.Runtime;

public interface IScriptExecutionModeProvider
{
    bool UseInProcessExecution { get; }
}

public sealed class ScriptExecutionModeProvider(Func<bool> readUseInProcessExecution)
    : IScriptExecutionModeProvider
{
    private readonly Func<bool> _readUseInProcessExecution =
        readUseInProcessExecution ?? throw new ArgumentNullException(nameof(readUseInProcessExecution));

    public bool UseInProcessExecution => _readUseInProcessExecution();
}
