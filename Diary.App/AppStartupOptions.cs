namespace Diary.App;

public sealed record AppStartupOptions(bool CoreOnly)
{
    public const string CoreOnlyArgument = "--core-only";

    public static AppStartupOptions Default { get; } = new(false);

    public static AppStartupOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return new AppStartupOptions(args.Any(arg =>
            string.Equals(arg, CoreOnlyArgument, StringComparison.OrdinalIgnoreCase)));
    }
}
