using System.Runtime.CompilerServices;

namespace TreeSpitter;

/// <summary>The parse loop, for a lex function. Implemented by <see cref="ParserEngine{TLexer}"/>.</summary>
internal interface IParserEngine
{
    SyntaxTree Parse(byte[] input, int length, SyntaxTree? oldTree);
}

/// <summary>
/// The GLR parser: a port of tree-sitter's <c>parser.c</c>. The engine is specialized for the lex function of the
/// language, the table-driven lexer of the VM or a generated lexer. An incremental parse reuses the unchanged subtrees
/// of the old tree, and appends its nodes to the arena of the old tree.
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
    private SubtreeArena _arena;
    private SubtreeArena? _spareArena;
    private readonly ParseStack _stack;
    private readonly SubtreeList _trailingExtras = new();
    private readonly SubtreeList _trailingExtras2 = new();
    private readonly List<(ushort Symbol, int Count, int DynamicPrecedence, ushort ProductionId)> _reduceActions = [];
    private readonly ParseActionEntry[] _actions;
    private int _finishedTree;
    private int _nodesPerKilobyte = 400;
    private int _childrenPerKilobyte = 400;
    private int _acceptCount;
    private int _cachedToken;
    private int _cachedTokenPosition;

    // The node of the old tree that can be reused next, tree-sitter's ReusableNode: a path from the root
    private ReusableEntry[] _reusable = new ReusableEntry[64];
    private int _reusableCount;

    public ParserEngine(Language language, TLexer lexFunction)
    {
        _language = language;
        _lexFunction = lexFunction;
        _arena = new SubtreeArena(language, ReturnArena);
        _spareArena = _arena;
        _stack = new ParseStack(_arena);
        _actions = language.ParseActions;
    }

    private struct ReusableEntry
    {
        public int Tree;
        public int ChildIndex;
        public int ByteOffset;
    }

    /// <summary>Called when the last tree of an arena is disposed: the next parse can reuse its memory.</summary>
    private void ReturnArena(SubtreeArena arena) => Volatile.Write(ref _spareArena, arena);

    public SyntaxTree Parse(byte[] input, int length, SyntaxTree? oldTree)
    {
        if (oldTree is null)
        {
            var arena = Interlocked.Exchange(ref _spareArena, null) ?? new SubtreeArena(_language, ReturnArena);

            // The capacity of the arena is estimated from the previous parses, to avoid resizing the arrays
            arena.Reset(EstimateCapacity(_nodesPerKilobyte, length), EstimateCapacity(_childrenPerKilobyte, length));
            arena.AddReference();
            try
            {
                var tree = Parse(arena, input, length, 0);
                if (length > 0)
                {
                    _nodesPerKilobyte = Math.Max(_nodesPerKilobyte, (int)Math.Min(int.MaxValue, arena.Count * 1024L / length));
                    _childrenPerKilobyte = Math.Max(_childrenPerKilobyte, (int)Math.Min(int.MaxValue, arena.ChildrenCount * 1024L / length));
                }

                arena.LiveCount = arena.Count;
                return tree;
            }
            finally
            {
                arena.RemoveReference();
            }
        }

        if (oldTree.Language != _language)
        {
            throw new ArgumentException("The old tree was parsed with another language.", nameof(oldTree));
        }

        var oldArena = oldTree.Arena;
        oldArena.AddReference();
        try
        {
            // The trees of a lineage share their arena: parses and edits of these trees are serialized
            lock (oldArena)
            {
                oldTree.Balance();
                if (oldArena.Count <= 2 * oldArena.LiveCount + 4096)
                {
                    return Parse(oldArena, input, length, oldTree.Root);
                }

                // The arena is mostly made of the nodes of older trees: the old tree is copied to a new arena, so that
                // the arenas don't grow forever. The cost of the copy is amortized over the parses.
                var arena = Interlocked.Exchange(ref _spareArena, null) ?? new SubtreeArena(_language, ReturnArena);
                arena.Reset(oldArena.LiveCount + 1024, (int)Math.Min(int.MaxValue / 2, (long)oldArena.ChildrenCount * oldArena.LiveCount / oldArena.Count) + 1024);
                var root = oldArena.CopyTree(oldTree.Root, arena);
                arena.LiveCount = arena.Count;
                arena.AddReference();
                try
                {
                    return Parse(arena, input, length, root);
                }
                finally
                {
                    arena.RemoveReference();
                }
            }
        }
        finally
        {
            oldArena.RemoveReference();
        }
    }

    private SyntaxTree Parse(SubtreeArena arena, byte[] input, int length, int oldRoot)
    {
        _lexer.Input = input;
        _lexer.Length = length;
        _arena = arena;
        arena.SetInput(input, length);
        var firstOwnedNode = arena.Count;
        _stack.Reset(arena);
        _finishedTree = 0;
        _acceptCount = 0;
        _cachedToken = 0;
        _cachedTokenPosition = -1;
        ResetReusableNode(oldRoot);

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

                var allowNodeReuse = versionCount == 1;
                while (_stack.IsActive(version))
                {
                    Advance(version, allowNodeReuse);
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

        _reusableCount = 0;
        _stack.Clear();
        arena.SetInput(null, 0);

        // The tree is balanced when it's edited or reused
        return new SyntaxTree(_language, input, length, arena, _finishedTree, firstOwnedNode);
    }

    private static int EstimateCapacity(int perKilobyte, int length) =>
        (int)Math.Min(int.MaxValue / 2, 64 + (long)length * perKilobyte / 1024 * 9 / 8);

    private int Row(int position) => _arena.Lines.Row(position);

    // The nodes of the old tree

    private void ResetReusableNode(int root)
    {
        _reusableCount = 0;
        if (root == 0)
        {
            return;
        }

        _reusable[0] = new ReusableEntry { Tree = root, ChildIndex = 0, ByteOffset = 0 };
        _reusableCount = 1;

        // The root isn't reused: its structure was changed when it was accepted (the end and the extras were added)
        if (!ReusableDescend())
        {
            _reusableCount = 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReusableAdvance()
    {
        ref var last = ref _reusable[_reusableCount - 1];
        var byteOffset = last.ByteOffset + _arena.TotalBytes(last.Tree);
        int tree;
        int nextIndex;
        do
        {
            nextIndex = _reusable[--_reusableCount].ChildIndex + 1;
            if (_reusableCount == 0)
            {
                return;
            }

            tree = _reusable[_reusableCount - 1].Tree;
        }
        while (_arena[tree].ChildCount <= nextIndex);

        PushReusable(_arena.Children[_arena[tree].FirstChild + nextIndex], nextIndex, byteOffset);
    }

    private bool ReusableDescend()
    {
        var last = _reusable[_reusableCount - 1];
        ref var data = ref _arena[last.Tree];
        if (data.ChildCount == 0)
        {
            return false;
        }

        PushReusable(_arena.Children[data.FirstChild], 0, last.ByteOffset);
        return true;
    }

    private void ReusableAdvancePastLeaf()
    {
        while (ReusableDescend())
        {
        }

        ReusableAdvance();
    }

    private void PushReusable(int tree, int childIndex, int byteOffset)
    {
        if (_reusableCount == _reusable.Length)
        {
            Array.Resize(ref _reusable, _reusable.Length * 2);
        }

        _reusable[_reusableCount++] = new ReusableEntry { Tree = tree, ChildIndex = childIndex, ByteOffset = byteOffset };
    }

    /// <summary>
    /// Finds a node of the old tree that can be reused at a position, tree-sitter's <c>ts_parser__reuse_node</c>.
    /// Returns 0 when there's none. The top of the stack can be broken down: the caller reads the state again. The
    /// state isn't passed by reference, so that the locals of the parse loop stay in registers.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ReuseNode(int version, ushort state, int position)
    {
        while (_reusableCount > 0)
        {
            var entry = _reusable[_reusableCount - 1];
            var result = entry.Tree;
            ref var data = ref _arena[result];
            var byteOffset = entry.ByteOffset;
            var endByteOffset = data.Symbol == Language.EndSymbol ? int.MaxValue : byteOffset + data.Padding + data.Size;
            if (byteOffset > position)
            {
                break;
            }

            if (byteOffset < position)
            {
                if (endByteOffset <= position || !ReusableDescend())
                {
                    ReusableAdvance();
                }

                continue;
            }

            if ((data.Flags & (SubtreeFlags.HasChanges | SubtreeFlags.IsMissing | SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight)) != 0 ||
                data.Symbol == Language.ErrorSymbol)
            {
                if (!ReusableDescend())
                {
                    ReusableAdvance();
                    BreakdownTopOfStack(version);
                    state = _stack.State(version);
                }

                continue;
            }

            _language.TableEntry(state, _arena.LeafSymbol(result), out var entryCount, out var entryReusable);
            if (!CanReuseFirstLeaf(state, result, entryCount, entryReusable))
            {
                ReusableAdvancePastLeaf();
                break;
            }

            return result;
        }

        return 0;
    }

    /// <summary>
    /// Descends into a reused lookahead until a node created in the current state, tree-sitter's
    /// <c>ts_parser__breakdown_lookahead</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int BreakdownLookahead(int lookahead, ushort state)
    {
        var didDescend = false;
        var tree = _reusableCount > 0 ? _reusable[_reusableCount - 1].Tree : 0;
        while (_arena[tree].ChildCount > 0 && _arena[tree].ParseState != state)
        {
            ReusableDescend();
            tree = _reusable[_reusableCount - 1].Tree;
            didDescend = true;
        }

        return didDescend ? tree : lookahead;
    }

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
        var lookaheadEnd = 0;
        lexer.Position = startPosition;

        while (true)
        {
            LexerStart();
            var found = _lexFunction.Lex(lexer, lexState);
            LexerFinish();
            lookaheadEnd = LookaheadEnd(lexer, lookaheadEnd);
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
            return _arena.NewError(firstErrorCharacter, errorStart - startPosition, errorEnd - errorStart, lookaheadEnd - errorEnd, parseState);
        }

        var symbol = lexer.ResultSymbol;
        var tokenStart = lexer.TokenStart;
        var tokenEnd = lexer.TokenEnd;
        var isKeyword = false;
        if (symbol == language.KeywordCaptureToken && symbol != 0)
        {
            var keyword = _lexFunction.MatchKeyword(lexer.Input, tokenStart, tokenEnd);
            if (keyword != KeywordTable.UseAutomaton)
            {
                // The keywords are a set of strings: the lookup gives the keyword of the automaton
                if (keyword >= 0)
                {
                    isKeyword = true;
                    if (language.HasActions(parseState, (ushort)keyword) || language.IsReservedWord(parseState, (ushort)keyword))
                    {
                        symbol = (ushort)keyword;
                    }
                }

                return _arena.NewLeaf(symbol, tokenStart - startPosition, tokenEnd - tokenStart, lookaheadEnd - tokenEnd, parseState, isKeyword);
            }

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

        return _arena.NewLeaf(symbol, tokenStart - startPosition, tokenEnd - tokenStart, lookaheadEnd - tokenEnd, parseState, isKeyword);
    }

    /// <summary>
    /// The end of the bytes read by the lexer, tree-sitter's <c>ts_lexer_finish</c>: the lookahead character, and
    /// the bytes that can make it an invalid sequence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LookaheadEnd(Lexer lexer, int lookaheadEnd)
    {
        var position = lexer.Position;
        var end = position + 1;
        if (position < lexer.Length && lexer.Input[position] >= 0x80 && Lexer.Decode(lexer.Input, position, lexer.Length, out _) == Lexer.DecodeError)
        {
            end += 4;
        }

        return end > lookaheadEnd ? end : lookaheadEnd;
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

        if (_arena[tree].Size == 0 && leafSymbol != Language.EndSymbol)
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

    private bool SelectChildren(int left, SubtreeList children, int position)
    {
        // A temporary node, discarded after the comparison
        var nodeCount = _arena.Count;
        var childrenCount = _arena.ChildrenCount;
        var scratch = _arena.NewNode(_arena[left].Symbol, children, 0, position);
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
                if (SelectChildren(parent, nextChildren, position))
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

    /// <summary>
    /// A reduction of the only version of the stack when the popped path has no merged nodes: the same result as
    /// <see cref="Reduce"/> followed by the renumbering of the new version, without creating a version.
    /// </summary>
    private bool ReduceInPlace(int version, ushort symbol, int count, int dynamicPrecedence, ushort productionId, bool endOfNonTerminalExtra, bool isFragile)
    {
        if (!_stack.TryPopSinglePath(version, count, _trailingExtras, out var baseNode, out var firstChild, out var childCount))
        {
            return false;
        }

        ref var stackNode = ref _stack.Node(baseNode);
        var state = stackNode.State;
        var parent = _arena.NewNode(symbol, firstChild, childCount, productionId, stackNode.Position);
        var nextState = _language.NextState(state, symbol);
        ref var parentData = ref _arena[parent];
        if (endOfNonTerminalExtra && nextState == state)
        {
            parentData.Flags |= SubtreeFlags.Extra;
        }

        if (isFragile)
        {
            parentData.Flags |= SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight;
            parentData.ParseState = SubtreeArena.StateNone;
        }
        else
        {
            parentData.ParseState = state;
        }

        parentData.DynamicPrecedence += dynamicPrecedence;

        _stack.Head(version).Node = baseNode;
        _stack.Push(version, parent, false, nextState);
        for (var j = 0; j < _trailingExtras.Count; j++)
        {
            _stack.Push(version, _trailingExtras.Items[j], false, nextState);
        }

        return true;
    }

    /// <summary>
    /// Whether the action at <paramref name="index"/> is the only one of its entry that does something: the others are
    /// shifts of repetitions, which the parse loop skips. Then the reduction doesn't need a new stack version.
    /// </summary>
    private bool IsOnlyAction(int entryIndex, int entryCount, int index)
    {
        for (var i = 0; i < entryCount; i++)
        {
            if (i == index)
            {
                continue;
            }

            var other = _actions[entryIndex + i];
            if (other.Type != ParseActionType.Shift || !other.IsRepetition)
            {
                return false;
            }
        }

        return true;
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
                    root = _arena.NewNode(symbol, trees, productionId, 0);
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

            if (lookaheadSymbol != 0)
            {
                hasShiftAction = AddCandidateRecoveryActions(state, lookaheadSymbol);
            }
            else
            {
                var language = _language;
                if (Compatibility.TreeSitter026 || state < language.LargeStateCount)
                {
                    // The lookaheads by symbol: tree-sitter 0.26 scans the terminals, the lookahead iterator of 0.27
                    // visits the large states by symbol
                    for (var symbol = 1; symbol < language.TokenCount; symbol++)
                    {
                        hasShiftAction |= AddCandidateRecoveryActions(state, (ushort)symbol);
                    }
                }
                else
                {
                    // The lookaheads of a small state in the order of the lookahead iterator of 0.27
                    var order = language.LookaheadOrder;
                    for (int k = language.LookaheadOrderStarts[state], end = language.LookaheadOrderStarts[state + 1]; k < end; k++)
                    {
                        var symbol = order[k];
                        if (symbol != Language.EndSymbol && symbol < language.TokenCount)
                        {
                            hasShiftAction |= AddCandidateRecoveryActions(state, symbol);
                        }
                    }
                }

                if (!Compatibility.TreeSitter026)
                {
                    // tree-sitter 0.27 sorts the reductions by symbol, descending, with a stable insertion sort
                    for (var j = 1; j < _reduceActions.Count; j++)
                    {
                        var key = _reduceActions[j];
                        var k = j - 1;
                        while (k >= 0 && _reduceActions[k].Symbol < key.Symbol)
                        {
                            _reduceActions[k + 1] = _reduceActions[k];
                            k--;
                        }

                        _reduceActions[k + 1] = key;
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

                // tree-sitter 0.27: the next version took the slot of the removed one, it's processed next
                if (!Compatibility.TreeSitter026)
                {
                    continue;
                }
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

    /// <summary>
    /// Adds the reductions of a lookahead to the candidate recovery actions, tree-sitter's
    /// <c>ts_parser__process_candidate_recovery_actions</c>. Returns whether the lookahead can be shifted.
    /// </summary>
    private bool AddCandidateRecoveryActions(ushort state, ushort symbol)
    {
        var actions = _actions;
        var hasShiftAction = false;
        var index = _language.TableEntry(state, symbol, out var count, out _);
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
                        var exists = false;
                        foreach (var existing in _reduceActions)
                        {
                            if (existing.Symbol == action.StateOrSymbol && existing.Count == action.ChildCount)
                            {
                                exists = true;
                                break;
                            }
                        }

                        if (!exists)
                        {
                            _reduceActions.Add((action.StateOrSymbol, action.ChildCount, action.DynamicPrecedence, action.ProductionId));
                        }
                    }

                    break;
            }
        }

        return hasShiftAction;
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
                    if (Compatibility.TreeSitter026)
                    {
                        slice.Subtrees.InsertRange(0, _arena.Children, errorData.FirstChild, errorData.ChildCount);
                    }
                    else
                    {
                        // tree-sitter 0.27 nests the children of the error in a repetition, instead of splicing them
                        // again at each recovery
                        var first = _arena.ReserveChildren(errorData.ChildCount);
                        Array.Copy(_arena.Children, _arena[errorTree].FirstChild, _arena.Children, first, _arena[errorTree].ChildCount);
                        var nested = _arena.NewNode(Language.ErrorRepeatSymbol, first, _arena[errorTree].ChildCount, 0, _stack.Position(slice.Version));
                        slice.Subtrees.Insert(0, nested);
                    }
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
            _arena.TotalBytes(lookahead) * SubtreeArena.ErrorCostPerSkippedChar +
            TotalRows(lookahead, position) * SubtreeArena.ErrorCostPerSkippedLine;
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
            errorRepeat = _arena.NewNode(Language.ErrorRepeatSymbol, first.Subtrees, 0, _stack.Position(version));
            _stack.ReturnList(first.Subtrees);
        }

        _stack.Push(version, errorRepeat, false, Language.ErrorState);
    }

    /// <summary>The rows spanned by a subtree and its padding, at a position.</summary>
    private int TotalRows(int subtree, int position)
    {
        var totalBytes = _arena.TotalBytes(subtree);
        return totalBytes <= 0 ? 0 : Row(position + totalBytes) - Row(position);
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
                        var missingTree = _arena.NewMissingLeaf((ushort)missingSymbol, _arena.TotalBytes(lookahead) + _arena[lookahead].LookaheadBytes, Compatibility.TreeSitter026 ? (ushort)0 : state);
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

        // Recovering with the current lookahead accounts for its lookahead bytes
        if (_arena[lookahead].ChildCount > 0)
        {
            lookahead = BreakdownLookahead(lookahead, Language.ErrorState);
        }

        Recover(version, lookahead);
    }

    // The parse loop

    private void Advance(int version, bool allowNodeReuse)
    {
        var language = _language;
        var actions = _actions;
        var state = _stack.State(version);
        var position = _stack.Position(version);

        var lookahead = 0;
        int entryIndex = 0, entryCount = 0;
        bool entryReusable;

        // Reuse a node of the old tree
        var didReuse = false;
        if (allowNodeReuse && _reusableCount > 0)
        {
            lookahead = ReuseNode(version, state, position);
            state = _stack.State(version);
            if (lookahead != 0)
            {
                didReuse = true;
                entryIndex = language.TableEntry(state, _arena.LeafSymbol(lookahead), out entryCount, out _);
            }
        }

        // Reuse the token returned by the lexer for another version
        if (lookahead == 0 && _cachedToken != 0 && _cachedTokenPosition == position)
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
            var reducedInPlace = false;
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
                            if (_arena[lookahead].ChildCount > 0)
                            {
                                lookahead = BreakdownLookahead(lookahead, state);
                                nextState = language.NextState(state, _arena[lookahead].Symbol);
                            }

                            Shift(version, nextState, lookahead, action.IsExtra);
                            if (didReuse)
                            {
                                ReusableAdvance();
                            }

                            return;
                        }

                    case ParseActionType.Reduce:
                        {
                            if (_stack.VersionCount == 1 && (entryCount == 1 || IsOnlyAction(entryIndex, entryCount, i)) &&
                                ReduceInPlace(version, action.StateOrSymbol, action.ChildCount, action.DynamicPrecedence, action.ProductionId, lookahead == 0, entryCount > 1))
                            {
                                reducedInPlace = true;
                                break;
                            }

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
                        if (_arena[lookahead].ChildCount > 0)
                        {
                            lookahead = BreakdownLookahead(lookahead, Language.ErrorState);
                        }

                        Recover(version, lookahead);
                        if (didReuse)
                        {
                            ReusableAdvance();
                        }

                        return;
                }
            }

            if (reducedInPlace || lastReductionVersion != VersionNone)
            {
                if (!reducedInPlace)
                {
                    _stack.RenumberVersion(lastReductionVersion, version);
                }

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

            // tree-sitter 0.27: an invalid lookahead in the error state continues the error recovery
            if (state == Language.ErrorState && !Compatibility.TreeSitter026)
            {
                Recover(version, lookahead);
                return;
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
