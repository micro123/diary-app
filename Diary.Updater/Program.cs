using System.Text.Json;
using Diary.Update;

return await UpdaterProgram.RunAsync(args);

internal static class UpdaterProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--machine-version")
            {
                Console.Write(JsonSerializer.Serialize(
                    new UpdateMachineVersion(UpdateProtocol.UpdaterProtocolVersion, UpdateProtocol.CurrentRid),
                    UpdateJson.CompactContext.UpdateMachineVersion));
                return 0;
            }

            var command = Parse(args);
            var plan = await UpdateTransactionStore.LoadPlanAsync(command.PlanPath);
            var validated = UpdatePlanValidator.Validate(plan, command.PlanPath);
            ValidateRuntime(validated);
            await ValidateCurrentUpdaterAsync(validated, command.HandoffToken is not null);
            if (command.Mode == UpdaterMode.Apply)
                await ValidateTargetUpdaterAsync(validated);

            if (command.WaitProcessId is { } waitProcessId)
            {
                if (command.Mode == UpdaterMode.Apply)
                {
                    var waitingStore = new UpdateTransactionStore(validated);
                    await waitingStore.WriteStatusAsync(UpdateTransactionState.WaitingForExit);
                }
                await UpdateProcessServices.WaitForExitAsync(
                    waitProcessId,
                    TimeSpan.FromSeconds(plan.WaitForExitTimeoutSeconds));
            }

            if (command.Mode == UpdaterMode.Recover)
            {
                var executor = new UpdateTransactionExecutor();
                var state = await executor.RecoverAsync(validated, rollbackApplied: true);
                if (state == UpdateTransactionState.RolledBack && plan.Restart?.PreviousSha256 is not null)
                    executor.StartRecoveredApplication(validated);
                Console.WriteLine($"更新事务已恢复到状态：{state}");
                return 0;
            }

            var store = new UpdateTransactionStore(validated);
            if (command.HandoffToken is not null)
            {
                if (!FixedTimeEquals(command.HandoffToken, plan.HandoffToken))
                    throw new InvalidDataException("更新器交接令牌无效。");
                var status = await store.ReadStatusAsync();
                if (status?.State != UpdateTransactionState.HandingOff)
                    throw new InvalidOperationException("更新事务不处于可领取的交接状态。");
            }
            else if (UpdateProtocol.UpdaterProtocolVersion < plan.MinUpdaterVersion)
            {
                await HandoffAsync(validated, command.PlanPath, store);
                return 0;
            }

            var result = await new UpdateTransactionExecutor().ApplyAsync(validated);
            Console.WriteLine($"更新事务完成：{result.State}");
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"更新失败：{exception.Message}");
            return 1;
        }
    }

    private static UpdaterCommand Parse(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("--apply" or "--recover"))
            throw new ArgumentException("缺少更新器命令或事务计划路径。");
        var mode = args[0] == "--apply" ? UpdaterMode.Apply : UpdaterMode.Recover;
        var planPath = Path.GetFullPath(args[1]);
        int? waitProcessId = null;
        string? handoffToken = null;
        for (var index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"参数 {args[index]} 缺少值。");
            switch (args[index])
            {
                case "--wait-pid" when int.TryParse(args[index + 1], out var processId):
                    waitProcessId = processId;
                    break;
                case "--handoff-token":
                    handoffToken = args[index + 1];
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[index]}");
            }
        }
        if (mode == UpdaterMode.Recover && handoffToken is not null)
            throw new ArgumentException("--recover 不接受交接令牌。");
        return new(mode, planPath, waitProcessId, handoffToken);
    }

    private static void ValidateRuntime(ValidatedUpdatePlan plan)
    {
        if (!string.Equals(plan.Plan.Rid, UpdateProtocol.CurrentRid, StringComparison.Ordinal))
            throw new InvalidDataException($"事务 RID {plan.Plan.Rid} 与当前更新器 {UpdateProtocol.CurrentRid} 不匹配。");
    }

    private static async ValueTask ValidateCurrentUpdaterAsync(ValidatedUpdatePlan plan, bool isHandoff)
    {
        var expected = isHandoff ? plan.Plan.TargetUpdater : plan.Plan.BootstrapUpdater;
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前更新器路径。");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(expected.Path), comparison))
            throw new InvalidDataException("当前更新器路径与事务计划不匹配。");
        var actualHash = await UpdateHash.ComputeSha256Async(processPath);
        if (!string.Equals(actualHash, expected.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("当前更新器哈希与事务计划不匹配。");
    }

    private static async ValueTask HandoffAsync(
        ValidatedUpdatePlan plan,
        string planPath,
        UpdateTransactionStore store)
    {
        var target = plan.Plan.TargetUpdater;
        await store.WriteStatusAsync(UpdateTransactionState.HandoffPrepared);
        await store.WriteStatusAsync(UpdateTransactionState.HandingOff);
        using var process = UpdateProcessServices.StartUpdater(target.Path, planPath, plan.Plan.HandoffToken);
    }

    private static async ValueTask ValidateTargetUpdaterAsync(ValidatedUpdatePlan plan)
    {
        var target = plan.Plan.TargetUpdater;
        var info = new FileInfo(target.Path);
        if (!info.Exists)
            throw new FileNotFoundException("目标更新器不存在。", target.Path);
        await UpdateHash.VerifyFileAsync(target.Path, info.Length, target.Sha256);
        var machineVersion = await UpdateProcessServices.ProbeUpdaterAsync(target.Path);
        if (machineVersion.ProtocolVersion < plan.Plan.MinUpdaterVersion
            || machineVersion.ProtocolVersion != target.ProtocolVersion
            || machineVersion.Rid != plan.Plan.Rid)
            throw new InvalidDataException("目标更新器的协议版本或 RID 与事务计划不匹配。");
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("用法：");
        Console.Error.WriteLine("  Diary.Updater --apply <transaction.json> [--wait-pid <pid>] [--handoff-token <token>]");
        Console.Error.WriteLine("  Diary.Updater --recover <transaction.json>");
        Console.Error.WriteLine("  Diary.Updater --machine-version");
    }

    private enum UpdaterMode
    {
        Apply,
        Recover,
    }

    private sealed record UpdaterCommand(
        UpdaterMode Mode,
        string PlanPath,
        int? WaitProcessId,
        string? HandoffToken);
}
