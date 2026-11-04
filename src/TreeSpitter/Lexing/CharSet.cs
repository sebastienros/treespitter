using System.Globalization;
using System.Text;

namespace TreeSpitter.Lexing;

/// <summary>
/// An immutable set of Unicode code points, stored as sorted, disjoint ranges.
/// </summary>
internal sealed class CharSet : IEquatable<CharSet>
{
    /// <summary>The greatest Unicode code point.</summary>
    public const int MaxCodePoint = 0x10FFFF;

    /// <summary>The set with no code point.</summary>
    public static readonly CharSet Empty = new([]);

    /// <summary>The set with all the code points.</summary>
    public static readonly CharSet All = new([0, MaxCodePoint]);

    // Pairs of inclusive bounds: [start0, end0, start1, end1, ...]
    private readonly int[] _ranges;

    // Bitmap of the ASCII chars, for the common case
    private readonly ulong _ascii0;
    private readonly ulong _ascii1;

    private CharSet(int[] ranges)
    {
        _ranges = ranges;
        for (var i = 0; i < ranges.Length; i += 2)
        {
            for (var c = ranges[i]; c <= ranges[i + 1] && c < 128; c++)
            {
                if (c < 64)
                {
                    _ascii0 |= 1UL << c;
                }
                else
                {
                    _ascii1 |= 1UL << (c - 64);
                }
            }
        }
    }

    /// <summary>Gets whether the set has no code point.</summary>
    public bool IsEmpty => _ranges.Length == 0;

    /// <summary>The number of ranges.</summary>
    public int RangeCount => _ranges.Length / 2;

    /// <summary>Whether the set contains a code point which isn't ASCII.</summary>
    public bool HasNonAscii => _ranges.Length > 0 && _ranges[_ranges.Length - 1] >= 128;

    /// <summary>Gets the sorted, disjoint and non adjacent inclusive ranges of the set.</summary>
    public IEnumerable<(int Start, int End)> Ranges
    {
        get
        {
            for (var i = 0; i < _ranges.Length; i += 2)
            {
                yield return (_ranges[i], _ranges[i + 1]);
            }
        }
    }

    /// <summary>Returns the set of a single code point.</summary>
    public static CharSet Single(int codePoint) => new([codePoint, codePoint]);

    /// <summary>Returns the set of an inclusive range of code points.</summary>
    public static CharSet Range(int start, int end) => start > end ? Empty : new([start, end]);

    /// <summary>Returns the set of the code points of inclusive ranges, which can overlap.</summary>
    public static CharSet FromRanges(IEnumerable<(int Start, int End)> ranges)
    {
        var list = new List<(int Start, int End)>();
        foreach (var range in ranges)
        {
            if (range.Start <= range.End)
            {
                list.Add(range);
            }
        }

        list.Sort((a, b) => a.Start.CompareTo(b.Start));

        var result = new List<int>();
        foreach (var (start, end) in list)
        {
            var count = result.Count;
            if (count > 0 && start <= result[count - 1] + 1)
            {
                if (end > result[count - 1])
                {
                    result[count - 1] = end;
                }
            }
            else
            {
                result.Add(start);
                result.Add(end);
            }
        }

        return new CharSet(result.ToArray());
    }

    /// <summary>Returns the set of the code points of a string.</summary>
    public static CharSet FromChars(string chars)
    {
        var ranges = new List<(int, int)>();
        for (var i = 0; i < chars.Length; i++)
        {
            int c = chars[i];
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                c = char.ConvertToUtf32(chars[i], chars[i + 1]);
                i++;
            }

            ranges.Add((c, c));
        }

        return FromRanges(ranges);
    }

    /// <summary>Returns whether the set contains a code point.</summary>
    public bool Contains(int codePoint)
    {
        if (codePoint < 64)
        {
            return (_ascii0 & (1UL << codePoint)) != 0;
        }

        if (codePoint < 128)
        {
            return (_ascii1 & (1UL << (codePoint - 64))) != 0;
        }

        // Binary search over the ranges
        var ranges = _ranges;
        int lo = 0, hi = ranges.Length / 2 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (codePoint < ranges[mid * 2])
            {
                hi = mid - 1;
            }
            else if (codePoint > ranges[mid * 2 + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the code points of this set or of another one.</summary>
    public CharSet Union(CharSet other)
    {
        if (other.IsEmpty)
        {
            return this;
        }

        if (IsEmpty)
        {
            return other;
        }

        // The first chars of the tokens are unions of large classes, like the letters of the identifiers
        if (Covers(other))
        {
            return this;
        }

        if (other.Covers(this))
        {
            return other;
        }

        // Both are sorted: merge them, joining the overlapping and adjacent ranges
        var left = _ranges;
        var right = other._ranges;
        var merged = new int[left.Length + right.Length];
        var count = 0;
        int i = 0, j = 0;
        while (i < left.Length || j < right.Length)
        {
            int start, end;
            if (j >= right.Length || (i < left.Length && left[i] <= right[j]))
            {
                start = left[i];
                end = left[i + 1];
                i += 2;
            }
            else
            {
                start = right[j];
                end = right[j + 1];
                j += 2;
            }

            if (count > 0 && start <= merged[count - 1] + 1)
            {
                merged[count - 1] = Math.Max(merged[count - 1], end);
            }
            else
            {
                merged[count++] = start;
                merged[count++] = end;
            }
        }

        Array.Resize(ref merged, count);
        return new CharSet(merged);
    }

    // Whether each range of another set is within a range of this one, the ranges being disjoint and not adjacent
    private bool Covers(CharSet other)
    {
        var ranges = _ranges;
        var i = 0;
        for (var j = 0; j < other._ranges.Length; j += 2)
        {
            while (i < ranges.Length && ranges[i + 1] < other._ranges[j])
            {
                i += 2;
            }

            if (i == ranges.Length || ranges[i] > other._ranges[j] || ranges[i + 1] < other._ranges[j + 1])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns the code points which aren't in this set.</summary>
    public CharSet Negate()
    {
        var result = new List<int>();
        var next = 0;
        for (var i = 0; i < _ranges.Length; i += 2)
        {
            if (_ranges[i] > next)
            {
                result.Add(next);
                result.Add(_ranges[i] - 1);
            }

            next = _ranges[i + 1] + 1;
        }

        if (next <= MaxCodePoint)
        {
            result.Add(next);
            result.Add(MaxCodePoint);
        }

        return new CharSet(result.ToArray());
    }

    /// <summary>Returns the code points of both this set and another one.</summary>
    public CharSet Intersect(CharSet other) => Negate().Union(other.Negate()).Negate();

    /// <summary>Returns the code points of this set which aren't in another one.</summary>
    public CharSet Except(CharSet other) => Intersect(other.Negate());

    /// <summary>Returns whether this set and another one have a code point in common.</summary>
    public bool Overlaps(CharSet other) => !Intersect(other).IsEmpty;

    /// <summary>
    /// Adds the case variants of the chars, like tree-sitter's handling of the <c>i</c> flag: the long s (U+017F) and
    /// the Kelvin sign (U+212A) aren't folded onto the ASCII letters.
    /// </summary>
    public CharSet CaseFold()
    {
        var ranges = new List<(int, int)>(Ranges);
        var budget = 0x30000; // Huge ranges, like negated classes, are already closed under case folding in practice

        foreach (var (start, end) in Ranges)
        {
            if (end - start > budget)
            {
                continue;
            }

            budget -= end - start;

            for (var c = start; c <= end; c++)
            {
                if (c is 0x017F or 0x212A || c > 0xFFFF || c is >= 0xD800 and <= 0xDFFF)
                {
                    continue;
                }

                var ch = (char)c;
                var upper = char.ToUpperInvariant(ch);
                var lower = char.ToLowerInvariant(ch);
                if (upper != ch && !(c < 128 ^ upper < 128))
                {
                    ranges.Add((upper, upper));
                }

                if (lower != ch && !(c < 128 ^ lower < 128))
                {
                    ranges.Add((lower, lower));
                }
            }
        }

        return FromRanges(ranges);
    }

    /// <inheritdoc />
    public bool Equals(CharSet? other) => other is not null && _ranges.AsSpan().SequenceEqual(other._ranges);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CharSet other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            foreach (var value in _ranges)
            {
                hash = hash * 31 + value;
            }

            return hash;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (Equals(All))
        {
            return "[\\s\\S]";
        }

        var builder = new StringBuilder("[");
        var ranges = this;
        if (_ranges.Length > 0 && _ranges[0] == 0)
        {
            builder.Append('^');
            ranges = Negate();
        }

        foreach (var (start, end) in ranges.Ranges)
        {
            AppendChar(builder, start);
            if (end > start)
            {
                if (end > start + 1)
                {
                    builder.Append('-');
                }

                AppendChar(builder, end);
            }
        }

        return builder.Append(']').ToString();
    }

    private static void AppendChar(StringBuilder builder, int c)
    {
        switch (c)
        {
            case '\n':
                builder.Append("\\n");
                break;
            case '\r':
                builder.Append("\\r");
                break;
            case '\t':
                builder.Append("\\t");
                break;
            case '\\':
                builder.Append("\\\\");
                break;
            case ']':
                builder.Append("\\]");
                break;
            case '[':
                builder.Append("\\[");
                break;
            case '-':
                builder.Append("\\-");
                break;
            case '^':
                builder.Append("\\^");
                break;
            default:
                if (c < 0x20 || c is >= 0x7F and < 0xA0 || c is >= 0xD800 and <= 0xDFFF)
                {
                    builder.Append("\\u").Append(c.ToString("X4", CultureInfo.InvariantCulture));
                }
                else if (c > 0xFFFF)
                {
                    builder.Append("\\u{").Append(c.ToString("X", CultureInfo.InvariantCulture)).Append('}');
                }
                else
                {
                    builder.Append((char)c);
                }

                break;
        }
    }
}
