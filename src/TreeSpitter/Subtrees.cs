using System.Runtime.CompilerServices;

namespace TreeSpitter;

/// <summary>
/// A node of a syntax tree, as stored in the arena: tree-sitter's <c>SubtreeHeapData</c>. Positions are relative, like
/// in tree-sitter: the padding (the skipped characters before the node) and the size, so that the subtrees can be
/// shared by the trees of successive incremental parses.
/// </summary>
internal struct SubtreeData
{
    public ushort Symbol;
    public ushort ParseState;
    public ushort ProductionId;
    public ushort Flags;
    public int Padding;
    public int Size;

    /// <summary>How far after its end the lexer read to lex the node: edits in this range invalidate it.</summary>
    public int LookaheadBytes;

    public int FirstChild;
    public int ChildCount;
    public int VisibleChildCount;
    public int NamedChildCount;
    public int VisibleDescendantCount;
    public int ErrorCost;
    public int DynamicPrecedence;

    /// <summary>The first code point of an error leaf.</summary>
    public int LookaheadChar;

    public ushort FirstLeafSymbol;
    public ushort FirstLeafParseState;

    /// <summary>The depth of the binary trees of the repetitions, used to balance them.</summary>
    public int RepeatDepth;
}

/// <summary>The flags of <see cref="SubtreeData"/>.</summary>
internal static class SubtreeFlags
{
    public const ushort Visible = 1;
    public const ushort Named = 2;
    public const ushort Extra = 4;
    public const ushort FragileLeft = 8;
    public const ushort FragileRight = 16;
    public const ushort IsKeyword = 32;
    public const ushort IsMissing = 64;
    public const ushort HasExternalTokens = 128;
    public const ushort HasChanges = 256;

    /// <summary>The subtree contains an unbalanced repetition: <see cref="SubtreeArena.Balance"/> must visit it.</summary>
    public const ushort NeedsBalancing = 512;
}

/// <summary>
/// The arena of the nodes: node 0 is "no node", nodes are structs and their children are runs of node indices in one
/// array. This replaces the reference counted subtrees of tree-sitter. Nodes are never modified once a tree is built:
/// an incremental parse appends its nodes to the arena of the old tree and shares the reused subtrees, an edit copies
/// the nodes it changes. The arena is shared by the trees of a lineage, and goes back to its parser when they are all
/// disposed.
/// </summary>
internal sealed class SubtreeArena
{
    public const ushort StateNone = ushort.MaxValue;

    public const int ErrorCostPerRecovery = 500;
    public const int ErrorCostPerMissingTree = 110;
    public const int ErrorCostPerSkippedTree = 100;
    public const int ErrorCostPerSkippedLine = 30;
    public const int ErrorCostPerSkippedChar = 1;

    public SubtreeData[] Nodes = new SubtreeData[1024];
    public int Count = 1;
    public int[] Children = new int[1024];
    public int ChildrenCount;

    private readonly Language _language;
    private int[] _compareStack = new int[64];
    private readonly Stack<EditEntry> _editStack = new();
    private readonly List<(int Node, int Position)> _balanceStack = [];
    private readonly List<int> _compressStack = [];
    private int _references;

    public SubtreeArena(Language language, Action<SubtreeArena>? release)
    {
        _language = language;
        Release = release;
    }

    /// <summary>Called when the last tree of the arena is disposed.</summary>
    public Action<SubtreeArena>? Release { get; }

    private byte[]? _input;
    private int _inputLength;
    private LineIndex? _lines;

    /// <summary>
    /// The number of nodes of the newest tree when it was created from scratch or compacted: when the arena is much
    /// bigger, it's mostly made of the nodes of older trees.
    /// </summary>
    public int LiveCount;

    /// <summary>Sets the input of the current parse, to compute the rows spanned by error nodes.</summary>
    public void SetInput(byte[]? input, int length)
    {
        _input = input;
        _inputLength = length;
        _lines = null;
    }

    /// <summary>The lines of the input of the current parse, computed when needed.</summary>
    public LineIndex Lines => _lines ??= new LineIndex(_input ?? [], _inputLength);

    /// <summary>Prepares the arena for a parse from scratch.</summary>
    public void Reset(int expectedNodes, int expectedChildren)
    {
        Count = 1;
        ChildrenCount = 0;
        if (Nodes.Length < expectedNodes)
        {
            Nodes = AllocateUninitialized<SubtreeData>(expectedNodes);
        }

        if (Children.Length < expectedChildren)
        {
            Children = AllocateUninitialized<int>(expectedChildren);
        }

        Nodes[0] = default;
    }

    /// <summary>Counts a tree using the arena.</summary>
    public void AddReference() => Interlocked.Increment(ref _references);

    /// <summary>Releases a tree, and the arena when it was the last one.</summary>
    public void RemoveReference()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            Release?.Invoke(this);
        }
    }

    private static T[] AllocateUninitialized<T>(int length)
    {
#if NET
        return GC.AllocateUninitializedArray<T>(length);
#else
        return new T[length];
#endif
    }

    public ref SubtreeData this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Nodes[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Allocate()
    {
        if (Count == Nodes.Length)
        {
            Array.Resize(ref Nodes, Nodes.Length * 2);
        }

        return Count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsExtra(int subtree) => (Nodes[subtree].Flags & SubtreeFlags.Extra) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsVisible(int subtree) => (Nodes[subtree].Flags & SubtreeFlags.Visible) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsMissing(int subtree) => (Nodes[subtree].Flags & SubtreeFlags.IsMissing) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int TotalBytes(int subtree) => Nodes[subtree].Padding + Nodes[subtree].Size;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ErrorCost(int subtree)
    {
        ref var node = ref Nodes[subtree];
        return (node.Flags & SubtreeFlags.IsMissing) != 0 ? ErrorCostPerMissingTree + ErrorCostPerRecovery : node.ErrorCost;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort LeafSymbol(int subtree)
    {
        ref var node = ref Nodes[subtree];
        return node.ChildCount > 0 ? node.FirstLeafSymbol : node.Symbol;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort LeafParseState(int subtree)
    {
        ref var node = ref Nodes[subtree];
        return node.ChildCount > 0 ? node.FirstLeafParseState : node.ParseState;
    }

    /// <summary>tree-sitter's <c>ts_subtree_is_fragile</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsFragile(int subtree) => (Nodes[subtree].Flags & (SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight)) != 0;

    /// <summary>The rows spanned by the content of a node at a position (the start of its padding).</summary>
    private int Rows(int paddingStart, int padding, int size)
    {
        if (size <= 0)
        {
            return 0;
        }

        var start = paddingStart + padding;
        var lines = Lines;
        return lines.Row(start + size) - lines.Row(start);
    }

    public int NewLeaf(ushort symbol, int padding, int size, int lookaheadBytes, ushort parseState, bool isKeyword)
    {
        var index = Allocate();
        ref var node = ref Nodes[index];
        var metadata = _language.Metadata(symbol);
        node = default;
        node.Symbol = symbol;
        node.ParseState = parseState;
        node.Padding = padding;
        node.Size = size;
        node.LookaheadBytes = lookaheadBytes;
        node.Flags = (ushort)(((metadata & Language.VisibleFlag) != 0 ? SubtreeFlags.Visible : 0) |
            ((metadata & Language.NamedFlag) != 0 ? SubtreeFlags.Named : 0) |
            (symbol == Language.EndSymbol ? SubtreeFlags.Extra : 0) |
            (isKeyword ? SubtreeFlags.IsKeyword : 0));
        return index;
    }

    public int NewError(int lookaheadChar, int padding, int size, int lookaheadBytes, ushort parseState)
    {
        var index = NewLeaf(Language.ErrorSymbol, padding, size, lookaheadBytes, parseState, false);
        ref var node = ref Nodes[index];
        node.Flags |= SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight;
        node.LookaheadChar = lookaheadChar;
        return index;
    }

    public int NewMissingLeaf(ushort symbol, int lookaheadBytes, ushort parseState)
    {
        var index = NewLeaf(symbol, 0, 0, lookaheadBytes, parseState, false);
        Nodes[index].Flags |= SubtreeFlags.IsMissing;
        return index;
    }

    /// <summary>A copy of a node with another value of the extra flag, tree-sitter's make_mut + set_extra.</summary>
    public int WithExtra(int subtree, bool extra)
    {
        var index = Allocate();
        Nodes[index] = Nodes[subtree];
        if (extra)
        {
            Nodes[index].Flags |= SubtreeFlags.Extra;
        }
        else
        {
            Nodes[index].Flags &= unchecked((ushort)~SubtreeFlags.Extra);
        }

        return index;
    }

    /// <summary>A copy of a leaf with another symbol, tree-sitter's make_mut + set_symbol.</summary>
    public int WithSymbol(int subtree, ushort symbol)
    {
        var index = Allocate();
        ref var node = ref Nodes[index];
        node = Nodes[subtree];
        node.Symbol = symbol;
        var metadata = _language.Metadata(symbol);
        node.Flags = (ushort)((node.Flags & ~(SubtreeFlags.Visible | SubtreeFlags.Named)) |
            ((metadata & Language.VisibleFlag) != 0 ? SubtreeFlags.Visible : 0) |
            ((metadata & Language.NamedFlag) != 0 ? SubtreeFlags.Named : 0));
        return index;
    }

    /// <summary>Reserves room for children, returns the index of the first one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReserveChildren(int count)
    {
        if (ChildrenCount + count > Children.Length)
        {
            Array.Resize(ref Children, Math.Max(Children.Length * 2, ChildrenCount + count));
        }

        var start = ChildrenCount;
        ChildrenCount += count;
        return start;
    }

    /// <summary>
    /// Creates a node from children stored in the children array, tree-sitter's <c>ts_subtree_new_node</c>. The
    /// position of the node (the start of its padding) is used for the rows spanned by error nodes.
    /// </summary>
    public int NewNode(ushort symbol, int firstChild, int childCount, ushort productionId, int position)
    {
        var index = Allocate();
        ref var node = ref Nodes[index];
        var metadata = _language.Metadata(symbol);
        var fragile = symbol is Language.ErrorSymbol or Language.ErrorRepeatSymbol;
        node = default;
        node.Symbol = symbol;
        node.ProductionId = productionId;
        node.FirstChild = firstChild;
        node.ChildCount = childCount;
        node.Flags = (ushort)(((metadata & Language.VisibleFlag) != 0 ? SubtreeFlags.Visible : 0) |
            ((metadata & Language.NamedFlag) != 0 ? SubtreeFlags.Named : 0) |
            (fragile ? SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight : 0));
        SummarizeChildren(index, position);
        return index;
    }

    /// <summary>Creates a node from a list of children, which is copied.</summary>
    public int NewNode(ushort symbol, SubtreeList children, ushort productionId, int position)
    {
        var first = ReserveChildren(children.Count);
        Array.Copy(children.Items, 0, Children, first, children.Count);
        return NewNode(symbol, first, children.Count, productionId, position);
    }

    public int NewErrorNode(SubtreeList children, bool extra, int position)
    {
        var index = NewNode(Language.ErrorSymbol, children, 0, position);
        if (extra)
        {
            Nodes[index].Flags |= SubtreeFlags.Extra;
        }

        return index;
    }

    /// <summary>tree-sitter's <c>ts_subtree_summarize_children</c>.</summary>
    public void SummarizeChildren(int index, int position)
    {
        // The summary is accumulated in locals: the node and its children are in the same array, the JIT wouldn't keep
        // the fields of the node in registers
        var nodes = Nodes;
        ref var self = ref nodes[index];
        var symbol = self.Symbol;
        var flags = (ushort)(self.Flags & ~(SubtreeFlags.HasExternalTokens | SubtreeFlags.NeedsBalancing));
        var parseState = self.ParseState;
        var language = _language;
        var maxAliasSequenceLength = language.MaxAliasSequenceLength;
        var aliasBase = self.ProductionId > 0 ? self.ProductionId * maxAliasSequenceLength : -1;
        var aliasSequences = language.AliasSequences;
        var isError = symbol is Language.ErrorSymbol or Language.ErrorRepeatSymbol;
        var children = Children;
        var count = self.ChildCount;
        var first = self.FirstChild;
        var structuralIndex = 0;
        var padding = 0;
        var size = 0;
        var lookaheadEnd = 0;
        var errorCost = 0;
        var dynamicPrecedence = 0;
        var visibleDescendantCount = 0;
        var visibleChildCount = 0;
        var namedChildCount = 0;

        for (var i = 0; i < count; i++)
        {
            ref var child = ref nodes[children[first + i]];
            var childFlags = child.Flags;
            var childSymbol = child.Symbol;
            if (i == 0)
            {
                padding = child.Padding;
                size = child.Size;
            }
            else
            {
                size += child.Padding + child.Size;
            }

            var childLookaheadEnd = padding + size + child.LookaheadBytes;
            if (childLookaheadEnd > lookaheadEnd)
            {
                lookaheadEnd = childLookaheadEnd;
            }

            var childIsExtra = (childFlags & SubtreeFlags.Extra) != 0;
            var grandchildCount = child.ChildCount;
            if (childSymbol == Language.ErrorRepeatSymbol && !Compatibility.TreeSitter026)
            {
                // tree-sitter 0.27 refunds the extent cost of an error repetition, which the node charges again for its
                // own extent: grouping errors doesn't change their cost. The start of the padding of the child is
                // computed here, the sizes already include the child.
                var childPosition = i == 0 ? position : position + padding + size - child.Padding - child.Size;
                errorCost += child.ErrorCost -
                    (ErrorCostPerRecovery + ErrorCostPerSkippedChar * child.Size + ErrorCostPerSkippedLine * Rows(childPosition, child.Padding, child.Size));
            }
            else
            {
                if (childSymbol != Language.ErrorRepeatSymbol)
                {
                    errorCost += (childFlags & SubtreeFlags.IsMissing) != 0 ? ErrorCostPerMissingTree + ErrorCostPerRecovery : child.ErrorCost;
                }

                if (isError && !childIsExtra && !(childSymbol == Language.ErrorSymbol && grandchildCount == 0))
                {
                    if ((childFlags & SubtreeFlags.Visible) != 0)
                    {
                        errorCost += ErrorCostPerSkippedTree;
                    }
                    else if (grandchildCount > 0)
                    {
                        errorCost += ErrorCostPerSkippedTree * child.VisibleChildCount;
                    }
                }
            }

            dynamicPrecedence += child.DynamicPrecedence;
            visibleDescendantCount += child.VisibleDescendantCount;

            ushort alias = 0;
            if (aliasBase >= 0 && !childIsExtra && childSymbol != 0 && structuralIndex < maxAliasSequenceLength)
            {
                alias = aliasSequences[aliasBase + structuralIndex];
            }

            if (alias != 0)
            {
                visibleDescendantCount++;
                visibleChildCount++;
                if ((language.Metadata(alias) & Language.NamedFlag) != 0)
                {
                    namedChildCount++;
                }
            }
            else if ((childFlags & SubtreeFlags.Visible) != 0)
            {
                visibleDescendantCount++;
                visibleChildCount++;
                if ((childFlags & SubtreeFlags.Named) != 0)
                {
                    namedChildCount++;
                }
            }
            else if (grandchildCount > 0)
            {
                visibleChildCount += child.VisibleChildCount;
                namedChildCount += child.NamedChildCount;
            }

            flags |= (ushort)(childFlags & (SubtreeFlags.HasExternalTokens | SubtreeFlags.NeedsBalancing));
            if (childSymbol == Language.ErrorSymbol)
            {
                flags |= SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight;
                parseState = StateNone;
            }

            if (!childIsExtra)
            {
                structuralIndex++;
            }
        }

        if (isError)
        {
            errorCost += ErrorCostPerRecovery + ErrorCostPerSkippedChar * size + ErrorCostPerSkippedLine * Rows(position, padding, size);
        }

        var repeatDepth = 0;
        if (count > 0)
        {
            var firstChild = children[first];
            ref var firstData = ref nodes[firstChild];
            ref var lastData = ref nodes[children[first + count - 1]];
            self.FirstLeafSymbol = firstData.ChildCount > 0 ? firstData.FirstLeafSymbol : firstData.Symbol;
            self.FirstLeafParseState = firstData.ChildCount > 0 ? firstData.FirstLeafParseState : firstData.ParseState;
            flags |= (ushort)((firstData.Flags & SubtreeFlags.FragileLeft) | (lastData.Flags & SubtreeFlags.FragileRight));
            if (count >= 2 && (flags & (SubtreeFlags.Visible | SubtreeFlags.Named)) == 0 && firstData.Symbol == symbol)
            {
                repeatDepth = Math.Max(firstData.RepeatDepth, lastData.RepeatDepth) + 1;

                // Balancing compresses the repetitions whose first child is deeper than the last one by at least 2
                if (firstData.RepeatDepth - lastData.RepeatDepth >= 2)
                {
                    flags |= SubtreeFlags.NeedsBalancing;
                }
            }
        }

        self.Flags = flags;
        self.ParseState = parseState;
        self.Padding = padding;
        self.Size = size;
        self.LookaheadBytes = lookaheadEnd - size - padding;
        self.ErrorCost = errorCost;
        self.DynamicPrecedence = dynamicPrecedence;
        self.VisibleDescendantCount = visibleDescendantCount;
        self.VisibleChildCount = visibleChildCount;
        self.NamedChildCount = namedChildCount;
        self.RepeatDepth = repeatDepth;
    }

    /// <summary>The number of visible nodes in a subtree, counted in the node count of the stack.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NodeCount(int subtree)
    {
        ref var node = ref Nodes[subtree];
        var count = node.VisibleDescendantCount;
        if ((node.Flags & SubtreeFlags.Visible) != 0)
        {
            count++;
        }

        if (node.Symbol == Language.ErrorRepeatSymbol)
        {
            count++;
        }

        return count;
    }

    /// <summary>tree-sitter's <c>ts_subtree_compare</c>.</summary>
    public int Compare(int left, int right)
    {
        var stack = _compareStack;
        var size = 0;
        Push(ref stack, ref size, left);
        Push(ref stack, ref size, right);
        while (size > 0)
        {
            right = stack[--size];
            left = stack[--size];
            ref var l = ref Nodes[left];
            ref var r = ref Nodes[right];
            var result = l.Symbol < r.Symbol ? -1 : r.Symbol < l.Symbol ? 1 : l.ChildCount < r.ChildCount ? -1 : r.ChildCount < l.ChildCount ? 1 : 0;
            if (result != 0)
            {
                _compareStack = stack;
                return result;
            }

            for (var i = l.ChildCount; i > 0; i--)
            {
                Push(ref stack, ref size, Children[l.FirstChild + i - 1]);
                Push(ref stack, ref size, Children[r.FirstChild + i - 1]);
            }
        }

        _compareStack = stack;
        return 0;

        static void Push(ref int[] stack, ref int size, int value)
        {
            if (size == stack.Length)
            {
                Array.Resize(ref stack, stack.Length * 2);
            }

            stack[size++] = value;
        }
    }

    private struct EditEntry
    {
        /// <summary>The index of the slot of the node in the children array, or -1 for the root.</summary>
        public int Slot;
        public int Tree;
        public int Start;
        public int OldEnd;
        public int NewEnd;
    }

    /// <summary>
    /// Applies an edit to a tree, tree-sitter's <c>ts_subtree_edit</c>: the nodes touching the edit are copied with their
    /// new padding and size and flagged as changed, so that the next parse doesn't reuse them. Returns the new root.
    /// </summary>
    public int Edit(int root, int start, int oldEnd, int newEnd)
    {
        var stack = _editStack;
        stack.Clear();
        stack.Push(new EditEntry { Slot = -1, Tree = root, Start = start, OldEnd = oldEnd, NewEnd = newEnd });
        var result = root;
        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            var editStart = entry.Start;
            var editOldEnd = entry.OldEnd;
            var editNewEnd = entry.NewEnd;
            var isNoop = editOldEnd == editStart && editNewEnd == editStart;
            var isPureInsertion = editOldEnd == editStart;

            ref var data = ref Nodes[entry.Tree];
            var size = data.Size;
            var padding = data.Padding;
            var totalSize = padding + size;
            var endByte = totalSize + data.LookaheadBytes;
            if (editStart > endByte || (isNoop && editStart == endByte))
            {
                continue;
            }

            if (editOldEnd <= padding)
            {
                // The edit is in the space before the node: shift it
                padding = editNewEnd + (padding - editOldEnd);
            }
            else if (editStart < padding)
            {
                // The edit starts before the node and extends into it: shrink its content
                size = Math.Max(0, size - (editOldEnd - padding));
                padding = editNewEnd;
            }
            else if (editStart < totalSize || (editStart == totalSize && isPureInsertion))
            {
                // The edit is within the node: resize it
                size = editNewEnd - padding + Math.Max(0, totalSize - editOldEnd);
            }

            // A copy: the other trees sharing the node keep it unchanged
            var copy = Allocate();
            ref var copied = ref Nodes[copy];
            copied = Nodes[entry.Tree];
            copied.Padding = padding;
            copied.Size = size;
            copied.Flags |= SubtreeFlags.HasChanges;
            var childCount = copied.ChildCount;
            if (childCount > 0)
            {
                var firstChild = ReserveChildren(childCount);
                Array.Copy(Children, Nodes[copy].FirstChild, Children, firstChild, childCount);
                Nodes[copy].FirstChild = firstChild;
            }

            if (entry.Slot < 0)
            {
                result = copy;
            }
            else
            {
                Children[entry.Slot] = copy;
            }

            var first = Nodes[copy].FirstChild;
            var childRight = 0;
            for (var i = 0; i < childCount; i++)
            {
                var child = Children[first + i];
                var childSize = Nodes[child].Padding + Nodes[child].Size;
                var childLeft = childRight;
                childRight = childLeft + childSize;

                // The children ending before the edit are not affected
                if (childRight + Nodes[child].LookaheadBytes < editStart)
                {
                    continue;
                }

                // The children after the edit are not affected
                if (childLeft > editOldEnd || (childLeft == editOldEnd && childSize > 0 && i > 0))
                {
                    break;
                }

                var childEdit = new EditEntry
                {
                    Slot = first + i,
                    Tree = child,
                    Start = Math.Max(0, editStart - childLeft),
                    OldEnd = Math.Max(0, editOldEnd - childLeft),
                    NewEnd = Math.Max(0, editNewEnd - childLeft),
                };

                // The inserted text goes to the first child touching the edit, the next ones only shrink
                if (childRight > editStart || (childRight == editStart && isPureInsertion))
                {
                    editNewEnd = editStart;
                }
                else
                {
                    childEdit.OldEnd = childEdit.Start;
                    childEdit.NewEnd = childEdit.Start;
                }

                stack.Push(childEdit);
            }
        }

        return result;
    }

    /// <summary>Copies a tree to another arena, returns its new root.</summary>
    public int CopyTree(int root, SubtreeArena target)
    {
        var stack = new Stack<(int Source, int Slot)>();
        stack.Push((root, -1));
        var result = 0;
        while (stack.Count > 0)
        {
            var (source, slot) = stack.Pop();
            var copy = target.Allocate();
            ref var data = ref target.Nodes[copy];
            data = Nodes[source];
            var count = data.ChildCount;
            if (count > 0)
            {
                var sourceFirst = data.FirstChild;
                var first = target.ReserveChildren(count);
                target.Nodes[copy].FirstChild = first;
                for (var i = count - 1; i >= 0; i--)
                {
                    stack.Push((Children[sourceFirst + i], first + i));
                }
            }

            if (slot < 0)
            {
                result = copy;
            }
            else
            {
                target.Children[slot] = copy;
            }
        }

        return result;
    }

    /// <summary>
    /// Balances the binary trees of the repetitions built by a parse, tree-sitter's <c>ts_parser__balance_subtree</c>
    /// and <c>ts_subtree_compress</c>, and returns the new root. Only the nodes created by the parse (from
    /// <paramref name="firstOwned"/>) are balanced, like tree-sitter balances the nodes whose reference count is 1. The
    /// balancing copies the nodes it changes, so that the trees sharing them aren't changed: trees are balanced when they
    /// are first edited or reused, a tree which isn't doesn't pay for it. Unlike tree-sitter, which visits all the nodes,
    /// only the subtrees containing unbalanced repetitions are visited.
    /// </summary>
    public int Balance(int root, int firstOwned)
    {
        if ((Nodes[root].Flags & SubtreeFlags.NeedsBalancing) == 0 || root < firstOwned)
        {
            return root;
        }

        // The nodes from firstCopy are the copies, which can be changed
        var firstCopy = Count;
        var stack = _balanceStack;
        var compressStack = _compressStack;
        stack.Clear();
        compressStack.Clear();
        root = CopyNode(root);

        // The nodes and the starts of their padding, for the rows of error repetitions
        stack.Add((root, 0));
        while (stack.Count > 0)
        {
            var (tree, position) = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (Nodes[tree].RepeatDepth > 0)
            {
                var child1 = Children[Nodes[tree].FirstChild];
                var child2 = Children[Nodes[tree].FirstChild + Nodes[tree].ChildCount - 1];
                var repeatDelta = Nodes[child1].RepeatDepth - Nodes[child2].RepeatDepth;
                if (repeatDelta > 0)
                {
                    var n = repeatDelta;
                    for (var i = n / 2; i > 0; i /= 2)
                    {
                        Compress(tree, i, firstOwned, firstCopy, position, compressStack);
                        n -= i;
                    }
                }
            }

            var count = Nodes[tree].ChildCount;
            var first = Nodes[tree].FirstChild;
            var childPosition = position;
            for (var i = 0; i < count; i++)
            {
                var child = Children[first + i];
                var childBytes = TotalBytes(child);
                if ((Nodes[child].Flags & SubtreeFlags.NeedsBalancing) != 0 && child >= firstOwned)
                {
                    if (child < firstCopy)
                    {
                        child = CopyNode(child);
                        Children[first + i] = child;
                    }

                    stack.Add((child, childPosition));
                }

                childPosition += childBytes;
            }
        }

        return root;
    }

    /// <summary>A copy of a node, with a copy of its children.</summary>
    private int CopyNode(int node)
    {
        var copy = Allocate();
        Nodes[copy] = Nodes[node];
        var count = Nodes[copy].ChildCount;
        if (count > 0)
        {
            var first = ReserveChildren(count);
            Array.Copy(Children, Nodes[node].FirstChild, Children, first, count);
            Nodes[copy].FirstChild = first;
        }

        return copy;
    }

    /// <summary>tree-sitter's <c>ts_subtree_compress</c>: the nodes of the left spine of a repetition all start at <paramref name="position"/>.</summary>
    private void Compress(int tree, int count, int firstOwned, int firstCopy, int position, List<int> stack)
    {
        var initialSize = stack.Count;
        var symbol = Nodes[tree].Symbol;
        for (var i = 0; i < count; i++)
        {
            // The tree is a copy
            if (Nodes[tree].ChildCount < 2)
            {
                break;
            }

            var childSlot = Nodes[tree].FirstChild;
            var child = Children[childSlot];
            if (Nodes[child].ChildCount < 2 || child < firstOwned || Nodes[child].Symbol != symbol)
            {
                break;
            }

            var grandchild = Children[Nodes[child].FirstChild];
            if (Nodes[grandchild].ChildCount < 2 || grandchild < firstOwned || Nodes[grandchild].Symbol != symbol)
            {
                break;
            }

            if (child < firstCopy)
            {
                child = CopyNode(child);
                Children[childSlot] = child;
            }

            var grandchildSlot = Nodes[child].FirstChild;
            if (grandchild < firstCopy)
            {
                grandchild = CopyNode(grandchild);
                Children[grandchildSlot] = grandchild;
            }

            var grandchildLast = Nodes[grandchild].FirstChild + Nodes[grandchild].ChildCount - 1;
            Children[childSlot] = grandchild;
            Children[grandchildSlot] = Children[grandchildLast];
            Children[grandchildLast] = child;
            stack.Add(tree);
            tree = grandchild;
        }

        while (stack.Count > initialSize)
        {
            tree = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            var child = Children[Nodes[tree].FirstChild];
            var last = Nodes[child].FirstChild + Nodes[child].ChildCount - 1;
            var grandchild = Children[last];
            var grandchildPosition = position;
            for (var i = Nodes[child].FirstChild; i < last; i++)
            {
                grandchildPosition += TotalBytes(Children[i]);
            }

            SummarizeChildren(grandchild, grandchildPosition);
            SummarizeChildren(child, position);
            SummarizeChildren(tree, position);
        }
    }
}

/// <summary>A growable list of node indices, tree-sitter's <c>SubtreeArray</c>.</summary>
internal sealed class SubtreeList
{
    public int[] Items = new int[16];
    public int Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int subtree)
    {
        if (Count == Items.Length)
        {
            Array.Resize(ref Items, Items.Length * 2);
        }

        Items[Count++] = subtree;
    }

    public void Clear() => Count = 0;

    public void Reverse() => Array.Reverse(Items, 0, Count);

    public void CopyFrom(SubtreeList other)
    {
        if (Items.Length < other.Count)
        {
            Items = new int[other.Items.Length];
        }

        Array.Copy(other.Items, Items, other.Count);
        Count = other.Count;
    }

    public void Insert(int index, int subtree)
    {
        if (Count == Items.Length)
        {
            Array.Resize(ref Items, Items.Length * 2);
        }

        Array.Copy(Items, index, Items, index + 1, Count - index);
        Items[index] = subtree;
        Count++;
    }

    public void InsertRange(int index, int[] source, int sourceIndex, int count)
    {
        if (Count + count > Items.Length)
        {
            Array.Resize(ref Items, Math.Max(Items.Length * 2, Count + count));
        }

        Array.Copy(Items, index, Items, index + count, Count - index);
        Array.Copy(source, sourceIndex, Items, index, count);
        Count += count;
    }

    public void RemoveAt(int index)
    {
        Array.Copy(Items, index + 1, Items, index, Count - index - 1);
        Count--;
    }

    /// <summary>Moves the trailing extras to <paramref name="destination"/>, tree-sitter's <c>remove_trailing_extras</c>.</summary>
    public void RemoveTrailingExtras(SubtreeArena arena, SubtreeList destination)
    {
        destination.Clear();
        while (Count > 0)
        {
            var last = Items[Count - 1];
            if (arena.IsExtra(last))
            {
                Count--;
                destination.Add(last);
            }
            else
            {
                break;
            }
        }

        destination.Reverse();
    }
}

/// <summary>The offsets of the line starts of a text, to compute rows and columns.</summary>
internal sealed class LineIndex
{
    private readonly int[] _lineStarts;

    public LineIndex(byte[] input, int length)
    {
        var starts = new List<int> { 0 };
        var span = new ReadOnlySpan<byte>(input, 0, length);
        var offset = 0;
        while (true)
        {
            var index = span.Slice(offset).IndexOf((byte)'\n');
            if (index < 0)
            {
                break;
            }

            offset += index + 1;
            starts.Add(offset);
        }

        _lineStarts = [.. starts];
    }

    public int Row(int position)
    {
        var index = Array.BinarySearch(_lineStarts, position);
        return index >= 0 ? index : ~index - 1;
    }

    public int Column(int position) => position - _lineStarts[Row(position)];
}
