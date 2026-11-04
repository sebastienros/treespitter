namespace TreeSpitter.Generation;

// A port of tree-sitter's build_tables/token_conflicts.rs and build_tables/coincident_tokens.rs.

internal struct TokenConflictStatus : IEquatable<TokenConflictStatus>
{
    public bool MatchesPrefix;
    public bool DoesMatchContinuation;
    public bool DoesMatchValidContinuation;
    public bool DoesMatchSeparators;
    public bool MatchesSameString;
    public bool MatchesDifferentString;

    public readonly bool Equals(TokenConflictStatus other) =>
        MatchesPrefix == other.MatchesPrefix &&
        DoesMatchContinuation == other.DoesMatchContinuation &&
        DoesMatchValidContinuation == other.DoesMatchValidContinuation &&
        DoesMatchSeparators == other.DoesMatchSeparators &&
        MatchesSameString == other.MatchesSameString &&
        MatchesDifferentString == other.MatchesDifferentString;

    public override readonly bool Equals(object? obj) => obj is TokenConflictStatus other && Equals(other);

    public override readonly int GetHashCode() =>
        (MatchesPrefix ? 1 : 0) | (DoesMatchContinuation ? 2 : 0) | (DoesMatchValidContinuation ? 4 : 0) |
        (DoesMatchSeparators ? 8 : 0) | (MatchesSameString ? 16 : 0) | (MatchesDifferentString ? 32 : 0);
}

internal sealed class TokenConflictMap
{
    private readonly int _n;
    private readonly TokenConflictStatus[] _statusMatrix;
    private readonly LexicalGrammar _grammar;

    public TokenConflictMap(LexicalGrammar grammar, List<TokenSet> followingTokens)
    {
        _grammar = grammar;
        var cursor = new NfaCursor(grammar.Nfa);
        var startingChars = GetStartingChars(cursor, grammar);
        var followingChars = GetFollowingChars(startingChars, followingTokens);
        _n = grammar.Variables.Count;
        _statusMatrix = new TokenConflictStatus[_n * _n];
        for (var i = 0; i < _n; i++)
        {
            for (var j = 0; j < i; j++)
            {
                var (left, right) = ComputeConflictStatus(cursor, grammar, followingChars, i, j);
                _statusMatrix[MatrixIndex(i, j)] = left;
                _statusMatrix[MatrixIndex(j, i)] = right;
            }
        }
    }

    private int MatrixIndex(int i, int j) => _n * i + j;

    public bool HasSameConflictStatus(int a, int b, int other) =>
        _statusMatrix[MatrixIndex(a, other)].Equals(_statusMatrix[MatrixIndex(b, other)]);

    public bool DoesMatchDifferentString(int i, int j) => _statusMatrix[MatrixIndex(i, j)].MatchesDifferentString;

    public bool DoesMatchSameString(int i, int j) => _statusMatrix[MatrixIndex(i, j)].MatchesSameString;

    public bool DoesConflict(int i, int j)
    {
        var entry = _statusMatrix[MatrixIndex(i, j)];
        return entry.DoesMatchValidContinuation || entry.DoesMatchSeparators || entry.MatchesSameString;
    }

    public bool DoesMatchPrefix(int i, int j) => _statusMatrix[MatrixIndex(i, j)].MatchesPrefix;

    public bool DoesMatchShorterOrLonger(int i, int j)
    {
        var entry = _statusMatrix[MatrixIndex(i, j)];
        var reverse = _statusMatrix[MatrixIndex(j, i)];
        return (entry.DoesMatchValidContinuation || entry.DoesMatchSeparators) && !reverse.DoesMatchSeparators;
    }

    public bool DoesOverlap(int i, int j)
    {
        var status = _statusMatrix[MatrixIndex(i, j)];
        return status.DoesMatchSeparators || status.MatchesPrefix || status.MatchesSameString || status.DoesMatchContinuation;
    }

    public static bool PreferToken(LexicalGrammar grammar, (int Precedence, int Id) left, (int Precedence, int Id) right)
    {
        if (left.Precedence != right.Precedence)
        {
            return left.Precedence > right.Precedence;
        }

        var leftImplicit = grammar.Variables[left.Id].ImplicitPrecedence;
        var rightImplicit = grammar.Variables[right.Id].ImplicitPrecedence;
        if (leftImplicit != rightImplicit)
        {
            return leftImplicit > rightImplicit;
        }

        return left.Id < right.Id;
    }

    public static bool PreferTransition(LexicalGrammar grammar, NfaTransition transition, int completedId, int completedPrecedence, bool hasSeparatorTransitions)
    {
        if (transition.Precedence < completedPrecedence)
        {
            return false;
        }

        if (transition.Precedence == completedPrecedence)
        {
            if (transition.IsSeparator)
            {
                return false;
            }

            if (hasSeparatorTransitions && !grammar.VariableIndicesForNfaStates(transition.States).Any(i => i == completedId))
            {
                return false;
            }
        }

        return true;
    }

    private static List<CharacterSet> GetStartingChars(NfaCursor cursor, LexicalGrammar grammar)
    {
        var result = new List<CharacterSet>();
        foreach (var variable in grammar.Variables)
        {
            cursor.Reset([variable.StartState]);
            var all = CharacterSet.Empty;
            foreach (var (chars, _) in cursor.TransitionChars())
            {
                all = all.Add(chars);
            }

            result.Add(all);
        }

        return result;
    }

    private static List<CharacterSet> GetFollowingChars(List<CharacterSet> startingChars, List<TokenSet> followingTokens)
    {
        var result = new List<CharacterSet>();
        foreach (var tokens in followingTokens)
        {
            var chars = CharacterSet.Empty;
            foreach (var token in tokens.Symbols)
            {
                if (token.IsTerminal)
                {
                    chars = chars.Add(startingChars[token.Index]);
                }
            }

            result.Add(chars);
        }

        return result;
    }

    private sealed class StateSetComparer : IEqualityComparer<List<int>>
    {
        public static readonly StateSetComparer Instance = new();

        public bool Equals(List<int>? x, List<int>? y) => x is not null && y is not null && x.SequenceEqual(y);

        public int GetHashCode(List<int> obj)
        {
            unchecked
            {
                var hash = obj.Count;
                foreach (var i in obj)
                {
                    hash = hash * 31 + i;
                }

                return hash;
            }
        }
    }

    private static (TokenConflictStatus, TokenConflictStatus) ComputeConflictStatus(NfaCursor cursor, LexicalGrammar grammar, List<CharacterSet> followingChars, int i, int j)
    {
        var visited = new HashSet<List<int>>(StateSetComparer.Instance);
        var queue = new List<List<int>> { new() { grammar.Variables[i].StartState, grammar.Variables[j].StartState } };
        var left = default(TokenConflictStatus);
        var right = default(TokenConflictStatus);

        while (queue.Count > 0)
        {
            var stateSet = queue[queue.Count - 1];
            queue.RemoveAt(queue.Count - 1);

            var live = grammar.VariableIndicesForNfaStates(stateSet).ToList();
            if (live.Count == 1)
            {
                if (live[0] == i)
                {
                    left.MatchesDifferentString = true;
                }
                else
                {
                    right.MatchesDifferentString = true;
                }

                continue;
            }

            cursor.Reset(stateSet);
            var withinSeparator = cursor.TransitionChars().Any(t => t.IsSeparator);

            (int Id, int Precedence)? completion = null;
            foreach (var (id, precedence) in cursor.Completions())
            {
                if (withinSeparator)
                {
                    if (id == i)
                    {
                        left.DoesMatchSeparators = true;
                    }
                    else
                    {
                        right.DoesMatchSeparators = true;
                    }
                }

                if (completion is { } previous)
                {
                    if (id == previous.Id)
                    {
                        continue;
                    }

                    int preferredId;
                    if (PreferToken(grammar, (previous.Precedence, previous.Id), (precedence, id)))
                    {
                        preferredId = previous.Id;
                    }
                    else
                    {
                        preferredId = id;
                        completion = (id, precedence);
                    }

                    if (preferredId == i)
                    {
                        left.MatchesSameString = true;
                    }
                    else
                    {
                        right.MatchesSameString = true;
                    }
                }
                else
                {
                    completion = (id, precedence);
                }
            }

            foreach (var transition in cursor.Transitions())
            {
                var canAdvance = true;
                if (completion is { } completed)
                {
                    int? advancedId = null;
                    var successorContainsCompletedId = false;
                    foreach (var variableId in grammar.VariableIndicesForNfaStates(transition.States))
                    {
                        if (variableId == completed.Id)
                        {
                            successorContainsCompletedId = true;
                            break;
                        }

                        advancedId = variableId;
                    }

                    if (advancedId is { } advanced && !successorContainsCompletedId)
                    {
                        if (PreferTransition(grammar, transition, completed.Id, completed.Precedence, withinSeparator))
                        {
                            canAdvance = true;
                            if (advanced == i)
                            {
                                left.DoesMatchContinuation = true;
                                if (transition.Characters.DoesIntersect(followingChars[j]))
                                {
                                    left.DoesMatchValidContinuation = true;
                                }
                            }
                            else
                            {
                                right.DoesMatchContinuation = true;
                                if (transition.Characters.DoesIntersect(followingChars[i]))
                                {
                                    right.DoesMatchValidContinuation = true;
                                }
                            }
                        }
                        else if (completed.Id == i)
                        {
                            left.MatchesPrefix = true;
                        }
                        else
                        {
                            right.MatchesPrefix = true;
                        }
                    }
                }

                if (canAdvance && visited.Add(transition.States))
                {
                    queue.Add(transition.States);
                }
            }
        }

        return (left, right);
    }
}

internal sealed class CoincidentTokenIndex
{
    private readonly List<int>[] _entries;
    private readonly int _n;

    public CoincidentTokenIndex(ParseTable table, LexicalGrammar lexicalGrammar)
    {
        _n = lexicalGrammar.Variables.Count;
        _entries = new List<int>[_n * _n];
        // The pairs of terminals of each state, a pair and its reverse having the same entry
        var terminals = new List<int>();
        for (var stateIndex = 0; stateIndex < table.States.Count; stateIndex++)
        {
            var keys = table.States[stateIndex].TerminalEntries.Keys;
            terminals.Clear();
            for (var k = 0; k < keys.Count; k++)
            {
                if (keys[k].IsTerminal)
                {
                    terminals.Add(keys[k].Index);
                }
            }

            for (var i = 0; i < terminals.Count; i++)
            {
                for (var j = i; j < terminals.Count; j++)
                {
                    var index = Index(terminals[i], terminals[j]);
                    var list = _entries[index] ??= [];
                    if (list.Count == 0 || list[list.Count - 1] != stateIndex)
                    {
                        list.Add(stateIndex);
                    }
                }
            }
        }
    }

    public IReadOnlyList<int> StatesWith(Symbol a, Symbol b) => (IReadOnlyList<int>?)_entries[Index(a.Index, b.Index)] ?? [];

    public bool Contains(Symbol a, Symbol b) => _entries[Index(a.Index, b.Index)] is { Count: > 0 };

    private int Index(int a, int b) => a < b ? a * _n + b : b * _n + a;
}
