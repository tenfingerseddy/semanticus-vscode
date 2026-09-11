namespace Semanticus.Dax.Text;

/// <summary>
/// A zero-based, half-open <c>[Start, End)</c> range of UTF-16 code units (spec §1: "zero-based,
/// half-open UTF-16 spans"). All offsets/lengths count code units, not glyphs; a surrogate pair is
/// width two, an unpaired surrogate width one.
/// </summary>
public readonly struct TextSpan : IEquatable<TextSpan>
{
    public TextSpan(int start, int length)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Start = start;
        Length = length;
    }

    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;
    public bool IsEmpty => Length == 0;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    public bool Contains(int position) => position >= Start && position < End;
    public bool Contains(TextSpan span) => span.Start >= Start && span.End <= End;
    public bool OverlapsWith(TextSpan other) => Math.Max(Start, other.Start) < Math.Min(End, other.End);

    public bool Equals(TextSpan other) => Start == other.Start && Length == other.Length;
    public override bool Equals(object? obj) => obj is TextSpan s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(Start, Length);
    public static bool operator ==(TextSpan a, TextSpan b) => a.Equals(b);
    public static bool operator !=(TextSpan a, TextSpan b) => !a.Equals(b);
    public override string ToString() => $"[{Start}, {End})";
}
