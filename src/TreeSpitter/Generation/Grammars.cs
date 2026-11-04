namespace TreeSpitter.Generation;

// A port of tree-sitter's cli/generate/src/grammars.rs.

/// <summary>The visibility of a variable. The order matters: visible kinds are <c>&gt;= Anonymous</c>.</summary>
internal enum VariableType : byte
{
    Hidden,
    Auxiliary,
    Anonymous,
    Named,
}

internal sealed class Variable(string name, VariableType kind, IRule rule)
{
    public string Name { get; set; } = name;

    public VariableType Kind { get; set; } = kind;

    public IRule Rule { get; set; } = rule;
}

internal readonly record struct PrecedenceEntry(bool IsSymbol, string Value) : IComparable<PrecedenceEntry>
{
    public static PrecedenceEntry Name(string value) => new(false, value);

    public static PrecedenceEntry Symbol(string value) => new(true, value);

    public int CompareTo(PrecedenceEntry other)
    {
        var result = IsSymbol.CompareTo(other.IsSymbol);
        return result != 0 ? result : string.CompareOrdinal(Value, other.Value);
    }

    public override string ToString() => IsSymbol ? "$" + Value : "'" + Value + "'";
}

internal sealed class ReservedWordContext<T>(string name, List<T> reservedWords)
{
    public string Name { get; } = name;

    public List<T> ReservedWords { get; } = reservedWords;
}

internal sealed class InputGrammar
{
    public string Name { get; set; } = "";

    public List<Variable> Variables { get; } = [];

    public List<IRule> ExtraSymbols { get; set; } = [];

    public List<List<string>> ExpectedConflicts { get; set; } = [];

    public List<List<PrecedenceEntry>> PrecedenceOrderings { get; set; } = [];

    public List<IRule> ExternalTokens { get; set; } = [];

    public List<string> VariablesToInline { get; set; } = [];

    public List<string> SupertypeSymbols { get; set; } = [];

    public string? WordToken { get; set; }

    public List<ReservedWordContext<IRule>> ReservedWords { get; } = [];
}

internal sealed class LexicalVariable(string name, VariableType kind, int implicitPrecedence, int startState)
{
    public string Name { get; } = name;

    public VariableType Kind { get; } = kind;

    public int ImplicitPrecedence { get; } = implicitPrecedence;

    public int StartState { get; } = startState;
}

internal sealed class LexicalGrammar(Nfa nfa, List<LexicalVariable> variables)
{
    public Nfa Nfa { get; } = nfa;

    public List<LexicalVariable> Variables { get; } = variables;

    /// <summary>The variables of NFA states (sorted ids), without consecutive duplicates.</summary>
    public IEnumerable<int> VariableIndicesForNfaStates(IReadOnlyList<int> stateIds)
    {
        var previous = -1;
        foreach (var stateId in stateIds)
        {
            var variableId = VariableIndexForNfaState(stateId);
            if (variableId != previous)
            {
                previous = variableId;
                yield return variableId;
            }
        }
    }

    /// <summary>The variable of an NFA state: the first variable whose start state is greater than or equal to the state.</summary>
    public int VariableIndexForNfaState(int stateId)
    {
        // partition_point(|v| v.start_state < state_id)
        int low = 0, high = Variables.Count;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (Variables[mid].StartState < stateId)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}

internal sealed class ProductionStep : IEquatable<ProductionStep>, IComparable<ProductionStep>
{
    public const int NoReservedWords = int.MaxValue;

    public ProductionStep(Symbol symbol)
    {
        Symbol = symbol;
    }

    public Symbol Symbol { get; set; }

    public Precedence Precedence { get; set; }

    public Associativity? Associativity { get; set; }

    public Alias? Alias { get; set; }

    public string? FieldName { get; set; }

    public int ReservedWordSetId { get; set; }

    public ProductionStep Clone() => new(Symbol)
    {
        Precedence = Precedence,
        Associativity = Associativity,
        Alias = Alias,
        FieldName = FieldName,
        ReservedWordSetId = ReservedWordSetId,
    };

    public bool Equals(ProductionStep? other) =>
        other is not null &&
        Symbol == other.Symbol &&
        Precedence == other.Precedence &&
        Associativity == other.Associativity &&
        Equals(Alias, other.Alias) &&
        FieldName == other.FieldName &&
        ReservedWordSetId == other.ReservedWordSetId;

    public override bool Equals(object? obj) => obj is ProductionStep other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Symbol.GetHashCode();
            hash = hash * 31 + Precedence.GetHashCode();
            hash = hash * 31 + (Associativity.HasValue ? (int)Associativity.Value + 1 : 0);
            hash = hash * 31 + (Alias?.GetHashCode() ?? 0);
            hash = hash * 31 + (FieldName is null ? 0 : StringComparer.Ordinal.GetHashCode(FieldName));
            hash = hash * 31 + ReservedWordSetId;
            return hash;
        }
    }

    public int CompareTo(ProductionStep? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Symbol.CompareTo(other.Symbol);
        if (result != 0)
        {
            return result;
        }

        result = Precedence.CompareTo(other.Precedence);
        if (result != 0)
        {
            return result;
        }

        result = CompareAssociativity(Associativity, other.Associativity);
        if (result != 0)
        {
            return result;
        }

        result = Alias.Compare(Alias, other.Alias);
        if (result != 0)
        {
            return result;
        }

        result = CompareField(FieldName, other.FieldName);
        if (result != 0)
        {
            return result;
        }

        return ReservedWordSetId.CompareTo(other.ReservedWordSetId);
    }

    public static int CompareAssociativity(Associativity? left, Associativity? right)
    {
        if (!left.HasValue)
        {
            return right.HasValue ? -1 : 0;
        }

        return !right.HasValue ? 1 : left.Value.CompareTo(right.Value);
    }

    public static int CompareField(string? left, string? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return right is null ? 1 : string.CompareOrdinal(left, right);
    }
}

/// <summary>A production: a sequence of steps. The instances are compared by reference in the inline map.</summary>
internal sealed class Production
{
    public List<ProductionStep> Steps { get; set; } = [];

    public int DynamicPrecedence { get; set; }

    public Symbol? FirstSymbol => Steps.Count > 0 ? Steps[0].Symbol : null;

    public Production Clone() => new()
    {
        Steps = Steps.Select(s => s.Clone()).ToList(),
        DynamicPrecedence = DynamicPrecedence,
    };

    public bool StructurallyEquals(Production other)
    {
        if (DynamicPrecedence != other.DynamicPrecedence || Steps.Count != other.Steps.Count)
        {
            return false;
        }

        for (var i = 0; i < Steps.Count; i++)
        {
            if (!Steps[i].Equals(other.Steps[i]))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class InlinedProductionMap
{
    public List<Production> Productions { get; } = [];

    public Dictionary<(Production Production, int StepIndex), List<int>> ProductionMap { get; } = new(new ProductionStepKeyComparer());

    public IReadOnlyList<int>? InlinedProductionIndices(Production production, int stepIndex) =>
        ProductionMap.TryGetValue((production, stepIndex), out var indices) ? indices : null;

    private sealed class ProductionStepKeyComparer : IEqualityComparer<(Production Production, int StepIndex)>
    {
        public bool Equals((Production Production, int StepIndex) x, (Production Production, int StepIndex) y) =>
            ReferenceEquals(x.Production, y.Production) && x.StepIndex == y.StepIndex;

        public int GetHashCode((Production Production, int StepIndex) obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Production) * 31 + obj.StepIndex;
    }
}

internal sealed class SyntaxVariable(string name, VariableType kind, List<Production> productions)
{
    public string Name { get; } = name;

    public VariableType Kind { get; set; } = kind;

    public List<Production> Productions { get; } = productions;

    public bool IsAuxiliary => Kind == VariableType.Auxiliary;

    public bool IsHidden => Kind is VariableType.Hidden or VariableType.Auxiliary;
}

internal sealed class ExternalToken(string name, VariableType kind, Symbol? correspondingInternalToken)
{
    public string Name { get; } = name;

    public VariableType Kind { get; } = kind;

    public Symbol? CorrespondingInternalToken { get; } = correspondingInternalToken;
}

internal sealed class SyntaxGrammar
{
    public List<SyntaxVariable> Variables { get; set; } = [];

    public List<Symbol> ExtraSymbols { get; set; } = [];

    public List<List<Symbol>> ExpectedConflicts { get; set; } = [];

    public List<ExternalToken> ExternalTokens { get; set; } = [];

    public List<Symbol> SupertypeSymbols { get; set; } = [];

    public List<Symbol> VariablesToInline { get; set; } = [];

    public Symbol? WordToken { get; set; }

    public List<List<PrecedenceEntry>> PrecedenceOrderings { get; set; } = [];

    public List<TokenSet> ReservedWordSets { get; set; } = [];
}

/// <summary>The default aliases: the alias of the symbols that always appear aliased with the same name.</summary>
internal sealed class AliasMap : Dictionary<Symbol, Alias>
{
}
