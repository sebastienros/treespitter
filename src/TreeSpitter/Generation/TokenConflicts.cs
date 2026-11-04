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

/// <summary>
/// tree-sitter's <c>CoincidentTokenIndex</c>: the pairs of terminals that are valid in the same parse states.
/// </summary>
/// <remarks>
/// tree-sitter stores the list of the states of every pair, but only asks two questions of these lists: whether a pair
/// has a state (<c>contains</c>), and whether the word token is valid in every state of a pair (<c>states_with</c> in
/// <c>identify_keywords</c>). The lists hold the square of the number of terminals of every state, hundreds of millions
/// of entries for the large grammars, so both answers are stored as bit matrices instead: for each terminal, the
/// terminals valid with it in a state, and in a state without the word token.
/// </remarks>
internal sealed class CoincidentTokenIndex
{
    private readonly int _words;
    private readonly ulong[] _coincident;
    private readonly ulong[]? _coincidentWithoutWordToken;

    public CoincidentTokenIndex(ParseTable table, LexicalGrammar lexicalGrammar, Symbol? wordToken)
    {
        var n = lexicalGrammar.Variables.Count;
        _words = (n + 63) >> 6;
        _coincident = new ulong[n * _words];
        _coincidentWithoutWordToken = wordToken is { IsTerminal: true } ? new ulong[n * _words] : null;
        var stateTerminals = new ulong[_words];
        var terminals = new List<int>();
        foreach (var state in table.States)
        {
            var keys = state.TerminalEntries.Keys;
            Array.Clear(stateTerminals, 0, _words);
            terminals.Clear();
            for (var k = 0; k < keys.Count; k++)
            {
                if (keys[k].IsTerminal)
                {
                    stateTerminals[keys[k].Index >> 6] |= 1UL << (keys[k].Index & 63);
                    terminals.Add(keys[k].Index);
                }
            }

            var withoutWordToken = _coincidentWithoutWordToken is not null && !state.TerminalEntries.ContainsKey(wordToken!.Value);
            foreach (var terminal in terminals)
            {
                Or(_coincident, terminal, stateTerminals);
                if (withoutWordToken)
                {
                    Or(_coincidentWithoutWordToken!, terminal, stateTerminals);
                }
            }
        }
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are valid in the same state.</summary>
    public bool Contains(Symbol a, Symbol b) => Get(_coincident, a.Index, b.Index);

    /// <summary>
    /// Whether the word token (a terminal) is valid in all the states in which <paramref name="a"/> and
    /// <paramref name="b"/> are valid (<c>states_with(a, b).iter().all(...)</c>), true when there is no such state.
    /// </summary>
    public bool WordTokenIsValidInAllStatesWith(Symbol a, Symbol b) =>
        _coincidentWithoutWordToken is null
            ? throw new InvalidOperationException("The index was built without a terminal word token.")
            : !Get(_coincidentWithoutWordToken, a.Index, b.Index);

    private void Or(ulong[] matrix, int row, ulong[] bits)
    {
        var offset = row * _words;
        for (var w = 0; w < _words; w++)
        {
            matrix[offset + w] |= bits[w];
        }
    }

    private bool Get(ulong[] matrix, int row, int column) => (matrix[(row * _words) + (column >> 6)] & (1UL << (column & 63))) != 0;
}
