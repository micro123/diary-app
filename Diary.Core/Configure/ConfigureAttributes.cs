using System.Numerics;

namespace Diary.Core.Configure;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public abstract class ConfigureAttribute(
    ConfigureItemType type,
    string caption)
    : Attribute
{
    public ConfigureItemType Type { get; } = type;
    public string Caption { get; } = caption;
}

public class ConfigureTextAttribute(string caption, bool password = false)
    : ConfigureAttribute(ConfigureItemType.Text, caption)
{
    public bool IsPassword { get; } = password;
}

public class ConfigureIntegralAttribute(string caption, long min, long max)
    : ConfigureAttribute(ConfigureItemType.Integral, caption)
{
    public long Max { get; } = max;
    public long Min { get; } = min;
}

public class ConfigureRealAttribute(string caption, double min, double max)
    : ConfigureAttribute(ConfigureItemType.Real, caption)
{
    public double Max { get; } = max;
    public double Min { get; } = min;
}

public class ConfigureSwitchAttribute(string caption)
    : ConfigureAttribute(ConfigureItemType.Switch, caption)
{
}

public class ConfigureChoiceAttribute(string caption, params string[] options)
    : ConfigureAttribute(ConfigureItemType.Choice, caption)
{
    public IEnumerable<string> Choices { get; } = options;
}

public class ConfigureUserAttribute(string caption, string key) : ConfigureAttribute(ConfigureItemType.User, caption)
{
    public string Key { get; } = key;
}

public class ConfigureButtonAttribute(string caption, string text, string command)
    : ConfigureAttribute(ConfigureItemType.Button, caption)
{
    public string Text { get; } = text;
    public string Command { get; } = command;
}

public class ConfigurePathAttribute(string caption, bool isFolder = false)
    : ConfigureAttribute(ConfigureItemType.Path, caption)
{
    public bool IsFolder { get; } = isFolder;
}

public class ConfigureGroupAttribute(string caption) : ConfigureAttribute(ConfigureItemType.Group, caption);