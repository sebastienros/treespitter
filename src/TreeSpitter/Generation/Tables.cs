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

/// <summary>
/// The actions of a terminal in a state. The parse tables of the large grammars have millions of entries before the
/// minimization, and most entries have a single action: it is stored in the entry, the other actions in an array.
/// </summary>
internal sealed class ParseTableEntry : IEquatable<ParseTableEntry>
{
    private ParseAction _first;
    private ParseAction[]? _others;
    private int _count;

    public ParseTableEntry()
    {
    }

    public ParseTableEntry(ParseAction action)
    {
        _first = action;
        _count = 1;
    }

    public ParseActionList Actions => new(this);

    public bool Reusable { get; set; } = true;

    public ParseTableEntry Clone()
    {
        var clone = new ParseTableEntry { Reusable = Reusable };
        foreach (var action in Actions)
        {
            clone.Actions.Add(action);
        }

        return clone;
    }

    public bool Equals(ParseTableEntry? other)
    {
        if (other is null || Reusable != other.Reusable || _count != other._count)
        {
            return false;
        }

        for (var i = 0; i < _count; i++)
        {
            if (Actions[i] != other.Actions[i])
            {
                return false;
            }
        }

        return true;
    }

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

    /// <summary>The list of the actions of an entry, with the operations of <see cref="List{T}"/> used by the generator.</summary>
    public readonly struct ParseActionList(ParseTableEntry entry) : IReadOnlyList<ParseAction>
    {
        public int Count => entry._count;

        /// <summary>The action at an index, by reference so that it can be replaced.</summary>
        public ref ParseAction this[int index]
        {
            get
            {
                if ((uint)index >= (uint)entry._count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return ref index == 0 ? ref entry._first : ref entry._others![index - 1];
            }
        }

        ParseAction IReadOnlyList<ParseAction>.this[int index] => this[index];

        public void Add(ParseAction action)
        {
            if (entry._count == 0)
            {
                entry._first = action;
            }
            else
            {
                var others = entry._others;
                if (others is null)
                {
                    others = new ParseAction[2];
                    entry._others = others;
                }
                else if (entry._count - 1 == others.Length)
                {
                    Array.Resize(ref entry._others, others.Length * 2);
                    others = entry._others;
                }

                others[entry._count - 1] = action;
            }

            entry._count++;
        }

        public void Clear()
        {
            entry._count = 0;
            entry._first = default;
            if (entry._others is { } others)
            {
                Array.Clear(others, 0, others.Length);
            }
        }

        public bool Contains(ParseAction action)
        {
            foreach (var a in this)
            {
                if (a == action)
                {
                    return true;
                }
            }

            return false;
        }

        public void RemoveAt(int index) => RemoveRange(index, 1);

        public void RemoveRange(int index, int count)
        {
            if (index < 0 || count < 0 || index + count > entry._count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            for (var i = index; i + count < entry._count; i++)
            {
                this[i] = this[i + count];
            }

            for (var i = entry._count - count; i < entry._count; i++)
            {
                this[i] = default;
            }

            entry._count -= count;
        }

        public Enumerator GetEnumerator() => new(entry);

        IEnumerator<ParseAction> IEnumerable<ParseAction>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Enumerates the actions, without allocations.</summary>
        public struct Enumerator(ParseTableEntry entry) : IEnumerator<ParseAction>
        {
            private int _index = -1;

            public readonly ParseAction Current => _index == 0 ? entry._first : entry._others![_index - 1];

            readonly object System.Collections.IEnumerator.Current => Current;

            public bool MoveNext() => ++_index < entry._count;

            public void Reset() => _index = -1;

            public readonly void Dispose()
            {
            }
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
/// <remarks>
/// The parse tables of the large grammars have millions of entries before the minimization: the maps of a few entries
/// are searched linearly, and the larger ones have a compact index of open addressing (without removals).
/// </remarks>
internal sealed class OrderedMap<TKey, TValue>
    where TKey : notnull
{
    private const int LinearSearchCount = 8;

    private static readonly IEqualityComparer<TKey> KeyComparer = EqualityComparer<TKey>.Default;

    private readonly List<TKey> _keys = [];
    private readonly List<TValue> _values = [];

    // The indices of the keys plus one, 0 for the empty slots: a power of 2 at least twice the number of keys
    private int[]? _slots;

    public int Count => _keys.Count;

    public IReadOnlyList<TKey> Keys => _keys;

    public IReadOnlyList<TValue> Values => _values;

    /// <summary>The entries in insertion order, enumerated without allocations (the minimization compares states a lot).</summary>
    public EntryCollection Entries => new(this);

    /// <summary>The entries of a map, a struct enumerable for <c>foreach</c>.</summary>
    public readonly struct EntryCollection(OrderedMap<TKey, TValue> map) : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        public Enumerator GetEnumerator() => new(map);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Enumerates the entries, the ones added during the enumeration included.</summary>
    public struct Enumerator(OrderedMap<TKey, TValue> map) : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private int _index = -1;

        public readonly KeyValuePair<TKey, TValue> Current => new(map._keys[_index], map._values[_index]);

        readonly object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < map._keys.Count;

        public void Reset() => _index = -1;

        public readonly void Dispose()
        {
        }
    }

    public bool ContainsKey(TKey key) => IndexOf(key) >= 0;

    public bool TryGetValue(TKey key, out TValue value)
    {
        var index = IndexOf(key);
        if (index >= 0)
        {
            value = _values[index];
            return true;
        }

        value = default!;
        return false;
    }

    public TValue this[TKey key]
    {
        get
        {
            var index = IndexOf(key);
            return index >= 0 ? _values[index] : throw new KeyNotFoundException();
        }

        set
        {
            var index = IndexOf(key);
            if (index >= 0)
            {
                _values[index] = value;
            }
            else
            {
                Append(key, value);
            }
        }
    }

    public TValue GetOrAdd(TKey key, Func<TValue> factory)
    {
        var index = IndexOf(key);
        if (index >= 0)
        {
            return _values[index];
        }

        var value = factory();
        Append(key, value);
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

    /// <summary>Makes room for a number of entries, which are then added without reallocations.</summary>
    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _keys.Capacity)
        {
            return;
        }

        _keys.Capacity = capacity;
        _values.Capacity = capacity;
        if (capacity > LinearSearchCount)
        {
            var size = LinearSearchCount * 4;
            while (size < capacity * 2)
            {
                size *= 2;
            }

            if (_slots is null || _slots.Length < size)
            {
                Rehash(size);
            }
        }
    }

    private int IndexOf(TKey key)
    {
        if (_slots is null)
        {
            for (var i = 0; i < _keys.Count; i++)
            {
                if (KeyComparer.Equals(_keys[i], key))
                {
                    return i;
                }
            }

            return -1;
        }

        var mask = _slots.Length - 1;
        for (var slot = Hash(key) & mask; ; slot = (slot + 1) & mask)
        {
            var index = _slots[slot] - 1;
            if (index < 0)
            {
                return -1;
            }

            if (KeyComparer.Equals(_keys[index], key))
            {
                return index;
            }
        }
    }

    private void Append(TKey key, TValue value)
    {
        _keys.Add(key);
        _values.Add(value);
        if (_slots is not null)
        {
            if (_keys.Count * 2 > _slots.Length)
            {
                Rehash(_slots.Length * 2);
            }
            else
            {
                AddSlot(_slots, _keys.Count - 1);
            }
        }
        else if (_keys.Count > LinearSearchCount)
        {
            Rehash(LinearSearchCount * 4);
        }
    }

    private void Rehash(int size)
    {
        _slots = new int[size];
        for (var i = 0; i < _keys.Count; i++)
        {
            AddSlot(_slots, i);
        }
    }

    private void AddSlot(int[] slots, int index)
    {
        var mask = slots.Length - 1;
        var slot = Hash(_keys[index]) & mask;
        while (slots[slot] != 0)
        {
            slot = (slot + 1) & mask;
        }

        slots[slot] = index + 1;
    }

    // Fibonacci hashing, the keys being small numbers
    private static int Hash(TKey key) => (int)(unchecked((uint)KeyComparer.GetHashCode(key) * 2654435769u) >> 8);

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
