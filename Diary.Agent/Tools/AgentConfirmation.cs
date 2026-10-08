using Diary.ScriptHost;

namespace Diary.Agent.Tools;

public enum AgentConfirmationDecision
{
    Confirm,
    EditAndConfirm,
    Reject,
}

public sealed record WorkItemConfirmationRequest(
    Guid ConfirmationId,
    Guid RunId,
    Guid InvocationId,
    WorkItemCreateCommand Command,
    string PreviewVersion);

public sealed record WorkItemConfirmationResponse(
    AgentConfirmationDecision Decision,
    WorkItemCreateCommand? EditedCommand = null);

public sealed record ExternalToolConfirmationRequest(
    Guid ConfirmationId,
    Guid RunId,
    Guid InvocationId,
    string ServerName,
    string ToolName,
    System.Text.Json.JsonElement Arguments);

public interface IAgentConfirmationService
{
    ValueTask<WorkItemConfirmationResponse> ConfirmWorkItemAsync(
        WorkItemConfirmationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<AgentConfirmationDecision> ConfirmExternalToolAsync(
        ExternalToolConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AgentConfirmationCoordinator : IAgentConfirmationService
{
    private readonly object _sync = new();
    private PendingConfirmation? _pending;
    private PendingExternalConfirmation? _pendingExternal;

    public event EventHandler<WorkItemConfirmationRequest>? ConfirmationRequested;

    public event EventHandler<ExternalToolConfirmationRequest>? ExternalConfirmationRequested;

    public event Action<Guid>? ConfirmationCompleted;

    public WorkItemConfirmationRequest? Current
    {
        get
        {
            lock (_sync)
                return _pending?.Request;
        }
    }

    public ExternalToolConfirmationRequest? CurrentExternal
    {
        get
        {
            lock (_sync)
                return _pendingExternal?.Request;
        }
    }

    public ValueTask<WorkItemConfirmationResponse> ConfirmWorkItemAsync(
        WorkItemConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        PendingConfirmation pending;
        lock (_sync)
        {
            if (_pending is not null || _pendingExternal is not null)
                throw new InvalidOperationException("已有写操作正在等待确认。");
            pending = new PendingConfirmation(request);
            _pending = pending;
        }
        var registration = cancellationToken.Register(() => Complete(
            request.ConfirmationId,
            new WorkItemConfirmationResponse(AgentConfirmationDecision.Reject)));
        pending.Completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            ConfirmationRequested?.Invoke(this, request);
        }
        catch
        {
            Complete(request.ConfirmationId, new WorkItemConfirmationResponse(AgentConfirmationDecision.Reject));
        }
        return new ValueTask<WorkItemConfirmationResponse>(pending.Completion.Task);
    }

    public ValueTask<AgentConfirmationDecision> ConfirmExternalToolAsync(
        ExternalToolConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        PendingExternalConfirmation pending;
        lock (_sync)
        {
            if (_pending is not null || _pendingExternal is not null)
                throw new InvalidOperationException("已有写操作正在等待确认。");
            pending = new PendingExternalConfirmation(request);
            _pendingExternal = pending;
        }
        var registration = cancellationToken.Register(() =>
            CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject));
        pending.Completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            ExternalConfirmationRequested?.Invoke(this, request);
        }
        catch
        {
            CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject);
        }
        return new ValueTask<AgentConfirmationDecision>(pending.Completion.Task);
    }

    public bool Complete(Guid confirmationId, WorkItemConfirmationResponse response)
    {
        PendingConfirmation? pending;
        lock (_sync)
        {
            if (_pending?.Request.ConfirmationId != confirmationId)
                return false;
            pending = _pending;
            _pending = null;
        }
        var completed = pending.Completion.TrySetResult(response);
        if (completed)
        {
            try { ConfirmationCompleted?.Invoke(confirmationId); }
            catch { }
        }
        return completed;
    }

    public bool CompleteExternal(Guid confirmationId, AgentConfirmationDecision decision)
    {
        PendingExternalConfirmation? pending;
        lock (_sync)
        {
            if (_pendingExternal?.Request.ConfirmationId != confirmationId)
                return false;
            pending = _pendingExternal;
            _pendingExternal = null;
        }
        var completed = pending.Completion.TrySetResult(decision);
        if (completed)
        {
            try { ConfirmationCompleted?.Invoke(confirmationId); }
            catch { }
        }
        return completed;
    }

    public bool RejectCurrent()
    {
        var current = Current;
        if (current is not null)
        {
            return Complete(
                current.ConfirmationId,
                new WorkItemConfirmationResponse(AgentConfirmationDecision.Reject));
        }
        var external = CurrentExternal;
        return external is not null
               && CompleteExternal(external.ConfirmationId, AgentConfirmationDecision.Reject);
    }

    private sealed class PendingConfirmation(WorkItemConfirmationRequest request)
    {
        public WorkItemConfirmationRequest Request { get; } = request;

        public TaskCompletionSource<WorkItemConfirmationResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PendingExternalConfirmation(ExternalToolConfirmationRequest request)
    {
        public ExternalToolConfirmationRequest Request { get; } = request;

        public TaskCompletionSource<AgentConfirmationDecision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
