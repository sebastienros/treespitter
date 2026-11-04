using System.Text;

namespace TreeSpitter.Generation;

// A port of tree-sitter's cli/generate/src/rules.rs: the intermediate representation of the grammar rules used by the
// parser generator, symbols and token sets.

/// <summary>The kind of a grammar symbol. The order matters: it is the order of tree-sitter's <c>SymbolType</c>.</summary>
internal enum SymbolType : byte
{
    External,
    End,
    EndOfNonTerminalExtra,
    Terminal,
    NonTerminal,
}

internal enum Associativity : byte
{
    Left,
    Right,
}

/// <summary>A symbol of the prepared grammar: a terminal, a non-terminal, an external token or the end of the input.</summary>
internal readonly record struct Symbol(SymbolType Kind, int Index) : IComparable<Symbol>
{
    public static readonly Symbol End = new(SymbolType.End, 0);

    public static readonly Symbol EndOfNonTerminalExtra = new(SymbolType.EndOfNonTerminalExtra, 0);

    public static Symbol Terminal(int index) => new(SymbolType.Terminal, index);

    public static Symbol NonTerminal(int index) => new(SymbolType.NonTerminal, index);

    public static Symbol External(int index) => new(SymbolType.External, index);

    public bool IsTerminal => Kind == SymbolType.Terminal;

    public bool IsNonTerminal => Kind == SymbolType.NonTerminal;

    public bool IsExternal => Kind == SymbolType.External;

    public bool IsEof => Kind == SymbolType.End;

    public int CompareTo(Symbol other)
    {
        // The enums are compared as their values: Enum.CompareTo(object) boxes them
        var result = ((byte)Kind).CompareTo((byte)other.Kind);
        return result != 0 ? result : Index.CompareTo(other.Index);
    }

    public override string ToString() => Kind + "(" + Index + ")";
}

/// <summary>An alias, <c>alias(rule, name)</c>.</summary>
internal sealed record Alias(string Value, bool IsNamed) : IComparable<Alias>
{
    public VariableType Kind => IsNamed ? VariableType.Named : VariableType.Anonymous;

    public int CompareTo(Alias? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = string.CompareOrdinal(Value, other.Value);
        return result != 0 ? result : IsNamed.CompareTo(other.IsNamed);
    }

    public static int Compare(Alias? left, Alias? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return left.CompareTo(right);
    }
}

internal enum PrecedenceValueKind : byte
{
    None,
    Integer,
    Name,
}

/// <summary>A static precedence: none, a number or a name.</summary>
internal readonly record struct Precedence(PrecedenceValueKind Kind, int Value, string? Name) : IComparable<Precedence>
{
    public static readonly Precedence None = default;

    public static Precedence Integer(int value) => new(PrecedenceValueKind.Integer, value, null);

    public static Precedence Named(string name) => new(PrecedenceValueKind.Name, 0, name);

    public bool IsNone => Kind == PrecedenceValueKind.None;

    public int CompareTo(Precedence other)
    {
        var result = ((byte)Kind).CompareTo((byte)other.Kind);
        if (result != 0)
        {
            return result;
        }

        return Kind switch
        {
            PrecedenceValueKind.Integer => Value.CompareTo(other.Value),
            PrecedenceValueKind.Name => string.CompareOrdinal(Name, other.Name),
            _ => 0,
        };
    }

    public override string ToString() => Kind switch
    {
        PrecedenceValueKind.Integer => Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PrecedenceValueKind.Name => "'" + Name + "'",
        _ => "none",
    };
}

/// <summary>The parameters of a metadata rule: precedence, associativity, token, alias and field.</summary>
internal sealed record MetadataParams
{
    public Precedence Precedence { get; set; }

    public int DynamicPrecedence { get; set; }

    public Associativity? Associativity { get; set; }

    public bool IsToken { get; set; }

    public bool IsMainToken { get; set; }

    public Alias? Alias { get; set; }

    public string? FieldName { get; set; }

    public bool IsDefault => Precedence.IsNone && DynamicPrecedence == 0 && Associativity is null && !IsToken && !IsMainToken && Alias is null && FieldName is null;

    public MetadataParams Copy() => this with { };
}

/// <summary>The intermediate representation of the rules, tree-sitter's <c>Rule</c> enum.</summary>
internal abstract class IRule : IEquatable<IRule>
{
    private int _hash;

    public static readonly IRule Blank = new BlankIRule();

    public static IRule Choice(IEnumerable<IRule> rules)
    {
        var elements = new List<IRule>();
        foreach (var rule in rules)
        {
            ChoiceHelper(elements, rule);
        }

        return new ChoiceIRule(elements);
    }

    public static IRule Seq(IReadOnlyList<IRule> rules) => new SeqIRule(rules);

    public static IRule Repeat(IRule rule) => new RepeatIRule(rule);

    public static IRule Field(string name, IRule content) => AddMetadata(content, p => p.FieldName = name);

    public static IRule Alias(IRule content, string value, bool isNamed) => AddMetadata(content, p => p.Alias = new Alias(value, isNamed));

    public static IRule Token(IRule content) => AddMetadata(content, p => p.IsToken = true);

    public static IRule ImmediateToken(IRule content) => AddMetadata(content, p =>
    {
        p.IsToken = true;
        p.IsMainToken = true;
    });

    public static IRule Prec(Precedence value, IRule content) => AddMetadata(content, p => p.Precedence = value);

    public static IRule PrecLeft(Precedence value, IRule content) => AddMetadata(content, p =>
    {
        p.Associativity = Generation.Associativity.Left;
        p.Precedence = value;
    });

    public static IRule PrecRight(Precedence value, IRule content) => AddMetadata(content, p =>
    {
        p.Associativity = Generation.Associativity.Right;
        p.Precedence = value;
    });

    public static IRule PrecDynamic(int value, IRule content) => AddMetadata(content, p => p.DynamicPrecedence = value);

    private static IRule AddMetadata(IRule input, Action<MetadataParams> update)
    {
        if (input is MetadataIRule metadata && !metadata.Params.IsToken)
        {
            var parameters = metadata.Params.Copy();
            update(parameters);
            return new MetadataIRule(parameters, metadata.Rule);
        }

        var newParameters = new MetadataParams();
        update(newParameters);
        return new MetadataIRule(newParameters, input);
    }

    private static void ChoiceHelper(List<IRule> result, IRule rule)
    {
        if (rule is ChoiceIRule choice)
        {
            foreach (var element in choice.Elements)
            {
                ChoiceHelper(result, element);
            }
        }
        else if (!result.Contains(rule))
        {
            result.Add(rule);
        }
    }

    /// <summary>Whether the rule matches the empty string, tree-sitter's <c>Rule::is_empty</c>.</summary>
    public bool IsEmpty() => this switch
    {
        BlankIRule or PatternIRule or NamedSymbolIRule or SymbolIRule => false,
        StringIRule s => s.Value.Length == 0,
        MetadataIRule m => m.Rule.IsEmpty(),
        RepeatIRule r => r.Content.IsEmpty(),
        ReservedIRule r => r.Rule.IsEmpty(),
        ChoiceIRule c => c.Elements.Any(e => e.IsEmpty()),
        SeqIRule s => s.Elements.All(e => e.IsEmpty()),
        _ => false,
    };

    public abstract bool Equals(IRule? other);

    public sealed override bool Equals(object? obj) => obj is IRule other && Equals(other);

    public sealed override int GetHashCode()
    {
        if (_hash == 0)
        {
            var hash = ComputeHash();
            _hash = hash == 0 ? 1 : hash;
        }

        return _hash;
    }

    protected abstract int ComputeHash();

    protected static bool ListEquals(IReadOnlyList<IRule> left, IReadOnlyList<IRule> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    protected static int ListHash(IReadOnlyList<IRule> rules, int seed)
    {
        var hash = seed;
        foreach (var rule in rules)
        {
            hash = unchecked(hash * 31 + rule.GetHashCode());
        }

        return hash;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        Format(builder);
        return builder.ToString();
    }

    internal abstract void Format(StringBuilder builder);
}

internal sealed class BlankIRule : IRule
{
    public override bool Equals(IRule? other) => other is BlankIRule;

    protected override int ComputeHash() => 0x1b1a;

    internal override void Format(StringBuilder builder) => builder.Append("blank");
}

internal sealed class StringIRule(string value) : IRule
{
    public string Value { get; } = value;

    public override bool Equals(IRule? other) => other is StringIRule s && s.Value == Value;

    protected override int ComputeHash() => StringComparer.Ordinal.GetHashCode(Value) ^ 0x51;

    internal override void Format(StringBuilder builder) => builder.Append('"').Append(Value).Append('"');
}

internal sealed class PatternIRule(string value, string flags) : IRule
{
    public string Value { get; } = value;

    public string Flags { get; } = flags;

    public override bool Equals(IRule? other) => other is PatternIRule p && p.Value == Value && p.Flags == Flags;

    protected override int ComputeHash() => StringComparer.Ordinal.GetHashCode(Value) * 7 + StringComparer.Ordinal.GetHashCode(Flags);

    internal override void Format(StringBuilder builder) => builder.Append('/').Append(Value).Append('/').Append(Flags);
}

internal sealed class NamedSymbolIRule(string name) : IRule
{
    public string Name { get; } = name;

    public override bool Equals(IRule? other) => other is NamedSymbolIRule n && n.Name == Name;

    protected override int ComputeHash() => StringComparer.Ordinal.GetHashCode(Name) ^ 0x4e;

    internal override void Format(StringBuilder builder) => builder.Append('$').Append(Name);
}

internal sealed class SymbolIRule(Symbol symbol) : IRule
{
    public Symbol Symbol { get; } = symbol;

    public override bool Equals(IRule? other) => other is SymbolIRule s && s.Symbol == Symbol;

    protected override int ComputeHash() => Symbol.GetHashCode() ^ 0x5359;

    internal override void Format(StringBuilder builder) => builder.Append(Symbol);
}

internal sealed class ChoiceIRule(IReadOnlyList<IRule> elements) : IRule
{
    public IReadOnlyList<IRule> Elements { get; } = elements;

    public override bool Equals(IRule? other) => other is ChoiceIRule c && ListEquals(Elements, c.Elements);

    protected override int ComputeHash() => ListHash(Elements, 0xc401);

    internal override void Format(StringBuilder builder)
    {
        builder.Append("choice(");
        for (var i = 0; i < Elements.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            Elements[i].Format(builder);
        }

        builder.Append(')');
    }
}

internal sealed class SeqIRule(IReadOnlyList<IRule> elements) : IRule
{
    public IReadOnlyList<IRule> Elements { get; } = elements;

    public override bool Equals(IRule? other) => other is SeqIRule s && ListEquals(Elements, s.Elements);

    protected override int ComputeHash() => ListHash(Elements, 0x5e9);

    internal override void Format(StringBuilder builder)
    {
        builder.Append("seq(");
        for (var i = 0; i < Elements.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            Elements[i].Format(builder);
        }

        builder.Append(')');
    }
}

internal sealed class RepeatIRule(IRule content) : IRule
{
    public IRule Content { get; } = content;

    public override bool Equals(IRule? other) => other is RepeatIRule r && r.Content.Equals(Content);

    protected override int ComputeHash() => Content.GetHashCode() * 17 + 0x3e9;

    internal override void Format(StringBuilder builder)
    {
        builder.Append("repeat(");
        Content.Format(builder);
        builder.Append(')');
    }
}

internal sealed class MetadataIRule(MetadataParams parameters, IRule rule) : IRule
{
    public MetadataParams Params { get; } = parameters;

    public IRule Rule { get; } = rule;

    public override bool Equals(IRule? other) => other is MetadataIRule m && m.Params.Equals(Params) && m.Rule.Equals(Rule);

    protected override int ComputeHash() => Rule.GetHashCode() * 13 + Params.GetHashCode();

    internal override void Format(StringBuilder builder)
    {
        builder.Append("meta(");
        if (Params.IsToken)
        {
            builder.Append(Params.IsMainToken ? "immediate " : "token ");
        }

        if (!Params.Precedence.IsNone)
        {
            builder.Append("prec=").Append(Params.Precedence).Append(' ');
        }

        if (Params.Alias is not null)
        {
            builder.Append("alias=").Append(Params.Alias.Value).Append(' ');
        }

        if (Params.FieldName is not null)
        {
            builder.Append("field=").Append(Params.FieldName).Append(' ');
        }

        Rule.Format(builder);
        builder.Append(')');
    }
}

internal sealed class ReservedIRule(IRule rule, string contextName) : IRule
{
    public IRule Rule { get; } = rule;

    public string ContextName { get; } = contextName;

    public override bool Equals(IRule? other) => other is ReservedIRule r && r.ContextName == ContextName && r.Rule.Equals(Rule);

    protected override int ComputeHash() => Rule.GetHashCode() * 11 + StringComparer.Ordinal.GetHashCode(ContextName);

    internal override void Format(StringBuilder builder)
    {
        builder.Append("reserved(").Append(ContextName).Append(", ");
        Rule.Format(builder);
        builder.Append(')');
    }
}

/// <summary>A set of tokens: terminals, external tokens, the end of the input and the end of a non-terminal extra.</summary>
internal sealed class TokenSet : IEquatable<TokenSet>
{
    private ulong[] _terminals;
    private ulong[] _externals;

    public TokenSet()
    {
        _terminals = [];
        _externals = [];
    }

    private TokenSet(ulong[] terminals, ulong[] externals, bool eof, bool endOfNonTerminalExtra)
    {
        _terminals = terminals;
        _externals = externals;
        Eof = eof;
        EndOfNonTerminalExtra = endOfNonTerminalExtra;
    }

    public bool Eof { get; private set; }

    public bool EndOfNonTerminalExtra { get; private set; }

    public static TokenSet Of(params Symbol[] symbols)
    {
        var set = new TokenSet();
        foreach (var symbol in symbols)
        {
            set.Insert(symbol);
        }

        return set;
    }

    public static TokenSet From(IEnumerable<Symbol> symbols)
    {
        var set = new TokenSet();
        foreach (var symbol in symbols)
        {
            set.Insert(symbol);
        }

        return set;
    }

    public TokenSet Clone() => new((ulong[])_terminals.Clone(), (ulong[])_externals.Clone(), Eof, EndOfNonTerminalExtra);

    public bool IsEmpty
    {
        get
        {
            if (Eof || EndOfNonTerminalExtra)
            {
                return false;
            }

            foreach (var word in _terminals)
            {
                if (word != 0)
                {
                    return false;
                }
            }

            foreach (var word in _externals)
            {
                if (word != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public int Count
    {
        get
        {
            var count = (Eof ? 1 : 0) + (EndOfNonTerminalExtra ? 1 : 0);
            foreach (var word in _terminals)
            {
                count += PopCount(word);
            }

            foreach (var word in _externals)
            {
                count += PopCount(word);
            }

            return count;
        }
    }

    private static int PopCount(ulong value)
    {
#if NET
        return System.Numerics.BitOperations.PopCount(value);
#else
        var count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }

        return count;
#endif
    }

    /// <summary>The symbols in the order of tree-sitter: terminals, external tokens, end, end of non-terminal extra.</summary>
    public SymbolCollection Symbols => new(this, terminalsOnly: false);

    public SymbolCollection Terminals => new(this, terminalsOnly: true);

    /// <summary>The symbols of a set, a struct enumerable for <c>foreach</c> (the sets are enumerated millions of times).</summary>
    public readonly struct SymbolCollection(TokenSet set, bool terminalsOnly) : IEnumerable<Symbol>
    {
        public SymbolEnumerator GetEnumerator() => new(set, terminalsOnly);

        IEnumerator<Symbol> IEnumerable<Symbol>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Enumerates the bits of the terminals, then of the external tokens, then the end symbols.</summary>
    public struct SymbolEnumerator(TokenSet set, bool terminalsOnly) : IEnumerator<Symbol>
    {
        // 0: terminals, 1: externals, 2: end, 3: end of non-terminal extra, 4: done
        private int _part;
        private int _word = -1;
        private ulong _bits;

        public Symbol Current { readonly get; private set; }

        readonly object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (true)
            {
                switch (_part)
                {
                    case 0:
                    case 1:
                        {
                            var words = _part == 0 ? set._terminals : set._externals;
                            while (_bits == 0)
                            {
                                if (++_word >= words.Length)
                                {
                                    break;
                                }

                                _bits = words[_word];
                            }

                            if (_bits != 0)
                            {
                                var index = (_word * 64) + TrailingZeroCount(_bits);
                                _bits &= _bits - 1;
                                Current = _part == 0 ? Symbol.Terminal(index) : Symbol.External(index);
                                return true;
                            }

                            _part = terminalsOnly ? 4 : _part + 1;
                            _word = -1;
                            break;
                        }

                    case 2:
                        _part = 3;
                        if (set.Eof)
                        {
                            Current = Symbol.End;
                            return true;
                        }

                        break;
                    case 3:
                        _part = 4;
                        if (set.EndOfNonTerminalExtra)
                        {
                            Current = Symbol.EndOfNonTerminalExtra;
                            return true;
                        }

                        break;
                    default:
                        return false;
                }
            }
        }

        public void Reset()
        {
            _part = 0;
            _word = -1;
            _bits = 0;
        }

        public readonly void Dispose()
        {
        }
    }

    private static int TrailingZeroCount(ulong word)
    {
#if NET
        return System.Numerics.BitOperations.TrailingZeroCount(word);
#else
        // The index of the lowest bit with a de Bruijn sequence: the source generator runs this code
        return DeBruijnPositions[(int)(((word & (0UL - word)) * DeBruijnSequence) >> 58)];
#endif
    }

#if !NET
    private const ulong DeBruijnSequence = 0x022FDD63CC95386DUL;

    private static readonly byte[] DeBruijnPositions = CreateDeBruijnPositions();

    private static byte[] CreateDeBruijnPositions()
    {
        var positions = new byte[64];
        for (var i = 0; i < 64; i++)
        {
            positions[(int)(((1UL << i) * DeBruijnSequence) >> 58)] = (byte)i;
        }

        return positions;
    }
#endif

    /// <summary>
    /// Writes the bits of the set at an offset of an array: the terminals and the external tokens on fixed numbers of
    /// words, then the end symbols, so that the sets are equal if and only if their bits are.
    /// </summary>
    public void CopyBits(ulong[] destination, int offset, int terminalWords, int externalWords)
    {
        CopyWords(_terminals, destination, offset, terminalWords);
        CopyWords(_externals, destination, offset + terminalWords, externalWords);
        destination[offset + terminalWords + externalWords] = (Eof ? 1UL : 0) | (EndOfNonTerminalExtra ? 2UL : 0);
    }

    private static void CopyWords(ulong[] source, ulong[] destination, int offset, int count)
    {
        var copied = Math.Min(source.Length, count);
        Array.Copy(source, 0, destination, offset, copied);
        Array.Clear(destination, offset + copied, count - copied);
        for (var i = copied; i < source.Length; i++)
        {
            if (source[i] != 0)
            {
                throw new InvalidOperationException("The set has a symbol out of the grammar.");
            }
        }
    }

    /// <summary>Removes all the symbols, keeping the storage.</summary>
    public void Clear()
    {
        Array.Clear(_terminals, 0, _terminals.Length);
        Array.Clear(_externals, 0, _externals.Length);
        Eof = false;
        EndOfNonTerminalExtra = false;
    }

    public bool Contains(Symbol symbol) => symbol.Kind switch
    {
        SymbolType.Terminal => Get(_terminals, symbol.Index),
        SymbolType.External => Get(_externals, symbol.Index),
        SymbolType.End => Eof,
        SymbolType.EndOfNonTerminalExtra => EndOfNonTerminalExtra,
        _ => throw new InvalidOperationException("Cannot store non-terminals in a TokenSet"),
    };

    public bool ContainsTerminal(int index) => Get(_terminals, index);

    /// <summary>Whether a terminal of the set is in the bits of a row of terminals, at an offset of an array.</summary>
    public bool IntersectsTerminals(ulong[] bits, int offset, int words)
    {
        var count = Math.Min(words, _terminals.Length);
        for (var w = 0; w < count; w++)
        {
            if ((_terminals[w] & bits[offset + w]) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Get(ulong[] words, int index)
    {
        var word = index >> 6;
        return word < words.Length && (words[word] & (1UL << (index & 63))) != 0;
    }

    /// <summary>Adds a symbol, returns whether it wasn't in the set.</summary>
    public bool Insert(Symbol symbol)
    {
        switch (symbol.Kind)
        {
            case SymbolType.Terminal:
                return Set(ref _terminals, symbol.Index);
            case SymbolType.External:
                return Set(ref _externals, symbol.Index);
            case SymbolType.End:
                {
                    var result = !Eof;
                    Eof = true;
                    return result;
                }

            case SymbolType.EndOfNonTerminalExtra:
                {
                    var result = !EndOfNonTerminalExtra;
                    EndOfNonTerminalExtra = true;
                    return result;
                }

            default:
                throw new InvalidOperationException("Cannot store non-terminals in a TokenSet");
        }
    }

    private static bool Set(ref ulong[] words, int index)
    {
        var word = index >> 6;
        if (word >= words.Length)
        {
            Array.Resize(ref words, word + 1);
        }

        var mask = 1UL << (index & 63);
        var result = (words[word] & mask) == 0;
        words[word] |= mask;
        return result;
    }

    public bool Remove(Symbol symbol)
    {
        switch (symbol.Kind)
        {
            case SymbolType.Terminal:
                return Clear(_terminals, symbol.Index);
            case SymbolType.External:
                return Clear(_externals, symbol.Index);
            case SymbolType.End:
                {
                    var result = Eof;
                    Eof = false;
                    return result;
                }

            case SymbolType.EndOfNonTerminalExtra:
                {
                    var result = EndOfNonTerminalExtra;
                    EndOfNonTerminalExtra = false;
                    return result;
                }

            default:
                throw new InvalidOperationException("Cannot store non-terminals in a TokenSet");
        }
    }

    private static bool Clear(ulong[] words, int index)
    {
        var word = index >> 6;
        if (word >= words.Length)
        {
            return false;
        }

        var mask = 1UL << (index & 63);
        var result = (words[word] & mask) != 0;
        words[word] &= ~mask;
        return result;
    }

    public bool InsertAllTerminals(TokenSet other) => Or(ref _terminals, other._terminals);

    public bool InsertAll(TokenSet other)
    {
        var result = false;
        if (other.Eof && !Eof)
        {
            Eof = true;
            result = true;
        }

        if (other.EndOfNonTerminalExtra && !EndOfNonTerminalExtra)
        {
            EndOfNonTerminalExtra = true;
            result = true;
        }

        result |= Or(ref _terminals, other._terminals);
        result |= Or(ref _externals, other._externals);
        return result;
    }

    private static bool Or(ref ulong[] words, ulong[] other)
    {
        if (other.Length > words.Length)
        {
            Array.Resize(ref words, other.Length);
        }

        var result = false;
        for (var i = 0; i < other.Length; i++)
        {
            var merged = words[i] | other[i];
            if (merged != words[i])
            {
                words[i] = merged;
                result = true;
            }
        }

        return result;
    }

    public bool Equals(TokenSet? other)
    {
        if (other is null)
        {
            return false;
        }

        return Eof == other.Eof && EndOfNonTerminalExtra == other.EndOfNonTerminalExtra && WordsEqual(_terminals, other._terminals) && WordsEqual(_externals, other._externals);
    }

    private static bool WordsEqual(ulong[] left, ulong[] right)
    {
        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var l = i < left.Length ? left[i] : 0;
            var r = i < right.Length ? right[i] : 0;
            if (l != r)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is TokenSet other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (Eof ? 3 : 1) * (EndOfNonTerminalExtra ? 7 : 5);
            hash = WordsHash(_terminals, hash);
            hash = WordsHash(_externals, hash * 31);
            return hash;
        }
    }

    private static int WordsHash(ulong[] words, int hash)
    {
        unchecked
        {
            var last = words.Length - 1;
            while (last >= 0 && words[last] == 0)
            {
                last--;
            }

            for (var i = 0; i <= last; i++)
            {
                hash = hash * 31 + words[i].GetHashCode();
            }

            return hash;
        }
    }

    /// <summary>The lexicographic order of tree-sitter's TokenSet, on the bits of the terminals, then externals.</summary>
    public int CompareTo(TokenSet other)
    {
        var result = CompareBits(_terminals, other._terminals);
        if (result != 0)
        {
            return result;
        }

        result = CompareBits(_externals, other._externals);
        if (result != 0)
        {
            return result;
        }

        result = Eof.CompareTo(other.Eof);
        return result != 0 ? result : EndOfNonTerminalExtra.CompareTo(other.EndOfNonTerminalExtra);
    }

    private static int CompareBits(ulong[] left, ulong[] right)
    {
        // Lexicographic comparison of the bit sequences, the shorter sequence of trailing zeros being smaller
        var leftLength = BitLength(left);
        var rightLength = BitLength(right);
        var length = Math.Min(leftLength, rightLength);
        for (var i = 0; i < length; i++)
        {
            var l = Get(left, i);
            var r = Get(right, i);
            if (l != r)
            {
                return l ? 1 : -1;
            }
        }

        return leftLength.CompareTo(rightLength);
    }

    private static int BitLength(ulong[] words)
    {
        for (var w = words.Length - 1; w >= 0; w--)
        {
            if (words[w] != 0)
            {
                var bits = 64;
                while ((words[w] & (1UL << (bits - 1))) == 0)
                {
                    bits--;
                }

                return w * 64 + bits;
            }
        }

        return 0;
    }
}
