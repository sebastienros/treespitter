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

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = VariableIndex;
            hash = hash * 31 + StepIndex;
            hash = hash * 31 + Production.DynamicPrecedence;
            hash = hash * 31 + Production.Steps.Count;
            hash = hash * 31 + (HasPrecedingInheritedFields ? 1 : 0);
            hash = hash * 31 + Precedence.GetHashCode();
            hash = hash * 31 + (Associativity.HasValue ? (int)Associativity.Value + 1 : 0);
            var steps = Production.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (i < StepIndex)
                {
                    hash = hash * 31 + (step.Alias?.GetHashCode() ?? 0);
                    hash = hash * 31 + (step.FieldName is null ? 0 : StringComparer.Ordinal.GetHashCode(step.FieldName));
                    if (HasPrecedingInheritedFields)
                    {
                        hash = hash * 31 + step.Symbol.GetHashCode();
                    }
                }
                else
                {
                    hash = hash * 31 + step.GetHashCode();
                }
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
    public ParseItem Item { get; } = item;

    public TokenSet Lookaheads { get; set; } = new();

    public int FollowingReservedWordSet { get; set; }
}

/// <summary>A set of items with their lookaheads: a state of the parse table.</summary>
internal sealed class ParseItemSet : IEquatable<ParseItemSet>
{
    public List<ParseItemSetEntry> Entries { get; } = [];

    public ParseItemSetEntry Insert(ParseItem item)
    {
        int low = 0, high = Entries.Count;
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

        var entry = new ParseItemSetEntry(item);
        Entries.Insert(low, entry);
        return entry;
    }

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

    private readonly record struct Addition(ParseItem Item, FollowSetInfo Info);

    private readonly SyntaxGrammar _syntaxGrammar;
    private readonly InlinedProductionMap _inlines;
    private readonly Dictionary<Symbol, TokenSet> _firstSets = [];
    private readonly Dictionary<Symbol, int> _reservedFirstSets = [];
    private readonly Dictionary<Symbol, TokenSet> _lastSets = [];
    private readonly List<Addition>[] _transitiveClosureAdditions;

    public ParseItemSetBuilder(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, InlinedProductionMap inlines)
    {
        _syntaxGrammar = syntaxGrammar;
        _inlines = inlines;
        _transitiveClosureAdditions = new List<Addition>[syntaxGrammar.Variables.Count];

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
                            FindOrPush(additions, new Addition(item.SubstituteProduction(inlines.Productions[index]), followSetInfo));
                        }
                    }
                    else
                    {
                        FindOrPush(additions, new Addition(item, followSetInfo));
                    }
                }
            }

            _transitiveClosureAdditions[i] = additions;
        }
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
        _reservedFirstSets.TryGetValue(symbol, out var id) ? _syntaxGrammar.ReservedWordSets[id] : null;

    public TokenSet LastSet(Symbol symbol) => _lastSets[symbol];

    public ParseItemSet TransitiveClosure(ParseItemSet itemSet)
    {
        var result = new ParseItemSet();
        foreach (var entry in itemSet.Entries)
        {
            if (_inlines.InlinedProductionIndices(entry.Item.Production, entry.Item.StepIndex) is { } productions)
            {
                foreach (var index in productions)
                {
                    AddItem(result, entry.Item.SubstituteProduction(_inlines.Productions[index]), entry.Lookaheads, entry.FollowingReservedWordSet);
                }
            }
            else
            {
                AddItem(result, entry.Item, entry.Lookaheads, entry.FollowingReservedWordSet);
            }
        }

        return result;
    }

    private void AddItem(ParseItemSet set, ParseItem item, TokenSet lookaheads, int followingReservedWordSet)
    {
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
            foreach (var addition in _transitiveClosureAdditions[step.Symbol.Index])
            {
                var entry = set.Insert(addition.Item);
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

        var e = set.Insert(item);
        e.Lookaheads.InsertAll(lookaheads);
        e.FollowingReservedWordSet = Math.Max(e.FollowingReservedWordSet, followingReservedWordSet);
    }
}
