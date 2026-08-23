using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Diary.App.Fonts;

internal sealed class UserFontCollection : IFontCollection
{
    private readonly Lock _syncRoot = new();
    private readonly byte[] _fontData;
    private RuntimeFontCollection? _inner;
    private bool _disposed;

    public UserFontCollection(byte[] fontData, string familyName)
    {
        ArgumentNullException.ThrowIfNull(fontData);
        if (string.IsNullOrWhiteSpace(familyName))
            throw new ArgumentException("字体族名称不能为空。", nameof(familyName));

        _fontData = fontData;
        FamilyName = familyName;
    }

    public static Uri CollectionKey { get; } = new("fonts:UserFont", UriKind.Absolute);

    public Uri Key => CollectionKey;

    public string FamilyName { get; }

    public int Count => 1;

    public FontFamily this[int index] => index == 0
        ? new FontFamily(Key, FamilyName)
        : throw new ArgumentOutOfRangeException(nameof(index));

    public bool TryGetGlyphTypeface(
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        return TryGetInner(out var inner)
            && inner.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);
    }

    public bool TryGetFamilyTypefaces(
        string familyName,
        [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
    {
        familyTypefaces = null;
        return TryGetInner(out var inner)
            && inner.TryGetFamilyTypefaces(familyName, out familyTypefaces);
    }

    public bool TryCreateSyntheticGlyphTypeface(
        GlyphTypeface glyphTypeface,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        syntheticGlyphTypeface = null;
        return TryGetInner(out var inner)
            && inner.TryCreateSyntheticGlyphTypeface(
                glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);
    }

    public bool TryGetNearestMatch(
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        return TryGetInner(out var inner)
            && inner.TryGetNearestMatch(familyName, style, weight, stretch, out glyphTypeface);
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
        return TryGetInner(out var inner)
            && inner.TryMatchCharacter(
                codepoint, fontStyle, fontWeight, fontStretch, requestedFamilyName, culture, out typeface);
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
            if (_inner is not null)
                ((IDisposable)_inner).Dispose();
            _inner = null;
        }
    }

    private bool TryGetInner([NotNullWhen(true)] out RuntimeFontCollection? inner)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inner is not null)
            {
                inner = _inner;
                return true;
            }


            inner = _inner = new RuntimeFontCollection(Key, _fontData, FamilyName);
            return true;
        }
    }

    private sealed class RuntimeFontCollection : FontCollectionBase
    {
        public RuntimeFontCollection(Uri key, byte[] fontData, string familyName)
        {
            Key = key;
            using var stream = new MemoryStream(fontData, writable: false);
            if (!TryAddGlyphTypeface(stream, out var glyphTypeface) || glyphTypeface is null)
                throw new InvalidDataException($"无法加载字体文件中的字体族“{familyName}”。");
        }

        public override Uri Key { get; }
    }
}
