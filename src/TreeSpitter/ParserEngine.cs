using System.Runtime.CompilerServices;

namespace TreeSpitter;

/// <summary>The parse loop, for a lex function. Implemented by <see cref="ParserEngine{TLexer}"/>.</summary>
internal interface IParserEngine
{
    SyntaxTree Parse(byte[] input, int length);
}

/// <summary>
/// The GLR parser: a port of tree-sitter's <c>parser.c</c>, without incremental parsing. The engine is specialized for
/// the lex function of the language, the table-driven lexer of the VM or a generated lexer.
/// </summary>
internal sealed class ParserEngine<TLexer> : IParserEngine
    where TLexer : struct, ILexFunction
{
    private const int MaxVersionCount = 6;
    private const int MaxVersionCountOverflow = 4;
    private const int MaxSummaryDepth = 16;
    private const int MaxCostDifference = 18 * SubtreeArena.ErrorCostPerSkippedTree;
    private const int VersionNone = -1;

    private readonly Language _language;
    private readonly TLexer _lexFunction;
    private readonly Lexer _lexer = new();
    private readonly SubtreeArena _arena;
    private readonly ParseStack _stack;
    private readonly SubtreeList _trailingExtras = new();
    private readonly SubtreeList _trailingExtras2 = new();
    private readonly List<(ushort Symbol, int Count, int DynamicPrecedence, ushort ProductionId)> _reduceActions = [];
    private readonly ParseActionEntry[] _actions;
    private int _finishedTree;
    private int _acceptCount;
    private int _cachedToken;
    private int _cachedTokenPosition;
    private LineIndex? _lines;

    public ParserEngine(Language language, TLexer lexFunction)
    {
        _language = language;
        _lexFunction = lexFunction;
        _arena = new SubtreeArena(language);
        _stack = new ParseStack(_arena);
        _actions = language.ParseActions;
    }

    public SyntaxTree Parse(byte[] input, int length)
    {
        _lexer.Input = input;
        _lexer.Length = length;
        _arena.Reset(Math.Max(64, length / 2));
        _arena.Lines = null;
        _lines = null;
        _stack.Lines = null;
        _stack.Reset();
        _finishedTree = 0;
        _acceptCount = 0;
        _cachedToken = 0;
        _cachedTokenPosition = -1;

        var lastPosition = 0;
        int versionCount;
        do
        {
            for (var version = 0; ; version++)
            {
                versionCount = _stack.VersionCount;
                if (version >= versionCount)
                {
                    break;
                }

                while (_stack.IsActive(version))
                {
                    Advance(version);
                    var position = _stack.Position(version);
                    if (position > lastPosition || (version > 0 && position == lastPosition))
                    {
                        lastPosition = position;
                        break;
                    }
                }
            }

            var minErrorCost = CondenseStack();
            if (_finishedTree != 0 && _arena.ErrorCost(_finishedTree) < minErrorCost)
            {
                _stack.Clear();
                break;
            }
        }
        while (versionCount != 0);

        var tree = new SyntaxTree(_language, input, length, _arena, _finishedTree);
        return tree;
    }

    private LineIndex Lines()
    {
        if (_lines is null)
        {
            _lines = new LineIndex(_lexer.Input, _lexer.Length);
            _arena.Lines = _lines;
            _stack.Lines = _lines;
        }

        return _lines;
    }

    private int Row(int position) => Lines().Row(position);

    // Lexing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LexerStart()
    {
        var lexer = _lexer;
        lexer.TokenStart = lexer.Position;
        lexer.TokenEnd = -1;
        lexer.ResultSymbol = 0;
        if (lexer.Position == 0 && lexer.Length >= 3 && lexer.Input[0] == 0xEF && lexer.Input[1] == 0xBB && lexer.Input[2] == 0xBF)
        {
            // Skip the byte order mark
            lexer.Position = 3;
            lexer.TokenStart = 3;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LexerFinish()
    {
        var lexer = _lexer;
        if (lexer.TokenEnd < 0)
        {
            lexer.TokenEnd = lexer.Position;
        }

        if (lexer.TokenEnd < lexer.TokenStart)
        {
            lexer.TokenStart = lexer.TokenEnd;
        }
    }

    private int Lex(int version, ushort parseState)
    {
        var language = _language;
        var lexState = language.LexStates[parseState];
        if (lexState == Language.NoLookaheadLexState)
        {
            return 0;
        }

        var lexer = _lexer;
        var startPosition = _stack.Position(version);
        var errorMode = parseState == Language.ErrorState;
        var skippedError = false;
        var firstErrorCharacter = 0;
        var errorStart = 0;
        var errorEnd = 0;
        lexer.Position = startPosition;

        while (true)
        {
            LexerStart();
            var found = _lexFunction.Lex(lexer, lexState);
            LexerFinish();
            if (found)
            {
                break;
            }

            if (!errorMode)
            {
                errorMode = true;
                lexState = language.LexStates[Language.ErrorState];
                lexer.Position = startPosition;
                continue;
            }

            if (!skippedError)
            {
                skippedError = true;
                errorStart = lexer.TokenStart;
                errorEnd = lexer.TokenStart;
                firstErrorCharacter = Lexer.Decode(lexer.Input, lexer.Position, lexer.Length, out _);
            }

            if (lexer.Position == errorEnd)
            {
                if (lexer.Position >= lexer.Length)
                {
                    lexer.ResultSymbol = Language.ErrorSymbol;
                    break;
                }

                Lexer.Decode(lexer.Input, lexer.Position, lexer.Length, out var size);
                lexer.Position += size;
            }

            errorEnd = lexer.Position;
        }

        if (skippedError)
        {
            return _arena.NewError(firstErrorCharacter, startPosition, errorStart, errorEnd, parseState);
        }

        var symbol = lexer.ResultSymbol;
        var tokenStart = lexer.TokenStart;
        var tokenEnd = lexer.TokenEnd;
        var isKeyword = false;
        if (symbol == language.KeywordCaptureToken && symbol != 0)
        {
            lexer.Position = tokenStart;
            LexerStart();
            isKeyword = _lexFunction.LexKeyword(lexer, 0);
            LexerFinish();
            if (isKeyword && lexer.TokenEnd == tokenEnd &&
                (language.HasActions(parseState, lexer.ResultSymbol) || language.IsReservedWord(parseState, lexer.ResultSymbol)))
            {
                symbol = lexer.ResultSymbol;
            }
        }

        return _arena.NewLeaf(symbol, startPosition, tokenStart, tokenEnd, parseState, isKeyword);
    }

    private bool CanReuseFirstLeaf(ushort state, int tree, int tableEntryCount, bool tableEntryReusable)
    {
        var language = _language;
        var leafSymbol = _arena.LeafSymbol(tree);
        var leafState = _arena.LeafParseState(tree);
        var currentLexState = language.LexStates[state];
        if (currentLexState == Language.NoLookaheadLexState)
        {
            return false;
        }

        if (tableEntryCount > 0 && leafState != SubtreeArena.StateNone &&
            language.LexStates[leafState] == currentLexState &&
            language.ExternalLexStates[leafState] == language.ExternalLexStates[state] &&
            language.ReservedWordSetIds[leafState] == language.ReservedWordSetIds[state] &&
            (leafSymbol != language.KeywordCaptureToken || ((_arena[tree].Flags & SubtreeFlags.IsKeyword) == 0 && _arena[tree].ParseState == state)))
        {
            return true;
        }

        ref var data = ref _arena[tree];
        if (data.End == data.Start && leafSymbol != Language.EndSymbol)
        {
            return false;
        }

        return language.ExternalLexStates[state] == 0 && tableEntryReusable;
    }

    // Shift, reduce, accept

    private void Shift(int version, ushort state, int lookahead, bool extra)
    {
        var subtree = lookahead;
        if (extra != _arena.IsExtra(lookahead) && _arena[lookahead].ChildCount == 0)
        {
            subtree = _arena.WithExtra(lookahead, extra);
        }

        _stack.Push(version, subtree, _arena[subtree].ChildCount > 0, state);
    }

    private bool SelectTree(int left, int right)
    {
        if (left == 0)
        {
            return true;
        }

        if (right == 0)
        {
            return false;
        }

        var leftCost = _arena.ErrorCost(left);
        var rightCost = _arena.ErrorCost(right);
        if (rightCost < leftCost)
        {
            return true;
        }

        if (leftCost < rightCost)
        {
            return false;
        }

        var leftPrecedence = _arena[left].DynamicPrecedence;
        var rightPrecedence = _arena[right].DynamicPrecedence;
        if (rightPrecedence > leftPrecedence)
        {
            return true;
        }

        if (leftPrecedence > rightPrecedence)
        {
            return false;
        }

        if (leftCost > 0)
        {
            return true;
        }

        return _arena.Compare(left, right) == 1;
    }

    private bool SelectChildren(int left, SubtreeList children)
    {
        // A temporary node, discarded after the comparison
        var nodeCount = _arena.Count;
        var childrenCount = _arena.ChildrenCount;
        var scratch = _arena.NewNode(_arena[left].Symbol, children, 0, _stack.Position(0));
        var result = SelectTree(left, scratch);
        _arena.Count = nodeCount;
        _arena.ChildrenCount = childrenCount;
        return result;
    }

    private int Reduce(int version, ushort symbol, int count, int dynamicPrecedence, ushort productionId, bool isFragile, bool endOfNonTerminalExtra)
    {
        var initialVersionCount = _stack.VersionCount;
        var pop = _stack.PopCount(version, count);
        var removedVersionCount = 0;
        var haltedVersionCount = _stack.HaltedVersionCount();
        var popCount = pop.Count;
        for (var i = 0; i < popCount; i++)
        {
            var slice = pop[i];
            var sliceVersion = slice.Version - removedVersionCount;

            if (sliceVersion > MaxVersionCount + MaxVersionCountOverflow + haltedVersionCount)
            {
                _stack.RemoveVersion(sliceVersion);
                _stack.ReturnList(slice.Subtrees);
                removedVersionCount++;
                while (i + 1 < popCount)
                {
                    var nextSlice = pop[i + 1];
                    if (nextSlice.Version != slice.Version)
                    {
                        break;
                    }

                    _stack.ReturnList(nextSlice.Subtrees);
                    i++;
                }

                continue;
            }

            var children = slice.Subtrees;
            children.RemoveTrailingExtras(_arena, _trailingExtras);
            var position = _stack.Position(sliceVersion);
            var parent = _arena.NewNode(symbol, children, productionId, position);
            _stack.ReturnList(children);

            while (i + 1 < popCount)
            {
                var nextSlice = pop[i + 1];
                if (nextSlice.Version != slice.Version)
                {
                    break;
                }

                i++;
                var nextChildren = nextSlice.Subtrees;
                nextChildren.RemoveTrailingExtras(_arena, _trailingExtras2);
                if (SelectChildren(parent, nextChildren))
                {
                    _trailingExtras.CopyFrom(_trailingExtras2);
                    _trailingExtras2.Clear();
                    parent = _arena.NewNode(symbol, nextChildren, productionId, position);
                }
                else
                {
                    _trailingExtras2.Clear();
                }

                _stack.ReturnList(nextChildren);
            }

            var state = _stack.State(sliceVersion);
            var nextState = _language.NextState(state, symbol);
            ref var parentData = ref _arena[parent];
            if (endOfNonTerminalExtra && nextState == state)
            {
                parentData.Flags |= SubtreeFlags.Extra;
            }

            if (isFragile || popCount > 1 || initialVersionCount > 1)
            {
                parentData.Flags |= SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight;
                parentData.ParseState = SubtreeArena.StateNone;
            }
            else
            {
                parentData.ParseState = state;
            }

            parentData.DynamicPrecedence += dynamicPrecedence;

            _stack.Push(sliceVersion, parent, false, nextState);
            for (var j = 0; j < _trailingExtras.Count; j++)
            {
                _stack.Push(sliceVersion, _trailingExtras.Items[j], false, nextState);
            }

            for (var j = 0; j < sliceVersion; j++)
            {
                if (j == version)
                {
                    continue;
                }

                if (_stack.Merge(j, sliceVersion))
                {
                    removedVersionCount++;
                    break;
                }
            }
        }

        return _stack.VersionCount > initialVersionCount ? initialVersionCount : VersionNone;
    }

    private void Accept(int version, int lookahead)
    {
        _stack.Push(version, lookahead, false, 1);
        var pop = _stack.PopAll(version);
        for (var i = 0; i < pop.Count; i++)
        {
            var trees = pop[i].Subtrees;
            var root = 0;
            for (var j = trees.Count - 1; j >= 0; j--)
            {
                var tree = trees.Items[j];
                if (!_arena.IsExtra(tree))
                {
                    ref var data = ref _arena[tree];
                    var symbol = data.Symbol;
                    var productionId = data.ProductionId;
                    var firstChild = data.FirstChild;
                    var childCount = data.ChildCount;
                    trees.RemoveAt(j);
                    trees.InsertRange(j, _arena.Children, firstChild, childCount);
                    root = _arena.NewNode(symbol, trees, productionId, _arena[tree].PaddingStart);
                    break;
                }
            }

            _acceptCount++;
            if (_finishedTree != 0)
            {
                if (SelectTree(_finishedTree, root))
                {
                    _finishedTree = root;
                }
            }
            else
            {
                _finishedTree = root;
            }

            _stack.ReturnList(trees);
        }

        _stack.RemoveVersion(pop[0].Version);
        _stack.Halt(version);
    }

    // Error recovery

    private enum ErrorComparison
    {
        TakeLeft,
        PreferLeft,
        None,
        PreferRight,
        TakeRight,
    }

    private struct ErrorStatus
    {
        public int Cost;
        public int NodeCount;
        public int DynamicPrecedence;
        public bool IsInError;
    }

    private static ErrorComparison CompareVersions(ErrorStatus a, ErrorStatus b)
    {
        if (!a.IsInError && b.IsInError)
        {
            return a.Cost < b.Cost ? ErrorComparison.TakeLeft : ErrorComparison.PreferLeft;
        }

        if (a.IsInError && !b.IsInError)
        {
            return b.Cost < a.Cost ? ErrorComparison.TakeRight : ErrorComparison.PreferRight;
        }

        if (a.Cost < b.Cost)
        {
            return (long)(b.Cost - a.Cost) * (1 + a.NodeCount) > MaxCostDifference ? ErrorComparison.TakeLeft : ErrorComparison.PreferLeft;
        }

        if (b.Cost < a.Cost)
        {
            return (long)(a.Cost - b.Cost) * (1 + b.NodeCount) > MaxCostDifference ? ErrorComparison.TakeRight : ErrorComparison.PreferRight;
        }

        if (a.DynamicPrecedence > b.DynamicPrecedence)
        {
            return ErrorComparison.PreferLeft;
        }

        if (b.DynamicPrecedence > a.DynamicPrecedence)
        {
            return ErrorComparison.PreferRight;
        }

        return ErrorComparison.None;
    }

    private ErrorStatus VersionStatus(int version)
    {
        var cost = _stack.ErrorCost(version);
        var isPaused = _stack.IsPaused(version);
        if (isPaused)
        {
            cost += SubtreeArena.ErrorCostPerSkippedTree;
        }

        return new ErrorStatus
        {
            Cost = cost,
            NodeCount = _stack.NodeCountSinceError(version),
            DynamicPrecedence = _stack.DynamicPrecedence(version),
            IsInError = isPaused || _stack.State(version) == Language.ErrorState,
        };
    }

    private bool BetterVersionExists(int version, bool isInError, int cost)
    {
        if (_finishedTree != 0 && _arena.ErrorCost(_finishedTree) <= cost)
        {
            return true;
        }

        var position = _stack.Position(version);
        var status = new ErrorStatus
        {
            Cost = cost,
            IsInError = isInError,
            DynamicPrecedence = _stack.DynamicPrecedence(version),
            NodeCount = _stack.NodeCountSinceError(version),
        };

        for (var i = 0; i < _stack.VersionCount; i++)
        {
            if (i == version || !_stack.IsActive(i) || _stack.Position(i) < position)
            {
                continue;
            }

            var statusI = VersionStatus(i);
            switch (CompareVersions(status, statusI))
            {
                case ErrorComparison.TakeRight:
                    return true;
                case ErrorComparison.PreferRight:
                    if (_stack.CanMerge(i, version))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private bool BreakdownTopOfStack(int version)
    {
        var didBreakDown = false;
        bool pending;
        do
        {
            var pop = _stack.PopPending(version);
            if (pop.Count == 0)
            {
                break;
            }

            didBreakDown = true;
            pending = false;
            for (var i = 0; i < pop.Count; i++)
            {
                var slice = pop[i];
                var state = _stack.State(slice.Version);
                var parent = slice.Subtrees.Items[0];
                ref var parentData = ref _arena[parent];
                for (var j = 0; j < parentData.ChildCount; j++)
                {
                    var child = _arena.Children[parentData.FirstChild + j];
                    pending = _arena[child].ChildCount > 0;
                    if (_arena[child].Symbol == Language.ErrorSymbol)
                    {
                        state = Language.ErrorState;
                    }
                    else if (!_arena.IsExtra(child))
                    {
                        state = _language.NextState(state, _arena[child].Symbol);
                    }

                    _stack.Push(slice.Version, child, pending, state);
                }

                for (var j = 1; j < slice.Subtrees.Count; j++)
                {
                    _stack.Push(slice.Version, slice.Subtrees.Items[j], false, state);
                }

                _stack.ReturnList(slice.Subtrees);
            }
        }
        while (pending);

        return didBreakDown;
    }

    private bool DoAllPotentialReductions(int startingVersion, ushort lookaheadSymbol)
    {
        var initialVersionCount = _stack.VersionCount;
        var canShiftLookaheadSymbol = false;
        var version = startingVersion;
        var actions = _actions;
        for (var i = 0; ; i++)
        {
            var versionCount = _stack.VersionCount;
            if (version >= versionCount)
            {
                break;
            }

            var merged = false;
            for (var j = initialVersionCount; j < version; j++)
            {
                if (_stack.Merge(j, version))
                {
                    merged = true;
                    break;
                }
            }

            if (merged)
            {
                continue;
            }

            var state = _stack.State(version);
            var hasShiftAction = false;
            _reduceActions.Clear();

            int firstSymbol, endSymbol;
            if (lookaheadSymbol != 0)
            {
                firstSymbol = lookaheadSymbol;
                endSymbol = lookaheadSymbol + 1;
            }
            else
            {
                firstSymbol = 1;
                endSymbol = _language.TokenCount;
            }

            for (var symbol = firstSymbol; symbol < endSymbol; symbol++)
            {
                var index = _language.TableEntry(state, (ushort)symbol, out var count, out _);
                for (var j = 0; j < count; j++)
                {
                    var action = actions[index + j];
                    switch (action.Type)
                    {
                        case ParseActionType.Shift:
                        case ParseActionType.Recover:
                            if (!action.IsExtra && !action.IsRepetition)
                            {
                                hasShiftAction = true;
                            }

                            break;
                        case ParseActionType.Reduce:
                            if (action.ChildCount > 0)
                            {
                                var reduce = (action.StateOrSymbol, action.ChildCount, action.DynamicPrecedence, action.ProductionId);
                                var exists = false;
                                foreach (var existing in _reduceActions)
                                {
                                    if (existing.Symbol == reduce.StateOrSymbol && existing.Count == reduce.ChildCount)
                                    {
                                        exists = true;
                                        break;
                                    }
                                }

                                if (!exists)
                                {
                                    _reduceActions.Add(reduce);
                                }
                            }

                            break;
                    }
                }
            }

            var reductionVersion = VersionNone;
            for (var j = 0; j < _reduceActions.Count; j++)
            {
                var action = _reduceActions[j];
                reductionVersion = Reduce(version, action.Symbol, action.Count, action.DynamicPrecedence, action.ProductionId, true, false);
            }

            if (hasShiftAction)
            {
                canShiftLookaheadSymbol = true;
            }
            else if (reductionVersion != VersionNone && i < MaxVersionCount)
            {
                _stack.RenumberVersion(reductionVersion, version);
                continue;
            }
            else if (lookaheadSymbol != 0)
            {
                _stack.RemoveVersion(version);
            }

            if (version == startingVersion)
            {
                version = versionCount;
            }
            else
            {
                version++;
            }
        }

        return canShiftLookaheadSymbol;
    }

    private bool RecoverToState(int version, int depth, ushort goalState)
    {
        var pop = _stack.PopCount(version, depth);
        var slices = new List<StackSlice>(pop);
        var previousVersion = VersionNone;

        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            if (slice.Version == previousVersion)
            {
                _stack.ReturnList(slice.Subtrees);
                slices.RemoveAt(i--);
                continue;
            }

            if (_stack.State(slice.Version) != goalState)
            {
                _stack.Halt(slice.Version);
                _stack.ReturnList(slice.Subtrees);
                slices.RemoveAt(i--);
                continue;
            }

            var errorTrees = _stack.PopError(slice.Version);
            if (errorTrees is not null && errorTrees.Count > 0)
            {
                var errorTree = errorTrees.Items[0];
                ref var errorData = ref _arena[errorTree];
                if (errorData.ChildCount > 0)
                {
                    slice.Subtrees.InsertRange(0, _arena.Children, errorData.FirstChild, errorData.ChildCount);
                }
            }

            _stack.ReturnList(errorTrees);

            slice.Subtrees.RemoveTrailingExtras(_arena, _trailingExtras);
            if (slice.Subtrees.Count > 0)
            {
                var error = _arena.NewErrorNode(slice.Subtrees, true, _stack.Position(slice.Version));
                _stack.Push(slice.Version, error, false, goalState);
            }

            _stack.ReturnList(slice.Subtrees);

            for (var j = 0; j < _trailingExtras.Count; j++)
            {
                _stack.Push(slice.Version, _trailingExtras.Items[j], false, goalState);
            }

            previousVersion = slice.Version;
        }

        return previousVersion != VersionNone;
    }

    private void Recover(int version, int lookahead)
    {
        var didRecover = false;
        var previousVersionCount = _stack.VersionCount;
        var position = _stack.Position(version);
        var summary = _stack.GetSummary(version);
        var nodeCountSinceError = _stack.NodeCountSinceError(version);
        var currentErrorCost = _stack.ErrorCost(version);

        if (summary is not null && _arena[lookahead].Symbol != Language.ErrorSymbol)
        {
            for (var i = 0; i < summary.Count; i++)
            {
                var entry = summary[i];
                if (entry.State == Language.ErrorState)
                {
                    continue;
                }

                if (entry.Position == position)
                {
                    continue;
                }

                var depth = entry.Depth;
                if (nodeCountSinceError > 0)
                {
                    depth++;
                }

                var wouldMerge = false;
                for (var j = 0; j < previousVersionCount; j++)
                {
                    if (_stack.State(j) == entry.State && _stack.Position(j) == position)
                    {
                        wouldMerge = true;
                        break;
                    }
                }

                if (wouldMerge)
                {
                    continue;
                }

                var newCost = currentErrorCost +
                    entry.Depth * SubtreeArena.ErrorCostPerSkippedTree +
                    (position - entry.Position) * SubtreeArena.ErrorCostPerSkippedChar +
                    (Row(position) - Row(entry.Position)) * SubtreeArena.ErrorCostPerSkippedLine;
                if (BetterVersionExists(version, false, newCost))
                {
                    break;
                }

                if (_language.HasActions(entry.State, _arena[lookahead].Symbol))
                {
                    if (RecoverToState(version, depth, entry.State))
                    {
                        didRecover = true;
                        break;
                    }
                }
            }
        }

        for (var i = previousVersionCount; i < _stack.VersionCount; i++)
        {
            if (!_stack.IsActive(i))
            {
                _stack.RemoveVersion(i--);
            }
        }

        if (_arena[lookahead].Symbol == Language.EndSymbol)
        {
            var children = _stack.RentList();
            var parent = _arena.NewErrorNode(children, false, _stack.Position(version));
            _stack.ReturnList(children);
            _stack.Push(version, parent, false, 1);
            Accept(version, lookahead);
            return;
        }

        if (didRecover && _stack.VersionCount > MaxVersionCount)
        {
            _stack.Halt(version);
            return;
        }

        var lookaheadCost = currentErrorCost + SubtreeArena.ErrorCostPerSkippedTree +
            (_arena[lookahead].End - _arena[lookahead].PaddingStart) * SubtreeArena.ErrorCostPerSkippedChar +
            TotalRows(lookahead) * SubtreeArena.ErrorCostPerSkippedLine;
        if (BetterVersionExists(version, false, lookaheadCost))
        {
            _stack.Halt(version);
            return;
        }

        // An extra token is marked as extra, so it isn't counted in the error cost
        var index = _language.TableEntry(1, _arena[lookahead].Symbol, out var n, out _);
        if (n > 0)
        {
            var last = _actions[index + n - 1];
            if (last.Type == ParseActionType.Shift && last.IsExtra)
            {
                lookahead = _arena.WithExtra(lookahead, true);
            }
        }

        var errorChildren = _stack.RentList();
        errorChildren.Add(lookahead);
        var errorRepeat = _arena.NewNode(Language.ErrorRepeatSymbol, errorChildren, 0, position);
        _stack.ReturnList(errorChildren);

        if (nodeCountSinceError > 0)
        {
            var pop = _stack.PopCount(version, 1);
            if (pop.Count > 1)
            {
                for (var i = 1; i < pop.Count; i++)
                {
                    _stack.ReturnList(pop[i].Subtrees);
                }

                while (_stack.VersionCount > pop[0].Version + 1)
                {
                    _stack.RemoveVersion(pop[0].Version + 1);
                }
            }

            var first = pop[0];
            _stack.RenumberVersion(first.Version, version);
            first.Subtrees.Add(errorRepeat);
            errorRepeat = _arena.NewNode(Language.ErrorRepeatSymbol, first.Subtrees, 0, position);
            _stack.ReturnList(first.Subtrees);
        }

        _stack.Push(version, errorRepeat, false, Language.ErrorState);
    }

    private int TotalRows(int subtree)
    {
        ref var data = ref _arena[subtree];
        return data.End <= data.PaddingStart ? 0 : Row(data.End) - Row(data.PaddingStart);
    }

    private void HandleError(int version, int lookahead)
    {
        var previousVersionCount = _stack.VersionCount;

        DoAllPotentialReductions(version, 0);
        var versionCount = _stack.VersionCount;
        var position = _stack.Position(version);

        var didInsertMissingToken = false;
        for (var v = version; v < versionCount;)
        {
            if (!didInsertMissingToken)
            {
                var state = _stack.State(v);
                for (var missingSymbol = 1; missingSymbol < _language.TokenCount; missingSymbol++)
                {
                    var stateAfterMissingSymbol = _language.NextState(state, (ushort)missingSymbol);
                    if (stateAfterMissingSymbol == 0 || stateAfterMissingSymbol == state)
                    {
                        continue;
                    }

                    if (_language.HasReduceAction(stateAfterMissingSymbol, _arena.LeafSymbol(lookahead)))
                    {
                        var versionWithMissingTree = _stack.CopyVersion(v);
                        var missingTree = _arena.NewMissingLeaf((ushort)missingSymbol, position);
                        _stack.Push(versionWithMissingTree, missingTree, false, stateAfterMissingSymbol);
                        if (DoAllPotentialReductions(versionWithMissingTree, _arena.LeafSymbol(lookahead)))
                        {
                            didInsertMissingToken = true;
                            break;
                        }
                    }
                }
            }

            _stack.Push(v, 0, false, Language.ErrorState);
            v = v == version ? previousVersionCount : v + 1;
        }

        for (var i = previousVersionCount; i < versionCount; i++)
        {
            _stack.Merge(version, previousVersionCount);
        }

        _stack.RecordSummary(version, MaxSummaryDepth);
        Recover(version, lookahead);
    }

    // The parse loop

    private void Advance(int version)
    {
        var language = _language;
        var actions = _actions;
        var state = _stack.State(version);
        var position = _stack.Position(version);

        var lookahead = 0;
        int entryIndex = 0, entryCount = 0;
        bool entryReusable;

        // Reuse the token returned by the lexer for another version
        if (_cachedToken != 0 && _cachedTokenPosition == position)
        {
            entryIndex = language.TableEntry(state, _arena[_cachedToken].Symbol, out entryCount, out entryReusable);
            if (CanReuseFirstLeaf(state, _cachedToken, entryCount, entryReusable))
            {
                lookahead = _cachedToken;
            }
        }

        var needsLex = lookahead == 0;
        while (true)
        {
            if (needsLex)
            {
                needsLex = false;
                lookahead = Lex(version, state);
                if (lookahead != 0)
                {
                    _cachedToken = lookahead;
                    _cachedTokenPosition = position;
                    entryIndex = language.TableEntry(state, _arena[lookahead].Symbol, out entryCount, out _);
                }
                else
                {
                    // At the end of a non-terminal extra, the reduction is stored in the entry of the end symbol
                    entryIndex = language.TableEntry(state, Language.EndSymbol, out entryCount, out _);
                }
            }

            var didReduce = false;
            var lastReductionVersion = VersionNone;
            for (var i = 0; i < entryCount; i++)
            {
                var action = actions[entryIndex + i];
                switch (action.Type)
                {
                    case ParseActionType.Shift:
                        {
                            if (action.IsRepetition)
                            {
                                break;
                            }

                            var nextState = action.IsExtra ? state : action.StateOrSymbol;
                            Shift(version, nextState, lookahead, action.IsExtra);
                            return;
                        }

                    case ParseActionType.Reduce:
                        {
                            var isFragile = entryCount > 1;
                            var endOfNonTerminalExtra = lookahead == 0;
                            var reductionVersion = Reduce(version, action.StateOrSymbol, action.ChildCount, action.DynamicPrecedence, action.ProductionId, isFragile, endOfNonTerminalExtra);
                            didReduce = true;
                            if (reductionVersion != VersionNone)
                            {
                                lastReductionVersion = reductionVersion;
                            }

                            break;
                        }

                    case ParseActionType.Accept:
                        Accept(version, lookahead);
                        return;

                    case ParseActionType.Recover:
                        Recover(version, lookahead);
                        return;
                }
            }

            if (lastReductionVersion != VersionNone)
            {
                _stack.RenumberVersion(lastReductionVersion, version);
                state = _stack.State(version);
                if (lookahead == 0)
                {
                    needsLex = true;
                }
                else
                {
                    entryIndex = language.TableEntry(state, _arena.LeafSymbol(lookahead), out entryCount, out _);
                }

                continue;
            }

            if (didReduce)
            {
                _stack.Halt(version);
                return;
            }

            // An invalid keyword is the word token when the word token is valid
            ref var lookaheadData = ref _arena[lookahead];
            if ((lookaheadData.Flags & SubtreeFlags.IsKeyword) != 0 &&
                lookaheadData.Symbol != language.KeywordCaptureToken &&
                !language.IsReservedWord(state, lookaheadData.Symbol))
            {
                entryIndex = language.TableEntry(state, language.KeywordCaptureToken, out entryCount, out _);
                if (entryCount > 0)
                {
                    lookahead = _arena.WithSymbol(lookahead, language.KeywordCaptureToken);
                    continue;
                }
            }

            if (BreakdownTopOfStack(version))
            {
                state = _stack.State(version);
                needsLex = true;
                continue;
            }

            _stack.Pause(version, lookahead);
            return;
        }
    }

    private int CondenseStack()
    {
        var minErrorCost = int.MaxValue;
        for (var i = 0; i < _stack.VersionCount; i++)
        {
            if (_stack.IsHalted(i))
            {
                _stack.RemoveVersion(i);
                i--;
                continue;
            }

            var statusI = VersionStatus(i);
            if (!statusI.IsInError && statusI.Cost < minErrorCost)
            {
                minErrorCost = statusI.Cost;
            }

            for (var j = 0; j < i; j++)
            {
                var statusJ = VersionStatus(j);
                switch (CompareVersions(statusJ, statusI))
                {
                    case ErrorComparison.TakeLeft:
                        _stack.RemoveVersion(i);
                        i--;
                        j = i;
                        break;
                    case ErrorComparison.PreferLeft:
                    case ErrorComparison.None:
                        if (_stack.Merge(j, i))
                        {
                            i--;
                            j = i;
                        }

                        break;
                    case ErrorComparison.PreferRight:
                        if (_stack.Merge(j, i))
                        {
                            i--;
                            j = i;
                        }
                        else
                        {
                            _stack.SwapVersions(i, j);
                        }

                        break;
                    case ErrorComparison.TakeRight:
                        _stack.RemoveVersion(j);
                        i--;
                        j--;
                        break;
                }
            }
        }

        while (_stack.VersionCount > MaxVersionCount)
        {
            _stack.RemoveVersion(MaxVersionCount);
        }

        if (_stack.VersionCount > 0)
        {
            var hasUnpausedVersion = false;
            for (int i = 0, n = _stack.VersionCount; i < n; i++)
            {
                if (_stack.IsPaused(i))
                {
                    if (!hasUnpausedVersion && _acceptCount < MaxVersionCount)
                    {
                        minErrorCost = _stack.ErrorCost(i);
                        var lookahead = _stack.Resume(i);
                        HandleError(i, lookahead);
                        hasUnpausedVersion = true;
                    }
                    else
                    {
                        _stack.RemoveVersion(i);
                        i--;
                        n--;
                    }
                }
                else
                {
                    hasUnpausedVersion = true;
                }
            }
        }

        return minErrorCost;
    }
}
