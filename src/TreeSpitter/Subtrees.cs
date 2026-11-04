using System.Runtime.CompilerServices;

namespace TreeSpitter;

/// <summary>A node of a syntax tree, as stored in the arena of the tree: tree-sitter's <c>SubtreeHeapData</c>.</summary>
internal struct SubtreeData
{
    public ushort Symbol;
    public ushort ParseState;
    public ushort ProductionId;
    public ushort Flags;

    /// <summary>The start of the padding (the skipped characters before the node).</summary>
    public int PaddingStart;

    /// <summary>The start of the node, after the padding.</summary>
    public int Start;

    public int End;
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
}

/// <summary>
/// The arena of the nodes of a parse: nodes are created and never freed until the next parse, node 0 is "no node".
/// Children are stored as runs of node indices in a shared array. This replaces the reference counted subtrees of
/// tree-sitter.
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

    public SubtreeArena(Language language)
    {
        _language = language;
    }

    /// <summary>The input, to compute the rows spanned by error nodes.</summary>
    public LineIndex? Lines { get; set; }

    /// <summary>Arrays returned by a disposed tree, reused by the next parse.</summary>
    private SubtreeData[]? _spareNodes;
    private int[]? _spareChildren;

    public void Reset(int expectedNodes, int expectedChildren)
    {
        Count = 1;
        ChildrenCount = 0;
        if (Nodes.Length < expectedNodes)
        {
            var spare = Interlocked.Exchange(ref _spareNodes, null);
            Nodes = spare is not null && spare.Length >= expectedNodes ? spare : AllocateUninitialized<SubtreeData>(expectedNodes);
        }

        if (Children.Length < expectedChildren)
        {
            var spare = Interlocked.Exchange(ref _spareChildren, null);
            Children = spare is not null && spare.Length >= expectedChildren ? spare : AllocateUninitialized<int>(expectedChildren);
        }

        // Node 0 is "no node"
        Nodes[0] = default;
    }

    /// <summary>Gives the arrays to a tree: the next parse uses other arrays.</summary>
    public (SubtreeData[] Nodes, int[] Children) Detach()
    {
        var result = (Nodes, Children);
        Nodes = Interlocked.Exchange(ref _spareNodes, null) ?? [];
        Children = Interlocked.Exchange(ref _spareChildren, null) ?? [];
        return result;
    }

    /// <summary>Takes back the arrays of a disposed tree.</summary>
    public void Return(SubtreeData[] nodes, int[] children)
    {
        if (_spareNodes is null || _spareNodes.Length < nodes.Length)
        {
            Volatile.Write(ref _spareNodes, nodes);
        }

        if (_spareChildren is null || _spareChildren.Length < children.Length)
        {
            Volatile.Write(ref _spareChildren, children);
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

    /// <summary>The number of rows spanned by a node, its padding excluded.</summary>
    public int Rows(int subtree)
    {
        ref var node = ref Nodes[subtree];
        return Lines is null || node.End <= node.Start ? 0 : Lines.Row(node.End) - Lines.Row(node.Start);
    }

    /// <summary>The number of rows spanned by a node, its padding included.</summary>
    public int TotalRows(int subtree)
    {
        ref var node = ref Nodes[subtree];
        return Lines is null || node.End <= node.PaddingStart ? 0 : Lines.Row(node.End) - Lines.Row(node.PaddingStart);
    }

    public int NewLeaf(ushort symbol, int paddingStart, int start, int end, ushort parseState, bool isKeyword)
    {
        var index = Allocate();
        ref var node = ref Nodes[index];
        var metadata = _language.Metadata(symbol);
        node = default;
        node.Symbol = symbol;
        node.ParseState = parseState;
        node.PaddingStart = paddingStart;
        node.Start = start;
        node.End = end;
        node.Flags = (ushort)(((metadata & Language.VisibleFlag) != 0 ? SubtreeFlags.Visible : 0) |
            ((metadata & Language.NamedFlag) != 0 ? SubtreeFlags.Named : 0) |
            (symbol == Language.EndSymbol ? SubtreeFlags.Extra : 0) |
            (isKeyword ? SubtreeFlags.IsKeyword : 0));
        return index;
    }

    public int NewError(int lookaheadChar, int paddingStart, int start, int end, ushort parseState)
    {
        var index = NewLeaf(Language.ErrorSymbol, paddingStart, start, end, parseState, false);
        ref var node = ref Nodes[index];
        node.Flags |= SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight;
        node.LookaheadChar = lookaheadChar;
        return index;
    }

    public int NewMissingLeaf(ushort symbol, int position)
    {
        var index = NewLeaf(symbol, position, position, position, 0, false);
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

    /// <summary>Creates a node from children stored in the children array, tree-sitter's <c>ts_subtree_new_node</c>.</summary>
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
        node.PaddingStart = position;
        node.Start = position;
        node.End = position;
        node.Flags = (ushort)(((metadata & Language.VisibleFlag) != 0 ? SubtreeFlags.Visible : 0) |
            ((metadata & Language.NamedFlag) != 0 ? SubtreeFlags.Named : 0) |
            (fragile ? SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight : 0));
        SummarizeChildren(index);
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
    public void SummarizeChildren(int index)
    {
        ref var self = ref Nodes[index];
        self.NamedChildCount = 0;
        self.VisibleChildCount = 0;
        self.ErrorCost = 0;
        self.VisibleDescendantCount = 0;
        self.DynamicPrecedence = 0;

        var structuralIndex = 0;
        var language = _language;
        var aliasBase = self.ProductionId > 0 ? self.ProductionId * language.MaxAliasSequenceLength : -1;
        var aliasSequences = language.AliasSequences;
        var isError = self.Symbol is Language.ErrorSymbol or Language.ErrorRepeatSymbol;
        var children = Children;
        var nodes = Nodes;
        var count = self.ChildCount;
        var first = self.FirstChild;

        for (var i = 0; i < count; i++)
        {
            ref var child = ref nodes[children[first + i]];
            if (i == 0)
            {
                self.PaddingStart = child.PaddingStart;
                self.Start = child.Start;
            }

            self.End = child.End;

            var childIsMissing = (child.Flags & SubtreeFlags.IsMissing) != 0;
            if (child.Symbol != Language.ErrorRepeatSymbol)
            {
                self.ErrorCost += childIsMissing ? ErrorCostPerMissingTree + ErrorCostPerRecovery : child.ErrorCost;
            }

            var childIsExtra = (child.Flags & SubtreeFlags.Extra) != 0;
            var grandchildCount = child.ChildCount;
            if (isError)
            {
                if (!childIsExtra && !(child.Symbol == Language.ErrorSymbol && grandchildCount == 0))
                {
                    if ((child.Flags & SubtreeFlags.Visible) != 0)
                    {
                        self.ErrorCost += ErrorCostPerSkippedTree;
                    }
                    else if (grandchildCount > 0)
                    {
                        self.ErrorCost += ErrorCostPerSkippedTree * child.VisibleChildCount;
                    }
                }
            }

            self.DynamicPrecedence += child.DynamicPrecedence;
            self.VisibleDescendantCount += child.VisibleDescendantCount;

            ushort alias = 0;
            if (!childIsExtra && child.Symbol != 0 && aliasBase >= 0 && structuralIndex < language.MaxAliasSequenceLength)
            {
                alias = aliasSequences[aliasBase + structuralIndex];
            }

            if (alias != 0)
            {
                self.VisibleDescendantCount++;
                self.VisibleChildCount++;
                if ((language.Metadata(alias) & Language.NamedFlag) != 0)
                {
                    self.NamedChildCount++;
                }
            }
            else if ((child.Flags & SubtreeFlags.Visible) != 0)
            {
                self.VisibleDescendantCount++;
                self.VisibleChildCount++;
                if ((child.Flags & SubtreeFlags.Named) != 0)
                {
                    self.NamedChildCount++;
                }
            }
            else if (grandchildCount > 0)
            {
                self.VisibleChildCount += child.VisibleChildCount;
                self.NamedChildCount += child.NamedChildCount;
            }

            if ((child.Flags & SubtreeFlags.HasExternalTokens) != 0)
            {
                self.Flags |= SubtreeFlags.HasExternalTokens;
            }

            if (child.Symbol == Language.ErrorSymbol)
            {
                self.Flags |= SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight;
                self.ParseState = StateNone;
            }

            if (!childIsExtra)
            {
                structuralIndex++;
            }
        }

        if (isError)
        {
            self.ErrorCost += ErrorCostPerRecovery + ErrorCostPerSkippedChar * (self.End - self.Start) + ErrorCostPerSkippedLine * Rows(index);
        }

        if (count > 0)
        {
            var firstChild = children[first];
            var lastChild = children[first + count - 1];
            self.FirstLeafSymbol = LeafSymbol(firstChild);
            self.FirstLeafParseState = LeafParseState(firstChild);
            if ((nodes[firstChild].Flags & SubtreeFlags.FragileLeft) != 0)
            {
                self.Flags |= SubtreeFlags.FragileLeft;
            }

            if ((nodes[lastChild].Flags & SubtreeFlags.FragileRight) != 0)
            {
                self.Flags |= SubtreeFlags.FragileRight;
            }
        }
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
            if (l.Symbol < r.Symbol)
            {
                _compareStack = stack;
                return -1;
            }

            if (r.Symbol < l.Symbol)
            {
                _compareStack = stack;
                return 1;
            }

            if (l.ChildCount < r.ChildCount)
            {
                _compareStack = stack;
                return -1;
            }

            if (r.ChildCount < l.ChildCount)
            {
                _compareStack = stack;
                return 1;
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
