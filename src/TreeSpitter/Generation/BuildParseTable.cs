using System.Text;

namespace TreeSpitter.Generation;

// A port of tree-sitter's build_tables/build_parse_table.rs.

internal sealed class ParseTableBuilder
{
    private sealed class ReductionInfo
    {
        public Precedence Precedence { get; set; }

        public List<Symbol> Symbols { get; } = [];

        public bool HasLeftAssoc { get; set; }

        public bool HasRightAssoc { get; set; }

        public bool HasNonAssoc { get; set; }

        public void Reset()
        {
            Precedence = Precedence.None;
            Symbols.Clear();
            HasLeftAssoc = HasRightAssoc = HasNonAssoc = false;
        }
    }

    private sealed record AuxiliarySymbolInfo(Symbol AuxiliarySymbol, List<Symbol> ParentSymbols);

    /// <summary>
    /// A sequence of symbols, tree-sitter's <c>SymbolSequence</c>, as an immutable list linked from its last symbol: the
    /// sequences of the states extend the sequence of the state they come from, and are shared instead of copied.
    /// </summary>
    private sealed class SymbolSequence(Symbol last, SymbolSequence? previous)
    {
        public Symbol Last { get; } = last;

        public SymbolSequence? Previous { get; } = previous;

        public static List<Symbol> ToList(SymbolSequence? sequence)
        {
            var list = new List<Symbol>();
            for (var s = sequence; s is not null; s = s.Previous)
            {
                list.Add(s.Last);
            }

            list.Reverse();
            return list;
        }
    }

    /// <summary>
    /// A sequence of auxiliary symbol infos, tree-sitter's <c>AuxiliarySymbolSequence</c>, as an immutable list linked
    /// from its last info, shared like <see cref="SymbolSequence"/>.
    /// </summary>
    private sealed class AuxiliarySymbolSequence(AuxiliarySymbolInfo last, AuxiliarySymbolSequence? previous)
    {
        public AuxiliarySymbolInfo Last { get; } = last;

        public AuxiliarySymbolSequence? Previous { get; } = previous;
    }

    private readonly record struct QueueEntry(int StateId, AuxiliarySymbolSequence? PrecedingAuxiliarySymbols);

    /// <summary>
    /// The key of an item set in the dictionary of the states: for each entry, the id of the class of its item (the items
    /// equal by <see cref="ParseItem.Equals(ParseItem)"/>) and its reserved word set, then the bits of its lookaheads,
    /// packed in an array. Two item sets are equal if and only if their keys are: comparing the keys reads an array
    /// instead of the objects of an item set, and the successor item sets of the large grammars are looked up millions of
    /// times.
    /// </summary>
    private sealed class ItemSetKey(ulong[] words, int length, int hash)
    {
        public ulong[] Words { get; set; } = words;

        public int Length { get; set; } = length;

        public int Hash { get; set; } = hash;

        public ItemSetKey Copy() => new(Words.AsSpan(0, Length).ToArray(), Length, Hash);
    }

    /// <summary>
    /// A map of the symbols of a grammar to values, in arrays indexed by symbol: the maps of the successor item sets and of
    /// the reductions of a state, cleared for every state. The symbols are indexed in their order (externals, end, end of
    /// non-terminal extra, terminals, non-terminals) so that the keys are sorted as numbers.
    /// </summary>
    private sealed class SymbolMap<T>
        where T : class
    {
        private readonly Symbol[] _symbols;
        private readonly T?[] _values;
        private readonly List<int> _indices = [];
        private readonly List<Symbol> _sortedKeys = [];
        private readonly int _endIndex;
        private readonly int _terminalOffset;
        private readonly int _nonTerminalOffset;

        public SymbolMap(int terminalCount, int externalCount, int nonTerminalCount)
        {
            _endIndex = externalCount;
            _terminalOffset = externalCount + 2;
            _nonTerminalOffset = _terminalOffset + terminalCount;
            _symbols = new Symbol[_nonTerminalOffset + nonTerminalCount];
            _values = new T?[_symbols.Length];
            for (var i = 0; i < externalCount; i++)
            {
                _symbols[i] = Symbol.External(i);
            }

            _symbols[_endIndex] = Symbol.End;
            _symbols[_endIndex + 1] = Symbol.EndOfNonTerminalExtra;
            for (var i = 0; i < terminalCount; i++)
            {
                _symbols[_terminalOffset + i] = Symbol.Terminal(i);
            }

            for (var i = 0; i < nonTerminalCount; i++)
            {
                _symbols[_nonTerminalOffset + i] = Symbol.NonTerminal(i);
            }
        }

        public int Count => _indices.Count;

        public T this[Symbol symbol]
        {
            get => _values[Index(symbol)] ?? throw new KeyNotFoundException();
            set
            {
                var index = Index(symbol);
                if (_values[index] is null)
                {
                    _indices.Add(index);
                }

                _values[index] = value;
            }
        }

        public bool TryGetValue(Symbol symbol, out T value)
        {
            value = _values[Index(symbol)]!;
            return value is not null;
        }

        /// <summary>The symbols of the map, sorted (valid until the next call).</summary>
        public List<Symbol> SortedKeys()
        {
            _indices.Sort();
            _sortedKeys.Clear();
            foreach (var index in _indices)
            {
                _sortedKeys.Add(_symbols[index]);
            }

            return _sortedKeys;
        }

        public void Clear()
        {
            foreach (var index in _indices)
            {
                _values[index] = null;
            }

            _indices.Clear();
        }

        private int Index(Symbol symbol) => symbol.Kind switch
        {
            SymbolType.Terminal => _terminalOffset + symbol.Index,
            SymbolType.NonTerminal => _nonTerminalOffset + symbol.Index,
            SymbolType.External => symbol.Index,
            SymbolType.End => _endIndex,
            _ => _endIndex + 1,
        };
    }

    /// <summary>Compares the items by their fields, the productions by reference: the same items are equal items.</summary>
    private sealed class ItemIdentityComparer : IEqualityComparer<ParseItem>
    {
        public static readonly ItemIdentityComparer Instance = new();

        public bool Equals(ParseItem x, ParseItem y) =>
            x.VariableIndex == y.VariableIndex && x.StepIndex == y.StepIndex && x.HasPrecedingInheritedFields == y.HasPrecedingInheritedFields && ReferenceEquals(x.Production, y.Production);

        public int GetHashCode(ParseItem obj) =>
            (((System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Production) * 31) + obj.StepIndex) * 31 + obj.VariableIndex) * 2 + (obj.HasPrecedingInheritedFields ? 1 : 0);
    }

    private sealed class ItemSetKeyComparer : IEqualityComparer<ItemSetKey>
    {
        public static readonly ItemSetKeyComparer Instance = new();

        public bool Equals(ItemSetKey? x, ItemSetKey? y) =>
            x is not null && y is not null && x.Length == y.Length && x.Words.AsSpan(0, x.Length).SequenceEqual(y.Words.AsSpan(0, y.Length));

        public int GetHashCode(ItemSetKey obj) => obj.Hash;
    }

    private readonly ParseItemSetBuilder _itemSetBuilder;
    private readonly SyntaxGrammar _syntaxGrammar;
    private readonly LexicalGrammar _lexicalGrammar;
    private readonly List<VariableInfo> _variableInfo;
    private readonly Dictionary<ParseItemSetCore, int> _coreIdsByCore = [];
    private readonly Dictionary<ItemSetKey, int> _stateIdsByItemSet = new(ItemSetKeyComparer.Instance);
    private readonly Dictionary<ParseItem, int> _itemClassIds = [];
    private readonly Dictionary<ParseItem, int> _itemClassIdsByIdentity = new(ItemIdentityComparer.Instance);
    private readonly ItemSetKey _probe = new([], 0, 0);
    private readonly int _terminalWords;
    private readonly int _externalWords;
    private readonly List<(SymbolSequence? PrecedingSymbols, ParseItemSet ItemSet)> _parseStateInfoById = [];
    private readonly Queue<QueueEntry> _parseStateQueue = new();
    private readonly List<(Symbol Terminal, int StateId)> _nonTerminalExtraStates = [];
    private readonly HashSet<string> _actualConflicts;
    private readonly ParseTable _parseTable = new();
    private readonly Dictionary<ProductionInfo, int> _productionInfoIds = [];
    private readonly Dictionary<Production, int> _productionIdsByProduction = []; // by reference

    // The per-state collections of AddActions, reused
    private readonly SymbolMap<ParseItemSet> _terminalSuccessors;
    private readonly SymbolMap<ParseItemSet> _nonTerminalSuccessors;
    private readonly SymbolMap<ReductionInfo> _reductionInfos;
    private readonly List<ReductionInfo> _reductionInfoPool = [];
    private readonly Dictionary<Symbol, AuxiliarySymbolInfo> _auxiliaryNodeInfos = [];
    private readonly TokenSet _terminals = new();
    private readonly int _nonTerminalExtraCount;

    public ParseTableBuilder(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, ParseItemSetBuilder itemSetBuilder, List<VariableInfo> variableInfo)
    {
        _syntaxGrammar = syntaxGrammar;
        _lexicalGrammar = lexicalGrammar;
        _itemSetBuilder = itemSetBuilder;
        _variableInfo = variableInfo;
        _actualConflicts = new HashSet<string>(syntaxGrammar.ExpectedConflicts.Select(ConflictKey), StringComparer.Ordinal);
        _nonTerminalExtraCount = syntaxGrammar.ExtraSymbols.Count(s => s.IsNonTerminal);
        var (terminalCount, externalCount, nonTerminalCount) = (lexicalGrammar.Variables.Count, syntaxGrammar.ExternalTokens.Count, syntaxGrammar.Variables.Count);
        _terminalSuccessors = new(terminalCount, externalCount, nonTerminalCount);
        _nonTerminalSuccessors = new(terminalCount, externalCount, nonTerminalCount);
        _reductionInfos = new(terminalCount, externalCount, nonTerminalCount);
        _terminalWords = (lexicalGrammar.Variables.Count + 63) >> 6;
        _externalWords = (syntaxGrammar.ExternalTokens.Count + 63) >> 6;
    }

    public List<string> Warnings { get; } = [];

    private static string ConflictKey(List<Symbol> symbols) => string.Join(",", symbols.Select(s => s.Kind + ":" + s.Index));

    public ParseTable Build()
    {
        // The empty alias sequence has index 0
        AddProductionInfo(new ProductionInfo());

        // The error state is 0, the start state is 1
        AddParseState(null, null, new ParseItemSet());
        var start = new ParseItemSet();
        start.Insert(ParseItem.Start()).Lookaheads.Insert(Symbol.End);
        AddParseState(null, null, start);

        // The item sets of the non-terminal extras, by their first terminal
        var nonTerminalExtraItemSets = new SortedDictionary<Symbol, ParseItemSet>();
        foreach (var extra in _syntaxGrammar.ExtraSymbols.Where(s => s.IsNonTerminal))
        {
            var variable = _syntaxGrammar.Variables[extra.Index];
            foreach (var production in variable.Productions)
            {
                var first = production.FirstSymbol!.Value;
                if (!nonTerminalExtraItemSets.TryGetValue(first, out var set))
                {
                    set = new ParseItemSet();
                    nonTerminalExtraItemSets[first] = set;
                }

                set.Insert(new ParseItem(extra.Index, 1, production, false)).Lookaheads.Insert(Symbol.EndOfNonTerminalExtra);
            }
        }

        foreach (var (terminal, itemSet) in nonTerminalExtraItemSets)
        {
            if (terminal.IsNonTerminal)
            {
                throw new GrammarException($"The non-terminal rule `{SymbolName(terminal)}` is used in a non-terminal `extra` rule, which is not allowed.");
            }

            var stateId = AddParseState(null, null, itemSet);
            _nonTerminalExtraStates.Add((terminal, stateId));
        }

        while (_parseStateQueue.Count > 0)
        {
            var entry = _parseStateQueue.Dequeue();
            var itemSet = _itemSetBuilder.TransitiveClosure(_parseStateInfoById[entry.StateId].ItemSet);
            AddActions(_parseStateInfoById[entry.StateId].PrecedingSymbols, entry.PrecedingAuxiliarySymbols, entry.StateId, itemSet);
            _itemSetBuilder.Pool.Return(itemSet);
        }

        // The declared conflicts which didn't occur, sorted like tree-sitter 0.27
        if (_actualConflicts.Count > 0)
        {
            var conflicts = _syntaxGrammar.ExpectedConflicts
                .Where(c => _actualConflicts.Remove(ConflictKey(c)))
                .Select(c => c.Select(SymbolName).ToList())
                .ToList();
            conflicts.Sort((a, b) =>
            {
                for (var k = 0; k < Math.Min(a.Count, b.Count); k++)
                {
                    var c = string.CompareOrdinal(a[k], b[k]);
                    if (c != 0)
                    {
                        return c;
                    }
                }

                return a.Count.CompareTo(b.Count);
            });
            Warnings.Add("unnecessary conflicts:\n" + string.Join("\n", conflicts.Select(c => "  " + string.Join(", ", c.Select(name => "`" + name + "`")))));
        }

        return _parseTable;
    }

    private int AddParseState(SymbolSequence? precedingSymbols, AuxiliarySymbolSequence? precedingAuxiliarySymbols, ParseItemSet itemSet)
    {
        var key = Key(itemSet);
        return _stateIdsByItemSet.TryGetValue(key, out var existing) ? existing : AddNewParseState(precedingSymbols, precedingAuxiliarySymbols, itemSet, key);
    }

    /// <summary>The key of an item set, in the probe key (valid until the next call).</summary>
    private ItemSetKey Key(ParseItemSet itemSet)
    {
        var stride = 1 + _terminalWords + _externalWords + 1;
        var length = itemSet.Entries.Count * stride;
        if (_probe.Words.Length < length)
        {
            _probe.Words = new ulong[Math.Max(length, _probe.Words.Length * 2)];
        }

        var words = _probe.Words;
        var offset = 0;
        foreach (var entry in itemSet.Entries)
        {
            if (!_itemClassIdsByIdentity.TryGetValue(entry.Item, out var classId))
            {
                if (!_itemClassIds.TryGetValue(entry.Item, out classId))
                {
                    classId = _itemClassIds.Count;
                    _itemClassIds[entry.Item] = classId;
                }

                _itemClassIdsByIdentity[entry.Item] = classId;
            }

            words[offset] = ((ulong)(uint)classId << 32) | (uint)entry.FollowingReservedWordSet;
            entry.Lookaheads.CopyBits(words, offset + 1, _terminalWords, _externalWords);
            offset += stride;
        }

        unchecked
        {
            var hash = length;
            for (var i = 0; i < length; i++)
            {
                hash = (hash * 31) + words[i].GetHashCode();
            }

            _probe.Length = length;
            _probe.Hash = hash;
        }

        return _probe;
    }

    private int AddNewParseState(SymbolSequence? precedingSymbols, AuxiliarySymbolSequence? precedingAuxiliarySymbols, ParseItemSet itemSet, ItemSetKey key)
    {
        var core = itemSet.Core();
        if (!_coreIdsByCore.TryGetValue(core, out var coreId))
        {
            coreId = _coreIdsByCore.Count;
            _coreIdsByCore[core] = coreId;
        }

        var stateId = _parseTable.States.Count;
        _parseStateInfoById.Add((precedingSymbols, itemSet));
        _parseTable.States.Add(new ParseState { Id = stateId, CoreId = coreId });
        _parseStateQueue.Enqueue(new QueueEntry(stateId, precedingAuxiliarySymbols));
        _stateIdsByItemSet[key.Copy()] = stateId;
        return stateId;
    }

    private void AddActions(SymbolSequence? precedingSymbols, AuxiliarySymbolSequence? precedingAuxiliarySymbols, int stateId, ParseItemSet itemSet)
    {
        // The successors are visited by symbol, like the BTreeMaps of tree-sitter
        var terminalSuccessors = _terminalSuccessors;
        var nonTerminalSuccessors = _nonTerminalSuccessors;
        terminalSuccessors.Clear();
        nonTerminalSuccessors.Clear();
        _auxiliaryNodeInfos.Clear();
        var lookaheadsWithConflicts = new TokenSet();
        var reductionInfos = _reductionInfos;
        reductionInfos.Clear();
        var reductionInfoCount = 0;
        var state = _parseTable.States[stateId];

        // The parse tables of the large grammars have millions of entries: the terminal entries are allocated at their
        // size, the terminals of the shifts, reductions and extras
        var terminals = _terminals;
        terminals.Clear();
        foreach (var entry in itemSet.Entries)
        {
            if (entry.Item.NextSymbol is { } next)
            {
                if (!next.IsNonTerminal)
                {
                    terminals.Insert(next);
                }
            }
            else
            {
                terminals.InsertAll(entry.Lookaheads);
            }
        }

        if (!terminals.Contains(Symbol.EndOfNonTerminalExtra))
        {
            foreach (var (terminal, _) in _nonTerminalExtraStates)
            {
                terminals.Insert(terminal);
            }

            foreach (var extraToken in _syntaxGrammar.ExtraSymbols)
            {
                if (!extraToken.IsNonTerminal)
                {
                    terminals.Insert(extraToken);
                }
            }
        }

        state.TerminalEntries.EnsureCapacity(terminals.Count);

        foreach (var entry in itemSet.Entries)
        {
            var item = entry.Item;
            if (item.NextSymbol is { } nextSymbol)
            {
                var successor = item.Successor();
                ParseItemSet successorSet;
                if (nextSymbol.IsNonTerminal)
                {
                    var variable = _syntaxGrammar.Variables[nextSymbol.Index];
                    if (variable.IsAuxiliary)
                    {
                        // Vec::dedup of tree-sitter after the loop: only consecutive duplicates (the preceding infos
                        // are deduplicated already)
                        if (!_auxiliaryNodeInfos.TryGetValue(nextSymbol, out var info))
                        {
                            info = GetAuxiliaryNodeInfo(itemSet, nextSymbol);
                            _auxiliaryNodeInfos[nextSymbol] = info;
                        }

                        if (precedingAuxiliarySymbols is null || !SameAuxiliarySymbolInfo(precedingAuxiliarySymbols.Last, info))
                        {
                            precedingAuxiliarySymbols = new AuxiliarySymbolSequence(info, precedingAuxiliarySymbols);
                        }
                    }

                    if (variable.IsHidden && _variableInfo[nextSymbol.Index].Fields.Count > 0)
                    {
                        successor = successor.WithInheritedFields();
                    }

                    if (!nonTerminalSuccessors.TryGetValue(nextSymbol, out successorSet!))
                    {
                        successorSet = _itemSetBuilder.Pool.RentSet();
                        nonTerminalSuccessors[nextSymbol] = successorSet;
                    }
                }
                else if (!terminalSuccessors.TryGetValue(nextSymbol, out successorSet!))
                {
                    successorSet = _itemSetBuilder.Pool.RentSet();
                    terminalSuccessors[nextSymbol] = successorSet;
                }

                var successorEntry = successorSet.Insert(successor);
                successorEntry.Lookaheads.InsertAll(entry.Lookaheads);
                successorEntry.FollowingReservedWordSet = Math.Max(successorEntry.FollowingReservedWordSet, entry.FollowingReservedWordSet);
            }
            else
            {
                var symbol = Symbol.NonTerminal(item.VariableIndex);
                var action = item.IsAugmented
                    ? ParseAction.Accept
                    : ParseAction.Reduce(symbol, item.StepIndex, item.Production.DynamicPrecedence, GetProductionId(item));
                var precedence = item.Precedence;
                var associativity = item.Associativity;
                foreach (var lookahead in entry.Lookaheads.Symbols)
                {
                    var tableEntry = state.TerminalEntries.GetOrAdd(lookahead, () => new ParseTableEntry());
                    if (!reductionInfos.TryGetValue(lookahead, out var reductionInfo))
                    {
                        if (reductionInfoCount == _reductionInfoPool.Count)
                        {
                            _reductionInfoPool.Add(new ReductionInfo());
                        }

                        reductionInfo = _reductionInfoPool[reductionInfoCount++];
                        reductionInfo.Reset();
                        reductionInfos[lookahead] = reductionInfo;
                    }

                    if (tableEntry.Actions.Count == 0)
                    {
                        tableEntry.Actions.Add(action);
                    }
                    else
                    {
                        var comparison = ComparePrecedence(_syntaxGrammar, precedence, symbol, reductionInfo.Precedence, reductionInfo.Symbols);
                        if (comparison > 0)
                        {
                            tableEntry.Actions.Clear();
                            tableEntry.Actions.Add(action);
                            lookaheadsWithConflicts.Remove(lookahead);
                            reductionInfo.Reset();
                        }
                        else if (comparison == 0)
                        {
                            // Two items that reduce identically build the same tree: there is nothing to resolve
                            if (!tableEntry.Actions.Contains(action))
                            {
                                tableEntry.Actions.Add(action);
                                lookaheadsWithConflicts.Insert(lookahead);
                            }
                        }
                        else
                        {
                            continue;
                        }
                    }

                    reductionInfo.Precedence = precedence;
                    var index = reductionInfo.Symbols.BinarySearch(symbol);
                    if (index < 0)
                    {
                        reductionInfo.Symbols.Insert(~index, symbol);
                    }

                    switch (associativity)
                    {
                        case Associativity.Left:
                            reductionInfo.HasLeftAssoc = true;
                            break;
                        case Associativity.Right:
                            reductionInfo.HasRightAssoc = true;
                            break;
                        default:
                            reductionInfo.HasNonAssoc = true;
                            break;
                    }
                }
            }
        }

        state.NonTerminalEntries.EnsureCapacity(nonTerminalSuccessors.Count + _nonTerminalExtraCount);
        foreach (var symbol in terminalSuccessors.SortedKeys())
        {
            var nextStateId = AddSuccessorState(symbol, precedingSymbols, precedingAuxiliarySymbols, terminalSuccessors[symbol]);
            if (state.TerminalEntries.TryGetValue(symbol, out var existing) && existing.Actions.Count > 0)
            {
                lookaheadsWithConflicts.Insert(symbol);
            }

            state.TerminalEntries.GetOrAdd(symbol, () => new ParseTableEntry()).Actions.Add(ParseAction.Shift(nextStateId, false));
        }

        foreach (var symbol in nonTerminalSuccessors.SortedKeys())
        {
            var nextStateId = AddSuccessorState(symbol, precedingSymbols, precedingAuxiliarySymbols, nonTerminalSuccessors[symbol]);
            state.NonTerminalEntries[symbol] = new GotoAction(nextStateId);
        }

        foreach (var symbol in lookaheadsWithConflicts.Symbols.ToList())
        {
            HandleConflict(itemSet, stateId, precedingSymbols, precedingAuxiliarySymbols, symbol, reductionInfos[symbol]);
        }

        if (state.IsEndOfNonTerminalExtra)
        {
            if (state.TerminalEntries.Count > 1)
            {
                var parents = itemSet.Entries.Where(e => !e.Item.IsAugmented && e.Item.StepIndex > 0)
                    .Select(e => _syntaxGrammar.Variables[e.Item.VariableIndex].Name).Distinct();
                throw new GrammarException("Extra rules must have unambiguous endings. Conflicting rules: " + string.Join(", ", parents));
            }
        }
        else
        {
            foreach (var (terminal, extraStateId) in _nonTerminalExtraStates)
            {
                if (!state.TerminalEntries.ContainsKey(terminal))
                {
                    state.TerminalEntries[terminal] = new ParseTableEntry(ParseAction.Shift(extraStateId, false)) { Reusable = true };
                }
            }

            foreach (var extraToken in _syntaxGrammar.ExtraSymbols)
            {
                if (extraToken.IsNonTerminal)
                {
                    state.NonTerminalEntries[extraToken] = GotoAction.ShiftExtra;
                }
                else if (!state.TerminalEntries.ContainsKey(extraToken))
                {
                    state.TerminalEntries[extraToken] = new ParseTableEntry(ParseAction.ShiftExtra) { Reusable = true };
                }
            }
        }

        if (_syntaxGrammar.WordToken is { } keywordCaptureToken)
        {
            int? reservedWordSetId = null;
            foreach (var entry in itemSet.Entries)
            {
                int? id = null;
                if (entry.Item.Step is { } nextStep)
                {
                    if (nextStep.Symbol == keywordCaptureToken)
                    {
                        id = nextStep.ReservedWordSetId;
                    }
                }
                else if (entry.Lookaheads.Contains(keywordCaptureToken))
                {
                    id = entry.FollowingReservedWordSet;
                }

                if (id is { } value && (reservedWordSetId is null || value > reservedWordSetId))
                {
                    reservedWordSetId = value;
                }
            }

            if (reservedWordSetId is { } setId && setId < _syntaxGrammar.ReservedWordSets.Count)
            {
                // Shared by the states: the minimization copies it before changing it
                state.ReservedWords = _syntaxGrammar.ReservedWordSets[setId];
            }
        }
    }

    /// <summary>
    /// Adds the state of the successor item set of a symbol, recycling the item set when the state exists (the preceding
    /// symbols of a new state are the preceding symbols of the state and the symbol).
    /// </summary>
    private int AddSuccessorState(Symbol symbol, SymbolSequence? precedingSymbols, AuxiliarySymbolSequence? precedingAuxiliarySymbols, ParseItemSet itemSet)
    {
        var key = Key(itemSet);
        if (_stateIdsByItemSet.TryGetValue(key, out var existing))
        {
            _itemSetBuilder.Pool.Return(itemSet);
            return existing;
        }

        return AddNewParseState(new SymbolSequence(symbol, precedingSymbols), precedingAuxiliarySymbols, itemSet, key);
    }

    private static bool SameAuxiliarySymbolInfo(AuxiliarySymbolInfo left, AuxiliarySymbolInfo right) =>
        left.AuxiliarySymbol == right.AuxiliarySymbol && left.ParentSymbols.SequenceEqual(right.ParentSymbols);

    private void HandleConflict(ParseItemSet itemSet, int stateId, SymbolSequence? precedingSymbolSequence, AuxiliarySymbolSequence? precedingAuxiliarySymbols, Symbol conflictingLookahead, ReductionInfo reductionInfo)
    {
        var entry = _parseTable.States[stateId].TerminalEntries[conflictingLookahead];

        var consideredAssociativity = false;
        var shiftPrecedence = new List<(Precedence Precedence, Symbol Symbol)>();
        var conflictingItems = new SortedSet<ParseItem>();
        foreach (var itemEntry in itemSet.Entries)
        {
            var item = itemEntry.Item;
            if (item.Step is { } step)
            {
                if (item.StepIndex > 0 && _itemSetBuilder.FirstSet(step.Symbol).Contains(conflictingLookahead))
                {
                    if (item.VariableIndex != -1)
                    {
                        conflictingItems.Add(item);
                    }

                    (Precedence Precedence, Symbol Symbol) p = (item.Precedence, Symbol.NonTerminal(item.VariableIndex));
                    var index = -1;
                    for (var i = 0; i < shiftPrecedence.Count; i++)
                    {
                        if (shiftPrecedence[i].Precedence.CompareTo(p.Precedence) == 0 && shiftPrecedence[i].Symbol == p.Symbol)
                        {
                            index = i;
                            break;
                        }
                    }

                    if (index < 0)
                    {
                        shiftPrecedence.Add(p);
                        shiftPrecedence.Sort((a, b) =>
                        {
                            var c = a.Precedence.CompareTo(b.Precedence);
                            return c != 0 ? c : ((uint)a.Symbol.Index).CompareTo((uint)b.Symbol.Index);
                        });
                    }
                }
            }
            else if (itemEntry.Lookaheads.Contains(conflictingLookahead) && item.VariableIndex != -1)
            {
                conflictingItems.Add(item);
            }
        }

        var last = entry.Actions[entry.Actions.Count - 1];
        if (last.Kind == ParseActionKind.Shift)
        {
            // An intentional ambiguity of a repeat rule: keep it, and flag the shift
            var conflictingVariableIndex = conflictingItems.Min.VariableIndex;
            if (_syntaxGrammar.Variables[conflictingVariableIndex].IsAuxiliary && conflictingItems.All(i => i.VariableIndex == conflictingVariableIndex))
            {
                entry.Actions[entry.Actions.Count - 1] = ParseAction.Shift(last.State, true);
                return;
            }

            var shiftIsLess = false;
            var shiftIsEqual = false;
            var shiftIsMore = false;
            foreach (var p in shiftPrecedence)
            {
                var comparison = ComparePrecedence(_syntaxGrammar, p.Precedence, p.Symbol, reductionInfo.Precedence, reductionInfo.Symbols);
                if (comparison > 0)
                {
                    shiftIsMore = true;
                }
                else if (comparison < 0)
                {
                    shiftIsLess = true;
                }
                else
                {
                    shiftIsEqual = true;
                }
            }

            if (shiftIsMore && !shiftIsLess)
            {
                entry.Actions.RemoveRange(0, entry.Actions.Count - 1);
            }
            else if (shiftIsLess && !shiftIsMore)
            {
                // When a shift ties the purely right associative reductions while another has a lower precedence, the
                // right associativity wins: the lower precedence interpretation doesn't force a reduction
                if (shiftIsEqual && !reductionInfo.HasLeftAssoc && !reductionInfo.HasNonAssoc && reductionInfo.HasRightAssoc)
                {
                    entry.Actions.RemoveRange(0, entry.Actions.Count - 1);
                }
                else
                {
                    entry.Actions.RemoveAt(entry.Actions.Count - 1);
                    conflictingItems.RemoveWhere(i => !i.IsDone);
                }
            }
            else if (!shiftIsLess && !shiftIsMore)
            {
                consideredAssociativity = true;
                if (reductionInfo.HasLeftAssoc && !reductionInfo.HasNonAssoc && !reductionInfo.HasRightAssoc)
                {
                    entry.Actions.RemoveAt(entry.Actions.Count - 1);
                    conflictingItems.RemoveWhere(i => !i.IsDone);
                }
                else if (!reductionInfo.HasLeftAssoc && !reductionInfo.HasNonAssoc && reductionInfo.HasRightAssoc)
                {
                    entry.Actions.RemoveRange(0, entry.Actions.Count - 1);
                }
            }
        }

        if (entry.Actions.Count == 1)
        {
            return;
        }

        var actualConflict = new List<Symbol>();
        foreach (var item in conflictingItems)
        {
            var symbol = Symbol.NonTerminal(item.VariableIndex);
            if (_syntaxGrammar.Variables[symbol.Index].IsAuxiliary)
            {
                AuxiliarySymbolInfo? info = null;
                for (var s = precedingAuxiliarySymbols; s is not null; s = s.Previous)
                {
                    if (s.Last.AuxiliarySymbol == symbol)
                    {
                        info = s.Last;
                        break;
                    }
                }

                if (info is not null)
                {
                    actualConflict.AddRange(info.ParentSymbols);
                }
            }
            else
            {
                actualConflict.Add(symbol);
            }
        }

        actualConflict = actualConflict.Distinct().ToList();
        actualConflict.Sort();

        if (_syntaxGrammar.ExpectedConflicts.Any(c => c.SequenceEqual(actualConflict)))
        {
            _actualConflicts.Remove(ConflictKey(actualConflict));
            return;
        }

        var precedingSymbols = SymbolSequence.ToList(precedingSymbolSequence);
        var message = new StringBuilder();
        message.Append("Unresolved conflict for symbol sequence:\n\n");
        foreach (var symbol in precedingSymbols)
        {
            message.Append("  ").Append(SymbolName(symbol));
        }

        message.Append("  •  ").Append(SymbolName(conflictingLookahead)).Append("  …\n\n");
        message.Append("Possible interpretations:\n\n");
        var n = 1;
        foreach (var item in conflictingItems)
        {
            message.Append("  ").Append(n++).Append(":");
            foreach (var symbol in precedingSymbols.Take(precedingSymbols.Count - item.StepIndex))
            {
                message.Append("  ").Append(SymbolName(symbol));
            }

            message.Append("  (").Append(_syntaxGrammar.Variables[item.VariableIndex].Name);
            for (var i = 0; i < item.Production.Steps.Count; i++)
            {
                if (i == item.StepIndex)
                {
                    message.Append("  •");
                }

                message.Append("  ").Append(SymbolName(item.Production.Steps[i].Symbol));
            }

            message.Append(')');
            if (item.IsDone)
            {
                message.Append("  •  ").Append(SymbolName(conflictingLookahead)).Append("  …");
            }

            if (!item.Precedence.IsNone)
            {
                message.Append("  (precedence: ").Append(item.Precedence).Append(')');
            }

            message.Append('\n');
        }

        message.Append("\nPossible resolutions:\n\n");
        if (consideredAssociativity)
        {
            message.Append("  Specify a left or right associativity\n");
        }

        message.Append("  Add a conflict for these rules: ").Append(string.Join(", ", actualConflict.Select(s => "`" + SymbolName(s) + "`"))).Append('\n');
        throw new GrammarException(message.ToString());
    }

    /// <summary>tree-sitter's <c>compare_precedence</c>, for the single symbol of an item on the left.</summary>
    public static int ComparePrecedence(SyntaxGrammar grammar, Precedence left, Symbol leftSymbol, Precedence right, List<Symbol> rightSymbols)
    {
        if (left.Kind == PrecedenceValueKind.Integer && right.Kind == PrecedenceValueKind.Integer && (left.Value != 0 || right.Value != 0))
        {
            return left.Value.CompareTo(right.Value);
        }

        if (left.Kind == PrecedenceValueKind.Integer && right.Kind == PrecedenceValueKind.None && left.Value != 0)
        {
            return left.Value.CompareTo(0);
        }

        if (left.Kind == PrecedenceValueKind.None && right.Kind == PrecedenceValueKind.Integer && right.Value != 0)
        {
            return 0.CompareTo(right.Value);
        }

        bool MatchesSymbol(PrecedenceEntry entry, Symbol s) =>
            s.Index >= 0 && s.Index < grammar.Variables.Count && grammar.Variables[s.Index].Name == entry.Value;

        bool Matches(PrecedenceEntry entry, Precedence precedence, List<Symbol> symbols)
        {
            if (!entry.IsSymbol)
            {
                return precedence.Kind == PrecedenceValueKind.Name && precedence.Name == entry.Value;
            }

            foreach (var s in symbols)
            {
                if (MatchesSymbol(entry, s))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var list in grammar.PrecedenceOrderings)
        {
            var sawLeft = false;
            var sawRight = false;
            foreach (var entry in list)
            {
                var matchesLeft = entry.IsSymbol
                    ? MatchesSymbol(entry, leftSymbol)
                    : left.Kind == PrecedenceValueKind.Name && left.Name == entry.Value;
                var matchesRight = Matches(entry, right, rightSymbols);
                if (matchesLeft)
                {
                    sawLeft = true;
                    if (sawRight)
                    {
                        return -1;
                    }
                }
                else if (matchesRight)
                {
                    sawRight = true;
                    if (sawLeft)
                    {
                        return 1;
                    }
                }
            }
        }

        return 0;
    }

    private AuxiliarySymbolInfo GetAuxiliaryNodeInfo(ParseItemSet itemSet, Symbol symbol)
    {
        var parents = new List<Symbol>();
        foreach (var entry in itemSet.Entries)
        {
            var variableIndex = entry.Item.VariableIndex;
            if (entry.Item.NextSymbol == symbol && variableIndex >= 0 && !_syntaxGrammar.Variables[variableIndex].IsAuxiliary)
            {
                parents.Add(Symbol.NonTerminal(variableIndex));
            }
        }

        return new AuxiliarySymbolInfo(symbol, parents);
    }

    private int GetProductionId(ParseItem item)
    {
        // The id depends only on the steps of the production
        if (_productionIdsByProduction.TryGetValue(item.Production, out var id))
        {
            return id;
        }

        id = ComputeProductionId(item);
        _productionIdsByProduction[item.Production] = id;
        return id;
    }

    private int ComputeProductionId(ParseItem item)
    {
        var info = new ProductionInfo();
        for (var i = 0; i < item.Production.Steps.Count; i++)
        {
            var step = item.Production.Steps[i];
            info.AliasSequence.Add(step.Alias);
            if (step.FieldName is { } fieldName)
            {
                if (!info.FieldMap.TryGetValue(fieldName, out var locations))
                {
                    locations = [];
                    info.FieldMap[fieldName] = locations;
                }

                locations.Add(new FieldLocation(i, false));
            }

            if (step.Symbol.Kind == SymbolType.NonTerminal && !IsVisible(_syntaxGrammar.Variables[step.Symbol.Index].Kind))
            {
                foreach (var name in _variableInfo[step.Symbol.Index].Fields.Keys)
                {
                    if (!info.FieldMap.TryGetValue(name, out var locations))
                    {
                        locations = [];
                        info.FieldMap[name] = locations;
                    }

                    locations.Add(new FieldLocation(i, true));
                }
            }
        }

        while (info.AliasSequence.Count > 0 && info.AliasSequence[info.AliasSequence.Count - 1] is null)
        {
            info.AliasSequence.RemoveAt(info.AliasSequence.Count - 1);
        }

        if (item.Production.Steps.Count > _parseTable.MaxAliasedProductionLength)
        {
            _parseTable.MaxAliasedProductionLength = item.Production.Steps.Count;
        }

        return AddProductionInfo(info);
    }

    private int AddProductionInfo(ProductionInfo info)
    {
        if (_productionInfoIds.TryGetValue(info, out var id))
        {
            return id;
        }

        _parseTable.ProductionInfos.Add(info);
        id = _parseTable.ProductionInfos.Count - 1;
        _productionInfoIds[info] = id;
        return id;
    }

    private static bool IsVisible(VariableType kind) => kind is VariableType.Named or VariableType.Anonymous;

    private string SymbolName(Symbol symbol) => symbol.Kind switch
    {
        SymbolType.End or SymbolType.EndOfNonTerminalExtra => "EOF",
        SymbolType.External => _syntaxGrammar.ExternalTokens[symbol.Index].Name,
        SymbolType.NonTerminal => _syntaxGrammar.Variables[symbol.Index].Name,
        _ => _lexicalGrammar.Variables[symbol.Index].Kind == VariableType.Named
            ? _lexicalGrammar.Variables[symbol.Index].Name
            : "'" + _lexicalGrammar.Variables[symbol.Index].Name + "'",
    };
}
