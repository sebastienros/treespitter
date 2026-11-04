namespace TreeSpitter.Generation;

// A port of tree-sitter's build_tables/item.rs and build_tables/item_set_builder.rs.

/// <summary>An in-progress match of a production.</summary>
internal readonly struct ParseItem : IEquatable<ParseItem>, IComparable<ParseItem>
{
    public static readonly Production StartProduction = new()
    {
        Steps = [new ProductionStep(Symbol.NonTerminal(0)) { ReservedWordSetId = ProductionStep.NoReservedWords }],
    };

    public ParseItem(int variableIndex, int stepIndex, Production production, bool hasPrecedingInheritedFields)
    {
        VariableIndex = variableIndex;
        StepIndex = stepIndex;
        Production = production;
        HasPrecedingInheritedFields = hasPrecedingInheritedFields;
    }

    /// <summary>The index of the variable, -1 for the augmented start item (u32::MAX in tree-sitter).</summary>
    public int VariableIndex { get; }

    public int StepIndex { get; }

    public Production Production { get; }

    public bool HasPrecedingInheritedFields { get; }

    public static ParseItem Start() => new(-1, 0, StartProduction, false);

    public ProductionStep? Step => StepIndex < Production.Steps.Count ? Production.Steps[StepIndex] : null;

    public Symbol? NextSymbol => StepIndex < Production.Steps.Count ? Production.Steps[StepIndex].Symbol : null;

    public ProductionStep? PrevStep => StepIndex > 0 ? Production.Steps[StepIndex - 1] : null;

    public Associativity? Associativity => PrevStep?.Associativity;

    public Precedence Precedence => PrevStep?.Precedence ?? Precedence.None;

    public bool IsDone => StepIndex == Production.Steps.Count;

    public bool IsAugmented => VariableIndex == -1;

    public ParseItem Successor() => new(VariableIndex, StepIndex + 1, Production, HasPrecedingInheritedFields);

    public ParseItem WithInheritedFields() => new(VariableIndex, StepIndex, Production, true);

    public ParseItem SubstituteProduction(Production production) => new(VariableIndex, StepIndex, production, HasPrecedingInheritedFields);

    public bool Equals(ParseItem other)
    {
        if (VariableIndex != other.VariableIndex ||
            StepIndex != other.StepIndex ||
            Production.DynamicPrecedence != other.Production.DynamicPrecedence ||
            Production.Steps.Count != other.Production.Steps.Count ||
            Precedence != other.Precedence ||
            Associativity != other.Associativity ||
            HasPrecedingInheritedFields != other.HasPrecedingInheritedFields)
        {
            return false;
        }

        if (ReferenceEquals(Production, other.Production))
        {
            return true;
        }

        var steps = Production.Steps;
        var otherSteps = other.Production.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var otherStep = otherSteps[i];
            if (i < StepIndex)
            {
                if (!Equals(step.Alias, otherStep.Alias) || step.FieldName != otherStep.FieldName)
                {
                    return false;
                }

                if (HasPrecedingInheritedFields && step.Symbol != otherStep.Symbol)
                {
                    return false;
                }
            }
            else if (!step.Equals(otherStep))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ParseItem other && Equals(other);

    /// <summary>
    /// A hash consistent with <see cref="Equals(ParseItem)"/>, of the numbers only: the items are hashed millions of
    /// times (the classes of the items of the states, the cores), and hashing the names of the aliases, fields and
    /// precedences costs more than the rare collisions of the items that differ only by them.
    /// </summary>
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = VariableIndex;
            hash = hash * 31 + StepIndex;
            hash = hash * 31 + Production.DynamicPrecedence;
            hash = hash * 31 + Production.Steps.Count;
            hash = hash * 31 + (HasPrecedingInheritedFields ? 1 : 0);
            var precedence = Precedence;
            hash = hash * 31 + (int)precedence.Kind;
            hash = hash * 31 + precedence.Value;
            hash = hash * 31 + (Associativity.HasValue ? (int)Associativity.Value + 1 : 0);
            var steps = Production.Steps;
            for (var i = StepIndex; i < steps.Count; i++)
            {
                var step = steps[i];
                hash = hash * 31 + ((int)step.Symbol.Kind << 24 ^ step.Symbol.Index);
                hash = hash * 31 + step.ReservedWordSetId;
            }

            return hash;
        }
    }

    public int CompareTo(ParseItem other)
    {
        var result = StepIndex.CompareTo(other.StepIndex);
        if (result != 0)
        {
            return result;
        }

        result = ((uint)VariableIndex).CompareTo((uint)other.VariableIndex);
        if (result != 0)
        {
            return result;
        }

        result = Production.DynamicPrecedence.CompareTo(other.Production.DynamicPrecedence);
        if (result != 0)
        {
            return result;
        }

        result = Production.Steps.Count.CompareTo(other.Production.Steps.Count);
        if (result != 0)
        {
            return result;
        }

        result = Precedence.CompareTo(other.Precedence);
        if (result != 0)
        {
            return result;
        }

        result = ProductionStep.CompareAssociativity(Associativity, other.Associativity);
        if (result != 0)
        {
            return result;
        }

        if (ReferenceEquals(Production, other.Production))
        {
            return 0;
        }

        var steps = Production.Steps;
        var otherSteps = other.Production.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            if (i < StepIndex)
            {
                result = Alias.Compare(steps[i].Alias, otherSteps[i].Alias);
                if (result == 0)
                {
                    result = ProductionStep.CompareField(steps[i].FieldName, otherSteps[i].FieldName);
                }
            }
            else
            {
                result = steps[i].CompareTo(otherSteps[i]);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }
}

internal sealed class ParseItemSetEntry(ParseItem item)
{
    public ParseItem Item { get; private set; } = item;

    public TokenSet Lookaheads { get; set; } = new();

    public int FollowingReservedWordSet { get; set; }

    /// <summary>Makes a recycled entry a new entry of an item.</summary>
    public void Reset(ParseItem item)
    {
        Item = item;
        Lookaheads.Clear();
        FollowingReservedWordSet = 0;
    }
}

/// <summary>
/// Recycles the item sets which don't outlive the construction of a state: the closures, and the successor item sets of
/// the existing states, most of them. tree-sitter frees them; reusing them avoids most of the allocations of the table
/// construction, and the garbage collections of the large heap of the states.
/// </summary>
internal sealed class ParseItemSetPool
{
    private readonly Stack<ParseItemSet> _sets = new();
    private readonly Stack<ParseItemSetEntry> _entries = new();

    public ParseItemSet RentSet() => _sets.Count > 0 ? _sets.Pop() : new ParseItemSet(this);

    public ParseItemSetEntry RentEntry(ParseItem item)
    {
        if (_entries.Count == 0)
        {
            return new ParseItemSetEntry(item);
        }

        var entry = _entries.Pop();
        entry.Reset(item);
        return entry;
    }

    /// <summary>Recycles a set and its entries, which mustn't be used anymore.</summary>
    public void Return(ParseItemSet set)
    {
        foreach (var entry in set.Entries)
        {
            _entries.Push(entry);
        }

        set.Entries.Clear();
        _sets.Push(set);
    }
}

/// <summary>A set of items with their lookaheads: a state of the parse table.</summary>
internal sealed class ParseItemSet : IEquatable<ParseItemSet>
{
    private readonly ParseItemSetPool? _pool;

    public ParseItemSet()
    {
    }

    /// <summary>A set whose new entries come from a pool.</summary>
    public ParseItemSet(ParseItemSetPool pool)
    {
        _pool = pool;
    }

    public List<ParseItemSetEntry> Entries { get; } = [];

    public ParseItemSetEntry Insert(ParseItem item)
    {
        // The items are often inserted in order: compare with the last one first
        var count = Entries.Count;
        if (count > 0)
        {
            var last = Entries[count - 1];
            var comparison = last.Item.CompareTo(item);
            if (comparison == 0)
            {
                return last;
            }

            if (comparison < 0)
            {
                var appended = NewEntry(item);
                Entries.Add(appended);
                return appended;
            }
        }

        int low = 0, high = count - 1;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            var comparison = Entries[mid].Item.CompareTo(item);
            if (comparison == 0)
            {
                return Entries[mid];
            }

            if (comparison < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        var entry = NewEntry(item);
        Entries.Insert(low, entry);
        return entry;
    }

    private ParseItemSetEntry NewEntry(ParseItem item) => _pool?.RentEntry(item) ?? new ParseItemSetEntry(item);

    public ParseItemSetCore Core() => new(Entries.Select(e => e.Item).ToArray());

    public bool Equals(ParseItemSet? other)
    {
        if (other is null || other.Entries.Count != Entries.Count)
        {
            return false;
        }

        for (var i = 0; i < Entries.Count; i++)
        {
            var left = Entries[i];
            var right = other.Entries[i];
            if (left.FollowingReservedWordSet != right.FollowingReservedWordSet || !left.Item.Equals(right.Item) || !left.Lookaheads.Equals(right.Lookaheads))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ParseItemSet other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Entries.Count;
            foreach (var entry in Entries)
            {
                hash = hash * 31 + entry.Item.GetHashCode();
                hash = hash * 31 + entry.Lookaheads.GetHashCode();
                hash = hash * 31 + entry.FollowingReservedWordSet;
            }

            return hash;
        }
    }
}

internal sealed class ParseItemSetCore(ParseItem[] items) : IEquatable<ParseItemSetCore>
{
    public ParseItem[] Items { get; } = items;

    public bool Equals(ParseItemSetCore? other)
    {
        if (other is null || other.Items.Length != Items.Length)
        {
            return false;
        }

        for (var i = 0; i < Items.Length; i++)
        {
            if (!Items[i].Equals(other.Items[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ParseItemSetCore other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Items.Length;
            foreach (var item in Items)
            {
                hash = hash * 31 + item.GetHashCode();
            }

            return hash;
        }
    }
}

internal sealed class ParseItemSetBuilder
{
    private sealed class FollowSetInfo
    {
        public TokenSet Lookaheads { get; } = new();

        public int ReservedLookaheads { get; set; }

        public bool PropagatesLookaheads { get; set; }

        public bool SameAs(FollowSetInfo other) =>
            ReservedLookaheads == other.ReservedLookaheads && PropagatesLookaheads == other.PropagatesLookaheads && Lookaheads.Equals(other.Lookaheads);
    }

    /// <summary>An item added by the closure of a non-terminal, and the rank of the item among all the added items.</summary>
    private readonly record struct Addition(ParseItem Item, FollowSetInfo Info, int Rank);

    private readonly SyntaxGrammar _syntaxGrammar;
    private readonly InlinedProductionMap _inlines;
    private readonly SymbolTable<TokenSet> _firstSets;
    private readonly SymbolTable<int> _reservedFirstSets;
    private readonly SymbolTable<TokenSet> _lastSets;
    private readonly List<Addition>[] _transitiveClosureAdditions;

    // The additions of each non-terminal which propagate the following tokens
    private readonly Addition[][] _propagatingAdditions;

    // The entries of the added items of the current closure, by rank, and the ranks added; the non-terminals whose
    // additions were added, and the following tokens propagated by each
    private readonly ParseItemSetEntry?[] _addedEntries;
    private readonly List<int> _addedRanks = [];
    private readonly bool[] _expandedNonTerminals;
    private readonly List<int> _expandedNonTerminalList = [];
    private readonly HashSet<(int NonTerminal, TokenSet Tokens, int ReservedTokens)> _propagations = [];

    /// <summary>The pool of the closures, and of the item sets of the parse table builder.</summary>
    public ParseItemSetPool Pool { get; } = new();

    public ParseItemSetBuilder(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, InlinedProductionMap inlines)
    {
        _syntaxGrammar = syntaxGrammar;
        _inlines = inlines;
        _transitiveClosureAdditions = new List<Addition>[syntaxGrammar.Variables.Count];
        _firstSets = new(lexicalGrammar.Variables.Count, syntaxGrammar.ExternalTokens.Count, syntaxGrammar.Variables.Count);
        _reservedFirstSets = new(lexicalGrammar.Variables.Count, syntaxGrammar.ExternalTokens.Count, syntaxGrammar.Variables.Count);
        _lastSets = new(lexicalGrammar.Variables.Count, syntaxGrammar.ExternalTokens.Count, syntaxGrammar.Variables.Count);

        for (var i = 0; i < lexicalGrammar.Variables.Count; i++)
        {
            var symbol = Symbol.Terminal(i);
            _firstSets[symbol] = TokenSet.Of(symbol);
            _lastSets[symbol] = TokenSet.Of(symbol);
            _reservedFirstSets[symbol] = 0;
        }

        for (var i = 0; i < syntaxGrammar.ExternalTokens.Count; i++)
        {
            var symbol = Symbol.External(i);
            _firstSets[symbol] = TokenSet.Of(symbol);
            _lastSets[symbol] = TokenSet.Of(symbol);
            _reservedFirstSets[symbol] = 0;
        }

        var symbolsToProcess = new List<Symbol>();
        var processed = new HashSet<Symbol>();
        for (var i = 0; i < syntaxGrammar.Variables.Count; i++)
        {
            var symbol = Symbol.NonTerminal(i);
            var firstSet = new TokenSet();
            var reservedFirstSet = 0;
            processed.Clear();
            symbolsToProcess.Clear();
            symbolsToProcess.Add(symbol);
            while (symbolsToProcess.Count > 0)
            {
                var current = symbolsToProcess[symbolsToProcess.Count - 1];
                symbolsToProcess.RemoveAt(symbolsToProcess.Count - 1);
                foreach (var production in syntaxGrammar.Variables[current.Index].Productions)
                {
                    if (production.Steps.Count > 0)
                    {
                        var step = production.Steps[0];
                        if (step.Symbol.IsTerminal || step.Symbol.IsExternal)
                        {
                            firstSet.Insert(step.Symbol);
                        }
                        else if (processed.Add(step.Symbol))
                        {
                            symbolsToProcess.Add(step.Symbol);
                        }

                        reservedFirstSet = Math.Max(reservedFirstSet, step.ReservedWordSetId);
                    }
                }
            }

            _firstSets[symbol] = firstSet;
            _reservedFirstSets[symbol] = reservedFirstSet;

            var lastSet = new TokenSet();
            processed.Clear();
            symbolsToProcess.Clear();
            symbolsToProcess.Add(symbol);
            while (symbolsToProcess.Count > 0)
            {
                var current = symbolsToProcess[symbolsToProcess.Count - 1];
                symbolsToProcess.RemoveAt(symbolsToProcess.Count - 1);
                foreach (var production in syntaxGrammar.Variables[current.Index].Productions)
                {
                    if (production.Steps.Count > 0)
                    {
                        var step = production.Steps[production.Steps.Count - 1];
                        if (step.Symbol.IsTerminal || step.Symbol.IsExternal)
                        {
                            lastSet.Insert(step.Symbol);
                        }
                        else if (processed.Add(step.Symbol))
                        {
                            symbolsToProcess.Add(step.Symbol);
                        }
                    }
                }
            }

            _lastSets[symbol] = lastSet;
        }

        var emptyLookaheads = new TokenSet();
        var stack = new List<(int SymbolIndex, TokenSet Lookaheads, int ReservedWordSetId, bool PropagatesLookaheads)>();
        var followSetInfoByNonTerminal = new Dictionary<int, FollowSetInfo>();
        var order = new List<int>();
        for (var i = 0; i < syntaxGrammar.Variables.Count; i++)
        {
            stack.Clear();
            stack.Add((i, emptyLookaheads, 0, true));
            followSetInfoByNonTerminal.Clear();
            order.Clear();
            while (stack.Count > 0)
            {
                var (symbolIndex, lookaheads, reservedWordSetId, propagatesLookaheads) = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (!followSetInfoByNonTerminal.TryGetValue(symbolIndex, out var info))
                {
                    info = new FollowSetInfo();
                    followSetInfoByNonTerminal[symbolIndex] = info;
                    order.Add(symbolIndex);
                }

                var didAdd = info.Lookaheads.InsertAll(lookaheads);
                if (reservedWordSetId > info.ReservedLookaheads)
                {
                    info.ReservedLookaheads = reservedWordSetId;
                    didAdd = true;
                }

                didAdd |= propagatesLookaheads && !info.PropagatesLookaheads;
                info.PropagatesLookaheads |= propagatesLookaheads;
                if (!didAdd)
                {
                    continue;
                }

                foreach (var production in syntaxGrammar.Variables[symbolIndex].Productions)
                {
                    if (production.FirstSymbol is { IsNonTerminal: true } first)
                    {
                        if (production.Steps.Count > 1)
                        {
                            var nextStep = production.Steps[1];
                            stack.Add((first.Index, _firstSets[nextStep.Symbol], _reservedFirstSets[nextStep.Symbol], false));
                        }
                        else
                        {
                            stack.Add((first.Index, lookaheads, reservedWordSetId, propagatesLookaheads));
                        }
                    }
                }
            }

            var additions = new List<Addition>();
            foreach (var variableIndex in order)
            {
                var followSetInfo = followSetInfoByNonTerminal[variableIndex];
                var variable = syntaxGrammar.Variables[variableIndex];
                if (syntaxGrammar.VariablesToInline.Contains(Symbol.NonTerminal(variableIndex)))
                {
                    continue;
                }

                foreach (var production in variable.Productions)
                {
                    var item = new ParseItem(variableIndex, 0, production, false);
                    if (inlines.InlinedProductionIndices(production, 0) is { } inlined)
                    {
                        foreach (var index in inlined)
                        {
                            FindOrPush(additions, new Addition(item.SubstituteProduction(inlines.Productions[index]), followSetInfo, 0));
                        }
                    }
                    else
                    {
                        FindOrPush(additions, new Addition(item, followSetInfo, 0));
                    }
                }
            }

            _transitiveClosureAdditions[i] = additions;
        }

        // The items of an item set are sorted and unique by ParseItem.CompareTo: rank the added items in this order, the
        // items that compare equal having the same rank, so that a closure can collect them by rank instead of inserting
        // them in a sorted list
        var ranked = _transitiveClosureAdditions.SelectMany(a => a).Select(a => a.Item).ToList();
        ranked.Sort((a, b) => a.CompareTo(b));
        var representatives = new List<ParseItem>();
        foreach (var item in ranked)
        {
            if (representatives.Count == 0 || representatives[representatives.Count - 1].CompareTo(item) != 0)
            {
                representatives.Add(item);
            }
        }

        _propagatingAdditions = new Addition[_transitiveClosureAdditions.Length][];
        for (var i = 0; i < _transitiveClosureAdditions.Length; i++)
        {
            var additions = _transitiveClosureAdditions[i];
            for (var k = 0; k < additions.Count; k++)
            {
                var item = additions[k].Item;
                var rank = representatives.BinarySearch(item, Comparer<ParseItem>.Create((a, b) => a.CompareTo(b)));
                additions[k] = additions[k] with { Rank = rank };
            }

            _propagatingAdditions[i] = [.. additions.Where(a => a.Info.PropagatesLookaheads)];
        }

        _addedEntries = new ParseItemSetEntry?[representatives.Count];
        _expandedNonTerminals = new bool[syntaxGrammar.Variables.Count];
    }

    private static void FindOrPush(List<Addition> additions, Addition addition)
    {
        foreach (var existing in additions)
        {
            if (existing.Item.Equals(addition.Item) && existing.Info.SameAs(addition.Info))
            {
                return;
            }
        }

        additions.Add(addition);
    }

    public TokenSet FirstSet(Symbol symbol) => _firstSets[symbol];

    public TokenSet? ReservedFirstSet(Symbol symbol) =>
        _reservedFirstSets.Contains(symbol) ? _syntaxGrammar.ReservedWordSets[_reservedFirstSets[symbol]] : null;

    /// <summary>The values of the terminals, external tokens and non-terminals, in arrays: they are read for every item.</summary>
    private sealed class SymbolTable<T>(int terminalCount, int externalCount, int nonTerminalCount)
    {
        private readonly T[] _terminals = new T[terminalCount];
        private readonly T[] _externals = new T[externalCount];
        private readonly T[] _nonTerminals = new T[nonTerminalCount];

        public bool Contains(Symbol symbol) => symbol.Index >= 0 && symbol.Kind switch
        {
            SymbolType.Terminal => symbol.Index < _terminals.Length,
            SymbolType.External => symbol.Index < _externals.Length,
            SymbolType.NonTerminal => symbol.Index < _nonTerminals.Length,
            _ => false,
        };

        public T this[Symbol symbol]
        {
            get => Contains(symbol) ? Values(symbol)[symbol.Index] : throw new KeyNotFoundException($"No value for {symbol}.");
            set => Values(symbol)[symbol.Index] = value;
        }

        private T[] Values(Symbol symbol) => symbol.Kind switch
        {
            SymbolType.Terminal => _terminals,
            SymbolType.External => _externals,
            SymbolType.NonTerminal => _nonTerminals,
            _ => throw new KeyNotFoundException($"No value for {symbol}."),
        };
    }

    public TokenSet LastSet(Symbol symbol) => _lastSets[symbol];

    /// <summary>
    /// tree-sitter's <c>transitive_closure</c>: the items of an item set and the items added by the closure of their next
    /// non-terminals, sorted.
    /// </summary>
    /// <remarks>
    /// The result is the same as inserting the items one by one in a sorted set like tree-sitter: an entry keeps the first
    /// item inserted among the items that compare equal, the union of their lookaheads and the maximum of their reserved
    /// word sets, which don't depend on the order of the insertions. The items of the item set have a step index of at
    /// least 1 (or are the augmented start item, which sorts after the other items of step 0), and the added items a step
    /// index of 0, so the added items sort first: they are collected by rank, and the additions of a non-terminal are
    /// added once, then only their following tokens when they change.
    /// </remarks>
    public ParseItemSet TransitiveClosure(ParseItemSet itemSet)
    {
        var items = Pool.RentSet();
        foreach (var entry in itemSet.Entries)
        {
            if (_inlines.InlinedProductionIndices(entry.Item.Production, entry.Item.StepIndex) is { } productions)
            {
                foreach (var index in productions)
                {
                    AddItem(items, entry.Item.SubstituteProduction(_inlines.Productions[index]), entry.Lookaheads, entry.FollowingReservedWordSet);
                }
            }
            else
            {
                AddItem(items, entry.Item, entry.Lookaheads, entry.FollowingReservedWordSet);
            }
        }

        var result = Pool.RentSet();
        if (result.Entries.Capacity < _addedRanks.Count + items.Entries.Count)
        {
            result.Entries.Capacity = _addedRanks.Count + items.Entries.Count;
        }

        _addedRanks.Sort();
        foreach (var rank in _addedRanks)
        {
            result.Entries.Add(_addedEntries[rank]!);
            _addedEntries[rank] = null;
        }

        result.Entries.AddRange(items.Entries);
        items.Entries.Clear();
        Pool.Return(items);
        _addedRanks.Clear();
        foreach (var nonTerminal in _expandedNonTerminalList)
        {
            _expandedNonTerminals[nonTerminal] = false;
        }

        _expandedNonTerminalList.Clear();
        _propagations.Clear();
        return result;
    }

    private void AddItem(ParseItemSet items, ParseItem item, TokenSet lookaheads, int followingReservedWordSet)
    {
        System.Diagnostics.Debug.Assert(item.StepIndex > 0 || item.IsAugmented, "The items of a kernel are after their first step");
        if (item.Step is { Symbol.IsNonTerminal: true } step)
        {
            TokenSet followingTokens;
            int followingReservedTokens;
            var successor = item.Successor();
            if (successor.Step is { } nextStep)
            {
                followingTokens = _firstSets[nextStep.Symbol];
                followingReservedTokens = _reservedFirstSets[nextStep.Symbol];
            }
            else
            {
                followingTokens = lookaheads;
                followingReservedTokens = followingReservedWordSet;
            }

            var wordToken = _syntaxGrammar.WordToken;
            var nonTerminal = step.Symbol.Index;
            if (!_expandedNonTerminals[nonTerminal])
            {
                _expandedNonTerminals[nonTerminal] = true;
                _expandedNonTerminalList.Add(nonTerminal);
                _propagations.Add((nonTerminal, followingTokens, followingReservedTokens));
                foreach (var addition in _transitiveClosureAdditions[nonTerminal])
                {
                    var entry = _addedEntries[addition.Rank];
                    if (entry is null)
                    {
                        entry = Pool.RentEntry(addition.Item);
                        _addedEntries[addition.Rank] = entry;
                        _addedRanks.Add(addition.Rank);
                    }

                    entry.Lookaheads.InsertAll(addition.Info.Lookaheads);
                    if (wordToken is { } word && addition.Info.Lookaheads.Contains(word))
                    {
                        entry.FollowingReservedWordSet = Math.Max(entry.FollowingReservedWordSet, addition.Info.ReservedLookaheads);
                    }

                    if (addition.Info.PropagatesLookaheads)
                    {
                        entry.Lookaheads.InsertAll(followingTokens);
                        if (wordToken is { } w && followingTokens.Contains(w))
                        {
                            entry.FollowingReservedWordSet = Math.Max(entry.FollowingReservedWordSet, followingReservedTokens);
                        }
                    }
                }
            }
            else if (_propagations.Add((nonTerminal, followingTokens, followingReservedTokens)))
            {
                // The additions are in the set with their own lookaheads: only the following tokens can be new
                foreach (var addition in _propagatingAdditions[nonTerminal])
                {
                    var entry = _addedEntries[addition.Rank]!;
                    entry.Lookaheads.InsertAll(followingTokens);
                    if (wordToken is { } w && followingTokens.Contains(w))
                    {
                        entry.FollowingReservedWordSet = Math.Max(entry.FollowingReservedWordSet, followingReservedTokens);
                    }
                }
            }
        }

        var e = items.Insert(item);
        e.Lookaheads.InsertAll(lookaheads);
        e.FollowingReservedWordSet = Math.Max(e.FollowingReservedWordSet, followingReservedWordSet);
    }
}
