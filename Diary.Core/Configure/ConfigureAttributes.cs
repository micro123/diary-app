using System.Numerics;

namespace Diary.Core.Configure;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public abstract class ConfigureAttribute(
    ConfigureItemType type,
    string caption,
    string helpTip = "")
    : Attribute
{
    public ConfigureItemType Type { get; } = type;
    public string Caption { get; } = caption;
    public string HelpTip { get; } = helpTip;
}

public class ConfigureTextAttribute(string caption, bool password = false, string helpTip = "")
    : ConfigureAttribute(ConfigureItemType.Text, caption, helpTip)
{
    public bool IsPassword { get; } = password;
}

public class ConfigureIntegralAttribute(string caption, long min, long max, string helpTip = "")
    : ConfigureAttribute(ConfigureItemType.Integral, caption, helpTip)
{
    public long Max { get; } = max;
    public long Min { get; } = min;
}

public class ConfigureRealAttribute(string caption, double min, double max, string helpTip = "")
    : ConfigureAttribute(ConfigureItemType.Real, caption, helpTip)
{
    public double Max { get; } = max;
    public double Min { get; } = min;
}

public class ConfigureSwitchAttribute(string caption, string helpTip = "")
    : ConfigureAttribute(ConfigureItemType.Switch, caption, helpTip)
{
}

public class ConfigureChoiceAttribute(string caption, string helpTip = "", params string[] options)
    : ConfigureAttribute(ConfigureItemType.Choice, caption, helpTip)
{
    public IEnumerable<string> Choices { get; } = options;
}

public class ConfigureUserAttribute(string caption, string key, string helpTip = "") : ConfigureAttribute(ConfigureItemType.User, caption, helpTip)
{
    public string Key { get; } = key;
}

public class ConfigureButtonAttribute(string caption, string text, string command, string helpTip = "")
    : ConfigureAttribute(ConfigureItemType.Button, caption, helpTip)
{
    public string Text { get; } = text;
    public string Command { get; } = command;
}

public class ConfigurePathAttribute(string caption, bool isFolder = false, string helpTip = "")
    : ConfigureAttribute(ConfigureItemType.Path, caption, helpTip)
{
    public bool IsFolder { get; } = isFolder;
}

public class ConfigureGroupAttribute(string caption, string helpTip = "") : ConfigureAttribute(ConfigureItemType.Group, caption, helpTip);