using System.Runtime.CompilerServices;
using System.Text;

namespace TreeSpitter;

/// <summary>
/// The keywords of a language in a hash table of their UTF-8 bytes. tree-sitter lexes the word token (identifiers),
/// then runs the keyword automaton from the start of the token: when the keywords are a finite set of strings, the
/// lookup of the bytes of the token gives the same keyword without running the automaton.
/// </summary>
internal sealed class KeywordTable
{
    /// <summary>The result of <see cref="Lookup"/> when the token isn't a keyword.</summary>
    public const int NotFound = -1;

    /// <summary>The result of <see cref="Lookup"/> when the keyword automaton must decide (a keyword that is a prefix of another).</summary>
    public const int UseAutomaton = -2;

    private const int MaxKeywords = 4096;
    private const int MaxLength = 64;

    private readonly byte[][] _keys;
    private readonly int[] _values;
    private readonly int _mask;

    private KeywordTable(List<(byte[] Key, int Value)> entries)
    {
        var size = 16;
        while (size < entries.Count * 4)
        {
            size *= 2;
        }

        _keys = new byte[size][];
        _values = new int[size];
        _mask = size - 1;
        foreach (var (key, value) in entries)
        {
            var index = (int)Hash(key, 0, key.Length) & _mask;
            while (_keys[index] is not null)
            {
                index = (index + 1) & _mask;
            }

            _keys[index] = key;
            _values[index] = value;
        }
    }

    public int Count { get; private set; }

    /// <summary>The keyword symbol of a token, <see cref="NotFound"/> or <see cref="UseAutomaton"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Lookup(byte[] input, int start, int end)
    {
        var length = end - start;
        if (length > MaxLength)
        {
            return NotFound;
        }

        var index = (int)Hash(input, start, length) & _mask;
        while (true)
        {
            var key = _keys[index];
            if (key is null)
            {
                return NotFound;
            }

            if (key.Length == length && new ReadOnlySpan<byte>(input, start, length).SequenceEqual(key))
            {
                return _values[index];
            }

            index = (index + 1) & _mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(byte[] bytes, int start, int length)
    {
        var hash = 2166136261u ^ (uint)length;
        for (var i = 0; i < length; i++)
        {
            hash = (hash ^ bytes[start + i]) * 16777619u;
        }

        return hash ^ (hash >> 15);
    }

    /// <summary>
    /// Builds the table of a keyword automaton, or returns null when it doesn't accept a small finite set of strings
    /// (patterns, case-insensitive keywords).
    /// </summary>
    public static KeywordTable? Create(LexAutomaton automaton)
    {
        if (automaton.StateCount == 0)
        {
            return null;
        }

        var accepted = new List<(string Text, int Symbol)>();
        var path = new StringBuilder();
        var onPath = new HashSet<int>();
        if (!Enumerate(automaton, 0, path, onPath, accepted, true))
        {
            return null;
        }

        var texts = new HashSet<string>(accepted.Select(a => a.Text), StringComparer.Ordinal);
        var entries = new List<(byte[] Key, int Value)>();
        foreach (var (text, symbol) in accepted)
        {
            // A keyword which is a prefix of another one: the automaton reads past its end, let it decide
            var isPrefix = texts.Any(other => other.Length > text.Length && other.StartsWith(text, StringComparison.Ordinal));
            entries.Add((Encoding.UTF8.GetBytes(text), isPrefix ? UseAutomaton : symbol));
        }

        return new KeywordTable(entries) { Count = entries.Count };
    }

    private static bool Enumerate(LexAutomaton automaton, int state, StringBuilder path, HashSet<int> onPath, List<(string Text, int Symbol)> accepted, bool isStart)
    {
        if (accepted.Count > MaxKeywords || path.Length > MaxLength || !onPath.Add(state))
        {
            // Too many keywords, or a cycle: not a finite set of strings
            return false;
        }

        if (!isStart && automaton.Accept[state] >= 0)
        {
            accepted.Add((path.ToString(), automaton.Accept[state]));
        }

        for (var i = automaton.TransitionStart[state]; i < automaton.TransitionStart[state + 1]; i++)
        {
            var target = automaton.RangeTarget[i];
            if ((target & LexAutomaton.SkipFlag) != 0)
            {
                // The separators before the keywords: the token starts after them
                continue;
            }

            var low = automaton.RangeStart[i];
            var high = automaton.RangeEnd[i];
            if (high - low > 4 || low is >= 0xD800 and <= 0xDFFF || high > 0x10FFFF)
            {
                return false;
            }

            for (var c = low; c <= high; c++)
            {
                var length = path.Length;
                path.Append(char.ConvertFromUtf32(c));
                if (!Enumerate(automaton, target, path, onPath, accepted, false))
                {
                    return false;
                }

                path.Length = length;
            }
        }

        onPath.Remove(state);
        return true;
    }
}
