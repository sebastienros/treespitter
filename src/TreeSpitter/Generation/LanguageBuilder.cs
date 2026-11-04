using TreeSpitter.Grammars;

namespace TreeSpitter.Generation;

/// <summary>
/// Compiles a grammar to a <see cref="Language"/>: the work of <c>tree-sitter generate</c>, with the tables in memory
/// instead of a <c>parser.c</c> file.
/// </summary>
public static class LanguageCompiler
{
    /// <summary>Compiles a grammar. Throws a <see cref="GrammarException"/> for an invalid grammar or an unresolved conflict.</summary>
    public static Language Compile(Grammar grammar) => Compile(grammar, null);

    /// <summary>Compiles a grammar, and collects the warnings of the generation.</summary>
    public static Language Compile(Grammar grammar, List<string>? warnings)
    {
        if (grammar is null)
        {
            throw new ArgumentNullException(nameof(grammar));
        }

        var prepared = GrammarPreparer.Prepare(grammar, warnings);
        var variableInfo = VariableInfo.Compute(prepared.SyntaxGrammar, prepared.LexicalGrammar, prepared.DefaultAliases);
        var tables = TableBuilder.Build(prepared.SyntaxGrammar, prepared.LexicalGrammar, prepared.DefaultAliases, variableInfo, prepared.Inlines, warnings);
        return new LanguageBuilder(prepared, tables).Build();
    }
}

/// <summary>Converts the generated tables to a <see cref="Language"/>, like tree-sitter's <c>render.rs</c>.</summary>
internal sealed class LanguageBuilder(PreparedGrammar grammar, Tables tables)
{
    private readonly SyntaxGrammar _syntaxGrammar = grammar.SyntaxGrammar;
    private readonly LexicalGrammar _lexicalGrammar = grammar.LexicalGrammar;
    private readonly AliasMap _defaultAliases = grammar.DefaultAliases;
    private readonly ParseTable _parseTable = tables.ParseTable;
    private readonly Dictionary<Symbol, ushort> _symbolIds = [];
    private readonly Dictionary<Symbol, Symbol> _symbolMap = [];
    private readonly List<Alias> _uniqueAliases = [];
    private readonly Dictionary<Alias, ushort> _aliasIds = [];
    private readonly List<string> _fieldNames = [];

    public Language Build()
    {
        var symbols = _parseTable.Symbols;
        for (var i = 0; i < symbols.Count; i++)
        {
            _symbolIds[symbols[i]] = (ushort)i;
        }

        _symbolIds[Symbol.EndOfNonTerminalExtra] = _symbolIds[Symbol.End];

        BuildPublicSymbolMap();

        foreach (var info in _parseTable.ProductionInfos)
        {
            foreach (var name in info.FieldMap.Keys)
            {
                var index = _fieldNames.BinarySearch(name, StringComparer.Ordinal);
                if (index < 0)
                {
                    _fieldNames.Insert(~index, name);
                }
            }
        }

        var aliasesWithSymbols = new Dictionary<Alias, Symbol>();
        foreach (var info in _parseTable.ProductionInfos)
        {
            foreach (var alias in info.AliasSequence)
            {
                if (alias is null || aliasesWithSymbols.ContainsKey(alias) || _uniqueAliases.Contains(alias))
                {
                    continue;
                }

                var existing = SymbolsForAlias(alias);
                if (existing.Count > 0)
                {
                    aliasesWithSymbols[alias] = existing[0];
                }
                else
                {
                    var index = _uniqueAliases.BinarySearch(alias);
                    if (index < 0)
                    {
                        _uniqueAliases.Insert(~index, alias);
                    }
                }
            }
        }

        foreach (var (alias, symbol) in aliasesWithSymbols)
        {
            _aliasIds[alias] = _symbolIds[_symbolMap[symbol]];
        }

        for (var i = 0; i < _uniqueAliases.Count; i++)
        {
            _aliasIds[_uniqueAliases[i]] = (ushort)(symbols.Count + i);
        }

        var language = new Language
        {
            Name = grammar.Name,
            SymbolCount = symbols.Count,
            AliasCount = _uniqueAliases.Count,
            TokenCount = symbols.Count(s => s.IsTerminal || s.IsEof || (s.IsExternal && _syntaxGrammar.ExternalTokens[s.Index].CorrespondingInternalToken is null)),
            ExternalTokenCount = _syntaxGrammar.ExternalTokens.Count,
            StateCount = _parseTable.States.Count,
            FieldCount = _fieldNames.Count,
            ProductionIdCount = _parseTable.ProductionInfos.Count,
            MaxAliasSequenceLength = _parseTable.MaxAliasedProductionLength,
        };

        BuildSymbolMetadata(language);
        BuildParseTable(language);
        BuildLexModes(language);
        BuildAliasSequences(language);
        BuildFieldMaps(language);
        BuildExternalScannerTables(language);
        language.KeywordCaptureToken = _syntaxGrammar.WordToken is { } word && _symbolIds.TryGetValue(word, out var wordId) ? wordId : (ushort)0;
        language.SupertypeSymbols = _syntaxGrammar.SupertypeSymbols.Where(_symbolIds.ContainsKey).Select(s => _symbolIds[s]).ToArray();
        language.MainLexTable = ConvertLexTable(tables.MainLexTable);
        language.KeywordLexTable = ConvertLexTable(tables.KeywordLexTable);
        return language;
    }

    private void BuildPublicSymbolMap()
    {
        foreach (var symbol in _parseTable.Symbols)
        {
            var mapping = symbol;
            if (_defaultAliases.TryGetValue(symbol, out var alias))
            {
                var kind = alias.Kind;
                foreach (var other in _parseTable.Symbols)
                {
                    if (_defaultAliases.TryGetValue(other, out var otherAlias))
                    {
                        if (other.CompareTo(mapping) < 0 && otherAlias.Equals(alias))
                        {
                            mapping = other;
                        }
                    }
                    else if (MetadataForSymbol(other) == (alias.Value, kind))
                    {
                        mapping = other;
                        break;
                    }
                }
            }
            else if (symbol.IsTerminal)
            {
                var metadata = MetadataForSymbol(symbol);
                foreach (var other in _parseTable.Symbols)
                {
                    if (MetadataForSymbol(other) == metadata)
                    {
                        if (_symbolMap.TryGetValue(other, out var mapped) && mapped == symbol)
                        {
                            break;
                        }

                        mapping = other;
                        break;
                    }
                }
            }

            _symbolMap[symbol] = mapping;
        }
    }

    private (string Name, VariableType Kind) MetadataForSymbol(Symbol symbol) => symbol.Kind switch
    {
        SymbolType.End or SymbolType.EndOfNonTerminalExtra => ("end", VariableType.Hidden),
        SymbolType.NonTerminal => (_syntaxGrammar.Variables[symbol.Index].Name, _syntaxGrammar.Variables[symbol.Index].Kind),
        SymbolType.Terminal => (_lexicalGrammar.Variables[symbol.Index].Name, _lexicalGrammar.Variables[symbol.Index].Kind),
        _ => (_syntaxGrammar.ExternalTokens[symbol.Index].Name, _syntaxGrammar.ExternalTokens[symbol.Index].Kind),
    };

    private List<Symbol> SymbolsForAlias(Alias alias)
    {
        var result = new List<Symbol>();
        foreach (var symbol in _parseTable.Symbols)
        {
            if (_defaultAliases.TryGetValue(symbol, out var defaultAlias))
            {
                if (defaultAlias.Equals(alias))
                {
                    result.Add(symbol);
                }
            }
            else
            {
                var (name, kind) = MetadataForSymbol(symbol);
                if (name == alias.Value && kind == alias.Kind)
                {
                    result.Add(symbol);
                }
            }
        }

        return result;
    }

    private void BuildSymbolMetadata(Language language)
    {
        var symbols = _parseTable.Symbols;
        var count = symbols.Count + _uniqueAliases.Count;
        var names = new string[count];
        var flags = new byte[count];
        var publicMap = new ushort[count];
        for (var i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            names[i] = _defaultAliases.TryGetValue(symbol, out var alias) ? alias.Value : MetadataForSymbol(symbol).Name;
            publicMap[i] = _symbolIds[_symbolMap[symbol]];
            if (alias is not null)
            {
                flags[i] = (byte)(Language.VisibleFlag | (alias.IsNamed ? Language.NamedFlag : 0));
            }
            else
            {
                flags[i] = MetadataForSymbol(symbol).Kind switch
                {
                    VariableType.Named => Language.VisibleFlag | Language.NamedFlag,
                    VariableType.Anonymous => Language.VisibleFlag,
                    VariableType.Hidden => (byte)(Language.NamedFlag | (_syntaxGrammar.SupertypeSymbols.Contains(symbol) ? Language.SupertypeFlag : 0)),
                    _ => 0,
                };
            }
        }

        for (var i = 0; i < _uniqueAliases.Count; i++)
        {
            var id = symbols.Count + i;
            names[id] = _uniqueAliases[i].Value;
            flags[id] = (byte)(Language.VisibleFlag | (_uniqueAliases[i].IsNamed ? Language.NamedFlag : 0));
            publicMap[id] = (ushort)id;
        }

        language.SymbolNames = names;
        language.SymbolFlags = flags;
        language.PublicSymbolMap = publicMap;
        language.FieldNames = [null, .. _fieldNames];
    }

    private void BuildParseTable(Language language)
    {
        var symbolCount = _parseTable.Symbols.Count;
        var states = _parseTable.States;
        var table = new ushort[states.Count * symbolCount];
        var actions = new List<ulong>();
        var listIds = new Dictionary<ParseTableEntry, int>();

        int ListId(ParseTableEntry entry)
        {
            if (listIds.TryGetValue(entry, out var id))
            {
                return id;
            }

            id = actions.Count;
            listIds[entry] = id;
            actions.Add(ParseActionEntry.Header(entry.Actions.Count, entry.Reusable));
            foreach (var action in entry.Actions)
            {
                actions.Add(action.Kind switch
                {
                    ParseActionKind.Accept => ParseActionEntry.Accept(),
                    ParseActionKind.Recover => ParseActionEntry.Recover(),
                    ParseActionKind.ShiftExtra => ParseActionEntry.Shift(0, true, false),
                    ParseActionKind.Shift => ParseActionEntry.Shift(action.State, false, action.IsRepetition),
                    _ => ParseActionEntry.Reduce(_symbolIds[action.Symbol], action.ChildCount, action.DynamicPrecedence, action.ProductionId),
                });
            }

            return id;
        }

        // Action list 0 is the empty list
        ListId(new ParseTableEntry { Actions = [], Reusable = false });

        // Like render.rs: the first states and the states with many entries are "large" (a row of the parse table),
        // the others are "small" (lists of the lookaheads grouped by action list)
        var threshold = Math.Min(64, symbolCount / 2);
        var largeStateCount = 0;
        while (largeStateCount < states.Count &&
            (largeStateCount <= 1 || states[largeStateCount].TerminalEntries.Count + states[largeStateCount].NonTerminalEntries.Count > threshold))
        {
            largeStateCount++;
        }

        // The order in which tree-sitter's lookahead iterator visits the lookaheads of the small states, which the
        // error recovery depends on: the groups of lookaheads sharing an action list, by size then offset of the list,
        // and the symbols of a group in the order of the generator. The action lists are numbered in the order of
        // render.rs (the entries of each state by symbol id) so that their offsets are the ones of a parser.c.
        var lookaheadOrderStarts = new int[states.Count + 1];
        var lookaheadOrder = new List<ushort>();
        var groups = new Dictionary<int, List<Symbol>>();
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            foreach (var (symbol, action) in state.NonTerminalEntries.Entries)
            {
                table[i * symbolCount + _symbolIds[symbol]] = (ushort)(action.IsShiftExtra ? i : action.State);
            }

            groups.Clear();
            // The end of a non-terminal extra has no symbol order in render.rs: it comes first
            foreach (var (symbol, entry) in state.TerminalEntries.Entries.OrderBy(e => e.Key == Symbol.EndOfNonTerminalExtra ? -1 : _symbolIds[e.Key]))
            {
                var id = ListId(entry);
                table[i * symbolCount + _symbolIds[symbol]] = (ushort)id;
                if (!groups.TryGetValue(id, out var group))
                {
                    groups[id] = group = [];
                }

                group.Add(symbol);
            }

            lookaheadOrderStarts[i] = lookaheadOrder.Count;
            if (i >= largeStateCount)
            {
                foreach (var (_, group) in groups.OrderBy(g => g.Value.Count).ThenBy(g => g.Key))
                {
                    group.Sort();
                    foreach (var symbol in group)
                    {
                        lookaheadOrder.Add(_symbolIds[symbol]);
                    }
                }
            }
        }

        lookaheadOrderStarts[states.Count] = lookaheadOrder.Count;
        language.LargeStateCount = largeStateCount;
        language.LookaheadOrderStarts = lookaheadOrderStarts;
        language.LookaheadOrder = [.. lookaheadOrder];

        // Like render.rs: the action lists are indexed with 16 bits
        if (actions.Count >= ushort.MaxValue)
        {
            throw new GrammarException($"Parse table action count {actions.Count} exceeds maximum value of {ushort.MaxValue}");
        }

        language.ParseTable = table;
        language.ParseActions = actions.Select(a => new ParseActionEntry(a)).ToArray();
    }

    private void BuildLexModes(Language language)
    {
        var states = _parseTable.States;
        var lexStates = new ushort[states.Count];
        var externalLexStates = new ushort[states.Count];
        var reservedIds = new ushort[states.Count];
        var reservedSets = new List<TokenSet> { new() };
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var id = reservedSets.FindIndex(s => s.Equals(state.ReservedWords));
            if (id < 0)
            {
                reservedSets.Add(state.ReservedWords);
                id = reservedSets.Count - 1;
            }

            if (state.IsEndOfNonTerminalExtra)
            {
                lexStates[i] = Language.NoLookaheadLexState;
            }
            else
            {
                lexStates[i] = (ushort)state.LexStateId;
                externalLexStates[i] = (ushort)state.ExternalLexStateId;
                reservedIds[i] = (ushort)id;
            }
        }

        var maxSize = reservedSets.Max(s => s.Count);
        var words = new ushort[reservedSets.Count * Math.Max(1, maxSize)];
        for (var setId = 1; setId < reservedSets.Count; setId++)
        {
            var j = 0;
            foreach (var token in reservedSets[setId].Symbols)
            {
                words[setId * maxSize + j++] = _symbolIds[token];
            }
        }

        language.LexStates = lexStates;
        language.ExternalLexStates = externalLexStates;
        language.ReservedWordSetIds = reservedIds;
        language.MaxReservedWordSetSize = maxSize;
        language.ReservedWords = words;
    }

    private void BuildAliasSequences(Language language)
    {
        var length = _parseTable.MaxAliasedProductionLength;
        var sequences = new ushort[_parseTable.ProductionInfos.Count * length];
        for (var i = 0; i < _parseTable.ProductionInfos.Count; i++)
        {
            var aliases = _parseTable.ProductionInfos[i].AliasSequence;
            for (var j = 0; j < aliases.Count; j++)
            {
                if (aliases[j] is { } alias)
                {
                    sequences[i * length + j] = _aliasIds[alias];
                }
            }
        }

        language.AliasSequences = sequences;
    }

    private void BuildFieldMaps(Language language)
    {
        var flatMaps = new List<(int Index, List<(string Name, FieldLocation Location)> Entries)>();
        var next = 0;
        int FieldMapId(List<(string Name, FieldLocation Location)> flat)
        {
            foreach (var (index, entries) in flatMaps)
            {
                if (entries.SequenceEqual(flat))
                {
                    return index;
                }
            }

            var result = next;
            next += flat.Count;
            flatMaps.Add((result, flat));
            return result;
        }

        FieldMapId([]);
        var slices = new ushort[_parseTable.ProductionInfos.Count * 2];
        for (var i = 0; i < _parseTable.ProductionInfos.Count; i++)
        {
            var info = _parseTable.ProductionInfos[i];
            if (info.FieldMap.Count == 0)
            {
                continue;
            }

            var flat = new List<(string Name, FieldLocation Location)>();
            foreach (var (name, locations) in info.FieldMap)
            {
                foreach (var location in locations)
                {
                    flat.Add((name, location));
                }
            }

            slices[i * 2] = (ushort)FieldMapId(flat);
            slices[i * 2 + 1] = (ushort)flat.Count;
        }

        var entries = new FieldMapEntry[next];
        foreach (var (index, flat) in flatMaps)
        {
            for (var j = 0; j < flat.Count; j++)
            {
                var fieldId = (ushort)(_fieldNames.BinarySearch(flat[j].Name, StringComparer.Ordinal) + 1);
                entries[index + j] = new FieldMapEntry(fieldId, (byte)flat[j].Location.Index, flat[j].Location.Inherited);
            }
        }

        language.FieldMapSlices = slices;
        language.FieldMapEntries = entries;
    }

    private void BuildExternalScannerTables(Language language)
    {
        var externals = _syntaxGrammar.ExternalTokens;
        language.ExternalTokenNames = externals.Select(e => e.Name).ToArray();
        language.ExternalScannerSymbolMap = externals.Select((e, i) =>
        {
            var symbol = e.CorrespondingInternalToken ?? Symbol.External(i);
            return _symbolIds.TryGetValue(symbol, out var id) ? id : (ushort)0;
        }).ToArray();

        var states = new bool[_parseTable.ExternalLexStates.Count * Math.Max(1, externals.Count)];
        for (var i = 0; i < _parseTable.ExternalLexStates.Count; i++)
        {
            foreach (var token in _parseTable.ExternalLexStates[i].Symbols)
            {
                if (token.IsExternal)
                {
                    states[i * externals.Count + token.Index] = true;
                }
            }
        }

        language.ExternalScannerStates = states;
    }

    private LexAutomaton ConvertLexTable(LexTable table)
    {
        var count = table.States.Count;
        var accept = new int[count];
        var eof = new int[count];
        var transitionStart = new int[count + 1];
        var rangeStart = new List<int>();
        var rangeEnd = new List<int>();
        var rangeTarget = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var state = table.States[i];
            accept[i] = state.AcceptAction is { } symbol ? _symbolIds[symbol] : -1;
            eof[i] = state.EofAction is { } eofAction ? eofAction.State : NullTransitionAtEof(state);
            transitionStart[i] = rangeStart.Count;
            var ranges = new List<(int Start, int End, int Target)>();
            foreach (var (chars, action) in state.AdvanceActions)
            {
                foreach (var (start, end) in chars.Ranges)
                {
                    ranges.Add((start, end - 1, action.State | (action.InMainToken ? 0 : LexAutomaton.SkipFlag)));
                }
            }

            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach (var (start, end, target) in ranges)
            {
                rangeStart.Add(start);
                rangeEnd.Add(end);
                rangeTarget.Add(target);
            }
        }

        transitionStart[count] = rangeStart.Count;

        // The lexers of tree-sitter loop forever on a cycle of null transitions at the end of the input: stop instead
        for (var i = 0; i < count; i++)
        {
            for (int state = i, steps = 0; ; steps++)
            {
                var next = eof[state];
                if (next < 0 || next == state)
                {
                    break;
                }

                if (steps >= count)
                {
                    eof[i] = -1;
                    break;
                }

                state = next;
            }
        }

        return new LexAutomaton(accept, eof, transitionStart, [.. rangeStart], [.. rangeEnd], [.. rangeTarget]);
    }

    /// <summary>
    /// The target of the transition on the null character at the end of the input, where the lookahead of tree-sitter's
    /// lexers is 0, or -1. tree-sitter renders the leading single-character transitions of a lex state as a map when
    /// there are at least 8 of their ranges, and the map matches the null character at the end of the input, unlike the
    /// conditions of the other transitions, which check that the lexer isn't at the end. Grammars rely on it to match
    /// <c>'\0'</c> at the end of the input, like the terminators of tree-sitter-go.
    /// </summary>
    private static int NullTransitionAtEof(LexState state)
    {
        var leadingCount = 0;
        var leadingRangeCount = 0;
        foreach (var (chars, action) in state.AdvanceActions)
        {
            // Ranges are half-open here, inclusive in tree-sitter
            if (!action.InMainToken || !chars.Ranges.All(r => r.End - 1 <= r.Start + 1 && r.End - 1 <= ushort.MaxValue))
            {
                break;
            }

            leadingCount++;
            leadingRangeCount += chars.RangeCount;
        }

        if (leadingRangeCount < 8)
        {
            return -1;
        }

        for (var i = 0; i < leadingCount; i++)
        {
            if (state.AdvanceActions[i].Chars.Contains(0))
            {
                return state.AdvanceActions[i].Action.State;
            }
        }

        return -1;
    }
}
