using System.Runtime.CompilerServices;

namespace TreeSpitter;

internal struct StackLink
{
    public int Node;
    public int Subtree;
    public bool IsPending;
}

internal struct StackNode
{
    public ushort State;
    public int Position;
    public int ErrorCost;
    public int NodeCount;
    public int DynamicPrecedence;
    public int LinkCount;
    public StackLink Link0;

    /// <summary>The index of the block of the links 1 to 7, or -1.</summary>
    public int MoreLinks;
}

internal enum StackStatus : byte
{
    Active,
    Paused,
    Halted,
}

internal struct StackSummaryEntry
{
    public int Position;
    public int Depth;
    public ushort State;
}

internal struct StackHead
{
    public int Node;
    public List<StackSummaryEntry>? Summary;
    public int NodeCountAtLastError;
    public int LastExternalToken;
    public int LookaheadWhenPaused;
    public StackStatus Status;
}

internal struct StackSlice
{
    public SubtreeList Subtrees;
    public int Version;
}

/// <summary>
/// The graph-structured stack of the GLR parser: a port of tree-sitter's <c>stack.c</c>. Stack nodes are allocated in an
/// arena that is reset for each parse, instead of being reference counted. Versions (heads) are the tips of the stacks
/// being explored.
/// </summary>
internal sealed class ParseStack
{
    public const int MaxLinkCount = 8;
    private const int MaxIteratorCount = 64;

    private struct StackIterator
    {
        public int Node;
        public SubtreeList? Subtrees;
        public int SubtreeCount;
        public bool IsPending;
    }

    private enum IterationKind
    {
        PopCount,
        PopPending,
        PopError,
        PopAll,
        Summarize,
    }

    private SubtreeArena _arena;
    private StackNode[] _nodes = new StackNode[1024];
    private int _nodeCount;
    private StackLink[] _moreLinks = new StackLink[7 * 16];
    private int _moreLinkBlocks;
    private StackHead[] _heads = new StackHead[8];
    private int _headCount;
    private readonly List<StackSlice> _slices = [];
    private StackIterator[] _iterators = new StackIterator[8];
    private int _iteratorCount;
    private readonly Stack<SubtreeList> _listPool = new();
    private int _baseNode;

    public ParseStack(SubtreeArena arena)
    {
        _arena = arena;
    }

    public int VersionCount => _headCount;

    public List<StackSlice> Slices => _slices;

    public void Reset(SubtreeArena arena)
    {
        _arena = arena;
        _nodeCount = 1;
        _moreLinkBlocks = 0;
        _baseNode = NewNode(0, 0, false, 1);
        Clear();
    }

    public void Clear()
    {
        for (var i = 0; i < _headCount; i++)
        {
            ReleaseSummary(ref _heads[i]);
        }

        _headCount = 0;
        AddHead(new StackHead { Node = _baseNode, Status = StackStatus.Active });
    }

    public SubtreeList RentList()
    {
        if (_listPool.Count > 0)
        {
            var list = _listPool.Pop();
            list.Clear();
            return list;
        }

        return new SubtreeList();
    }

    public void ReturnList(SubtreeList? list)
    {
        if (list is not null)
        {
            _listPool.Push(list);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref StackNode Node(int index) => ref _nodes[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref StackHead Head(int version) => ref _heads[version];

    private StackLink GetLink(int node, int i)
    {
        ref var n = ref _nodes[node];
        return i == 0 ? n.Link0 : _moreLinks[n.MoreLinks * 7 + i - 1];
    }

    private void SetLink(int node, int i, StackLink link)
    {
        ref var n = ref _nodes[node];
        if (i == 0)
        {
            n.Link0 = link;
            return;
        }

        if (n.MoreLinks < 0)
        {
            if ((_moreLinkBlocks + 1) * 7 > _moreLinks.Length)
            {
                Array.Resize(ref _moreLinks, _moreLinks.Length * 2);
            }

            n.MoreLinks = _moreLinkBlocks++;
        }

        _moreLinks[n.MoreLinks * 7 + i - 1] = link;
    }

    private int NewNode(int previous, int subtree, bool isPending, ushort state)
    {
        if (_nodeCount == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        var index = _nodeCount++;
        ref var node = ref _nodes[index];
        node = default;
        node.State = state;
        node.MoreLinks = -1;
        if (previous != 0)
        {
            ref var prev = ref _nodes[previous];
            node.LinkCount = 1;
            node.Link0 = new StackLink { Node = previous, Subtree = subtree, IsPending = isPending };
            node.Position = prev.Position;
            node.ErrorCost = prev.ErrorCost;
            node.DynamicPrecedence = prev.DynamicPrecedence;
            node.NodeCount = prev.NodeCount;
            if (subtree != 0)
            {
                ref var data = ref _arena[subtree];
                node.ErrorCost += _arena.ErrorCost(subtree);
                node.Position += data.Padding + data.Size;
                node.NodeCount += _arena.NodeCount(subtree);
                node.DynamicPrecedence += data.DynamicPrecedence;
            }
        }

        return index;
    }

    private void AddHead(StackHead head)
    {
        if (_headCount == _heads.Length)
        {
            Array.Resize(ref _heads, _heads.Length * 2);
        }

        _heads[_headCount++] = head;
    }

    public int HaltedVersionCount()
    {
        var count = 0;
        for (var i = 0; i < _headCount; i++)
        {
            if (_heads[i].Status == StackStatus.Halted)
            {
                count++;
            }
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort State(int version) => _nodes[_heads[version].Node].State;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Position(int version) => _nodes[_heads[version].Node].Position;

    public int LastExternalToken(int version) => _heads[version].LastExternalToken;

    public void SetLastExternalToken(int version, int token) => _heads[version].LastExternalToken = token;

    public int ErrorCost(int version)
    {
        ref var head = ref _heads[version];
        ref var node = ref _nodes[head.Node];
        var result = node.ErrorCost;
        if (head.Status == StackStatus.Paused || (node.State == Language.ErrorState && node.Link0.Subtree == 0))
        {
            result += SubtreeArena.ErrorCostPerRecovery;
        }

        return result;
    }

    public int NodeCountSinceError(int version)
    {
        ref var head = ref _heads[version];
        ref var node = ref _nodes[head.Node];
        if (node.NodeCount < head.NodeCountAtLastError)
        {
            head.NodeCountAtLastError = node.NodeCount;
        }

        return node.NodeCount - head.NodeCountAtLastError;
    }

    public int DynamicPrecedence(int version) => _nodes[_heads[version].Node].DynamicPrecedence;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int version, int subtree, bool pending, ushort state)
    {
        ref var head = ref _heads[version];
        var newNode = NewNode(head.Node, subtree, pending, state);
        if (subtree == 0)
        {
            head.NodeCountAtLastError = _nodes[newNode].NodeCount;
        }

        head.Node = newNode;
    }

    /// <summary>
    /// The pop of <see cref="PopCount"/> when the path has no merged nodes: the children are written to the arena
    /// directly, the trailing extras to <paramref name="trailingExtras"/>. Returns false when the general pop is
    /// needed.
    /// </summary>
    public bool TryPopSinglePath(int version, int count, SubtreeList trailingExtras, out int baseNode, out int firstChild, out int childCount)
    {
        var nodes = _nodes;
        var top = _heads[version].Node;
        var current = top;
        var links = 0;
        var nonExtra = 0;
        var trailing = 0;
        while (nonExtra < count)
        {
            ref var node = ref nodes[current];
            var subtree = node.Link0.Subtree;
            if (node.LinkCount != 1 || subtree == 0)
            {
                baseNode = firstChild = childCount = 0;
                return false;
            }

            if ((_arena.Nodes[subtree].Flags & SubtreeFlags.Extra) != 0)
            {
                if (nonExtra == 0)
                {
                    trailing++;
                }
            }
            else
            {
                nonExtra++;
            }

            links++;
            current = node.Link0.Node;
        }

        trailingExtras.Clear();
        childCount = links - trailing;
        firstChild = _arena.ReserveChildren(childCount);
        var children = _arena.Children;
        current = top;
        for (var i = 0; i < trailing; i++)
        {
            trailingExtras.Add(nodes[current].Link0.Subtree);
            current = nodes[current].Link0.Node;
        }

        if (trailing > 1)
        {
            trailingExtras.Reverse();
        }

        for (var k = childCount - 1; k >= 0; k--)
        {
            ref var node = ref nodes[current];
            children[firstChild + k] = node.Link0.Subtree;
            current = node.Link0.Node;
        }

        baseNode = current;
        return true;
    }

    public List<StackSlice> PopCount(int version, int count) => Iterate(version, IterationKind.PopCount, count, true);

    public List<StackSlice> PopPending(int version)
    {
        var pop = Iterate(version, IterationKind.PopPending, 0, true);
        if (pop.Count > 0)
        {
            RenumberVersion(pop[0].Version, version);
            var first = pop[0];
            first.Version = version;
            pop[0] = first;
        }

        return pop;
    }

    public SubtreeList? PopError(int version)
    {
        var node = _heads[version].Node;
        var linkCount = _nodes[node].LinkCount;
        for (var i = 0; i < linkCount; i++)
        {
            var link = GetLink(node, i);
            if (link.Subtree != 0 && _arena[link.Subtree].Symbol == Language.ErrorSymbol)
            {
                _foundError = false;
                var pop = Iterate(version, IterationKind.PopError, 1, true);
                if (pop.Count > 0)
                {
                    RenumberVersion(pop[0].Version, version);
                    return pop[0].Subtrees;
                }

                break;
            }
        }

        return null;
    }

    private bool _foundError;

    public List<StackSlice> PopAll(int version) => Iterate(version, IterationKind.PopAll, 0, true);

    public void RecordSummary(int version, int maxDepth)
    {
        _summary = _summaryPool.Count > 0 ? _summaryPool.Pop() : [];
        _summaryMaxDepth = maxDepth;
        Iterate(version, IterationKind.Summarize, 0, false);
        ReleaseSummary(ref _heads[version]);
        _heads[version].Summary = _summary;
        _summary = null;
    }

    /// <summary>
    /// Releases the summary of a head, which owns it like in tree-sitter (<c>stack_head_delete</c>): the lists are
    /// reused by the next summaries, error recovery records one each time a version is paused.
    /// </summary>
    private void ReleaseSummary(ref StackHead head)
    {
        if (head.Summary is { } summary)
        {
            summary.Clear();
            _summaryPool.Push(summary);
            head.Summary = null;
        }
    }

    private readonly Stack<List<StackSummaryEntry>> _summaryPool = new();
    private List<StackSummaryEntry>? _summary;
    private int _summaryMaxDepth;

    public List<StackSummaryEntry>? GetSummary(int version) => _heads[version].Summary;

    private const int ActionNone = 0;
    private const int ActionStop = 1;
    private const int ActionPop = 2;

    private int Callback(IterationKind kind, int goal, ref StackIterator iterator)
    {
        switch (kind)
        {
            case IterationKind.PopCount:
                return iterator.SubtreeCount == goal ? ActionPop | ActionStop : ActionNone;
            case IterationKind.PopPending:
                if (iterator.SubtreeCount >= 1)
                {
                    return iterator.IsPending ? ActionPop | ActionStop : ActionStop;
                }

                return ActionNone;
            case IterationKind.PopError:
                if (iterator.Subtrees!.Count > 0)
                {
                    if (!_foundError && _arena[iterator.Subtrees.Items[0]].Symbol == Language.ErrorSymbol)
                    {
                        _foundError = true;
                        return ActionPop | ActionStop;
                    }

                    return ActionStop;
                }

                return ActionNone;
            case IterationKind.PopAll:
                return _nodes[iterator.Node].LinkCount == 0 ? ActionPop : ActionNone;
            default:
                {
                    var summary = _summary!;
                    var state = _nodes[iterator.Node].State;
                    var depth = iterator.SubtreeCount;
                    if (depth > _summaryMaxDepth)
                    {
                        return ActionStop;
                    }

                    for (var i = summary.Count - 1; i >= 0; i--)
                    {
                        var entry = summary[i];
                        if (entry.Depth < depth)
                        {
                            break;
                        }

                        if (entry.Depth == depth && entry.State == state)
                        {
                            return ActionNone;
                        }
                    }

                    summary.Add(new StackSummaryEntry { Position = _nodes[iterator.Node].Position, Depth = depth, State = state });
                    return ActionNone;
                }
        }
    }

    private SubtreeList CopyList(SubtreeList source)
    {
        var list = RentList();
        list.CopyFrom(source);
        return list;
    }

    private void AddIterator(StackIterator iterator)
    {
        if (_iteratorCount == _iterators.Length)
        {
            Array.Resize(ref _iterators, _iterators.Length * 2);
        }

        _iterators[_iteratorCount++] = iterator;
    }

    private void RemoveIterator(int index)
    {
        Array.Copy(_iterators, index + 1, _iterators, index, _iteratorCount - index - 1);
        _iteratorCount--;
    }

    /// <summary>tree-sitter's <c>stack__iter</c>.</summary>
    private List<StackSlice> Iterate(int version, IterationKind kind, int goal, bool includeSubtrees)
    {
        _slices.Clear();
        _iteratorCount = 0;
        AddIterator(new StackIterator
        {
            Node = _heads[version].Node,
            Subtrees = includeSubtrees ? RentList() : null,
            SubtreeCount = 0,
            IsPending = true,
        });

        while (_iteratorCount > 0)
        {
            var size = _iteratorCount;
            for (var i = 0; i < size; i++)
            {
                ref var iterator = ref _iterators[i];
                var node = iterator.Node;
                var action = Callback(kind, goal, ref iterator);
                var shouldPop = (action & ActionPop) != 0;
                var shouldStop = (action & ActionStop) != 0 || _nodes[node].LinkCount == 0;

                if (shouldPop)
                {
                    var subtrees = iterator.Subtrees ?? RentList();
                    if (!shouldStop)
                    {
                        subtrees = CopyList(subtrees);
                    }

                    subtrees.Reverse();
                    AddSlice(version, node, subtrees);
                }

                if (shouldStop)
                {
                    if (!shouldPop)
                    {
                        ReturnList(iterator.Subtrees);
                    }

                    RemoveIterator(i);
                    i--;
                    size--;
                    continue;
                }

                var linkCount = _nodes[node].LinkCount;
                for (var j = 1; j <= linkCount; j++)
                {
                    int target;
                    StackLink link;
                    if (j == linkCount)
                    {
                        link = GetLink(node, 0);
                        target = i;
                    }
                    else
                    {
                        if (_iteratorCount >= MaxIteratorCount)
                        {
                            continue;
                        }

                        link = GetLink(node, j);
                        var current = _iterators[i];
                        if (current.Subtrees is not null)
                        {
                            current.Subtrees = CopyList(current.Subtrees);
                        }

                        AddIterator(current);
                        target = _iteratorCount - 1;
                    }

                    ref var next = ref _iterators[target];
                    next.Node = link.Node;
                    if (link.Subtree != 0)
                    {
                        next.Subtrees?.Add(link.Subtree);
                        if (!_arena.IsExtra(link.Subtree))
                        {
                            next.SubtreeCount++;
                            if (!link.IsPending)
                            {
                                next.IsPending = false;
                            }
                        }
                    }
                    else
                    {
                        next.SubtreeCount++;
                        next.IsPending = false;
                    }
                }
            }
        }

        return _slices;
    }

    private void AddSlice(int originalVersion, int node, SubtreeList subtrees)
    {
        for (var i = _slices.Count - 1; i >= 0; i--)
        {
            var version = _slices[i].Version;
            if (_heads[version].Node == node)
            {
                _slices.Insert(i + 1, new StackSlice { Subtrees = subtrees, Version = version });
                return;
            }
        }

        var newVersion = AddVersion(originalVersion, node);
        _slices.Add(new StackSlice { Subtrees = subtrees, Version = newVersion });
    }

    private int AddVersion(int originalVersion, int node)
    {
        ref var original = ref _heads[originalVersion];
        AddHead(new StackHead
        {
            Node = node,
            NodeCountAtLastError = original.NodeCountAtLastError,
            LastExternalToken = original.LastExternalToken,
            Status = StackStatus.Active,
        });
        return _headCount - 1;
    }

    public bool HasAdvancedSinceError(int version)
    {
        ref var head = ref _heads[version];
        var node = head.Node;
        if (_nodes[node].ErrorCost == 0)
        {
            return true;
        }

        while (node != 0)
        {
            ref var n = ref _nodes[node];
            if (n.LinkCount > 0)
            {
                var subtree = n.Link0.Subtree;
                if (subtree != 0)
                {
                    if (_arena.TotalBytes(subtree) > 0)
                    {
                        return true;
                    }

                    if (n.NodeCount > head.NodeCountAtLastError && _arena.ErrorCost(subtree) == 0)
                    {
                        node = n.Link0.Node;
                        continue;
                    }
                }
            }

            break;
        }

        return false;
    }

    public void RemoveVersion(int version)
    {
        ReleaseSummary(ref _heads[version]);
        Array.Copy(_heads, version + 1, _heads, version, _headCount - version - 1);
        _headCount--;
    }

    public void RenumberVersion(int v1, int v2)
    {
        if (v1 == v2)
        {
            return;
        }

        ref var source = ref _heads[v1];
        ref var target = ref _heads[v2];
        if (target.Summary is not null && source.Summary is null)
        {
            source.Summary = target.Summary;
            target.Summary = null;
        }

        ReleaseSummary(ref target);
        target = source;

        // The summary of the source is the target's now
        source.Summary = null;
        RemoveVersion(v1);
    }

    public void SwapVersions(int v1, int v2) => (_heads[v1], _heads[v2]) = (_heads[v2], _heads[v1]);

    public int CopyVersion(int version)
    {
        var head = _heads[version];
        head.Summary = null;
        AddHead(head);
        return _headCount - 1;
    }

    public bool Merge(int version1, int version2)
    {
        if (!CanMerge(version1, version2))
        {
            return false;
        }

        ref var head1 = ref _heads[version1];
        var node2 = _heads[version2].Node;
        var linkCount = _nodes[node2].LinkCount;
        for (var i = 0; i < linkCount; i++)
        {
            AddLink(head1.Node, GetLink(node2, i));
        }

        if (_nodes[head1.Node].State == Language.ErrorState)
        {
            head1.NodeCountAtLastError = _nodes[head1.Node].NodeCount;
        }

        RemoveVersion(version2);
        return true;
    }

    public bool CanMerge(int version1, int version2)
    {
        ref var head1 = ref _heads[version1];
        ref var head2 = ref _heads[version2];
        ref var node1 = ref _nodes[head1.Node];
        ref var node2 = ref _nodes[head2.Node];
        return head1.Status == StackStatus.Active &&
            head2.Status == StackStatus.Active &&
            node1.State == node2.State &&
            node1.Position == node2.Position &&
            node1.ErrorCost == node2.ErrorCost &&
            _arena.ExternalStateEquals(head1.LastExternalToken, head2.LastExternalToken);
    }

    private bool SubtreeIsEquivalent(int left, int right)
    {
        if (left == right)
        {
            return true;
        }

        if (left == 0 || right == 0)
        {
            return false;
        }

        ref var l = ref _arena[left];
        ref var r = ref _arena[right];
        if (l.Symbol != r.Symbol)
        {
            return false;
        }

        if (_arena.ErrorCost(left) > 0 && _arena.ErrorCost(right) > 0)
        {
            return true;
        }

        return l.Padding == r.Padding &&
            l.Size == r.Size &&
            l.ChildCount == r.ChildCount &&
            (l.Flags & SubtreeFlags.Extra) == (r.Flags & SubtreeFlags.Extra) &&
            _arena.ExternalStateEquals(left, right);
    }

    private void AddLink(int self, StackLink link)
    {
        if (link.Node == self)
        {
            return;
        }

        var linkCount = _nodes[self].LinkCount;
        for (var i = 0; i < linkCount; i++)
        {
            var existing = GetLink(self, i);
            if (SubtreeIsEquivalent(existing.Subtree, link.Subtree))
            {
                if (existing.Node == link.Node)
                {
                    if (DynamicPrecedenceOf(link.Subtree) > DynamicPrecedenceOf(existing.Subtree))
                    {
                        existing.Subtree = link.Subtree;
                        SetLink(self, i, existing);
                        _nodes[self].DynamicPrecedence = _nodes[link.Node].DynamicPrecedence + DynamicPrecedenceOf(link.Subtree);
                    }

                    return;
                }

                ref var existingNode = ref _nodes[existing.Node];
                ref var linkNode = ref _nodes[link.Node];
                if (existingNode.State == linkNode.State && existingNode.Position == linkNode.Position && existingNode.ErrorCost == linkNode.ErrorCost)
                {
                    var count = linkNode.LinkCount;
                    var target = existing.Node;
                    var source = link.Node;
                    for (var j = 0; j < count; j++)
                    {
                        AddLink(target, GetLink(source, j));
                    }

                    var dynamicPrecedence = _nodes[source].DynamicPrecedence;
                    if (link.Subtree != 0)
                    {
                        dynamicPrecedence += _arena[link.Subtree].DynamicPrecedence;
                    }

                    if (dynamicPrecedence > _nodes[self].DynamicPrecedence)
                    {
                        _nodes[self].DynamicPrecedence = dynamicPrecedence;
                    }

                    return;
                }
            }
        }

        if (linkCount == MaxLinkCount)
        {
            return;
        }

        SetLink(self, linkCount, link);
        _nodes[self].LinkCount = linkCount + 1;
        var nodeCount = _nodes[link.Node].NodeCount;
        var dynamic = _nodes[link.Node].DynamicPrecedence;
        if (link.Subtree != 0)
        {
            nodeCount += _arena.NodeCount(link.Subtree);
            dynamic += _arena[link.Subtree].DynamicPrecedence;
        }

        if (nodeCount > _nodes[self].NodeCount)
        {
            _nodes[self].NodeCount = nodeCount;
        }

        if (dynamic > _nodes[self].DynamicPrecedence)
        {
            _nodes[self].DynamicPrecedence = dynamic;
        }
    }

    private int DynamicPrecedenceOf(int subtree) => subtree == 0 ? 0 : _arena[subtree].DynamicPrecedence;

    public void Halt(int version) => _heads[version].Status = StackStatus.Halted;

    public void Pause(int version, int lookahead)
    {
        ref var head = ref _heads[version];
        head.Status = StackStatus.Paused;
        head.LookaheadWhenPaused = lookahead;
        head.NodeCountAtLastError = _nodes[head.Node].NodeCount;
    }

    public bool IsActive(int version) => _heads[version].Status == StackStatus.Active;

    public bool IsHalted(int version) => _heads[version].Status == StackStatus.Halted;

    public bool IsPaused(int version) => _heads[version].Status == StackStatus.Paused;

    public int Resume(int version)
    {
        ref var head = ref _heads[version];
        var result = head.LookaheadWhenPaused;
        head.Status = StackStatus.Active;
        head.LookaheadWhenPaused = 0;
        return result;
    }
}
