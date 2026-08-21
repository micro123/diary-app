using Diary.Update;

namespace Diary.App;

public sealed record AppStartupOptions(bool CoreOnly, string? UpdateTransactionPath)
{
    public const string CoreOnlyArgument = "--core-only";

    public static AppStartupOptions Default { get; } = new(false, null);

    public static AppStartupOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = args.ToArray();
        var coreOnly = values.Any(arg =>
            string.Equals(arg, CoreOnlyArgument, StringComparison.OrdinalIgnoreCase));
        string? transactionPath = null;
        for (var index = 0; index < values.Length; index++)
        {
            if (!string.Equals(
                    values[index],
                    UpdateProtocol.StartupTransactionArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (index + 1 < values.Length && !string.IsNullOrWhiteSpace(values[index + 1]))
                transactionPath = values[index + 1];
            break;
        }
        return new AppStartupOptions(coreOnly, transactionPath);
    }
}
