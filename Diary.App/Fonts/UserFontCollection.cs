using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Diary.App.Fonts;

internal sealed class UserFontCollection(byte[] fontData, string familyName) : IFontCollection
{
    private static readonly MethodInfo CreateGlyphTypefaceFromStreamMethod =
        typeof(IFontManagerImpl).GetMethod(
            "TryCreateGlyphTypeface",
            [typeof(Stream), typeof(FontSimulations), typeof(IGlyphTypeface).MakeByRefType()])
        ?? throw new MissingMethodException(
            typeof(IFontManagerImpl).FullName,
            "TryCreateGlyphTypeface(Stream, FontSimulations, out IGlyphTypeface)");

    private readonly Lock _syncRoot = new();
    private readonly Dictionary<FontSimulations, FontEntry> _typefaces = [];
    private IFontManagerImpl? _fontManager;
    private bool _disposed;

    public static Uri CollectionKey { get; } = new("fonts:UserFont", UriKind.Absolute);

    public Uri Key => CollectionKey;

    public string FamilyName { get; } = familyName;

    public int Count => 1;

    public FontFamily this[int index] => index == 0
        ? new FontFamily(Key, FamilyName)
        : throw new ArgumentOutOfRangeException(nameof(index));

    public void Initialize(IFontManagerImpl fontManager)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fontManager = fontManager;
        if (!TryGetOrCreateTypeface(FontSimulations.None, out _))
            throw new InvalidDataException($"无法加载字体文件中的字体族“{FamilyName}”。");
    }

    public bool TryGetGlyphTypeface(
        string requestedFamilyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        if (!string.Equals(requestedFamilyName, FamilyName, StringComparison.OrdinalIgnoreCase))
            return false;

        var simulations = FontSimulations.None;
        if (style != FontStyle.Normal)
            simulations |= FontSimulations.Oblique;
        if (weight >= FontWeight.SemiBold)
            simulations |= FontSimulations.Bold;
        return TryGetOrCreateTypeface(simulations, out glyphTypeface);
    }

    public bool TryMatchCharacter(
        int codepoint,
        FontStyle fontStyle,
        FontWeight fontWeight,
        FontStretch fontStretch,
        string? requestedFamilyName,
        CultureInfo? culture,
        out Typeface typeface)
    {
        typeface = default;
        if (!TryGetGlyphTypeface(
                requestedFamilyName ?? FamilyName,
                fontStyle,
                fontWeight,
                fontStretch,
                out var glyphTypeface)
            || !glyphTypeface.TryGetGlyph((uint)codepoint, out _))
        {
            return false;
        }

        typeface = new Typeface(new FontFamily(Key, FamilyName), fontStyle, fontWeight, fontStretch);
        return true;
    }

    public IEnumerator<FontFamily> GetEnumerator()
    {
        yield return this[0];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var entry in _typefaces.Values)
            {
                entry.GlyphTypeface.Dispose();
                entry.Stream.Dispose();
            }
            _typefaces.Clear();
        }
    }

    private bool TryGetOrCreateTypeface(
        FontSimulations simulations,
        [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_typefaces.TryGetValue(simulations, out var existing))
            {
                glyphTypeface = existing.GlyphTypeface;
                return true;
            }

            if (_fontManager is null)
            {
                glyphTypeface = null;
                return false;
            }

            var stream = new MemoryStream(fontData, writable: false);
            var arguments = new object?[] { stream, simulations, null };
            var created = (bool)(CreateGlyphTypefaceFromStreamMethod.Invoke(_fontManager, arguments) ?? false);
            glyphTypeface = arguments[2] as IGlyphTypeface;
            if (!created || glyphTypeface is null)
            {
                stream.Dispose();
                return false;
            }

            _typefaces.Add(simulations, new FontEntry(stream, glyphTypeface));
            return true;
        }
    }

    private sealed record FontEntry(Stream Stream, IGlyphTypeface GlyphTypeface);
}
