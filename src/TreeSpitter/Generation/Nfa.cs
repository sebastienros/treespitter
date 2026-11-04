using System.Text;

namespace TreeSpitter.Generation;

// A port of tree-sitter's cli/generate/src/nfa.rs.

/// <summary>A set of code points, as sorted, disjoint, half-open ranges <c>[start, end)</c>.</summary>
internal sealed class CharacterSet : IEquatable<CharacterSet>, IComparable<CharacterSet>
{
    /// <summary>One past the greatest code point, tree-sitter's <c>END</c>.</summary>
    public const int End = 0x110000;

    private readonly List<(int Start, int End)> _ranges;

    public CharacterSet()
    {
        _ranges = [];
    }

    private CharacterSet(List<(int Start, int End)> ranges)
    {
        _ranges = ranges;
    }

    public static CharacterSet Empty => new();

    public static CharacterSet FromChar(int c) => new([(c, c + 1)]);

    /// <summary>Creates a set from inclusive ranges.</summary>
    public static CharacterSet FromInclusiveRanges(IEnumerable<(int Start, int End)> ranges)
    {
        var set = new CharacterSet();
        foreach (var (start, end) in ranges)
        {
            set.AddIntRange(0, start, end + 1);
        }

        return set;
    }

    public IReadOnlyList<(int Start, int End)> Ranges => _ranges;

    public int RangeCount => _ranges.Count;

    public bool IsEmpty => _ranges.Count == 0;

    public CharacterSet Clone() => new([.. _ranges]);

    public CharacterSet Negate()
    {
        var result = new List<(int, int)>();
        var previousEnd = 0;
        foreach (var (start, end) in _ranges)
        {
            if (previousEnd < start)
            {
                result.Add((previousEnd, start));
            }

            previousEnd = end;
        }

        if (previousEnd < End)
        {
            result.Add((previousEnd, End));
        }

        return new CharacterSet(result);
    }

    public CharacterSet AddChar(int c)
    {
        AddIntRange(0, c, c + 1);
        return this;
    }

    /// <summary>Adds an inclusive range.</summary>
    public CharacterSet AddRange(int start, int end)
    {
        AddIntRange(0, start, end + 1);
        return this;
    }

    public CharacterSet Add(CharacterSet other)
    {
        var index = 0;
        foreach (var (start, end) in other._ranges)
        {
            index = AddIntRange(index, start, end);
        }

        return this;
    }

    private int AddIntRange(int i, int start, int end)
    {
        while (i < _ranges.Count)
        {
            var range = _ranges[i];
            if (range.Start > end)
            {
                _ranges.Insert(i, (start, end));
                return i;
            }

            if (range.End >= start)
            {
                range = (Math.Min(range.Start, start), Math.Max(range.End, end));
                _ranges[i] = range;
                while (i + 1 < _ranges.Count && _ranges[i + 1].Start <= _ranges[i].End)
                {
                    _ranges[i] = (_ranges[i].Start, Math.Max(_ranges[i].End, _ranges[i + 1].End));
                    _ranges.RemoveAt(i + 1);
                }

                return i;
            }

            i++;
        }

        _ranges.Add((start, end));
        return i;
    }

    public bool DoesIntersect(CharacterSet other)
    {
        int left = 0, right = 0;
        while (left < _ranges.Count && right < other._ranges.Count)
        {
            var l = _ranges[left];
            var r = other._ranges[right];
            if (l.End <= r.Start)
            {
                left++;
            }
            else if (l.Start >= r.End)
            {
                right++;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the intersection of the two sets, and removes it from both.</summary>
    public CharacterSet RemoveIntersection(CharacterSet other)
    {
        var intersection = new List<(int, int)>();
        var self = _ranges;
        var others = other._ranges;
        int leftI = 0, rightI = 0;
        while (leftI < self.Count && rightI < others.Count)
        {
            var left = self[leftI];
            var right = others[rightI];
            if (left.Start < right.Start)
            {
                if (left.End <= right.Start)
                {
                    leftI++;
                    continue;
                }

                if (left.End < right.End)
                {
                    intersection.Add((right.Start, left.End));
                    // swap(left.end, right.start)
                    var leftEnd = left.End;
                    self[leftI] = (left.Start, right.Start);
                    others[rightI] = (leftEnd, right.End);
                    leftI++;
                }
                else if (left.End == right.End)
                {
                    intersection.Add(right);
                    self[leftI] = (left.Start, right.Start);
                    others.RemoveAt(rightI);
                }
                else
                {
                    intersection.Add(right);
                    var newRange = (left.Start, right.Start);
                    self[leftI] = (right.End, left.End);
                    self.Insert(leftI, newRange);
                    others.RemoveAt(rightI);
                    leftI++;
                }
            }
            else if (left.Start == right.Start)
            {
                if (left.End < right.End)
                {
                    intersection.Add(left);
                    others[rightI] = (left.End, right.End);
                    self.RemoveAt(leftI);
                }
                else if (left.End == right.End)
                {
                    intersection.Add(left);
                    self.RemoveAt(leftI);
                    others.RemoveAt(rightI);
                }
                else
                {
                    intersection.Add(right);
                    self[leftI] = (right.End, left.End);
                    others.RemoveAt(rightI);
                }
            }
            else
            {
                if (left.Start >= right.End)
                {
                    rightI++;
                    continue;
                }

                if (left.End < right.End)
                {
                    intersection.Add(left);
                    var newRange = (right.Start, left.Start);
                    others[rightI] = (left.End, right.End);
                    others.Insert(rightI, newRange);
                    self.RemoveAt(leftI);
                    rightI++;
                }
                else if (left.End == right.End)
                {
                    intersection.Add(left);
                    others[rightI] = (right.Start, left.Start);
                    self.RemoveAt(leftI);
                }
                else
                {
                    intersection.Add((left.Start, right.End));
                    // swap(left.start, right.end)
                    var leftStart = left.Start;
                    self[leftI] = (right.End, left.End);
                    others[rightI] = (right.Start, leftStart);
                    rightI++;
                }
            }
        }

        return new CharacterSet(intersection);
    }

    public CharacterSet Difference(CharacterSet other)
    {
        var copy = other.Clone();
        RemoveIntersection(copy);
        return this;
    }

    public bool Contains(int c) => ContainsCodePointRange(c, c + 1);

    public bool ContainsCodePointRange(int start, int end)
    {
        int low = 0, high = _ranges.Count;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            var probe = _ranges[mid];
            if (probe.End <= start)
            {
                low = mid + 1;
            }
            else if (probe.Start > start)
            {
                high = mid;
            }
            else
            {
                low = mid;
                break;
            }
        }

        return low < _ranges.Count && _ranges[low].Start <= start && _ranges[low].End >= end;
    }

    /// <summary>A reduced list of ranges, assuming the characters of <paramref name="ruledOut"/> can be ignored.</summary>
    public CharacterSet SimplifyIgnoring(CharacterSet ruledOut)
    {
        var result = new List<(int, int)>();
        (int Start, int End)? previous = null;
        foreach (var range in _ranges)
        {
            if (ruledOut.ContainsCodePointRange(range.Start, range.End))
            {
                continue;
            }

            if (previous is { } p && ruledOut.ContainsCodePointRange(p.End, range.Start))
            {
                previous = (p.Start, range.End);
                continue;
            }

            if (previous is { } q)
            {
                result.Add(q);
            }

            previous = range;
        }

        if (previous is { } last)
        {
            result.Add(last);
        }

        return new CharacterSet(result);
    }

    public IEnumerable<int> Chars()
    {
        foreach (var (start, end) in _ranges)
        {
            for (var c = start; c < end; c++)
            {
                yield return c;
            }
        }
    }

    public bool Equals(CharacterSet? other)
    {
        if (other is null || other._ranges.Count != _ranges.Count)
        {
            return false;
        }

        for (var i = 0; i < _ranges.Count; i++)
        {
            if (_ranges[i] != other._ranges[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is CharacterSet other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            foreach (var (start, end) in _ranges)
            {
                hash = hash * 31 + start;
                hash = hash * 31 + end;
            }

            return hash;
        }
    }

    public int CompareTo(CharacterSet? other)
    {
        if (other is null)
        {
            return 1;
        }

        long leftCount = 0, rightCount = 0;
        foreach (var (start, end) in _ranges)
        {
            leftCount += end - start;
        }

        foreach (var (start, end) in other._ranges)
        {
            rightCount += end - start;
        }

        var result = leftCount.CompareTo(rightCount);
        if (result != 0)
        {
            return result;
        }

        var count = Math.Min(_ranges.Count, other._ranges.Count);
        for (var i = 0; i < count; i++)
        {
            var l = _ranges[i];
            var r = other._ranges[i];
            result = (l.End - l.Start).CompareTo(r.End - r.Start);
            if (result != 0)
            {
                return result;
            }

            result = l.Start.CompareTo(r.Start);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    public override string ToString()
    {
        var builder = new StringBuilder("[");
        foreach (var (start, end) in _ranges)
        {
            Append(builder, start);
            if (end - 1 > start)
            {
                builder.Append('-');
                Append(builder, end - 1);
            }
        }

        return builder.Append(']').ToString();

        static void Append(StringBuilder builder, int c)
        {
            if (c is >= 0x20 and < 0x7f)
            {
                builder.Append((char)c);
            }
            else
            {
                builder.Append("\\u{").Append(c.ToString("x", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            }
        }
    }
}

internal enum NfaStateKind : byte
{
    Advance,
    Split,
    Accept,
}

internal sealed class NfaState
{
    public NfaStateKind Kind { get; set; }

    // Advance
    public CharacterSet? Chars { get; set; }

    public int StateId { get; set; }

    public bool IsSeparator { get; set; }

    public int Precedence { get; set; }

    // Split
    public int Left { get; set; }

    public int Right { get; set; }

    // Accept
    public int VariableIndex { get; set; }

    public static NfaState Advance(CharacterSet chars, int stateId, bool isSeparator, int precedence) =>
        new() { Kind = NfaStateKind.Advance, Chars = chars, StateId = stateId, IsSeparator = isSeparator, Precedence = precedence };

    public static NfaState Split(int left, int right) => new() { Kind = NfaStateKind.Split, Left = left, Right = right };

    public static NfaState Accept(int variableIndex, int precedence) => new() { Kind = NfaStateKind.Accept, VariableIndex = variableIndex, Precedence = precedence };
}

internal sealed class Nfa
{
    public List<NfaState> States { get; } = [];

    public int LastStateId => States.Count - 1;
}

internal sealed class NfaTransition(CharacterSet characters, bool isSeparator, int precedence, List<int> states)
{
    public CharacterSet Characters { get; set; } = characters;

    public bool IsSeparator { get; } = isSeparator;

    public int Precedence { get; } = precedence;

    public List<int> States { get; } = states;
}

/// <summary>A set of NFA states, closed under the split transitions.</summary>
internal sealed class NfaCursor(Nfa nfa)
{
    private readonly Nfa _nfa = nfa;

    public List<int> StateIds { get; private set; } = [];

    public void Reset(List<int> states)
    {
        StateIds = [];
        AddStates([.. states]);
    }

    public void ForceReset(List<int> states)
    {
        StateIds = states;
    }

    public IEnumerable<(CharacterSet Chars, bool IsSeparator)> TransitionChars()
    {
        foreach (var id in StateIds)
        {
            var state = _nfa.States[id];
            if (state.Kind == NfaStateKind.Advance)
            {
                yield return (state.Chars!, state.IsSeparator);
            }
        }
    }

    public List<NfaTransition> Transitions()
    {
        var result = new List<NfaTransition>();
        foreach (var id in StateIds)
        {
            var state = _nfa.States[id];
            if (state.Kind != NfaStateKind.Advance)
            {
                continue;
            }

            var chars = state.Chars!.Clone();
            var isSep = state.IsSeparator;
            var prec = state.Precedence;
            var target = state.StateId;
            var i = 0;
            while (i < result.Count && !chars.IsEmpty)
            {
                var intersection = result[i].Characters.RemoveIntersection(chars);
                if (!intersection.IsEmpty)
                {
                    var intersectionStates = new List<int>(result[i].States);
                    var j = intersectionStates.BinarySearch(target);
                    if (j < 0)
                    {
                        intersectionStates.Insert(~j, target);
                    }

                    var transition = new NfaTransition(intersection, result[i].IsSeparator && isSep, Math.Max(result[i].Precedence, prec), intersectionStates);
                    if (result[i].Characters.IsEmpty)
                    {
                        result[i] = transition;
                    }
                    else
                    {
                        result.Insert(i, transition);
                        i++;
                    }
                }

                i++;
            }

            if (!chars.IsEmpty)
            {
                result.Add(new NfaTransition(chars, isSep, prec, [target]));
            }
        }

        for (var i = 0; i < result.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (result[j].IsSeparator == result[i].IsSeparator &&
                    result[j].Precedence == result[i].Precedence &&
                    result[j].States.SequenceEqual(result[i].States))
                {
                    result[j].Characters = result[j].Characters.Add(result[i].Characters);
                    result.RemoveAt(i);
                    i--;
                    break;
                }
            }
        }

        // A stable sort: the elements with the same characters keep their order
        var sorted = result.Select((t, index) => (t, index)).ToList();
        sorted.Sort((a, b) =>
        {
            var c = a.t.Characters.CompareTo(b.t.Characters);
            return c != 0 ? c : a.index.CompareTo(b.index);
        });
        return sorted.Select(e => e.t).ToList();
    }

    public IEnumerable<(int VariableIndex, int Precedence)> Completions()
    {
        foreach (var id in StateIds)
        {
            var state = _nfa.States[id];
            if (state.Kind == NfaStateKind.Accept)
            {
                yield return (state.VariableIndex, state.Precedence);
            }
        }
    }

    public void AddStates(List<int> newStateIds)
    {
        var i = 0;
        while (i < newStateIds.Count)
        {
            var stateId = newStateIds[i];
            var state = _nfa.States[stateId];
            if (state.Kind == NfaStateKind.Split)
            {
                var hasLeft = false;
                var hasRight = false;
                foreach (var id in newStateIds)
                {
                    if (id == state.Left)
                    {
                        hasLeft = true;
                    }

                    if (id == state.Right)
                    {
                        hasRight = true;
                    }
                }

                if (!hasLeft)
                {
                    newStateIds.Add(state.Left);
                }

                if (!hasRight)
                {
                    newStateIds.Add(state.Right);
                }
            }
            else
            {
                var j = StateIds.BinarySearch(stateId);
                if (j < 0)
                {
                    StateIds.Insert(~j, stateId);
                }
            }

            i++;
        }
    }
}
