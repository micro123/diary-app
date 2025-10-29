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


public class ConfigureTextAttribute(string caption)
    : ConfigureAttribute(ConfigureItemType.Text, caption)
{
}

public class ConfigureIntegralAttribute<T>(string caption, T min, T max)
    : ConfigureAttribute(ConfigureItemType.Integral, caption) where T: INumber<T>
{
    public T Max { get; } = max;
    public T Min { get; } = min;
}

public class ConfigureRealAttribute<T>(string caption, T min, T max)
    : ConfigureAttribute(ConfigureItemType.Integral, caption) where T : IFloatingPoint<T>
{
    public T Max { get; } = max;
    public T Min { get; } = min;
}

public class ConfigureSwitchAttribute(string caption, string onValue = "on", string offValue = "off")
    : ConfigureAttribute(ConfigureItemType.Switch, caption)
{
    public string OnValue { get; } = onValue;
    public string OffValue { get; } = offValue;
}

public class ConfigureChoiceAttribute(string caption, IEnumerable<string> options)
    : ConfigureAttribute(ConfigureItemType.Choice, caption)
{
    public IEnumerable<string> Choices { get; } = options;
}

