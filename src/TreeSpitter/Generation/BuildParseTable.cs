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

    private readonly record struct QueueEntry(int StateId, List<AuxiliarySymbolInfo> PrecedingAuxiliarySymbols);

    private readonly ParseItemSetBuilder _itemSetBuilder;
    private readonly SyntaxGrammar _syntaxGrammar;
    private readonly LexicalGrammar _lexicalGrammar;
    private readonly List<VariableInfo> _variableInfo;
    private readonly Dictionary<ParseItemSetCore, int> _coreIdsByCore = [];
    private readonly Dictionary<ParseItemSet, int> _stateIdsByItemSet = [];
    private readonly List<(List<Symbol> PrecedingSymbols, ParseItemSet ItemSet)> _parseStateInfoById = [];
    private readonly Queue<QueueEntry> _parseStateQueue = new();
    private readonly List<(Symbol Terminal, int StateId)> _nonTerminalExtraStates = [];
    private readonly HashSet<string> _actualConflicts;
    private readonly ParseTable _parseTable = new();
    private readonly Dictionary<ProductionInfo, int> _productionInfoIds = [];

    public ParseTableBuilder(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, ParseItemSetBuilder itemSetBuilder, List<VariableInfo> variableInfo)
    {
        _syntaxGrammar = syntaxGrammar;
        _lexicalGrammar = lexicalGrammar;
        _itemSetBuilder = itemSetBuilder;
        _variableInfo = variableInfo;
        _actualConflicts = new HashSet<string>(syntaxGrammar.ExpectedConflicts.Select(ConflictKey), StringComparer.Ordinal);
    }

    public List<string> Warnings { get; } = [];

    private static string ConflictKey(List<Symbol> symbols) => string.Join(",", symbols.Select(s => s.Kind + ":" + s.Index));

    public ParseTable Build()
    {
        // The empty alias sequence has index 0
        AddProductionInfo(new ProductionInfo());

        // The error state is 0, the start state is 1
        AddParseState([], [], new ParseItemSet());
        var start = new ParseItemSet();
        start.Insert(ParseItem.Start()).Lookaheads.Insert(Symbol.End);
        AddParseState([], [], start);

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

            var stateId = AddParseState([], [], itemSet);
            _nonTerminalExtraStates.Add((terminal, stateId));
        }

        while (_parseStateQueue.Count > 0)
        {
            var entry = _parseStateQueue.Dequeue();
            var itemSet = _itemSetBuilder.TransitiveClosure(_parseStateInfoById[entry.StateId].ItemSet);
            AddActions([.. _parseStateInfoById[entry.StateId].PrecedingSymbols], entry.PrecedingAuxiliarySymbols, entry.StateId, itemSet);
        }

        return _parseTable;
    }

    private int AddParseState(List<Symbol> precedingSymbols, List<AuxiliarySymbolInfo> precedingAuxiliarySymbols, ParseItemSet itemSet)
    {
        if (_stateIdsByItemSet.TryGetValue(itemSet, out var existing))
        {
            return existing;
        }

        var core = itemSet.Core();
        if (!_coreIdsByCore.TryGetValue(core, out var coreId))
        {
            coreId = _coreIdsByCore.Count;
            _coreIdsByCore[core] = coreId;
        }

        var stateId = _parseTable.States.Count;
        _parseStateInfoById.Add(([.. precedingSymbols], itemSet));
        _parseTable.States.Add(new ParseState { Id = stateId, CoreId = coreId });
        _parseStateQueue.Enqueue(new QueueEntry(stateId, [.. precedingAuxiliarySymbols]));
        _stateIdsByItemSet[itemSet] = stateId;
        return stateId;
    }

    private void AddActions(List<Symbol> precedingSymbols, List<AuxiliarySymbolInfo> precedingAuxiliarySymbols, int stateId, ParseItemSet itemSet)
    {
        var terminalSuccessors = new SortedDictionary<Symbol, ParseItemSet>();
        var nonTerminalSuccessors = new SortedDictionary<Symbol, ParseItemSet>();
        var lookaheadsWithConflicts = new TokenSet();
        var reductionInfos = new Dictionary<Symbol, ReductionInfo>();
        var state = _parseTable.States[stateId];

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
                        precedingAuxiliarySymbols.Add(GetAuxiliaryNodeInfo(itemSet, nextSymbol));
                    }

                    if (variable.IsHidden && _variableInfo[nextSymbol.Index].Fields.Count > 0)
                    {
                        successor = successor.WithInheritedFields();
                    }

                    if (!nonTerminalSuccessors.TryGetValue(nextSymbol, out successorSet!))
                    {
                        successorSet = new ParseItemSet();
                        nonTerminalSuccessors[nextSymbol] = successorSet;
                    }
                }
                else if (!terminalSuccessors.TryGetValue(nextSymbol, out successorSet!))
                {
                    successorSet = new ParseItemSet();
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
                        reductionInfo = new ReductionInfo();
                        reductionInfos[lookahead] = reductionInfo;
                    }

                    if (tableEntry.Actions.Count == 0)
                    {
                        tableEntry.Actions.Add(action);
                    }
                    else
                    {
                        var comparison = ComparePrecedence(_syntaxGrammar, precedence, [symbol], reductionInfo.Precedence, reductionInfo.Symbols);
                        if (comparison > 0)
                        {
                            tableEntry.Actions.Clear();
                            tableEntry.Actions.Add(action);
                            lookaheadsWithConflicts.Remove(lookahead);
                            reductionInfo.Reset();
                        }
                        else if (comparison == 0)
                        {
                            tableEntry.Actions.Add(action);
                            lookaheadsWithConflicts.Insert(lookahead);
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

        // Vec::dedup: only consecutive duplicates
        for (var i = precedingAuxiliarySymbols.Count - 1; i > 0; i--)
        {
            if (precedingAuxiliarySymbols[i].AuxiliarySymbol == precedingAuxiliarySymbols[i - 1].AuxiliarySymbol &&
                precedingAuxiliarySymbols[i].ParentSymbols.SequenceEqual(precedingAuxiliarySymbols[i - 1].ParentSymbols))
            {
                precedingAuxiliarySymbols.RemoveAt(i);
            }
        }

        foreach (var (symbol, nextItemSet) in terminalSuccessors)
        {
            precedingSymbols.Add(symbol);
            var nextStateId = AddParseState(precedingSymbols, precedingAuxiliarySymbols, nextItemSet);
            precedingSymbols.RemoveAt(precedingSymbols.Count - 1);
            if (state.TerminalEntries.TryGetValue(symbol, out var existing) && existing.Actions.Count > 0)
            {
                lookaheadsWithConflicts.Insert(symbol);
            }

            state.TerminalEntries.GetOrAdd(symbol, () => new ParseTableEntry()).Actions.Add(ParseAction.Shift(nextStateId, false));
        }

        foreach (var (symbol, nextItemSet) in nonTerminalSuccessors)
        {
            precedingSymbols.Add(symbol);
            var nextStateId = AddParseState(precedingSymbols, precedingAuxiliarySymbols, nextItemSet);
            precedingSymbols.RemoveAt(precedingSymbols.Count - 1);
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
                    state.TerminalEntries[terminal] = new ParseTableEntry { Reusable = true, Actions = [ParseAction.Shift(extraStateId, false)] };
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
                    state.TerminalEntries[extraToken] = new ParseTableEntry { Reusable = true, Actions = [ParseAction.ShiftExtra] };
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
                state.ReservedWords = _syntaxGrammar.ReservedWordSets[setId].Clone();
            }
        }
    }

    private void HandleConflict(ParseItemSet itemSet, int stateId, List<Symbol> precedingSymbols, List<AuxiliarySymbolInfo> precedingAuxiliarySymbols, Symbol conflictingLookahead, ReductionInfo reductionInfo)
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
                    var index = shiftPrecedence.FindIndex(x => x.Precedence.CompareTo(p.Precedence) == 0 && x.Symbol == p.Symbol);
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
            var shiftIsMore = false;
            foreach (var p in shiftPrecedence)
            {
                var comparison = ComparePrecedence(_syntaxGrammar, p.Precedence, [p.Symbol], reductionInfo.Precedence, reductionInfo.Symbols);
                if (comparison > 0)
                {
                    shiftIsMore = true;
                }
                else if (comparison < 0)
                {
                    shiftIsLess = true;
                }
            }

            if (shiftIsMore && !shiftIsLess)
            {
                entry.Actions.RemoveRange(0, entry.Actions.Count - 1);
            }
            else if (shiftIsLess && !shiftIsMore)
            {
                entry.Actions.RemoveAt(entry.Actions.Count - 1);
                conflictingItems.RemoveWhere(i => !i.IsDone);
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
                for (var i = precedingAuxiliarySymbols.Count - 1; i >= 0; i--)
                {
                    if (precedingAuxiliarySymbols[i].AuxiliarySymbol == symbol)
                    {
                        info = precedingAuxiliarySymbols[i];
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

    public static int ComparePrecedence(SyntaxGrammar grammar, Precedence left, IReadOnlyList<Symbol> leftSymbols, Precedence right, IReadOnlyList<Symbol> rightSymbols)
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

        bool Matches(PrecedenceEntry entry, Precedence precedence, IReadOnlyList<Symbol> symbols)
        {
            if (!entry.IsSymbol)
            {
                return precedence.Kind == PrecedenceValueKind.Name && precedence.Name == entry.Value;
            }

            foreach (var s in symbols)
            {
                if (s.Index >= 0 && s.Index < grammar.Variables.Count && grammar.Variables[s.Index].Name == entry.Value)
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
                var matchesLeft = Matches(entry, left, leftSymbols);
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
