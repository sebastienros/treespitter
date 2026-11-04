namespace TreeSpitter.Generation;

// A port of tree-sitter's cli/generate/src/tables.rs.

internal enum ParseActionKind : byte
{
    Accept,
    Shift,
    ShiftExtra,
    Recover,
    Reduce,
}

internal readonly record struct ParseAction(ParseActionKind Kind, int State, bool IsRepetition, Symbol Symbol, int ChildCount, int DynamicPrecedence, int ProductionId)
{
    public static readonly ParseAction Accept = new(ParseActionKind.Accept, 0, false, default, 0, 0, 0);

    public static readonly ParseAction ShiftExtra = new(ParseActionKind.ShiftExtra, 0, false, default, 0, 0, 0);

    public static readonly ParseAction Recover = new(ParseActionKind.Recover, 0, false, default, 0, 0, 0);

    public static ParseAction Shift(int state, bool isRepetition) => new(ParseActionKind.Shift, state, isRepetition, default, 0, 0, 0);

    public static ParseAction Reduce(Symbol symbol, int childCount, int dynamicPrecedence, int productionId) =>
        new(ParseActionKind.Reduce, 0, false, symbol, childCount, dynamicPrecedence, productionId);
}

internal sealed class ParseTableEntry : IEquatable<ParseTableEntry>
{
    public List<ParseAction> Actions { get; set; } = [];

    public bool Reusable { get; set; } = true;

    public ParseTableEntry Clone() => new() { Actions = [.. Actions], Reusable = Reusable };

    public bool Equals(ParseTableEntry? other) => other is not null && Reusable == other.Reusable && Actions.SequenceEqual(other.Actions);

    public override bool Equals(object? obj) => obj is ParseTableEntry other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Reusable ? 1 : 2;
            foreach (var action in Actions)
            {
                hash = hash * 31 + action.GetHashCode();
            }

            return hash;
        }
    }
}

/// <summary>A goto action: a state, or -1 for a non-terminal extra (<c>GotoAction::ShiftExtra</c>).</summary>
internal readonly record struct GotoAction(int State)
{
    public static readonly GotoAction ShiftExtra = new(-1);

    public bool IsShiftExtra => State < 0;
}

/// <summary>An insertion-ordered map, like the IndexMap of the Rust code.</summary>
internal sealed class OrderedMap<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, int> _indices = [];
    private readonly List<TKey> _keys = [];
    private readonly List<TValue> _values = [];

    public int Count => _keys.Count;

    public IReadOnlyList<TKey> Keys => _keys;

    public IReadOnlyList<TValue> Values => _values;

    public IEnumerable<KeyValuePair<TKey, TValue>> Entries
    {
        get
        {
            for (var i = 0; i < _keys.Count; i++)
            {
                yield return new KeyValuePair<TKey, TValue>(_keys[i], _values[i]);
            }
        }
    }

    public bool ContainsKey(TKey key) => _indices.ContainsKey(key);

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_indices.TryGetValue(key, out var index))
        {
            value = _values[index];
            return true;
        }

        value = default!;
        return false;
    }

    public TValue this[TKey key]
    {
        get => _values[_indices[key]];
        set
        {
            if (_indices.TryGetValue(key, out var index))
            {
                _values[index] = value;
            }
            else
            {
                _indices[key] = _keys.Count;
                _keys.Add(key);
                _values.Add(value);
            }
        }
    }

    public TValue GetOrAdd(TKey key, Func<TValue> factory)
    {
        if (_indices.TryGetValue(key, out var index))
        {
            return _values[index];
        }

        var value = factory();
        _indices[key] = _keys.Count;
        _keys.Add(key);
        _values.Add(value);
        return value;
    }

    /// <summary>Adds the entries of another map, replacing the values of existing keys (IndexMap::extend).</summary>
    public void Extend(OrderedMap<TKey, TValue> other)
    {
        for (var i = 0; i < other._keys.Count; i++)
        {
            this[other._keys[i]] = other._values[i];
        }
    }

    public void SetValueAt(int index, TValue value) => _values[index] = value;
}

internal sealed class ParseState
{
    public int Id { get; set; }

    public OrderedMap<Symbol, ParseTableEntry> TerminalEntries { get; set; } = new();

    public OrderedMap<Symbol, GotoAction> NonTerminalEntries { get; set; } = new();

    public TokenSet ReservedWords { get; set; } = new();

    public int LexStateId { get; set; }

    public int ExternalLexStateId { get; set; }

    public int CoreId { get; set; }

    public bool IsEndOfNonTerminalExtra => TerminalEntries.ContainsKey(Symbol.EndOfNonTerminalExtra);

    public IEnumerable<int> ReferencedStates()
    {
        foreach (var entry in TerminalEntries.Values)
        {
            foreach (var action in entry.Actions)
            {
                if (action.Kind == ParseActionKind.Shift)
                {
                    yield return action.State;
                }
            }
        }

        foreach (var action in NonTerminalEntries.Values)
        {
            if (!action.IsShiftExtra)
            {
                yield return action.State;
            }
        }
    }

    public void UpdateReferencedStates(Func<int, ParseState, int> update)
    {
        var updates = new List<(Symbol Symbol, int ActionIndex, int NewState)>();
        foreach (var (symbol, entry) in TerminalEntries.Entries)
        {
            for (var i = 0; i < entry.Actions.Count; i++)
            {
                var action = entry.Actions[i];
                if (action.Kind == ParseActionKind.Shift)
                {
                    var result = update(action.State, this);
                    if (result != action.State)
                    {
                        updates.Add((symbol, i, result));
                    }
                }
            }
        }

        foreach (var (symbol, action) in NonTerminalEntries.Entries)
        {
            if (!action.IsShiftExtra)
            {
                var result = update(action.State, this);
                if (result != action.State)
                {
                    updates.Add((symbol, 0, result));
                }
            }
        }

        foreach (var (symbol, actionIndex, newState) in updates)
        {
            if (symbol.IsNonTerminal)
            {
                NonTerminalEntries[symbol] = new GotoAction(newState);
            }
            else
            {
                var entry = TerminalEntries[symbol];
                var action = entry.Actions[actionIndex];
                if (action.Kind == ParseActionKind.Shift)
                {
                    entry.Actions[actionIndex] = ParseAction.Shift(newState, action.IsRepetition);
                }
            }
        }
    }
}

internal readonly record struct FieldLocation(int Index, bool Inherited);

internal sealed class ProductionInfo : IEquatable<ProductionInfo>
{
    public List<Alias?> AliasSequence { get; } = [];

    public SortedDictionary<string, List<FieldLocation>> FieldMap { get; } = new(StringComparer.Ordinal);

    public bool Equals(ProductionInfo? other)
    {
        if (other is null || AliasSequence.Count != other.AliasSequence.Count || FieldMap.Count != other.FieldMap.Count)
        {
            return false;
        }

        for (var i = 0; i < AliasSequence.Count; i++)
        {
            if (!Equals(AliasSequence[i], other.AliasSequence[i]))
            {
                return false;
            }
        }

        foreach (var (name, locations) in FieldMap)
        {
            if (!other.FieldMap.TryGetValue(name, out var otherLocations) || !locations.SequenceEqual(otherLocations))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ProductionInfo other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = AliasSequence.Count;
            foreach (var alias in AliasSequence)
            {
                hash = hash * 31 + (alias?.GetHashCode() ?? 0);
            }

            foreach (var (name, locations) in FieldMap)
            {
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(name) + locations.Count;
            }

            return hash;
        }
    }
}

internal sealed class ParseTable
{
    public List<ParseState> States { get; set; } = [];

    public List<Symbol> Symbols { get; } = [];

    public List<ProductionInfo> ProductionInfos { get; } = [];

    public int MaxAliasedProductionLength { get; set; } = 1;

    public List<TokenSet> ExternalLexStates { get; } = [];
}

internal readonly record struct AdvanceAction(int State, bool InMainToken) : IComparable<AdvanceAction>
{
    public int CompareTo(AdvanceAction other)
    {
        var result = State.CompareTo(other.State);
        return result != 0 ? result : InMainToken.CompareTo(other.InMainToken);
    }
}

internal sealed class LexState : IComparable<LexState>
{
    public Symbol? AcceptAction { get; set; }

    public AdvanceAction? EofAction { get; set; }

    public List<(CharacterSet Chars, AdvanceAction Action)> AdvanceActions { get; } = [];

    public int CompareTo(LexState? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = CompareOptional(AcceptAction, other.AcceptAction);
        if (result != 0)
        {
            return result;
        }

        result = CompareOptional(EofAction, other.EofAction);
        if (result != 0)
        {
            return result;
        }

        var count = Math.Min(AdvanceActions.Count, other.AdvanceActions.Count);
        for (var i = 0; i < count; i++)
        {
            result = AdvanceActions[i].Chars.CompareTo(other.AdvanceActions[i].Chars);
            if (result != 0)
            {
                return result;
            }

            result = AdvanceActions[i].Action.CompareTo(other.AdvanceActions[i].Action);
            if (result != 0)
            {
                return result;
            }
        }

        return AdvanceActions.Count.CompareTo(other.AdvanceActions.Count);
    }

    private static int CompareOptional<T>(T? left, T? right)
        where T : struct, IComparable<T>
    {
        if (!left.HasValue)
        {
            return right.HasValue ? -1 : 0;
        }

        return !right.HasValue ? 1 : left.Value.CompareTo(right.Value);
    }
}

internal sealed class LexTable
{
    public List<LexState> States { get; set; } = [];
}
