using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json.Linq;

namespace Diary.ConfigTool.ViewModels;

public sealed partial class JsonPropertyRowViewModel : ObservableObject
{
    private string _rawValue;
    private bool _revealSensitive;

    public JsonPropertyRowViewModel(string path, string pointer, JTokenType tokenType, string value)
    {
        Path = path;
        Pointer = pointer;
        Type = tokenType.ToString();
        TokenType = tokenType;
        _rawValue = value;
        IsSensitive = IsSensitivePath(path);
    }

    public string Path { get; }
    public string Pointer { get; }
    public string Type { get; }
    public JTokenType TokenType { get; }
    public bool IsSensitive { get; }

    public string Value
    {
        get => IsSensitive && !_revealSensitive ? "••••••••" : _rawValue;
        set
        {
            if (IsSensitive && !_revealSensitive)
                return;
            if (SetProperty(ref _rawValue, value))
                OnPropertyChanged();
        }
    }

    public JToken ToToken()
        => TokenType switch
        {
            JTokenType.Boolean when bool.TryParse(_rawValue, out var boolean) => new JValue(boolean),
            JTokenType.Integer when long.TryParse(_rawValue, out var integer) => new JValue(integer),
            JTokenType.Float when double.TryParse(
                _rawValue,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) => new JValue(number),
            JTokenType.Null when string.IsNullOrWhiteSpace(_rawValue) => JValue.CreateNull(),
            JTokenType.Boolean => throw new FormatException($"{Path} 需要 true 或 false。"),
            JTokenType.Integer => throw new FormatException($"{Path} 需要整数。"),
            JTokenType.Float => throw new FormatException($"{Path} 需要数字。"),
            _ => new JValue(_rawValue),
        };

    public void SetRevealSensitive(bool reveal)
    {
        if (_revealSensitive == reveal)
            return;
        _revealSensitive = reveal;
        OnPropertyChanged(nameof(Value));
    }

    private static bool IsSensitivePath(string path)
    {
        var name = path.ToLowerInvariant();
        return name.Contains("password", StringComparison.Ordinal)
               || name.Contains("secret", StringComparison.Ordinal)
               || name.Contains("token", StringComparison.Ordinal)
               || name.Contains("apikey", StringComparison.Ordinal)
               || name.Contains("api_key", StringComparison.Ordinal)
               || name.Contains("credential", StringComparison.Ordinal)
               || name.EndsWith(".key", StringComparison.Ordinal);
    }
}
