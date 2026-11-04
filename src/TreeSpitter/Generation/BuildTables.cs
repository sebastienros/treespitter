namespace TreeSpitter.Generation;

// A port of tree-sitter's build_tables.rs, build_tables/minimize_parse_table.rs, build_tables/build_lex_table.rs and
// dedup.rs.

internal sealed class Tables(ParseTable parseTable, LexTable mainLexTable, LexTable keywordLexTable)
{
    public ParseTable ParseTable { get; } = parseTable;

    public LexTable MainLexTable { get; } = mainLexTable;

    public LexTable KeywordLexTable { get; } = keywordLexTable;
}

internal static class TableBuilder
{
    public static Tables Build(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, AliasMap simpleAliases, List<VariableInfo> variableInfo, InlinedProductionMap inlines, List<string>? warnings)
    {
        var itemSetBuilder = new ParseItemSetBuilder(syntaxGrammar, lexicalGrammar, inlines);
        var followingTokens = GetFollowingTokens(syntaxGrammar, lexicalGrammar, inlines, itemSetBuilder);
        var builder = new ParseTableBuilder(syntaxGrammar, lexicalGrammar, itemSetBuilder, variableInfo);
        var parseTable = builder.Build();
        warnings?.AddRange(builder.Warnings);

        var tokenConflictMap = new TokenConflictMap(lexicalGrammar, followingTokens);
        var coincidentTokenIndex = new CoincidentTokenIndex(parseTable, lexicalGrammar);
        var keywords = IdentifyKeywords(lexicalGrammar, parseTable, syntaxGrammar.WordToken, tokenConflictMap, coincidentTokenIndex);
        PopulateErrorState(parseTable, syntaxGrammar, lexicalGrammar, coincidentTokenIndex, tokenConflictMap, keywords);
        PopulateUsedSymbols(parseTable, syntaxGrammar, lexicalGrammar);
        new Minimizer(parseTable, syntaxGrammar, lexicalGrammar, simpleAliases, tokenConflictMap, keywords).Minimize();
        var (mainLexTable, keywordLexTable) = LexTableBuilder.BuildLexTables(parseTable, syntaxGrammar, lexicalGrammar, keywords, coincidentTokenIndex, tokenConflictMap);
        PopulateExternalLexStates(parseTable, syntaxGrammar);
        MarkFragileTokens(parseTable, lexicalGrammar, tokenConflictMap);
        return new Tables(parseTable, mainLexTable, keywordLexTable);
    }

    private static List<TokenSet> GetFollowingTokens(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, InlinedProductionMap inlines, ParseItemSetBuilder builder)
    {
        var result = Enumerable.Range(0, lexicalGrammar.Variables.Count).Select(_ => new TokenSet()).ToList();
        var productions = syntaxGrammar.Variables.SelectMany(v => v.Productions).Concat(inlines.Productions);
        var allTokens = TokenSet.From(Enumerable.Range(0, result.Count).Select(Symbol.Terminal));
        foreach (var production in productions)
        {
            for (var i = 1; i < production.Steps.Count; i++)
            {
                var leftTokens = builder.LastSet(production.Steps[i - 1].Symbol);
                var rightTokens = builder.FirstSet(production.Steps[i].Symbol);
                var rightReservedTokens = builder.ReservedFirstSet(production.Steps[i].Symbol);
                foreach (var leftToken in leftTokens.Symbols)
                {
                    if (leftToken.IsTerminal)
                    {
                        result[leftToken.Index].InsertAllTerminals(rightTokens);
                        if (rightReservedTokens is not null)
                        {
                            result[leftToken.Index].InsertAllTerminals(rightReservedTokens);
                        }
                    }
                }
            }
        }

        foreach (var extra in syntaxGrammar.ExtraSymbols)
        {
            if (extra.IsTerminal)
            {
                foreach (var entry in result)
                {
                    entry.Insert(extra);
                }

                result[extra.Index] = allTokens.Clone();
            }
        }

        return result;
    }

    private static void PopulateErrorState(ParseTable parseTable, SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, CoincidentTokenIndex coincidentTokenIndex, TokenConflictMap tokenConflictMap, TokenSet keywords)
    {
        var state = parseTable.States[0];
        var n = lexicalGrammar.Variables.Count;

        // The tokens which don't overlap with any other token in any way, besides matching exactly the same string
        var conflictFreeTokens = new TokenSet();
        for (var i = 0; i < n; i++)
        {
            var conflicts = false;
            for (var j = 0; j < n; j++)
            {
                if (j != i && !coincidentTokenIndex.Contains(Symbol.Terminal(i), Symbol.Terminal(j)) && tokenConflictMap.DoesMatchShorterOrLonger(i, j))
                {
                    conflicts = true;
                    break;
                }
            }

            if (!conflicts)
            {
                conflictFreeTokens.Insert(Symbol.Terminal(i));
            }
        }

        // Exclude from the error recovery state the tokens which conflict with one of the conflict-free tokens
        for (var i = 0; i < n; i++)
        {
            var symbol = Symbol.Terminal(i);
            if (!conflictFreeTokens.Contains(symbol) && !keywords.Contains(symbol) && syntaxGrammar.WordToken != symbol)
            {
                var excluded = false;
                foreach (var t in conflictFreeTokens.Symbols)
                {
                    if (!coincidentTokenIndex.Contains(symbol, t) && tokenConflictMap.DoesConflict(symbol.Index, t.Index))
                    {
                        excluded = true;
                        break;
                    }
                }

                if (excluded)
                {
                    continue;
                }
            }

            if (!state.TerminalEntries.ContainsKey(symbol))
            {
                state.TerminalEntries[symbol] = new ParseTableEntry { Reusable = false, Actions = [ParseAction.Recover] };
            }
        }

        for (var i = 0; i < syntaxGrammar.ExternalTokens.Count; i++)
        {
            if (syntaxGrammar.ExternalTokens[i].CorrespondingInternalToken is null && !state.TerminalEntries.ContainsKey(Symbol.External(i)))
            {
                state.TerminalEntries[Symbol.External(i)] = new ParseTableEntry { Reusable = false, Actions = [ParseAction.Recover] };
            }
        }

        state.TerminalEntries[Symbol.End] = new ParseTableEntry { Reusable = false, Actions = [ParseAction.Recover] };
    }

    private static void PopulateUsedSymbols(ParseTable parseTable, SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar)
    {
        var terminalUsages = new bool[lexicalGrammar.Variables.Count];
        var nonTerminalUsages = new bool[syntaxGrammar.Variables.Count];
        var externalUsages = new bool[syntaxGrammar.ExternalTokens.Count];
        foreach (var state in parseTable.States)
        {
            foreach (var symbol in state.TerminalEntries.Keys)
            {
                if (symbol.Kind == SymbolType.Terminal)
                {
                    terminalUsages[symbol.Index] = true;
                }
                else if (symbol.Kind == SymbolType.External)
                {
                    externalUsages[symbol.Index] = true;
                }
            }

            foreach (var symbol in state.NonTerminalEntries.Keys)
            {
                nonTerminalUsages[symbol.Index] = true;
            }
        }

        parseTable.Symbols.Add(Symbol.End);
        for (var i = 0; i < terminalUsages.Length; i++)
        {
            if (terminalUsages[i])
            {
                // The word token gets a low symbol id
                if (syntaxGrammar.WordToken is { } word && word.Index == i)
                {
                    parseTable.Symbols.Insert(1, Symbol.Terminal(i));
                }
                else
                {
                    parseTable.Symbols.Add(Symbol.Terminal(i));
                }
            }
        }

        for (var i = 0; i < externalUsages.Length; i++)
        {
            if (externalUsages[i])
            {
                parseTable.Symbols.Add(Symbol.External(i));
            }
        }

        for (var i = 0; i < nonTerminalUsages.Length; i++)
        {
            if (nonTerminalUsages[i])
            {
                parseTable.Symbols.Add(Symbol.NonTerminal(i));
            }
        }
    }

    private static void PopulateExternalLexStates(ParseTable parseTable, SyntaxGrammar syntaxGrammar)
    {
        var externalTokensByCorrespondingInternalToken = new Dictionary<int, int>();
        for (var i = 0; i < syntaxGrammar.ExternalTokens.Count; i++)
        {
            if (syntaxGrammar.ExternalTokens[i].CorrespondingInternalToken is { } symbol)
            {
                externalTokensByCorrespondingInternalToken[symbol.Index] = i;
            }
        }

        // External lex state 0 is the absence of external tokens
        parseTable.ExternalLexStates.Add(new TokenSet());
        foreach (var state in parseTable.States)
        {
            var externalTokens = new TokenSet();
            foreach (var token in state.TerminalEntries.Keys)
            {
                if (token.IsExternal)
                {
                    externalTokens.Insert(token);
                }
                else if (token.IsTerminal && externalTokensByCorrespondingInternalToken.TryGetValue(token.Index, out var index))
                {
                    externalTokens.Insert(Symbol.External(index));
                }
            }

            var id = parseTable.ExternalLexStates.FindIndex(t => t.Equals(externalTokens));
            if (id < 0)
            {
                parseTable.ExternalLexStates.Add(externalTokens);
                id = parseTable.ExternalLexStates.Count - 1;
            }

            state.ExternalLexStateId = id;
        }
    }

    private static TokenSet IdentifyKeywords(LexicalGrammar lexicalGrammar, ParseTable parseTable, Symbol? wordToken, TokenConflictMap tokenConflictMap, CoincidentTokenIndex coincidentTokenIndex)
    {
        if (wordToken is not { } word)
        {
            return new TokenSet();
        }

        var cursor = new NfaCursor(lexicalGrammar.Nfa);

        // The tokens that start with letters or underscore and can match the same string as the word token
        var candidates = new TokenSet();
        for (var i = 0; i < lexicalGrammar.Variables.Count; i++)
        {
            cursor.Reset([lexicalGrammar.Variables[i].StartState]);
            if (AllCharsAreAlphabetical(cursor) &&
                tokenConflictMap.DoesMatchSameString(i, word.Index) &&
                !tokenConflictMap.DoesMatchDifferentString(i, word.Index))
            {
                candidates.Insert(Symbol.Terminal(i));
            }
        }

        // Exclude the candidates which shadow another candidate
        var keywords = new TokenSet();
        foreach (var token in candidates.Symbols)
        {
            var shadows = false;
            foreach (var other in candidates.Symbols)
            {
                if (other != token && tokenConflictMap.DoesMatchSameString(other.Index, token.Index))
                {
                    shadows = true;
                    break;
                }
            }

            if (!shadows)
            {
                keywords.Insert(token);
            }
        }

        // Exclude the candidates for which substituting the word token would introduce new lexical conflicts
        var result = new TokenSet();
        foreach (var token in keywords.Symbols)
        {
            var include = true;
            for (var other = 0; other < lexicalGrammar.Variables.Count; other++)
            {
                if (candidates.Contains(Symbol.Terminal(other)))
                {
                    continue;
                }

                if (coincidentTokenIndex.StatesWith(token, Symbol.Terminal(other)).All(stateId => parseTable.States[stateId].TerminalEntries.ContainsKey(word)))
                {
                    continue;
                }

                if (!tokenConflictMap.HasSameConflictStatus(token.Index, word.Index, other))
                {
                    include = false;
                    break;
                }
            }

            if (include)
            {
                result.Insert(token);
            }
        }

        return result;
    }

    private static bool AllCharsAreAlphabetical(NfaCursor cursor)
    {
        foreach (var (chars, isSeparator) in cursor.TransitionChars())
        {
            if (isSeparator)
            {
                continue;
            }

            foreach (var c in chars.Chars())
            {
                if (c != '_' && !IsAlphabetic(c))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Rust's <c>char::is_alphabetic</c>: the Unicode Alphabetic property, approximated by the letter categories.</summary>
    internal static bool IsAlphabetic(int c)
    {
        if (c < 128)
        {
            return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
        }

        if (c is >= 0xD800 and <= 0xDFFF || c > 0x10FFFF)
        {
            return false;
        }

        var category = c <= 0xFFFF
            ? System.Globalization.CharUnicodeInfo.GetUnicodeCategory((char)c)
            : System.Globalization.CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(c), 0);
        return category is System.Globalization.UnicodeCategory.UppercaseLetter or System.Globalization.UnicodeCategory.LowercaseLetter
            or System.Globalization.UnicodeCategory.TitlecaseLetter or System.Globalization.UnicodeCategory.ModifierLetter
            or System.Globalization.UnicodeCategory.OtherLetter or System.Globalization.UnicodeCategory.LetterNumber;
    }

    private static void MarkFragileTokens(ParseTable parseTable, LexicalGrammar lexicalGrammar, TokenConflictMap tokenConflictMap)
    {
        var n = lexicalGrammar.Variables.Count;
        var valid = new bool[n];
        foreach (var state in parseTable.States)
        {
            Array.Clear(valid, 0, n);
            foreach (var token in state.TerminalEntries.Keys)
            {
                if (token.IsTerminal)
                {
                    valid[token.Index] = true;
                }
            }

            foreach (var (token, entry) in state.TerminalEntries.Entries)
            {
                if (!token.IsTerminal)
                {
                    continue;
                }

                for (var i = 0; i < n; i++)
                {
                    if (valid[i] && tokenConflictMap.DoesOverlap(i, token.Index))
                    {
                        entry.Reusable = false;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Splits the groups of states until <paramref name="shouldSplit"/> holds for no pair of states of a group.</summary>
    public static bool SplitStateIdGroups<TState>(IReadOnlyList<TState> states, List<List<int>> stateIdsByGroupId, int[] groupIdsByStateId, int startGroupId, Func<TState, TState, int[], bool> shouldSplit)
    {
        var result = false;
        var groupId = startGroupId;
        while (groupId < stateIdsByGroupId.Count)
        {
            var stateIds = stateIdsByGroupId[groupId];
            var splitStateIds = new List<int>();
            var split = new HashSet<int>();
            for (var i = 0; i < stateIds.Count; i++)
            {
                var leftStateId = stateIds[i];
                if (split.Contains(leftStateId))
                {
                    continue;
                }

                var leftState = states[leftStateId];
                for (var j = i + 1; j < stateIds.Count; j++)
                {
                    var rightStateId = stateIds[j];
                    if (split.Contains(rightStateId))
                    {
                        continue;
                    }

                    if (shouldSplit(leftState, states[rightStateId], groupIdsByStateId))
                    {
                        splitStateIds.Add(rightStateId);
                        split.Add(rightStateId);
                    }
                }
            }

            if (splitStateIds.Count > 0)
            {
                result = true;
                stateIds.RemoveAll(split.Contains);
                var newGroupId = stateIdsByGroupId.Count;
                foreach (var id in splitStateIds)
                {
                    groupIdsByStateId[id] = newGroupId;
                }

                stateIdsByGroupId.Add(splitStateIds);
            }

            groupId++;
        }

        return result;
    }
}

internal sealed class Minimizer(ParseTable parseTable, SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, AliasMap simpleAliases, TokenConflictMap tokenConflictMap, TokenSet keywords)
{
    public void Minimize()
    {
        MergeCompatibleStates();
        RemoveUnitReductions();
        RemoveUnusedStates();
        ReorderStatesByDescendingSize();
    }

    private void RemoveUnitReductions()
    {
        var aliasedSymbols = new HashSet<Symbol>();
        foreach (var variable in syntaxGrammar.Variables)
        {
            foreach (var production in variable.Productions)
            {
                foreach (var step in production.Steps)
                {
                    if (step.Alias is not null)
                    {
                        aliasedSymbols.Add(step.Symbol);
                    }
                }
            }
        }

        var unitReductionSymbolsByState = new Dictionary<int, Symbol>();
        for (var i = 0; i < parseTable.States.Count; i++)
        {
            var state = parseTable.States[i];
            var onlyUnitReductions = true;
            Symbol? unitReductionSymbol = null;
            foreach (var entry in state.TerminalEntries.Values)
            {
                foreach (var action in entry.Actions)
                {
                    if (action.Kind == ParseActionKind.ShiftExtra)
                    {
                        continue;
                    }

                    if (action.Kind == ParseActionKind.Reduce &&
                        action.ChildCount == 1 &&
                        action.ProductionId == 0 &&
                        !simpleAliases.ContainsKey(action.Symbol) &&
                        !syntaxGrammar.SupertypeSymbols.Contains(action.Symbol) &&
                        !syntaxGrammar.ExtraSymbols.Contains(action.Symbol) &&
                        !aliasedSymbols.Contains(action.Symbol) &&
                        syntaxGrammar.Variables[action.Symbol.Index].Kind != VariableType.Named &&
                        (unitReductionSymbol is null || unitReductionSymbol == action.Symbol))
                    {
                        unitReductionSymbol = action.Symbol;
                        continue;
                    }

                    onlyUnitReductions = false;
                    break;
                }

                if (!onlyUnitReductions)
                {
                    break;
                }
            }

            if (unitReductionSymbol is { } symbol && onlyUnitReductions)
            {
                unitReductionSymbolsByState[i] = symbol;
            }
        }

        foreach (var state in parseTable.States)
        {
            var done = false;
            while (!done)
            {
                done = true;
                state.UpdateReferencedStates((otherStateId, s) =>
                {
                    if (unitReductionSymbolsByState.TryGetValue(otherStateId, out var symbol))
                    {
                        done = false;
                        if (s.NonTerminalEntries.TryGetValue(symbol, out var action) && !action.IsShiftExtra)
                        {
                            return action.State;
                        }
                    }

                    return otherStateId;
                });
            }
        }
    }

    private void MergeCompatibleStates()
    {
        var coreCount = 1 + parseTable.States.Max(s => s.CoreId);
        var groupIdsByStateId = new int[parseTable.States.Count];
        var stateIdsByGroupId = Enumerable.Range(0, coreCount).Select(_ => new List<int>()).ToList();
        for (var i = 0; i < parseTable.States.Count; i++)
        {
            var state = parseTable.States[i];
            stateIdsByGroupId[state.CoreId].Add(i);
            groupIdsByStateId[i] = state.CoreId;
        }

        TableBuilder.SplitStateIdGroups(parseTable.States, stateIdsByGroupId, groupIdsByStateId, 0, StatesConflict);
        while (TableBuilder.SplitStateIdGroups(parseTable.States, stateIdsByGroupId, groupIdsByStateId, 0, StateSuccessorsDiffer))
        {
        }

        var errorGroupIndex = stateIdsByGroupId.FindIndex(g => g.Contains(0));
        var startGroupIndex = stateIdsByGroupId.FindIndex(g => g.Contains(1));
        Swap(stateIdsByGroupId, errorGroupIndex, 0);
        Swap(stateIdsByGroupId, startGroupIndex, 1);

        // The group ids changed with the swaps
        for (var groupId = 0; groupId < stateIdsByGroupId.Count; groupId++)
        {
            foreach (var stateId in stateIdsByGroupId[groupId])
            {
                groupIdsByStateId[stateId] = groupId;
            }
        }

        var newStates = new List<ParseState>(stateIdsByGroupId.Count);
        foreach (var stateIds in stateIdsByGroupId)
        {
            var parseState = parseTable.States[stateIds[0]];
            for (var k = 1; k < stateIds.Count; k++)
            {
                var other = parseTable.States[stateIds[k]];
                parseState.TerminalEntries.Extend(other.TerminalEntries);
                parseState.NonTerminalEntries.Extend(other.NonTerminalEntries);
                parseState.ReservedWords.InsertAll(other.ReservedWords);
                foreach (var symbol in parseState.TerminalEntries.Keys)
                {
                    parseState.ReservedWords.Remove(symbol);
                }
            }

            parseState.UpdateReferencedStates((stateId, _) => groupIdsByStateId[stateId]);
            newStates.Add(parseState);
        }

        parseTable.States = newStates;
    }

    private static void Swap(List<List<int>> list, int i, int j) => (list[i], list[j]) = (list[j], list[i]);

    private bool StatesConflict(ParseState left, ParseState right, int[] groupIdsByStateId)
    {
        foreach (var (token, leftEntry) in left.TerminalEntries.Entries)
        {
            if (right.TerminalEntries.TryGetValue(token, out var rightEntry))
            {
                if (EntriesConflict(leftEntry, rightEntry, groupIdsByStateId))
                {
                    return true;
                }
            }
            else if (TokenConflicts(right, token))
            {
                return true;
            }
        }

        foreach (var token in right.TerminalEntries.Keys)
        {
            if (!left.TerminalEntries.ContainsKey(token) && TokenConflicts(left, token))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StateSuccessorsDiffer(ParseState state1, ParseState state2, int[] groupIdsByStateId)
    {
        foreach (var (token, entry1) in state1.TerminalEntries.Entries)
        {
            var last1 = entry1.Actions[entry1.Actions.Count - 1];
            if (last1.Kind == ParseActionKind.Shift && state2.TerminalEntries.TryGetValue(token, out var entry2))
            {
                var last2 = entry2.Actions[entry2.Actions.Count - 1];
                if (last2.Kind == ParseActionKind.Shift && groupIdsByStateId[last1.State] != groupIdsByStateId[last2.State])
                {
                    return true;
                }
            }
        }

        foreach (var (symbol, s1) in state1.NonTerminalEntries.Entries)
        {
            if (state2.NonTerminalEntries.TryGetValue(symbol, out var s2))
            {
                if (s1.IsShiftExtra && s2.IsShiftExtra)
                {
                    continue;
                }

                if (!s1.IsShiftExtra && !s2.IsShiftExtra)
                {
                    if (groupIdsByStateId[s1.State] != groupIdsByStateId[s2.State])
                    {
                        return true;
                    }

                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool EntriesConflict(ParseTableEntry entry1, ParseTableEntry entry2, int[] groupIdsByStateId)
    {
        if (entry1.Actions.Count != entry2.Actions.Count)
        {
            return true;
        }

        for (var i = 0; i < entry1.Actions.Count; i++)
        {
            var action1 = entry1.Actions[i];
            var action2 = entry2.Actions[i];
            if (action1.Kind == ParseActionKind.Shift && action2.Kind == ParseActionKind.Shift)
            {
                if (groupIdsByStateId[action1.State] == groupIdsByStateId[action2.State] && action1.IsRepetition == action2.IsRepetition)
                {
                    continue;
                }

                return true;
            }

            if (action1 != action2)
            {
                return true;
            }
        }

        return false;
    }

    private bool TokenConflicts(ParseState rightState, Symbol newToken)
    {
        if (newToken == Symbol.EndOfNonTerminalExtra)
        {
            return true;
        }

        if (newToken.IsExternal)
        {
            return true;
        }

        if (rightState.ReservedWords.Contains(newToken))
        {
            return false;
        }

        if (syntaxGrammar.ExternalTokens.Any(e => e.CorrespondingInternalToken == newToken))
        {
            return true;
        }

        foreach (var token in rightState.TerminalEntries.Keys)
        {
            if (!token.IsTerminal)
            {
                continue;
            }

            if (syntaxGrammar.WordToken == token && keywords.Contains(newToken))
            {
                continue;
            }

            if (syntaxGrammar.WordToken == newToken && keywords.Contains(token))
            {
                continue;
            }

            if (newToken.IsTerminal &&
                (tokenConflictMap.DoesConflict(newToken.Index, token.Index) || tokenConflictMap.DoesMatchSameString(newToken.Index, token.Index)))
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveUnusedStates()
    {
        var used = new bool[parseTable.States.Count];
        used[0] = true;
        used[1] = true;
        foreach (var state in parseTable.States)
        {
            foreach (var referenced in state.ReferencedStates())
            {
                used[referenced] = true;
            }
        }

        var removed = 0;
        var replacement = new int[parseTable.States.Count];
        for (var stateId = 0; stateId < parseTable.States.Count; stateId++)
        {
            replacement[stateId] = stateId - removed;
            if (!used[stateId])
            {
                removed++;
            }
        }

        var newStates = new List<ParseState>();
        for (var stateId = 0; stateId < parseTable.States.Count; stateId++)
        {
            if (used[stateId])
            {
                var state = parseTable.States[stateId];
                state.UpdateReferencedStates((other, _) => replacement[other]);
                newStates.Add(state);
            }
        }

        parseTable.States = newStates;
    }

    private void ReorderStatesByDescendingSize()
    {
        var count = parseTable.States.Count;
        var oldIdsByNewId = Enumerable.Range(0, count).ToArray();
        long Key(int i)
        {
            if (i <= 1)
            {
                return i - 1_000_000L;
            }

            var state = parseTable.States[i];
            return -(long)(state.TerminalEntries.Count + state.NonTerminalEntries.Count);
        }

        // sort_unstable_by_key: make it deterministic with the index as tie-breaker
        Array.Sort(oldIdsByNewId, (a, b) =>
        {
            var c = Key(a).CompareTo(Key(b));
            return c != 0 ? c : a.CompareTo(b);
        });

        var newIdsByOldId = new int[count];
        for (var id = 0; id < count; id++)
        {
            newIdsByOldId[oldIdsByNewId[id]] = id;
        }

        var newStates = new List<ParseState>(count);
        foreach (var oldId in oldIdsByNewId)
        {
            var state = parseTable.States[oldId];
            state.UpdateReferencedStates((id, _) => newIdsByOldId[id]);
            newStates.Add(state);
        }

        parseTable.States = newStates;
    }

    private string SymbolName(Symbol symbol) => symbol.IsNonTerminal
        ? syntaxGrammar.Variables[symbol.Index].Name
        : symbol.IsExternal ? syntaxGrammar.ExternalTokens[symbol.Index].Name : lexicalGrammar.Variables[symbol.Index].Name;
}

internal sealed class LexTableBuilder
{
    private readonly LexicalGrammar _lexicalGrammar;
    private readonly NfaCursor _cursor;
    private readonly Queue<(int StateId, List<int> NfaStates, bool EofValid)> _stateQueue = new();
    private readonly Dictionary<(string Key, bool EofValid), int> _stateIdsByNfaStateSet = [];

    private LexTableBuilder(LexicalGrammar lexicalGrammar)
    {
        _lexicalGrammar = lexicalGrammar;
        _cursor = new NfaCursor(lexicalGrammar.Nfa);
    }

    public LexTable Table { get; private set; } = new();

    public static (LexTable Main, LexTable Keyword) BuildLexTables(ParseTable parseTable, SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, TokenSet keywords, CoincidentTokenIndex coincidentTokenIndex, TokenConflictMap tokenConflictMap)
    {
        var keywordLexTable = new LexTable();
        if (syntaxGrammar.WordToken is not null)
        {
            var keywordBuilder = new LexTableBuilder(lexicalGrammar);
            keywordBuilder.AddStateForTokens(keywords);
            keywordLexTable = keywordBuilder.Table;
        }

        var parseStateIdsByTokenSet = new List<(TokenSet Tokens, List<int> ParseStateIds)>();
        for (var i = 0; i < parseTable.States.Count; i++)
        {
            var state = parseTable.States[i];
            var tokens = new TokenSet();
            foreach (var token in state.TerminalEntries.Keys.Concat(state.ReservedWords.Symbols))
            {
                if (token.IsTerminal)
                {
                    if (keywords.Contains(token))
                    {
                        if (syntaxGrammar.WordToken is { } word)
                        {
                            tokens.Insert(word);
                        }
                    }
                    else
                    {
                        tokens.Insert(token);
                    }
                }
                else if (token.IsEof)
                {
                    tokens.Insert(token);
                }
            }

            var didMerge = false;
            foreach (var entry in parseStateIdsByTokenSet)
            {
                if (MergeTokenSet(entry.Tokens, tokens, lexicalGrammar, tokenConflictMap, coincidentTokenIndex))
                {
                    didMerge = true;
                    entry.ParseStateIds.Add(i);
                    break;
                }
            }

            if (!didMerge)
            {
                parseStateIdsByTokenSet.Add((tokens, [i]));
            }
        }

        var builder = new LexTableBuilder(lexicalGrammar);
        foreach (var (tokens, parseStateIds) in parseStateIdsByTokenSet)
        {
            var lexStateId = builder.AddStateForTokens(tokens);
            foreach (var id in parseStateIds)
            {
                parseTable.States[id].LexStateId = lexStateId;
            }
        }

        var mainLexTable = builder.Table;
        MinimizeLexTable(mainLexTable, parseTable);
        SortStates(mainLexTable, parseTable);
        return (mainLexTable, keywordLexTable);
    }

    private int AddStateForTokens(TokenSet tokens)
    {
        var eofValid = false;
        var nfaStates = new List<int>();
        foreach (var token in tokens.Symbols)
        {
            if (token.IsTerminal)
            {
                nfaStates.Add(_lexicalGrammar.Variables[token.Index].StartState);
            }
            else
            {
                eofValid = true;
            }
        }

        var (stateId, _) = AddState(nfaStates, eofValid);
        while (_stateQueue.Count > 0)
        {
            var entry = _stateQueue.Dequeue();
            PopulateState(entry.StateId, entry.NfaStates, entry.EofValid);
        }

        return stateId;
    }

    private (int StateId, bool IsNew) AddState(List<int> nfaStates, bool eofValid)
    {
        _cursor.Reset(nfaStates);
        var key = (string.Join(",", _cursor.StateIds), eofValid);
        if (_stateIdsByNfaStateSet.TryGetValue(key, out var existing))
        {
            return (existing, false);
        }

        var stateId = Table.States.Count;
        Table.States.Add(new LexState());
        _stateQueue.Enqueue((stateId, [.. _cursor.StateIds], eofValid));
        _stateIdsByNfaStateSet[key] = stateId;
        return (stateId, true);
    }

    private void PopulateState(int stateId, List<int> nfaStates, bool eofValid)
    {
        _cursor.ForceReset(nfaStates);

        (int Id, int Precedence)? completion = null;
        foreach (var (id, precedence) in _cursor.Completions())
        {
            if (completion is { } previous && TokenConflictMap.PreferToken(_lexicalGrammar, (previous.Precedence, previous.Id), (precedence, id)))
            {
                continue;
            }

            completion = (id, precedence);
        }

        var transitions = _cursor.Transitions();
        var hasSeparator = _cursor.TransitionChars().Any(t => t.IsSeparator);
        var isEmpty = _cursor.StateIds.Count == 0;

        if (eofValid)
        {
            var (nextStateId, _) = AddState([], false);
            Table.States[stateId].EofAction = new AdvanceAction(nextStateId, true);
        }

        foreach (var transition in transitions)
        {
            if (completion is { } completed && !TokenConflictMap.PreferTransition(_lexicalGrammar, transition, completed.Id, completed.Precedence, hasSeparator))
            {
                continue;
            }

            var (nextStateId, _) = AddState(transition.States, eofValid && transition.IsSeparator);
            Table.States[stateId].AdvanceActions.Add((transition.Characters, new AdvanceAction(nextStateId, !transition.IsSeparator)));
        }

        if (completion is { } complete)
        {
            Table.States[stateId].AcceptAction = Symbol.Terminal(complete.Id);
        }
        else if (isEmpty)
        {
            Table.States[stateId].AcceptAction = Symbol.End;
        }
    }

    private static bool MergeTokenSet(TokenSet tokens, TokenSet other, LexicalGrammar lexicalGrammar, TokenConflictMap tokenConflictMap, CoincidentTokenIndex coincidentTokenIndex)
    {
        for (var i = 0; i < lexicalGrammar.Variables.Count; i++)
        {
            var symbol = Symbol.Terminal(i);
            TokenSet setWithoutTerminal;
            var inTokens = tokens.ContainsTerminal(i);
            var inOther = other.ContainsTerminal(i);
            if (inTokens && !inOther)
            {
                setWithoutTerminal = other;
            }
            else if (!inTokens && inOther)
            {
                setWithoutTerminal = tokens;
            }
            else
            {
                continue;
            }

            foreach (var existing in setWithoutTerminal.Terminals)
            {
                if (tokenConflictMap.DoesConflict(i, existing.Index) || tokenConflictMap.DoesMatchPrefix(i, existing.Index))
                {
                    return false;
                }

                if (!coincidentTokenIndex.Contains(symbol, existing) &&
                    (tokenConflictMap.DoesOverlap(existing.Index, i) || tokenConflictMap.DoesOverlap(i, existing.Index)))
                {
                    return false;
                }
            }
        }

        tokens.InsertAll(other);
        return true;
    }

    private static void MinimizeLexTable(LexTable table, ParseTable parseTable)
    {
        // Group the states by their accept action and their valid lookahead characters
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < table.States.Count; i++)
        {
            var state = table.States[i];
            var signature = new System.Text.StringBuilder();
            signature.Append(i == 0 ? '1' : '0').Append('|');
            signature.Append(state.AcceptAction is { } accept ? accept.Kind + ":" + accept.Index : "-").Append('|');
            signature.Append(state.EofAction.HasValue ? '1' : '0').Append('|');
            foreach (var (chars, action) in state.AdvanceActions)
            {
                foreach (var (start, end) in chars.Ranges)
                {
                    signature.Append(start).Append('-').Append(end).Append(',');
                }

                signature.Append(action.InMainToken ? 'm' : 's').Append(';');
            }

            var key = signature.ToString();
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(i);
        }

        var stateIdsByGroupId = groups.Values.ToList();
        stateIdsByGroupId.Sort((a, b) =>
        {
            for (var k = 0; k < Math.Min(a.Count, b.Count); k++)
            {
                var c = a[k].CompareTo(b[k]);
                if (c != 0)
                {
                    return c;
                }
            }

            return a.Count.CompareTo(b.Count);
        });
        var errorGroupIndex = stateIdsByGroupId.FindIndex(g => g.Contains(0));
        (stateIdsByGroupId[errorGroupIndex], stateIdsByGroupId[0]) = (stateIdsByGroupId[0], stateIdsByGroupId[errorGroupIndex]);

        var groupIdsByStateId = new int[table.States.Count];
        for (var groupId = 0; groupId < stateIdsByGroupId.Count; groupId++)
        {
            foreach (var stateId in stateIdsByGroupId[groupId])
            {
                groupIdsByStateId[stateId] = groupId;
            }
        }

        while (TableBuilder.SplitStateIdGroups(table.States, stateIdsByGroupId, groupIdsByStateId, 1, LexStatesDiffer))
        {
        }

        var newStates = new List<LexState>(stateIdsByGroupId.Count);
        foreach (var stateIds in stateIdsByGroupId)
        {
            var state = table.States[stateIds[0]];
            var newState = new LexState { AcceptAction = state.AcceptAction };
            foreach (var (chars, action) in state.AdvanceActions)
            {
                newState.AdvanceActions.Add((chars, action with { State = groupIdsByStateId[action.State] }));
            }

            if (state.EofAction is { } eof)
            {
                newState.EofAction = eof with { State = groupIdsByStateId[eof.State] };
            }

            newStates.Add(newState);
        }

        foreach (var state in parseTable.States)
        {
            state.LexStateId = groupIdsByStateId[state.LexStateId];
        }

        table.States = newStates;
    }

    private static bool LexStatesDiffer(LexState left, LexState right, int[] groupIdsByStateId)
    {
        var count = Math.Min(left.AdvanceActions.Count, right.AdvanceActions.Count);
        for (var i = 0; i < count; i++)
        {
            if (groupIdsByStateId[left.AdvanceActions[i].Action.State] != groupIdsByStateId[right.AdvanceActions[i].Action.State])
            {
                return true;
            }
        }

        return false;
    }

    private static void SortStates(LexTable table, ParseTable parseTable)
    {
        var count = table.States.Count;
        var oldIdsByNewId = Enumerable.Range(0, count).ToList();
        var tail = oldIdsByNewId.Skip(1).ToList();
        // A stable sort, like sort_by_key
        var sortedTail = tail.Select((id, index) => (id, index)).ToList();
        sortedTail.Sort((a, b) =>
        {
            var c = table.States[a.id].CompareTo(table.States[b.id]);
            return c != 0 ? c : a.index.CompareTo(b.index);
        });
        oldIdsByNewId = [0, .. sortedTail.Select(t => t.id)];
        if (count == 0)
        {
            oldIdsByNewId.Clear();
        }

        var newIdsByOldId = new int[count];
        for (var id = 0; id < oldIdsByNewId.Count; id++)
        {
            newIdsByOldId[oldIdsByNewId[id]] = id;
        }

        var newStates = new List<LexState>(count);
        foreach (var oldId in oldIdsByNewId)
        {
            var state = table.States[oldId];
            var newState = new LexState { AcceptAction = state.AcceptAction };
            foreach (var (chars, action) in state.AdvanceActions)
            {
                newState.AdvanceActions.Add((chars, action with { State = newIdsByOldId[action.State] }));
            }

            if (state.EofAction is { } eof)
            {
                newState.EofAction = eof with { State = newIdsByOldId[eof.State] };
            }

            newStates.Add(newState);
        }

        foreach (var state in parseTable.States)
        {
            state.LexStateId = newIdsByOldId[state.LexStateId];
        }

        table.States = newStates;
    }
}
