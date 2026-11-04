namespace TreeSpitter.Generation;

// A port of tree-sitter's build_tables.rs, build_tables/minimize_parse_table.rs, build_tables/build_lex_table.rs and
// dedup.rs.

internal sealed class Tables(ParseTable parseTable, LexTable mainLexTable, LexTable keywordLexTable, List<CharacterSet> largeCharacterSets)
{
    public ParseTable ParseTable { get; } = parseTable;

    public LexTable MainLexTable { get; } = mainLexTable;

    public LexTable KeywordLexTable { get; } = keywordLexTable;

    /// <summary>
    /// The large character sets of the tokens, tree-sitter's <c>large_character_sets</c>: render.rs looks them up with
    /// <c>set_contains</c>, which changes the conditions of the transitions on invalid UTF-8 (see
    /// <c>LanguageBuilder.DecodeErrorTransition</c>).
    /// </summary>
    public List<CharacterSet> LargeCharacterSets { get; } = largeCharacterSets;
}

internal static class TableBuilder
{
    public static Tables Build(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, AliasMap simpleAliases, List<VariableInfo> variableInfo, InlinedProductionMap inlines, List<string>? warnings)
    {
        var itemSetBuilder = new ParseItemSetBuilder(syntaxGrammar, lexicalGrammar, inlines);
        var followingTokens = GetFollowingTokens(syntaxGrammar, lexicalGrammar, inlines, itemSetBuilder);

        // The token conflicts depend only on the lexical grammar and the following tokens: tree-sitter computes them after
        // the parse table, they are computed before it here, when their garbage is collected without the large heap of
        // the states
        var tokenConflictMap = new TokenConflictMap(lexicalGrammar, followingTokens);
        var builder = new ParseTableBuilder(syntaxGrammar, lexicalGrammar, itemSetBuilder, variableInfo);
        var parseTable = builder.Build();
        warnings?.AddRange(builder.Warnings);

        var coincidentTokenIndex = new CoincidentTokenIndex(parseTable, lexicalGrammar, syntaxGrammar.WordToken);
        var keywords = IdentifyKeywords(lexicalGrammar, syntaxGrammar.WordToken, tokenConflictMap, coincidentTokenIndex);
        PopulateErrorState(parseTable, syntaxGrammar, lexicalGrammar, coincidentTokenIndex, tokenConflictMap, keywords);
        PopulateUsedSymbols(parseTable, syntaxGrammar, lexicalGrammar);
        new Minimizer(parseTable, syntaxGrammar, lexicalGrammar, simpleAliases, tokenConflictMap, keywords).Minimize();
        var (mainLexTable, keywordLexTable, largeCharacterSets) = LexTableBuilder.BuildLexTables(parseTable, syntaxGrammar, lexicalGrammar, keywords, coincidentTokenIndex, tokenConflictMap);
        PopulateExternalLexStates(parseTable, syntaxGrammar);
        MarkFragileTokens(parseTable, lexicalGrammar, tokenConflictMap);
        if (parseTable.States.Count > ushort.MaxValue)
        {
            throw new GrammarException($"State count `{parseTable.States.Count}` exceeds the max value {ushort.MaxValue}.");
        }

        return new Tables(parseTable, mainLexTable, keywordLexTable, largeCharacterSets);
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
                state.TerminalEntries[symbol] = new ParseTableEntry(ParseAction.Recover) { Reusable = false };
            }
        }

        for (var i = 0; i < syntaxGrammar.ExternalTokens.Count; i++)
        {
            if (syntaxGrammar.ExternalTokens[i].CorrespondingInternalToken is null && !state.TerminalEntries.ContainsKey(Symbol.External(i)))
            {
                state.TerminalEntries[Symbol.External(i)] = new ParseTableEntry(ParseAction.Recover) { Reusable = false };
            }
        }

        state.TerminalEntries[Symbol.End] = new ParseTableEntry(ParseAction.Recover) { Reusable = false };
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
                if (syntaxGrammar.WordToken == Symbol.Terminal(i))
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

    private static TokenSet IdentifyKeywords(LexicalGrammar lexicalGrammar, Symbol? wordToken, TokenConflictMap tokenConflictMap, CoincidentTokenIndex coincidentTokenIndex)
    {
        // An external word token has no lexical rule to compare with the keywords
        if (wordToken is not { IsTerminal: true } word)
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

                // The word token was valid in all the states of the keyword candidate and the other token
                if (coincidentTokenIndex.WordTokenIsValidInAllStatesWith(token, Symbol.Terminal(other)))
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

    /// <summary>
    /// tree-sitter's <c>split_state_id_groups</c>: splits the groups of states until <paramref name="shouldSplit"/>
    /// holds for no pair of states of a group, comparing each state with the states after it which weren't split.
    /// </summary>
    /// <remarks>
    /// The optional classes of the states are such that, for two states of a class, <c>shouldSplit</c> is the inequality
    /// of a signature of the states (the targets of their actions), and the comparison of a state of a class with another
    /// state depends only on the signature of the first state. The first state of a class compared with the states after
    /// it in a group splits the states of its class with another signature, so the states of its class after it have its
    /// signature and would split the same states, none: they aren't compared, the comparisons of the large groups
    /// (thousands of states of a core) taking quadratic time. The groups are split exactly like tree-sitter.
    /// </remarks>
    public static bool SplitStateIdGroups<TState>(IReadOnlyList<TState> states, List<List<int>> stateIdsByGroupId, int[] groupIdsByStateId, int startGroupId, Func<TState, TState, int[], bool> shouldSplit, Func<TState, int>? leftClass = null)
    {
        var result = false;
        var groupId = startGroupId;
        var splitStateIds = new List<int>();
        var split = new HashSet<int>();
        var leftClasses = new HashSet<int>();
        while (groupId < stateIdsByGroupId.Count)
        {
            var stateIds = stateIdsByGroupId[groupId];
            splitStateIds.Clear();
            split.Clear();
            leftClasses.Clear();
            for (var i = 0; i < stateIds.Count; i++)
            {
                var leftStateId = stateIds[i];
                if (split.Contains(leftStateId))
                {
                    continue;
                }

                var leftState = states[leftStateId];
                if (leftClass is not null && !leftClasses.Add(leftClass(leftState)))
                {
                    continue;
                }

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

                stateIdsByGroupId.Add([.. splitStateIds]);
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

        if (unitReductionSymbolsByState.Count == 0)
        {
            return;
        }

        // Like tree-sitter 0.27: the gotos first, then the shifts onto unit reduction states are redirected to the
        // goto of the reduced symbol, when it changes the target
        foreach (var state in parseTable.States)
        {
            var done = false;
            while (!done)
            {
                done = true;
                foreach (var (symbol, action) in state.NonTerminalEntries.Entries.ToList())
                {
                    if (!action.IsShiftExtra && unitReductionSymbolsByState.TryGetValue(action.State, out var unitSymbol))
                    {
                        done = false;
                        if (state.NonTerminalEntries.TryGetValue(unitSymbol, out var target) && !target.IsShiftExtra)
                        {
                            state.NonTerminalEntries[symbol] = target;
                        }
                    }
                }

                foreach (var (symbol, entry) in state.TerminalEntries.Entries.ToList())
                {
                    ParseTableEntry? changed = null;
                    for (var i = 0; i < entry.Actions.Count; i++)
                    {
                        var action = (changed ?? entry).Actions[i];
                        if (action.Kind == ParseActionKind.Shift &&
                            unitReductionSymbolsByState.TryGetValue(action.State, out var unitSymbol) &&
                            state.NonTerminalEntries.TryGetValue(unitSymbol, out var target) &&
                            !target.IsShiftExtra &&
                            target.State != action.State)
                        {
                            changed ??= entry.Clone();
                            changed.Actions[i] = ParseAction.Shift(target.State, action.IsRepetition);
                            done = false;
                        }
                    }

                    if (changed is not null)
                    {
                        state.TerminalEntries[symbol] = changed;
                    }
                }
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

        var conflictInfos = new StateConflictInfos(parseTable, syntaxGrammar, lexicalGrammar, tokenConflictMap, keywords);
        TableBuilder.SplitStateIdGroups(conflictInfos.States, stateIdsByGroupId, groupIdsByStateId, 0, conflictInfos.StatesConflict, s => s.Class);

        // The successors of the states, sorted by symbol: the groups are split until no state of a group has a successor
        // in another group than the one of another state of the group, which compares every pair of states many times
        var successors = Successors.Of(parseTable.States);
        while (TableBuilder.SplitStateIdGroups(successors, stateIdsByGroupId, groupIdsByStateId, 0, Successors.Differ, s => s.Class))
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
            if (stateIds.Count > 1)
            {
                // The states share the reserved word sets of the grammar
                parseState.ReservedWords = parseState.ReservedWords.Clone();
            }

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

    /// <summary>
    /// What tree-sitter's <c>states_conflict</c> reads of the states, prepared once: the minimization compares the pairs
    /// of states of a core, millions of pairs for the large grammars.
    /// </summary>
    /// <remarks>
    /// <c>states_conflict</c> is the disjunction of <c>entries_conflict</c> for the tokens of both states and of
    /// <c>token_conflicts</c> for the tokens of only one state, so the order of these checks doesn't change the result:
    /// the terminal entries of each state are sorted by token, the tokens of two states are merged, and the loop of
    /// <c>token_conflicts</c> on the tokens of the other state is an intersection of bit sets.
    /// </remarks>
    private sealed class StateConflictInfos
    {
        private readonly int _terminalCount;
        private readonly int _words;
        private readonly int _endKey;
        private readonly Symbol[] _symbolsByKey;

        // By key: the tokens which always conflict (the end of a non-terminal extra, the external tokens, and the
        // terminals that correspond to external tokens, after the reserved words), and the terminals they conflict with
        private readonly bool[] _isExternal;
        private readonly bool[] _correspondsToExternalToken;
        private readonly ulong[] _conflictingTerminals;

        public StateConflictInfos(ParseTable parseTable, SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, TokenConflictMap tokenConflictMap, TokenSet keywords)
        {
            _terminalCount = lexicalGrammar.Variables.Count;
            _words = (_terminalCount + 63) >> 6;
            var externalCount = syntaxGrammar.ExternalTokens.Count;
            _endKey = _terminalCount + externalCount;
            var keyCount = _endKey + 2;
            _symbolsByKey = new Symbol[keyCount];
            for (var i = 0; i < _terminalCount; i++)
            {
                _symbolsByKey[i] = Symbol.Terminal(i);
            }

            for (var i = 0; i < externalCount; i++)
            {
                _symbolsByKey[_terminalCount + i] = Symbol.External(i);
            }

            _symbolsByKey[_endKey] = Symbol.End;
            _symbolsByKey[_endKey + 1] = Symbol.EndOfNonTerminalExtra;

            _isExternal = new bool[keyCount];
            _correspondsToExternalToken = new bool[keyCount];
            _conflictingTerminals = new ulong[(_endKey + 1) * _words];
            var wordToken = syntaxGrammar.WordToken;
            for (var key = 0; key < keyCount; key++)
            {
                var newToken = _symbolsByKey[key];
                _isExternal[key] = newToken == Symbol.EndOfNonTerminalExtra || newToken.IsExternal;
                _correspondsToExternalToken[key] = syntaxGrammar.ExternalTokens.Any(e => e.CorrespondingInternalToken == newToken);
                if (key > _endKey || newToken.IsExternal)
                {
                    continue;
                }

                for (var j = 0; j < _terminalCount; j++)
                {
                    var token = Symbol.Terminal(j);
                    if (wordToken == token && keywords.Contains(newToken))
                    {
                        continue;
                    }

                    if (wordToken == newToken && keywords.Contains(token))
                    {
                        continue;
                    }

                    // Like tree-sitter, the index of the new token is used even for the end of the input, whose index is 0
                    if (tokenConflictMap.DoesConflict(newToken.Index, token.Index) || tokenConflictMap.DoesMatchSameString(newToken.Index, token.Index))
                    {
                        _conflictingTerminals[(key * _words) + (j >> 6)] |= 1UL << (j & 63);
                    }
                }
            }

            States = new StateConflictInfo[parseTable.States.Count];
            var keyClasses = new Dictionary<int[], int>(SequenceComparer<int>.Instance);
            var classes = new Dictionary<(int Keys, TokenSet ReservedWords), int>();
            var singleActionShapes = new Dictionary<ParseAction, int>();
            var shapes = new Dictionary<ParseAction[], int>(SequenceComparer<ParseAction>.Instance);
            for (var i = 0; i < States.Length; i++)
            {
                var state = parseTable.States[i];
                var terminals = new ulong[_words];
                var keys = new int[state.TerminalEntries.Count];
                var entries = new ParseTableEntry[keys.Length];
                var k = 0;
                foreach (var (token, entry) in state.TerminalEntries.Entries)
                {
                    keys[k] = Key(token);
                    entries[k++] = entry;
                    if (token.IsTerminal)
                    {
                        terminals[token.Index >> 6] |= 1UL << (token.Index & 63);
                    }
                }

                // The tokens are unique
                Array.Sort(keys, entries);
                var shapeIds = new int[keys.Length];
                var shiftStates = new int[keys.Length];
                for (k = 0; k < keys.Length; k++)
                {
                    (shapeIds[k], shiftStates[k]) = Shape(entries[k].Actions, singleActionShapes, shapes);
                }

                var keyClass = Intern(keyClasses, keys);
                if (!classes.TryGetValue((keyClass, state.ReservedWords), out var stateClass))
                {
                    stateClass = classes.Count;
                    classes[(keyClass, state.ReservedWords)] = stateClass;
                }

                States[i] = new StateConflictInfo(state, keys, entries, shapeIds, shiftStates, terminals, stateClass);
            }
        }

        /// <summary>
        /// The shape of the actions of an entry: an id of the actions with the states of the shifts left out, so that
        /// <c>entries_conflict</c> compares the shapes and the groups of the shifted states; and the state of the shift,
        /// -1 if there is none and -2 if there are several.
        /// </summary>
        private static (int ShapeId, int ShiftState) Shape(ParseTableEntry.ParseActionList actions, Dictionary<ParseAction, int> singleActionShapes, Dictionary<ParseAction[], int> shapes)
        {
            static ParseAction WithoutState(ParseAction action) => action.Kind == ParseActionKind.Shift ? ParseAction.Shift(0, action.IsRepetition) : action;

            var shiftState = -1;
            foreach (var action in actions)
            {
                if (action.Kind == ParseActionKind.Shift)
                {
                    shiftState = shiftState == -1 ? action.State : -2;
                }
            }

            int id;
            if (actions.Count == 1)
            {
                var shape = WithoutState(actions[0]);
                if (!singleActionShapes.TryGetValue(shape, out id))
                {
                    id = singleActionShapes.Count + shapes.Count;
                    singleActionShapes[shape] = id;
                }
            }
            else
            {
                var shape = actions.Select(WithoutState).ToArray();
                if (!shapes.TryGetValue(shape, out id))
                {
                    id = singleActionShapes.Count + shapes.Count;
                    shapes[shape] = id;
                }
            }

            return (id, shiftState);
        }

        public StateConflictInfo[] States { get; }

        /// <summary>tree-sitter's <c>states_conflict</c>.</summary>
        public bool StatesConflict(StateConflictInfo left, StateConflictInfo right, int[] groupIdsByStateId)
        {
            var leftKeys = left.Keys;
            var rightKeys = right.Keys;
            int i = 0, j = 0;
            while (i < leftKeys.Length && j < rightKeys.Length)
            {
                var leftKey = leftKeys[i];
                var rightKey = rightKeys[j];
                if (leftKey == rightKey)
                {
                    if (EntriesConflict(left, i, right, j, groupIdsByStateId))
                    {
                        return true;
                    }

                    i++;
                    j++;
                }
                else if (leftKey < rightKey)
                {
                    if (TokenConflicts(right, leftKey))
                    {
                        return true;
                    }

                    i++;
                }
                else
                {
                    if (TokenConflicts(left, rightKey))
                    {
                        return true;
                    }

                    j++;
                }
            }

            for (; i < leftKeys.Length; i++)
            {
                if (TokenConflicts(right, leftKeys[i]))
                {
                    return true;
                }
            }

            for (; j < rightKeys.Length; j++)
            {
                if (TokenConflicts(left, rightKeys[j]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>tree-sitter's <c>entries_conflict</c> for the entries of a token in two states: the shapes of their actions or the groups of their shifts differ.</summary>
        private static bool EntriesConflict(StateConflictInfo left, int i, StateConflictInfo right, int j, int[] groupIdsByStateId)
        {
            if (left.ShapeIds[i] != right.ShapeIds[j])
            {
                return true;
            }

            var shiftState = left.ShiftStates[i];
            return shiftState switch
            {
                -1 => false,
                -2 => Minimizer.EntriesConflict(left.Entries[i], right.Entries[j], groupIdsByStateId),
                _ => groupIdsByStateId[shiftState] != groupIdsByStateId[right.ShiftStates[j]],
            };
        }

        /// <summary>tree-sitter's <c>token_conflicts</c>: whether adding the token of a key to a state can change its lexing.</summary>
        private bool TokenConflicts(StateConflictInfo rightState, int newTokenKey)
        {
            if (_isExternal[newTokenKey])
            {
                return true;
            }

            if (rightState.State.ReservedWords.Contains(_symbolsByKey[newTokenKey]))
            {
                return false;
            }

            if (_correspondsToExternalToken[newTokenKey])
            {
                return true;
            }

            var terminals = rightState.Terminals;
            var offset = newTokenKey * _words;
            for (var w = 0; w < terminals.Length; w++)
            {
                if ((terminals[w] & _conflictingTerminals[offset + w]) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private int Key(Symbol token) => token.Kind switch
        {
            SymbolType.Terminal => token.Index,
            SymbolType.External => _terminalCount + token.Index,
            SymbolType.End => _endKey,
            SymbolType.EndOfNonTerminalExtra => _endKey + 1,
            _ => throw new InvalidOperationException("A non-terminal has no terminal entry"),
        };
    }

    /// <summary>
    /// The terminal entries of a state sorted by key with the shapes of their actions, the bits of its terminals, and its
    /// class for <see cref="TableBuilder.SplitStateIdGroups"/>: the states with the same tokens and reserved words.
    /// </summary>
    private sealed class StateConflictInfo(ParseState state, int[] keys, ParseTableEntry[] entries, int[] shapeIds, int[] shiftStates, ulong[] terminals, int stateClass)
    {
        public ParseState State { get; } = state;

        public int[] Keys { get; } = keys;

        public ParseTableEntry[] Entries { get; } = entries;

        public int[] ShapeIds { get; } = shapeIds;

        public int[] ShiftStates { get; } = shiftStates;

        public ulong[] Terminals { get; } = terminals;

        public int Class { get; } = stateClass;
    }

    /// <summary>The id of an array in a dictionary of arrays compared by their elements.</summary>
    private static int Intern<T>(Dictionary<T[], int> ids, T[] array)
        where T : IEquatable<T>
    {
        if (!ids.TryGetValue(array, out var id))
        {
            id = ids.Count;
            ids[array] = id;
        }

        return id;
    }

    /// <summary>Compares arrays by their elements.</summary>
    private sealed class SequenceComparer<T> : IEqualityComparer<T[]>
        where T : IEquatable<T>
    {
        public static readonly SequenceComparer<T> Instance = new();

        public bool Equals(T[]? x, T[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(T[] obj)
        {
            unchecked
            {
                var hash = obj.Length;
                foreach (var item in obj)
                {
                    hash = hash * 31 + item.GetHashCode();
                }

                return hash;
            }
        }
    }

    /// <summary>
    /// The successors of a state for tree-sitter's <c>state_successors_differ</c>: the targets of the terminals whose last action is
    /// a shift, and the gotos of the non-terminals (-1 for a non-terminal extra), sorted by symbol to be compared by
    /// merging instead of looking the symbols up.
    /// </summary>
    private sealed class Successors
    {
        private long[] _shiftKeys = [];
        private int[] _shiftTargets = [];
        private long[] _gotoKeys = [];
        private int[] _gotoTargets = [];

        /// <summary>The class of the state for <see cref="TableBuilder.SplitStateIdGroups"/>: the states with the same keys.</summary>
        public int Class { get; private set; }

        public static Successors[] Of(List<ParseState> states)
        {
            var result = new Successors[states.Count];
            var shiftKeyClasses = new Dictionary<long[], int>(SequenceComparer<long>.Instance);
            var gotoKeyClasses = new Dictionary<long[], int>(SequenceComparer<long>.Instance);
            var classes = new Dictionary<(int ShiftKeys, int GotoKeys), int>();
            for (var i = 0; i < result.Length; i++)
            {
                var successors = Of(states[i]);
                var key = (Intern(shiftKeyClasses, successors._shiftKeys), Intern(gotoKeyClasses, successors._gotoKeys));
                if (!classes.TryGetValue(key, out var keyClass))
                {
                    keyClass = classes.Count;
                    classes[key] = keyClass;
                }

                successors.Class = keyClass;
                result[i] = successors;
            }

            return result;
        }

        private static Successors Of(ParseState state)
        {
            var shiftCount = 0;
            foreach (var (_, entry) in state.TerminalEntries.Entries)
            {
                if (entry.Actions[entry.Actions.Count - 1].Kind == ParseActionKind.Shift)
                {
                    shiftCount++;
                }
            }

            var shiftKeys = new long[shiftCount];
            var shiftTargets = new int[shiftCount];
            var k = 0;
            foreach (var (token, entry) in state.TerminalEntries.Entries)
            {
                var last = entry.Actions[entry.Actions.Count - 1];
                if (last.Kind == ParseActionKind.Shift)
                {
                    shiftKeys[k] = Key(token);
                    shiftTargets[k++] = last.State;
                }
            }

            var gotoKeys = new long[state.NonTerminalEntries.Count];
            var gotoTargets = new int[gotoKeys.Length];
            k = 0;
            foreach (var (symbol, action) in state.NonTerminalEntries.Entries)
            {
                gotoKeys[k] = Key(symbol);
                gotoTargets[k++] = action.IsShiftExtra ? -1 : action.State;
            }

            // The symbols are unique
            Array.Sort(shiftKeys, shiftTargets);
            Array.Sort(gotoKeys, gotoTargets);
            return new Successors
            {
                _shiftKeys = shiftKeys,
                _shiftTargets = shiftTargets,
                _gotoKeys = gotoKeys,
                _gotoTargets = gotoTargets,
            };
        }

        /// <summary>
        /// tree-sitter's <c>state_successors_differ</c>: whether a terminal shifts to, or a non-terminal goes to, states of
        /// different groups in both states.
        /// </summary>
        public static bool Differ(Successors state1, Successors state2, int[] groupIdsByStateId)
        {
            int i = 0, j = 0;
            while (i < state1._shiftKeys.Length && j < state2._shiftKeys.Length)
            {
                var key1 = state1._shiftKeys[i];
                var key2 = state2._shiftKeys[j];
                if (key1 < key2)
                {
                    i++;
                }
                else if (key2 < key1)
                {
                    j++;
                }
                else
                {
                    if (groupIdsByStateId[state1._shiftTargets[i]] != groupIdsByStateId[state2._shiftTargets[j]])
                    {
                        return true;
                    }

                    i++;
                    j++;
                }
            }

            i = 0;
            j = 0;
            while (i < state1._gotoKeys.Length && j < state2._gotoKeys.Length)
            {
                var key1 = state1._gotoKeys[i];
                var key2 = state2._gotoKeys[j];
                if (key1 < key2)
                {
                    i++;
                }
                else if (key2 < key1)
                {
                    j++;
                }
                else
                {
                    var target1 = state1._gotoTargets[i];
                    var target2 = state2._gotoTargets[j];
                    if (target1 >= 0 && target2 >= 0
                        ? groupIdsByStateId[target1] != groupIdsByStateId[target2]
                        : target1 >= 0 || target2 >= 0)
                    {
                        return true;
                    }

                    i++;
                    j++;
                }
            }

            return false;
        }

        private static long Key(Symbol symbol) => ((long)symbol.Kind << 32) | (uint)symbol.Index;
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
    private readonly Dictionary<(List<int> NfaStates, bool EofValid), int> _stateIdsByNfaStateSet = new(NfaStateSetComparer.Instance);

    private LexTableBuilder(LexicalGrammar lexicalGrammar)
    {
        _lexicalGrammar = lexicalGrammar;
        _cursor = new NfaCursor(lexicalGrammar.Nfa);
    }

    public LexTable Table { get; private set; } = new();

    /// <summary>The number of ranges from which a character set is large, tree-sitter's <c>LARGE_CHARACTER_RANGE_COUNT</c>.</summary>
    public const int LargeCharacterRangeCount = 8;

    public static (LexTable Main, LexTable Keyword, List<CharacterSet> LargeCharacterSets) BuildLexTables(ParseTable parseTable, SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, TokenSet keywords, CoincidentTokenIndex coincidentTokenIndex, TokenConflictMap tokenConflictMap)
    {
        var keywordLexTable = new LexTable();
        if (syntaxGrammar.WordToken is not null)
        {
            var keywordBuilder = new LexTableBuilder(lexicalGrammar);
            keywordBuilder.AddStateForTokens(keywords);
            keywordLexTable = keywordBuilder.Table;
        }

        var words = (lexicalGrammar.Variables.Count + 63) >> 6;
        var incompatibleTerminals = IncompatibleTerminals(lexicalGrammar, tokenConflictMap, coincidentTokenIndex, words);
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
                if (MergeTokenSet(entry.Tokens, tokens, lexicalGrammar.Variables.Count, incompatibleTerminals, words))
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

        var largeCharacterSets = new List<CharacterSet>();
        for (var variableIndex = 0; variableIndex < lexicalGrammar.Variables.Count; variableIndex++)
        {
            var symbol = Symbol.Terminal(variableIndex);
            builder.Reset();
            builder.AddStateForTokens(TokenSet.Of(symbol));
            foreach (var state in builder.Table.States)
            {
                var characters = new CharacterSet();
                foreach (var (chars, action) in state.AdvanceActions)
                {
                    if (action.InMainToken)
                    {
                        characters.Add(chars);
                        continue;
                    }

                    if (chars.RangeCount > LargeCharacterRangeCount && !largeCharacterSets.Any(set => set.Equals(chars)))
                    {
                        largeCharacterSets.Add(chars.Clone());
                    }
                }

                if (characters.RangeCount > LargeCharacterRangeCount && !largeCharacterSets.Any(set => set.Equals(characters)))
                {
                    largeCharacterSets.Add(characters);
                }
            }
        }

        return (mainLexTable, keywordLexTable, largeCharacterSets);
    }

    private void Reset()
    {
        Table = new LexTable();
        _stateQueue.Clear();
        _stateIdsByNfaStateSet.Clear();
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
        var key = (_cursor.StateIds, eofValid);
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

    /// <summary>
    /// The terminals which <see cref="MergeTokenSet"/> doesn't lex in the same state as each terminal: the conflicting
    /// tokens, the tokens it matches a prefix of, and the overlapping tokens that aren't valid in the same parse state.
    /// </summary>
    private static ulong[] IncompatibleTerminals(LexicalGrammar lexicalGrammar, TokenConflictMap tokenConflictMap, CoincidentTokenIndex coincidentTokenIndex, int words)
    {
        var n = lexicalGrammar.Variables.Count;
        var result = new ulong[n * words];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                if (tokenConflictMap.DoesConflict(i, j) || tokenConflictMap.DoesMatchPrefix(i, j) ||
                    (!coincidentTokenIndex.Contains(Symbol.Terminal(i), Symbol.Terminal(j)) && (tokenConflictMap.DoesOverlap(j, i) || tokenConflictMap.DoesOverlap(i, j))))
                {
                    result[(i * words) + (j >> 6)] |= 1UL << (j & 63);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// tree-sitter's <c>merge_token_set</c>: adds the tokens of a set to another, unless a token of one set conflicts
    /// with a token of the other set without it (a pure condition: the rows of the incompatible terminals are
    /// intersected with the sets).
    /// </summary>
    private static bool MergeTokenSet(TokenSet tokens, TokenSet other, int terminalCount, ulong[] incompatibleTerminals, int words)
    {
        for (var i = 0; i < terminalCount; i++)
        {
            var inTokens = tokens.ContainsTerminal(i);
            var inOther = other.ContainsTerminal(i);
            if (inTokens != inOther && (inTokens ? other : tokens).IntersectsTerminals(incompatibleTerminals, i * words, words))
            {
                return false;
            }
        }

        tokens.InsertAll(other);
        return true;
    }

    /// <summary>Compares the keys of the lex states: their sets of NFA states, by their elements, and the validity of the end.</summary>
    private sealed class NfaStateSetComparer : IEqualityComparer<(List<int> NfaStates, bool EofValid)>
    {
        public static readonly NfaStateSetComparer Instance = new();

        public bool Equals((List<int> NfaStates, bool EofValid) x, (List<int> NfaStates, bool EofValid) y)
        {
            if (x.EofValid != y.EofValid || x.NfaStates.Count != y.NfaStates.Count)
            {
                return false;
            }

            for (var i = 0; i < x.NfaStates.Count; i++)
            {
                if (x.NfaStates[i] != y.NfaStates[i])
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode((List<int> NfaStates, bool EofValid) obj)
        {
            unchecked
            {
                var hash = obj.EofValid ? 1 : 2;
                foreach (var id in obj.NfaStates)
                {
                    hash = (hash * 31) + id;
                }

                return hash;
            }
        }
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
