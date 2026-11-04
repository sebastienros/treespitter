using System.Globalization;
using System.Text;

namespace TreeSpitter.Lexing;

/// <summary>A node of a parsed regular expression, the equivalent of the HIR of Rust's regex-syntax.</summary>
internal abstract class RegexNode
{
}

/// <summary>Matches the empty string.</summary>
internal sealed class RegexEmpty : RegexNode
{
    public static readonly RegexEmpty Instance = new();
}

/// <summary>A sequence of code points.</summary>
internal sealed class RegexLiteral(int[] codePoints) : RegexNode
{
    public int[] CodePoints { get; } = codePoints;
}

/// <summary>One code point of a set.</summary>
internal sealed class RegexClass(CharSet set) : RegexNode
{
    public CharSet Set { get; } = set;
}

/// <summary>A repetition, <c>Max</c> is -1 when unbounded.</summary>
internal sealed class RegexRepeat(RegexNode content, int min, int max) : RegexNode
{
    public RegexNode Content { get; } = content;

    public int Min { get; } = min;

    public int Max { get; } = max;
}

internal sealed class RegexConcat(IReadOnlyList<RegexNode> items) : RegexNode
{
    public IReadOnlyList<RegexNode> Items { get; } = items;
}

internal sealed class RegexAlternation(IReadOnlyList<RegexNode> alternatives) : RegexNode
{
    public IReadOnlyList<RegexNode> Alternatives { get; } = alternatives;
}

/// <summary>
/// The error thrown for an invalid or unsupported regular expression.
/// </summary>
public sealed class RegexParseException : Exception
{
    /// <summary>Creates an error for a pattern, at an offset of the pattern.</summary>
    public RegexParseException(string pattern, int position, string message)
        : base($"Invalid regular expression /{pattern}/ at {position}: {message}")
    {
        Pattern = pattern;
        Position = position;
    }

    /// <summary>Gets the pattern.</summary>
    public string Pattern { get; }

    /// <summary>Gets the offset of the error in the pattern.</summary>
    public int Position { get; }
}

/// <summary>
/// Parses the regular expressions of a grammar. The syntax is the one of JavaScript regular expressions, with the
/// interpretation of tree-sitter: <c>\w</c>, <c>\d</c> and <c>\s</c> are ASCII classes, <c>.</c> matches anything but
/// <c>\n</c>, and the Rust extensions understood by tree-sitter are accepted (<c>(?i)</c>, <c>\x{...}</c>,
/// <c>\U{...}</c>, POSIX and nested classes). Assertions, lookarounds and back-references aren't supported, as in
/// tree-sitter.
/// </summary>
internal sealed class RegexParser
{
    /// <summary>Parses a pattern, with JavaScript flags (<c>i</c> is honored).</summary>
    public static RegexNode Parse(string pattern, string? flags)
    {
        if (pattern is null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        return new RegexParser(pattern, flags ?? string.Empty).Parse();
    }

    internal static int ReadCodePoint(string text, int offset, out int codePoint)
    {
        if (offset >= text.Length)
        {
            codePoint = -1;
            return 0;
        }

        var c = text[offset];
        if (char.IsHighSurrogate(c) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]))
        {
            codePoint = char.ConvertToUtf32(c, text[offset + 1]);
            return 2;
        }

        codePoint = c;
        return 1;
    }

    private static readonly CharSet Digit = CharSet.Range('0', '9');
    private static readonly CharSet Word = CharSet.FromRanges([('0', '9'), ('A', 'Z'), ('a', 'z'), ('_', '_')]);
    private static readonly CharSet Space = CharSet.FromRanges([('\t', '\r'), (' ', ' ')]);
    private static readonly CharSet AllButNewLine = CharSet.Single('\n').Negate();

    private readonly string _pattern;
    private int _position;
    private bool _ignoreCase;
    private bool _dotAll;

    // Cleared by (?-u): the classes are byte classes
    private bool _unicode = true;

    public RegexParser(string pattern, string flags)
    {
        _pattern = pattern;
        _ignoreCase = flags.IndexOf('i') >= 0;
        _dotAll = false;
    }

    private bool AtEnd => _position >= _pattern.Length;

    private char Current => _pattern[_position];

    public RegexNode Parse()
    {
        var result = ParseDisjunction();
        if (!AtEnd)
        {
            throw Error("unmatched ')'");
        }

        return result;
    }

    private RegexNode ParseDisjunction()
    {
        var alternatives = new List<RegexNode> { ParseAlternative() };
        while (!AtEnd && Current == '|')
        {
            _position++;
            alternatives.Add(ParseAlternative());
        }

        return alternatives.Count == 1 ? alternatives[0] : new RegexAlternation(alternatives);
    }

    private RegexNode ParseAlternative()
    {
        var items = new List<RegexNode>();
        while (!AtEnd && Current != '|' && Current != ')')
        {
            var atom = ParseAtom();
            if (atom is null)
            {
                continue;
            }

            items.Add(ParseQuantifier(atom));
        }

        // Adjacent literals are merged
        var merged = new List<RegexNode>();
        foreach (var item in items)
        {
            if (item is RegexLiteral literal && merged.Count > 0 && merged[merged.Count - 1] is RegexLiteral previous)
            {
                merged[merged.Count - 1] = new RegexLiteral([.. previous.CodePoints, .. literal.CodePoints]);
            }
            else
            {
                merged.Add(item);
            }
        }

        return merged.Count switch
        {
            0 => RegexEmpty.Instance,
            1 => merged[0],
            _ => new RegexConcat(merged),
        };
    }

    private RegexNode ParseQuantifier(RegexNode atom)
    {
        if (AtEnd)
        {
            return atom;
        }

        int min, max;
        switch (Current)
        {
            case '*':
                min = 0;
                max = -1;
                _position++;
                break;
            case '+':
                min = 1;
                max = -1;
                _position++;
                break;
            case '?':
                min = 0;
                max = 1;
                _position++;
                break;
            case '{':
                if (!TryParseBraces(out min, out max))
                {
                    return atom;
                }

                break;
            default:
                return atom;
        }

        // Lazy quantifiers don't change the longest match
        if (!AtEnd && Current == '?')
        {
            _position++;
        }

        if (!AtEnd && Current is '*' or '+' or '?')
        {
            throw Error("nothing to repeat");
        }

        return new RegexRepeat(atom, min, max);
    }

    private bool TryParseBraces(out int min, out int max)
    {
        var start = _position;
        min = max = 0;

        _position++;
        if (!TryParseInt(out min))
        {
            _position = start;
            return false;
        }

        if (!AtEnd && Current == '}')
        {
            _position++;
            max = min;
            return true;
        }

        if (AtEnd || Current != ',')
        {
            _position = start;
            return false;
        }

        _position++;
        if (!AtEnd && Current == '}')
        {
            _position++;
            max = -1;
            return true;
        }

        if (!TryParseInt(out max) || AtEnd || Current != '}')
        {
            _position = start;
            return false;
        }

        _position++;
        if (max < min)
        {
            throw Error("numbers out of order in {} quantifier");
        }

        return true;
    }

    private bool TryParseInt(out int value)
    {
        value = 0;
        var start = _position;
        while (!AtEnd && Current is >= '0' and <= '9')
        {
            value = checked(value * 10 + (Current - '0'));
            _position++;
        }

        return _position > start;
    }

    private RegexNode? ParseAtom()
    {
        var c = Current;
        switch (c)
        {
            case '(':
                return ParseGroup();
            case '[':
                return ByteClass(ParseClass());
            case '.':
                _position++;
                return ByteClass(_dotAll ? CharSet.All : AllButNewLine);
            case '^':
            case '$':
                throw Error("assertions are not supported");
            case '\\':
                return ParseAtomEscape();
            case '*':
            case '+':
            case '?':
                throw Error("nothing to repeat");
            case '{':
                {
                    var start = _position;
                    if (TryParseBraces(out _, out _))
                    {
                        _position = start;
                        throw Error("nothing to repeat");
                    }

                    _position++;
                    return Literal('{');
                }

            default:
                {
                    var length = ReadCodePoint(_pattern, _position, out var codePoint);
                    _position += length;
                    return Literal(codePoint);
                }
        }
    }

    private RegexNode? ParseGroup()
    {
        var groupStart = _position;
        _position++;

        var savedIgnoreCase = _ignoreCase;
        var savedDotAll = _dotAll;
        var savedUnicode = _unicode;

        if (!AtEnd && Current == '?')
        {
            _position++;
            if (AtEnd)
            {
                throw Error("invalid group");
            }

            switch (Current)
            {
                case ':':
                    _position++;
                    break;
                case '=':
                case '!':
                    throw Error("lookahead assertions are not supported");
                case '<' when _position + 1 < _pattern.Length && _pattern[_position + 1] is '=' or '!':
                    throw Error("lookbehind assertions are not supported");
                case '<':
                    SkipGroupName(1);
                    break;
                case 'P' when _position + 1 < _pattern.Length && _pattern[_position + 1] == '<':
                    SkipGroupName(2);
                    break;
                default:
                    {
                        // Inline flags: (?i) applies to the rest of the enclosing group, (?i:...) to its content
                        ParseFlags();
                        if (!AtEnd && Current == ')')
                        {
                            _position++;
                            return null;
                        }

                        if (AtEnd || Current != ':')
                        {
                            throw Error("invalid group");
                        }

                        _position++;
                        break;
                    }
            }
        }

        var content = ParseDisjunction();
        if (AtEnd || Current != ')')
        {
            _position = groupStart;
            throw Error("unterminated group");
        }

        _position++;
        _ignoreCase = savedIgnoreCase;
        _dotAll = savedDotAll;
        _unicode = savedUnicode;
        return content;
    }

    private void SkipGroupName(int prefixLength)
    {
        _position += prefixLength;
        var end = _pattern.IndexOf('>', _position);
        if (end < 0)
        {
            throw Error("invalid group name");
        }

        _position = end + 1;
    }

    private void ParseFlags()
    {
        var enable = true;
        while (!AtEnd && Current != ')' && Current != ':')
        {
            switch (Current)
            {
                case '-':
                    enable = false;
                    break;
                case 'i':
                    _ignoreCase = enable;
                    break;
                case 's':
                    _dotAll = enable;
                    break;
                case 'u':
                    _unicode = enable;
                    break;
                case 'm':
                case 'U':
                case 'R':
                    // Line anchors aren't supported anyway, laziness doesn't change the longest match, Unicode is the default
                    break;
                case 'x':
                    throw Error("the x flag is not supported");
                default:
                    throw Error($"unknown flag '{Current}'");
            }

            _position++;
        }
    }

    private RegexNode ParseAtomEscape()
    {
        var start = _position;
        _position++;
        if (AtEnd)
        {
            throw Error("trailing backslash");
        }

        var c = Current;
        switch (c)
        {
            case 'b':
            case 'B':
            case 'A':
            case 'z':
            case 'Z':
            case 'G':
                throw Error("assertions are not supported");
            case 'k':
                throw Error("back-references are not supported");
            case >= '1' and <= '9':
                throw Error("back-references are not supported");
        }

        _position = start;
        if (TryParseClassEscape(out var set))
        {
            return ByteClass(set);
        }

        return Literal(ParseCharEscape(inClass: false));
    }

    // \d \D \w \W \s \S \p{..} \P{..}
    private bool TryParseClassEscape(out CharSet set)
    {
        set = CharSet.Empty;
        if (_position + 1 >= _pattern.Length || _pattern[_position] != '\\')
        {
            return false;
        }

        var c = _pattern[_position + 1];
        bool negatedClass;
        switch (c)
        {
            case 'd':
            case 'D':
                set = Digit;
                negatedClass = c == 'D';
                break;
            case 'w':
            case 'W':
                set = Word;
                negatedClass = c == 'W';
                break;
            case 's':
            case 'S':
                set = Space;
                negatedClass = c == 'S';
                break;
            case 'p':
            case 'P':
                {
                    _position += 2;
                    string name;
                    if (!AtEnd && Current == '{')
                    {
                        var end = _pattern.IndexOf('}', _position);
                        if (end < 0)
                        {
                            throw Error("unterminated property escape");
                        }

                        name = _pattern.Substring(_position + 1, end - _position - 1);
                        _position = end + 1;
                    }
                    else if (!AtEnd)
                    {
                        name = Current.ToString();
                        _position++;
                    }
                    else
                    {
                        throw Error("invalid property escape");
                    }

                    var negated = c == 'P';
                    if (name.StartsWith("^", StringComparison.Ordinal))
                    {
                        negated = !negated;
                        name = name.Substring(1);
                    }

                    // \p{name!=value} is the negation of \p{name=value} (regex-syntax 0.8.11)
                    var notEqual = name.IndexOf("!=", StringComparison.Ordinal);
                    if (notEqual >= 0)
                    {
                        negated = !negated;
                        name = name.Substring(0, notEqual) + "=" + name.Substring(notEqual + 2);
                    }

                    try
                    {
                        set = UnicodeProperties.Get(name);
                    }
                    catch (NotSupportedException e)
                    {
                        throw Error(e.Message);
                    }

                    set = FoldThenNegate(set, negated);
                    return true;
                }

            default:
                return false;
        }

        set = FoldThenNegate(set, negatedClass);
        _position += 2;
        return true;
    }

    private int ParseCharEscape(bool inClass)
    {
        _position++; // the backslash
        if (AtEnd)
        {
            throw Error("trailing backslash");
        }

        var c = Current;
        _position++;
        switch (c)
        {
            case 't':
                return '\t';
            case 'n':
                return '\n';
            case 'r':
                return '\r';
            case 'f':
                return '\f';
            case 'v':
                return '\v';
            case 'a':
                return 7;
            case 'e':
                return 27;
            case '0':
                return 0;
            case 'b' when inClass:
                return '\b';
            case 'c':
                if (!AtEnd && char.IsLetter(Current) && Current < 128)
                {
                    var letter = Current;
                    _position++;
                    return letter % 32;
                }

                return '\\';
            case 'x':
                return ParseHexEscape(2, 'x');
            case 'u':
                {
                    var value = ParseHexEscape(4, 'u');

                    // A surrogate pair written as two escapes
                    if (value is >= 0xD800 and <= 0xDBFF &&
                        _position + 5 < _pattern.Length && _pattern[_position] == '\\' && _pattern[_position + 1] == 'u')
                    {
                        var saved = _position;
                        _position += 2;
                        var low = ParseHexEscape(4, 'u');
                        if (low is >= 0xDC00 and <= 0xDFFF)
                        {
                            return char.ConvertToUtf32((char)value, (char)low);
                        }

                        _position = saved;
                    }

                    return value;
                }

            case 'U':
                return ParseHexEscape(8, 'U');
            default:
                {
                    // Identity escape, e.g. \/ or \.
                    _position--;
                    var length = ReadCodePoint(_pattern, _position, out var codePoint);
                    _position += length;
                    return codePoint;
                }
        }
    }

    private int ParseHexEscape(int digits, char letter)
    {
        if (!AtEnd && Current == '{')
        {
            var end = _pattern.IndexOf('}', _position);
            if (end < 0)
            {
                throw Error("unterminated escape");
            }

            var hex = _pattern.Substring(_position + 1, end - _position - 1);
            if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value > CharSet.MaxCodePoint)
            {
                throw Error("invalid escape");
            }

            _position = end + 1;
            return value;
        }

        if (_position + digits <= _pattern.Length &&
            int.TryParse(_pattern.Substring(_position, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var fixedValue))
        {
            _position += digits;
            return fixedValue;
        }

        // Not an escape: the letter itself, as JavaScript does without the u flag
        return letter;
    }

    private CharSet ParseClass()
    {
        var classStart = _position;
        _position++; // [

        var negated = false;
        if (!AtEnd && Current == '^')
        {
            negated = true;
            _position++;
        }

        // The code points and ranges, folded at the end, and the sets which are already folded (and negated)
        var leaves = CharSet.Empty;
        var set = CharSet.Empty;
        var first = true;
        while (true)
        {
            if (AtEnd)
            {
                _position = classStart;
                throw Error("unterminated character class");
            }

            var c = Current;
            if (c == ']' && !first)
            {
                _position++;
                break;
            }

            first = false;

            if (c == '[')
            {
                if (TryParsePosixClass(out var posix))
                {
                    set = set.Union(posix);
                }
                else
                {
                    set = set.Union(ParseClass());
                }

                continue;
            }

            var low = ParseClassAtom(out var lowSet);
            if (lowSet is not null)
            {
                set = set.Union(lowSet);
                continue;
            }

            if (!AtEnd && Current == '-' && _position + 1 < _pattern.Length && _pattern[_position + 1] != ']')
            {
                _position++;
                var high = ParseClassAtom(out var highSet);
                if (highSet is not null)
                {
                    // [a-\d]: the dash is a literal
                    leaves = leaves.Union(CharSet.Single(low)).Union(CharSet.Single('-'));
                    set = set.Union(highSet);
                    continue;
                }

                if (high < low)
                {
                    throw Error("range out of order in character class");
                }

                leaves = leaves.Union(CharSet.Range(low, high));
            }
            else
            {
                leaves = leaves.Union(CharSet.Single(low));
            }
        }

        if (_ignoreCase)
        {
            leaves = leaves.CaseFold();
        }

        set = set.Union(leaves);
        return negated ? set.Negate() : set;
    }

    private int ParseClassAtom(out CharSet? set)
    {
        set = null;
        if (Current == '\\')
        {
            if (TryParseClassEscape(out var escapeSet))
            {
                set = escapeSet;
                return -1;
            }

            return ParseCharEscape(inClass: true);
        }

        var length = ReadCodePoint(_pattern, _position, out var codePoint);
        _position += length;
        return codePoint;
    }

    private bool TryParsePosixClass(out CharSet set)
    {
        set = CharSet.Empty;
        if (_position + 1 >= _pattern.Length || _pattern[_position + 1] != ':')
        {
            return false;
        }

        var end = _pattern.IndexOf(":]", _position + 2, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        var name = _pattern.Substring(_position + 2, end - _position - 2);
        var negated = name.StartsWith("^", StringComparison.Ordinal);
        if (negated)
        {
            name = name.Substring(1);
        }

        var posix = name switch
        {
            "alnum" => CharSet.FromRanges([('0', '9'), ('A', 'Z'), ('a', 'z')]),
            "alpha" => CharSet.FromRanges([('A', 'Z'), ('a', 'z')]),
            "ascii" => CharSet.Range(0, 127),
            "blank" => CharSet.FromChars(" \t"),
            "cntrl" => CharSet.FromRanges([(0, 0x1F), (0x7F, 0x7F)]),
            "digit" => Digit,
            "graph" => CharSet.Range('!', '~'),
            "lower" => CharSet.Range('a', 'z'),
            "print" => CharSet.Range(' ', '~'),
            "punct" => CharSet.FromRanges([('!', '/'), (':', '@'), ('[', '`'), ('{', '~')]),
            "space" => Space,
            "upper" => CharSet.Range('A', 'Z'),
            "word" => Word,
            "xdigit" => CharSet.FromRanges([('0', '9'), ('A', 'F'), ('a', 'f')]),
            _ => null,
        };

        if (posix is null)
        {
            return false;
        }

        _position = end + 2;
        set = FoldThenNegate(posix, negated);
        return true;
    }

    private RegexNode Literal(int codePoint)
    {
        if (_ignoreCase)
        {
            var folded = CharSet.Single(codePoint).CaseFold();
            if (folded.RangeCount > 1 || !folded.Equals(CharSet.Single(codePoint)))
            {
                return new RegexClass(folded);
            }
        }

        return new RegexLiteral([codePoint]);
    }

    /// <summary>
    /// A class, which can't have non-ASCII bytes in a byte class (<c>(?-u:...)</c>): tree-sitter 0.27 rejects them, they
    /// are part of UTF-8 sequences.
    /// </summary>
    private RegexClass ByteClass(CharSet set)
    {
        if (!_unicode)
        {
            foreach (var (start, end) in set.Intersect(CharSet.Range(0, 0xFF)).Ranges)
            {
                if (end >= 0x80)
                {
                    var bytes = start == end ? $"\\x{start:x2}" : $"\\x{start:x2}-\\x{end:x2}";
                    throw Error($"The non-ASCII byte class {bytes} via (?-u:...) is not supported. Remove the `-u` flag.");
                }
            }
        }

        return new RegexClass(set);
    }

    /// <summary>
    /// Like tree-sitter 0.27, the leaves of a class (code points, ranges, properties, POSIX and Perl classes) are
    /// folded, then the negations are applied.
    /// </summary>
    private CharSet FoldThenNegate(CharSet set, bool negated)
    {
        if (_ignoreCase)
        {
            set = set.CaseFold();
        }

        return negated ? set.Negate() : set;
    }

    private RegexParseException Error(string message) => new(_pattern, _position, message);
}
