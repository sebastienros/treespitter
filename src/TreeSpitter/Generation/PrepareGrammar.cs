using TreeSpitter.Grammars;
using TreeSpitter.Lexing;

namespace TreeSpitter.Generation;

/// <summary>The error thrown when a grammar can't be compiled to a parser, like the errors of <c>tree-sitter generate</c>.</summary>
public sealed class GrammarException : Exception
{
    /// <summary>Creates an error.</summary>
    public GrammarException(string message) : base(message)
    {
    }

    /// <summary>Creates an error with an inner exception.</summary>
    public GrammarException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The grammar prepared for the generation of the tables, the output of tree-sitter's <c>prepare_grammar</c>.</summary>
internal sealed class PreparedGrammar(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, InlinedProductionMap inlines, AliasMap defaultAliases, string name)
{
    public string Name { get; } = name;

    public SyntaxGrammar SyntaxGrammar { get; } = syntaxGrammar;

    public LexicalGrammar LexicalGrammar { get; } = lexicalGrammar;

    public InlinedProductionMap Inlines { get; } = inlines;

    public AliasMap DefaultAliases { get; } = defaultAliases;
}

internal static class GrammarPreparer
{
    public static PreparedGrammar Prepare(Grammar grammar, List<string>? warnings = null)
    {
        var input = InputGrammarReader.Read(grammar, warnings);
        ValidatePrecedences(input);

        var interned = SymbolInterner.Intern(input, warnings);
        var (syntax, lexical) = TokenExtractor.Extract(interned);
        RepeatExpander.Expand(syntax);
        var syntaxGrammar = GrammarFlattener.Flatten(syntax);
        ValidateIndirectRecursion(syntaxGrammar);
        var lexicalGrammar = TokenExpander.Expand(lexical);
        var defaultAliases = DefaultAliasExtractor.Extract(syntaxGrammar, lexicalGrammar);
        var inlines = InlineProcessor.Process(syntaxGrammar, lexicalGrammar);
        return new PreparedGrammar(syntaxGrammar, lexicalGrammar, inlines, defaultAliases, input.Name);
    }

    /// <summary>
    /// Checks that no rule derives itself through a chain of productions of a single non-terminal, which would loop
    /// while parsing. Like tree-sitter 0.27, the check runs on the flattened productions (repetitions included).
    /// </summary>
    private static void ValidateIndirectRecursion(SyntaxGrammar grammar)
    {
        var transitions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < grammar.Variables.Count; i++)
        {
            indices[grammar.Variables[i].Name] = i;
        }

        foreach (var variable in grammar.Variables)
        {
            var symbols = new SortedSet<int>();
            foreach (var production in variable.Productions)
            {
                if (production.Steps.Count == 1 && production.Steps[0].Symbol.IsNonTerminal)
                {
                    symbols.Add(production.Steps[0].Symbol.Index);
                }
            }

            // Rules referencing themselves directly don't loop
            transitions[variable.Name] = symbols.Select(s => grammar.Variables[s].Name).Where(n => n != variable.Name).ToList();
            order.Add(variable.Name);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in order)
        {
            var path = new List<string>();
            var cycle = GetCycle(start, transitions, visited, path);
            if (cycle is { } c)
            {
                throw new GrammarException("Grammar contains an indirectly recursive rule: " + string.Join(" -> ", path.Skip(c.Start).Take(c.End - c.Start + 1)));
            }
        }

        static (int Start, int End)? GetCycle(string current, Dictionary<string, List<string>> transitions, HashSet<string> visited, List<string> path)
        {
            var firstIndex = path.IndexOf(current);
            if (firstIndex >= 0)
            {
                path.Add(current);
                return (firstIndex, path.Count - 1);
            }

            if (!visited.Add(current))
            {
                return null;
            }

            path.Add(current);
            if (transitions.TryGetValue(current, out var next))
            {
                foreach (var symbol in next)
                {
                    var cycle = GetCycle(symbol, transitions, visited, path);
                    if (cycle is not null)
                    {
                        return cycle;
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            return null;
        }
    }

    private static void ValidatePrecedences(InputGrammar grammar)
    {
        var pairs = new Dictionary<(PrecedenceEntry, PrecedenceEntry), int>();
        foreach (var list in grammar.PrecedenceOrderings)
        {
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var entry1 = list[i];
                    var entry2 = list[j];
                    if (entry1 == entry2)
                    {
                        continue;
                    }

                    var ordering = 1;
                    if (entry1.CompareTo(entry2) > 0)
                    {
                        ordering = -1;
                        (entry1, entry2) = (entry2, entry1);
                    }

                    if (pairs.TryGetValue((entry1, entry2), out var existing))
                    {
                        if (existing != ordering)
                        {
                            throw new GrammarException($"Conflicting orderings for precedences {entry1} and {entry2}");
                        }
                    }
                    else
                    {
                        pairs[(entry1, entry2)] = ordering;
                    }
                }
            }
        }

        var names = new HashSet<string>(grammar.PrecedenceOrderings.SelectMany(l => l).Where(e => !e.IsSymbol).Select(e => e.Value), StringComparer.Ordinal);
        foreach (var variable in grammar.Variables)
        {
            Validate(variable.Name, variable.Rule, names);
        }

        static void Validate(string ruleName, IRule rule, HashSet<string> names)
        {
            switch (rule)
            {
                case RepeatIRule repeat:
                    Validate(ruleName, repeat.Content, names);
                    break;
                case SeqIRule seq:
                    foreach (var element in seq.Elements)
                    {
                        Validate(ruleName, element, names);
                    }

                    break;
                case ChoiceIRule choice:
                    foreach (var element in choice.Elements)
                    {
                        Validate(ruleName, element, names);
                    }

                    break;
                case MetadataIRule metadata:
                    if (metadata.Params.Precedence.Kind == PrecedenceValueKind.Name && !names.Contains(metadata.Params.Precedence.Name!))
                    {
                        throw new GrammarException($"Undeclared precedence '{metadata.Params.Precedence.Name}' in rule '{ruleName}'");
                    }

                    Validate(ruleName, metadata.Rule, names);
                    break;
            }
        }
    }
}

/// <summary>Converts a <see cref="Grammar"/> to tree-sitter's <c>InputGrammar</c>, like <c>parse_grammar.rs</c>.</summary>
internal static class InputGrammarReader
{
    public static InputGrammar Read(Grammar grammar, List<string>? warnings)
    {
        var result = new InputGrammar { Name = grammar.Name, WordToken = grammar.Word };

        var extras = new List<IRule>();
        foreach (var extra in grammar.Extras)
        {
            var rule = ParseRule(extra, false, warnings);
            if (rule is StringIRule { Value.Length: 0 })
            {
                throw new GrammarException("Rules in the `extras` array must not contain empty strings");
            }

            extras.Add(rule);
        }

        var externals = grammar.Externals.Select(e => ParseRule(e, false, warnings)).ToList();
        var precedenceOrderings = new List<List<PrecedenceEntry>>();
        foreach (var list in grammar.Precedences)
        {
            var ordering = new List<PrecedenceEntry>();
            foreach (var entry in list)
            {
                ordering.Add(entry switch
                {
                    StringRule s => PrecedenceEntry.Name(s.Value),
                    SymbolRule s => PrecedenceEntry.Symbol(s.Name),
                    _ => throw new GrammarException("Invalid rule in precedences array. Only strings and symbols are allowed"),
                });
            }

            precedenceOrderings.Add(ordering);
        }

        var rules = grammar.Rules.Select(r => (r.Name, Rule: ParseRule(r.Rule, false, warnings))).ToList();
        var conflicts = grammar.Conflicts.Select(c => c.ToList()).ToList();
        var supertypes = grammar.Supertypes.ToList();
        var inline = grammar.Inline.ToList();
        var reservedSets = grammar.Reserved.Select(set => new ReservedWordContext<IRule>(set.Name, set.Rules.Select(r => ParseRule(r, false, warnings)).ToList())).ToList();

        // tree-sitter 0.27's normalize: the used rules are the ones reachable from the start rule, the word rule, the
        // extras, the externals and the reserved words
        var rulesByName = new Dictionary<string, IRule>(StringComparer.Ordinal);
        foreach (var (name, rule) in rules)
        {
            rulesByName.TryAdd(name, rule);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>();
        if (rules.Count > 0)
        {
            stack.Add(rules[0].Name);
        }

        if (grammar.Word is { } word)
        {
            stack.Add(word);
        }

        foreach (var extra in extras)
        {
            CollectReferencedNames(extra, false, stack);
        }

        foreach (var external in externals)
        {
            CollectReferencedNames(external, true, stack);
        }

        foreach (var set in reservedSets)
        {
            foreach (var reservedWord in set.ReservedWords)
            {
                CollectReferencedNames(reservedWord, false, stack);
            }
        }

        while (stack.Count > 0)
        {
            var name = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (used.Add(name) && rulesByName.TryGetValue(name, out var rule))
            {
                CollectReferencedNames(rule, false, stack);
            }
        }

        foreach (var (name, rule) in rules)
        {
            if (!used.Contains(name))
            {
                conflicts.RemoveAll(c => c.Contains(name));
                supertypes.RemoveAll(s => s == name);
                inline.RemoveAll(s => s == name);
                extras.RemoveAll(r => RuleIsReferenced(r, name, true));
                externals.RemoveAll(r => RuleIsReferenced(r, name, true));
                precedenceOrderings.RemoveAll(o => o.Any(e => e.IsSymbol && e.Value == name));

                // The context of reserved words is kept: an empty context is meaningful
                foreach (var set in reservedSets)
                {
                    set.ReservedWords.RemoveAll(r => RuleIsReferenced(r, name, false));
                }

                continue;
            }

            if (warnings is not null && extras.Any(r => RuleIsReferenced(r, name, false)) && MatchesEmptyString(rule is MetadataIRule metadata ? metadata.Rule : rule))
            {
                warnings.Add($"named extra rule `{name}` matches the empty string. inline this to avoid infinite loops while parsing.");
            }

            result.Variables.Add(new Variable(name, VariableType.Named, rule));
        }

        result.ReservedWords.AddRange(reservedSets);
        result.ExtraSymbols = extras;
        result.ExternalTokens = externals;
        result.PrecedenceOrderings = precedenceOrderings;
        result.ExpectedConflicts = conflicts;
        result.SupertypeSymbols = supertypes;
        result.VariablesToInline = inline;
        return result;
    }

    /// <summary>Whether a string or a pattern matches the empty string.</summary>
    private static bool MatchesEmptyString(IRule rule)
    {
        switch (rule)
        {
            case StringIRule s:
                return s.Value.Length == 0;
            case PatternIRule pattern:
                try
                {
                    return IsNullable(Lexing.RegexParser.Parse(pattern.Value, pattern.Flags));
                }
                catch (Lexing.RegexParseException)
                {
                    return false;
                }

            default:
                return false;
        }

        static bool IsNullable(Lexing.RegexNode node) => node switch
        {
            Lexing.RegexEmpty => true,
            Lexing.RegexLiteral literal => literal.CodePoints.Length == 0,
            Lexing.RegexRepeat repeat => repeat.Min == 0 || IsNullable(repeat.Content),
            Lexing.RegexConcat concat => concat.Items.All(IsNullable),
            Lexing.RegexAlternation alternation => alternation.Alternatives.Any(IsNullable),
            _ => false,
        };
    }

    /// <summary>
    /// Adds the names referenced by a rule, tree-sitter's <c>collect_referenced_ids</c>: an external names itself, its
    /// top-level symbol isn't a reference.
    /// </summary>
    private static void CollectReferencedNames(IRule rule, bool skipTopLevel, List<string> names)
    {
        switch (rule)
        {
            case NamedSymbolIRule named:
                if (!skipTopLevel)
                {
                    names.Add(named.Name);
                }

                break;
            case ChoiceIRule choice:
                foreach (var element in choice.Elements)
                {
                    CollectReferencedNames(element, false, names);
                }

                break;
            case SeqIRule seq:
                foreach (var element in seq.Elements)
                {
                    CollectReferencedNames(element, false, names);
                }

                break;
            case MetadataIRule metadata:
                CollectReferencedNames(metadata.Rule, skipTopLevel, names);
                break;
            case ReservedIRule reserved:
                CollectReferencedNames(reserved.Rule, skipTopLevel, names);
                break;
            case RepeatIRule repeat:
                CollectReferencedNames(repeat.Content, false, names);
                break;
        }
    }

    private static bool RuleIsReferenced(IRule rule, string target, bool isExternal) => rule switch
    {
        NamedSymbolIRule named => named.Name == target && !isExternal,
        ChoiceIRule choice => choice.Elements.Any(r => RuleIsReferenced(r, target, false)),
        SeqIRule seq => seq.Elements.Any(r => RuleIsReferenced(r, target, false)),
        MetadataIRule metadata => RuleIsReferenced(metadata.Rule, target, isExternal),
        ReservedIRule reserved => RuleIsReferenced(reserved.Rule, target, isExternal),
        RepeatIRule repeat => RuleIsReferenced(repeat.Content, target, false),
        _ => false,
    };

    private static Precedence ToPrecedence(PrecedenceValue value) =>
        value.IsNamed ? Precedence.Named(value.Name!) : Precedence.Integer(value.Number);

    public static IRule ParseRule(Rule rule, bool isToken, List<string>? warnings)
    {
        switch (rule)
        {
            case AliasRule alias:
                return IRule.Alias(ParseRule(alias.Content, isToken, warnings), alias.Value, alias.Named);
            case BlankRule:
                return IRule.Blank;
            case StringRule s:
                return new StringIRule(s.Value);
            case PatternRule p:
                {
                    var flags = new System.Text.StringBuilder();
                    foreach (var c in p.Flags ?? "")
                    {
                        if (c == 'i')
                        {
                            flags.Append(c);
                        }
                        else if (c != 'u' && c != 'v')
                        {
                            warnings?.Add($"Warning: unsupported flag {c}");
                        }
                    }

                    return new PatternIRule(p.Value, flags.ToString());
                }

            case SymbolRule s:
                if (isToken)
                {
                    throw new GrammarException($"Grammar Error: Unexpected rule `{s.Name}` in `token()` call");
                }

                return new NamedSymbolIRule(s.Name);
            case ChoiceRule c:
                return IRule.Choice(c.Members.Select(m => ParseRule(m, isToken, warnings)));
            case FieldRule f:
                return IRule.Field(f.Name, ParseRule(f.Content, isToken, warnings));
            case SeqRule s:
                return IRule.Seq(s.Members.Select(m => ParseRule(m, isToken, warnings)).ToList());
            case Repeat1Rule r:
                return IRule.Repeat(ParseRule(r.Content, isToken, warnings));
            case RepeatRule r:
                return IRule.Choice([IRule.Repeat(ParseRule(r.Content, isToken, warnings)), IRule.Blank]);
            case PrecRule p:
                {
                    var content = ParseRule(p.Content, isToken, warnings);
                    return p.Precedence switch
                    {
                        PrecedenceKind.Left => IRule.PrecLeft(ToPrecedence(p.Value), content),
                        PrecedenceKind.Right => IRule.PrecRight(ToPrecedence(p.Value), content),
                        PrecedenceKind.Dynamic => IRule.PrecDynamic(p.Value.Number, content),
                        _ => IRule.Prec(ToPrecedence(p.Value), content),
                    };
                }

            case ReservedRule r:
                return new ReservedIRule(ParseRule(r.Content, isToken, warnings), r.ContextName);
            case TokenRule t when t.Immediate:
                // Like tree-sitter 0.27, the content of an immediate token is parsed as a token
                return IRule.ImmediateToken(ParseRule(t.Content, true, warnings));
            case TokenRule t:
                return IRule.Token(ParseRule(t.Content, true, warnings));
            default:
                throw new GrammarException($"Unsupported rule {rule}");
        }
    }
}

/// <summary>The interned grammar, and the grammar after the extraction of the tokens.</summary>
internal sealed class IntermediateGrammar<TExtra, TExternal>
{
    public List<Variable> Variables { get; set; } = [];

    public List<TExtra> ExtraSymbols { get; set; } = [];

    public List<List<Symbol>> ExpectedConflicts { get; set; } = [];

    public List<List<PrecedenceEntry>> PrecedenceOrderings { get; set; } = [];

    public List<TExternal> ExternalTokens { get; set; } = [];

    public List<Symbol> VariablesToInline { get; set; } = [];

    public List<Symbol> SupertypeSymbols { get; set; } = [];

    public Symbol? WordToken { get; set; }

    public List<ReservedWordContext<TExtra>> ReservedWordSets { get; set; } = [];
}

internal sealed class ExtractedLexicalGrammar(List<Variable> variables, List<IRule> separators)
{
    public List<Variable> Variables { get; } = variables;

    public List<IRule> Separators { get; } = separators;
}

internal static class SymbolInterner
{
    public static IntermediateGrammar<IRule, Variable> Intern(InputGrammar grammar, List<string>? warnings = null)
    {
        if (grammar.Variables.Count == 0)
        {
            throw new GrammarException("The grammar has no rules.");
        }

        if (VariableTypeForName(grammar.Variables[0].Name) == VariableType.Hidden)
        {
            throw new GrammarException("A grammar's start rule must be visible.");
        }

        var result = new IntermediateGrammar<IRule, Variable>();
        foreach (var variable in grammar.Variables)
        {
            result.Variables.Add(new Variable(variable.Name, VariableTypeForName(variable.Name), InternRule(grammar, variable.Rule)));
        }

        foreach (var external in grammar.ExternalTokens)
        {
            var rule = InternRule(grammar, external);
            var (name, kind) = external is NamedSymbolIRule named ? (named.Name, VariableTypeForName(named.Name)) : ("", VariableType.Anonymous);
            result.ExternalTokens.Add(new Variable(name, kind, rule));
        }

        foreach (var extra in grammar.ExtraSymbols)
        {
            result.ExtraSymbols.Add(InternRule(grammar, extra));
        }

        foreach (var name in grammar.SupertypeSymbols)
        {
            result.SupertypeSymbols.Add(InternName(grammar, name) ?? throw new GrammarException($"Undefined symbol `{name}` in grammar's supertypes array"));
        }

        foreach (var set in grammar.ReservedWords)
        {
            result.ReservedWordSets.Add(new ReservedWordContext<IRule>(set.Name, set.ReservedWords.Select(r => InternRule(grammar, r)).ToList()));
        }

        foreach (var conflict in grammar.ExpectedConflicts)
        {
            result.ExpectedConflicts.Add(conflict.Select(name => InternName(grammar, name) ?? throw new GrammarException($"Undefined symbol `{name}` in grammar's conflicts array")).ToList());
        }

        foreach (var name in grammar.VariablesToInline)
        {
            if (InternName(grammar, name) is { } symbol)
            {
                result.VariablesToInline.Add(symbol);
            }
        }

        // tree-sitter 0.27: an inlined rule never produces nodes, its supertype entry is dropped
        var supertypes = new List<Symbol>();
        for (var i = 0; i < result.SupertypeSymbols.Count; i++)
        {
            if (result.VariablesToInline.Contains(result.SupertypeSymbols[i]))
            {
                warnings?.Add($"rule `{grammar.SupertypeSymbols[i]}` is both a supertype and inlined. the supertype is ignored.");
            }
            else
            {
                supertypes.Add(result.SupertypeSymbols[i]);
            }
        }

        result.SupertypeSymbols.Clear();
        result.SupertypeSymbols.AddRange(supertypes);

        if (grammar.WordToken is { } word)
        {
            result.WordToken = InternName(grammar, word) ?? throw new GrammarException($"Undefined symbol `{word}` as grammar's word token");
        }

        for (var i = 0; i < result.Variables.Count; i++)
        {
            if (result.SupertypeSymbols.Contains(Symbol.NonTerminal(i)))
            {
                result.Variables[i].Kind = VariableType.Hidden;
            }
        }

        result.PrecedenceOrderings = grammar.PrecedenceOrderings;
        return result;
    }

    private static IRule InternRule(InputGrammar grammar, IRule rule) => rule switch
    {
        ChoiceIRule choice => new ChoiceIRule(choice.Elements.Select(e => InternRule(grammar, e)).ToList()),
        SeqIRule seq => new SeqIRule(seq.Elements.Select(e => InternRule(grammar, e)).ToList()),
        RepeatIRule repeat => new RepeatIRule(InternRule(grammar, repeat.Content)),
        MetadataIRule metadata => new MetadataIRule(metadata.Params.Copy(), InternRule(grammar, metadata.Rule)),
        ReservedIRule reserved => new ReservedIRule(InternRule(grammar, reserved.Rule), reserved.ContextName),
        NamedSymbolIRule named => new SymbolIRule(InternName(grammar, named.Name) ?? throw new GrammarException($"Undefined symbol `{named.Name}`")),
        _ => rule,
    };

    private static Symbol? InternName(InputGrammar grammar, string name)
    {
        for (var i = 0; i < grammar.Variables.Count; i++)
        {
            if (grammar.Variables[i].Name == name)
            {
                return Symbol.NonTerminal(i);
            }
        }

        for (var i = 0; i < grammar.ExternalTokens.Count; i++)
        {
            if (grammar.ExternalTokens[i] is NamedSymbolIRule named && named.Name == name)
            {
                return Symbol.External(i);
            }
        }

        return null;
    }

    internal static VariableType VariableTypeForName(string name) => name.StartsWith("_", StringComparison.Ordinal) ? VariableType.Hidden : VariableType.Named;
}

internal sealed class TokenExtractor
{
    private string _currentVariableName = "";
    private int _currentVariableTokenCount;
    private bool _isFirstRule;
    private readonly List<Variable> _extractedVariables = [];
    private readonly List<int> _extractedUsageCounts = [];

    public static (IntermediateGrammar<Symbol, ExternalToken>, ExtractedLexicalGrammar) Extract(IntermediateGrammar<IRule, Variable> grammar)
    {
        var extractor = new TokenExtractor();
        for (var i = 0; i < grammar.Variables.Count; i++)
        {
            extractor.ExtractTokensInVariable(i == 0, grammar.Variables[i]);
        }

        foreach (var variable in grammar.ExternalTokens)
        {
            extractor.ExtractTokensInVariable(false, variable);
        }

        var lexicalVariables = extractor._extractedVariables;

        // A variable whose whole rule was extracted as a token, which isn't used anywhere else, gives its name to the token
        var variables = new List<Variable>();
        var replacer = new SymbolReplacer();
        for (var i = 0; i < grammar.Variables.Count; i++)
        {
            var variable = grammar.Variables[i];
            if (variable.Rule is SymbolIRule { Symbol.Kind: SymbolType.Terminal } symbolRule)
            {
                var index = symbolRule.Symbol.Index;
                if (i > 0 && extractor._extractedUsageCounts[index] == 1)
                {
                    var lexicalVariable = lexicalVariables[index];
                    if (lexicalVariable.Kind == VariableType.Auxiliary || variable.Kind != VariableType.Hidden)
                    {
                        lexicalVariable.Kind = variable.Kind;
                        lexicalVariable.Name = variable.Name;
                        replacer.Replacements[i] = index;
                        continue;
                    }
                }
            }

            variables.Add(variable);
        }

        foreach (var variable in variables)
        {
            variable.Rule = replacer.ReplaceSymbolsInRule(variable.Rule);
        }

        var result = new IntermediateGrammar<Symbol, ExternalToken>
        {
            Variables = variables,
            ExpectedConflicts = grammar.ExpectedConflicts.Select(conflict =>
            {
                var symbols = conflict.Select(replacer.ReplaceSymbol).Distinct().ToList();
                symbols.Sort();
                return symbols;
            }).ToList(),
            SupertypeSymbols = grammar.SupertypeSymbols.Select(s =>
            {
                // A supertype absorbed into a token isn't allowed
                var symbol = replacer.ReplaceSymbol(s);
                return symbol.IsTerminal ? throw new GrammarException($"Terminal rule '{lexicalVariables[symbol.Index].Name}' cannot be used as a supertype") : symbol;
            }).ToList(),
            VariablesToInline = grammar.VariablesToInline.Select(replacer.ReplaceSymbol).ToList(),
            PrecedenceOrderings = grammar.PrecedenceOrderings,
        };

        var separators = new List<IRule>();
        foreach (var rule in grammar.ExtraSymbols)
        {
            if (rule is SymbolIRule symbol)
            {
                result.ExtraSymbols.Add(replacer.ReplaceSymbol(symbol.Symbol));
            }
            else if (lexicalVariables.FindIndex(v => v.Rule.Equals(rule)) is var index and >= 0)
            {
                result.ExtraSymbols.Add(Symbol.Terminal(index));
            }
            else
            {
                separators.Add(rule);
            }
        }

        foreach (var external in grammar.ExternalTokens)
        {
            var rule = replacer.ReplaceSymbolsInRule(external.Rule);
            if (rule is not SymbolIRule symbolRule)
            {
                throw new GrammarException("Non-symbol rules cannot be used as external tokens");
            }

            var symbol = symbolRule.Symbol;
            if (symbol.IsNonTerminal)
            {
                throw new GrammarException($"Rule '{variables[symbol.Index].Name}' cannot be used as both an external token and a non-terminal rule");
            }

            result.ExternalTokens.Add(symbol.IsExternal
                ? new ExternalToken(external.Name, external.Kind, null)
                : new ExternalToken(lexicalVariables[symbol.Index].Name, external.Kind, symbol));
        }

        if (grammar.WordToken is { } word)
        {
            var token = replacer.ReplaceSymbol(word);
            if (token.IsNonTerminal)
            {
                var wordVariable = variables[token.Index];
                var conflicting = variables.Where((v, i) => i != token.Index && v.Rule.Equals(wordVariable.Rule)).Select(v => v.Name).FirstOrDefault();
                throw new GrammarException($"Non-terminal symbol '{wordVariable.Name}' cannot be used as the word token" + (conflicting is null ? "" : $", because its rule is duplicated in '{conflicting}'"));
            }

            result.WordToken = token;
        }

        foreach (var context in grammar.ReservedWordSets)
        {
            var words = new List<Symbol>();
            foreach (var rule in context.ReservedWords)
            {
                if (rule is SymbolIRule symbol)
                {
                    words.Add(replacer.ReplaceSymbol(symbol.Symbol));
                }
                else if (lexicalVariables.FindIndex(v => v.Rule.Equals(rule)) is var index and >= 0)
                {
                    words.Add(Symbol.Terminal(index));
                }
                else
                {
                    var tokenName = rule switch
                    {
                        StringIRule s => s.Value,
                        PatternIRule p => p.Value,
                        _ => "unknown",
                    };
                    throw new GrammarException($"Reserved word '{tokenName}' must be a token");
                }
            }

            result.ReservedWordSets.Add(new ReservedWordContext<Symbol>(context.Name, words));
        }

        return (result, new ExtractedLexicalGrammar(lexicalVariables, separators));
    }

    private void ExtractTokensInVariable(bool isFirst, Variable variable)
    {
        _currentVariableName = variable.Name;
        _currentVariableTokenCount = 0;
        _isFirstRule = isFirst;
        variable.Rule = ExtractTokensInRule(variable.Rule);
    }

    private IRule ExtractTokensInRule(IRule input)
    {
        switch (input)
        {
            case StringIRule s:
                return new SymbolIRule(ExtractToken(input, s.Value));
            case PatternIRule:
                return new SymbolIRule(ExtractToken(input, null));
            case MetadataIRule metadata:
                if (metadata.Params.IsToken)
                {
                    var parameters = metadata.Params.Copy();
                    parameters.IsToken = false;
                    var stringValue = metadata.Rule is StringIRule value ? value.Value : null;
                    var ruleToExtract = parameters.IsDefault ? metadata.Rule : input;
                    return new SymbolIRule(ExtractToken(ruleToExtract, stringValue));
                }

                return new MetadataIRule(metadata.Params.Copy(), ExtractTokensInRule(metadata.Rule));
            case RepeatIRule repeat:
                return new RepeatIRule(ExtractTokensInRule(repeat.Content));
            case SeqIRule seq:
                return new SeqIRule(seq.Elements.Select(ExtractTokensInRule).ToList());
            case ChoiceIRule choice:
                return new ChoiceIRule(choice.Elements.Select(ExtractTokensInRule).ToList());
            case ReservedIRule reserved:
                return new ReservedIRule(ExtractTokensInRule(reserved.Rule), reserved.ContextName);
            default:
                return input;
        }
    }

    private Symbol ExtractToken(IRule rule, string? stringValue)
    {
        for (var i = 0; i < _extractedVariables.Count; i++)
        {
            if (_extractedVariables[i].Rule.Equals(rule))
            {
                _extractedUsageCounts[i]++;
                return Symbol.Terminal(i);
            }
        }

        var index = _extractedVariables.Count;
        Variable variable;
        if (stringValue is not null)
        {
            if (stringValue.Length == 0 && !_isFirstRule)
            {
                throw new GrammarException($"The rule `{_currentVariableName}` contains an empty string.\nTree-sitter does not support syntactic rules that contain an empty string\nunless they are used only as the grammar's start rule.\n");
            }

            variable = new Variable(stringValue, VariableType.Anonymous, rule);
        }
        else
        {
            _currentVariableTokenCount++;
            variable = new Variable($"{_currentVariableName}_token{_currentVariableTokenCount}", VariableType.Auxiliary, rule);
        }

        _extractedVariables.Add(variable);
        _extractedUsageCounts.Add(1);
        return Symbol.Terminal(index);
    }

    private sealed class SymbolReplacer
    {
        public Dictionary<int, int> Replacements { get; } = [];

        public IRule ReplaceSymbolsInRule(IRule rule) => rule switch
        {
            SymbolIRule symbol => new SymbolIRule(ReplaceSymbol(symbol.Symbol)),
            ChoiceIRule choice => new ChoiceIRule(choice.Elements.Select(ReplaceSymbolsInRule).ToList()),
            SeqIRule seq => new SeqIRule(seq.Elements.Select(ReplaceSymbolsInRule).ToList()),
            RepeatIRule repeat => new RepeatIRule(ReplaceSymbolsInRule(repeat.Content)),
            MetadataIRule metadata => new MetadataIRule(metadata.Params.Copy(), ReplaceSymbolsInRule(metadata.Rule)),
            ReservedIRule reserved => new ReservedIRule(ReplaceSymbolsInRule(reserved.Rule), reserved.ContextName),
            _ => rule,
        };

        public Symbol ReplaceSymbol(Symbol symbol)
        {
            if (!symbol.IsNonTerminal)
            {
                return symbol;
            }

            if (Replacements.TryGetValue(symbol.Index, out var replacement))
            {
                return Symbol.Terminal(replacement);
            }

            var adjusted = symbol.Index;
            foreach (var replaced in Replacements.Keys)
            {
                if (replaced < symbol.Index)
                {
                    adjusted--;
                }
            }

            return Symbol.NonTerminal(adjusted);
        }
    }
}

internal sealed class RepeatExpander
{
    private string _variableName = "";
    private int _repeatCountInVariable;
    private readonly int _precedingSymbolCount;
    private readonly List<Variable> _auxiliaryVariables = [];
    private readonly Dictionary<IRule, Symbol> _existingRepeats = [];

    private RepeatExpander(int precedingSymbolCount)
    {
        _precedingSymbolCount = precedingSymbolCount;
    }

    public static void Expand(IntermediateGrammar<Symbol, ExternalToken> grammar)
    {
        var expander = new RepeatExpander(grammar.Variables.Count);
        for (var i = 0; i < grammar.Variables.Count; i++)
        {
            if (expander.ExpandVariable(i, grammar.Variables[i]))
            {
                // A hidden variable with a top-level repetition becomes recursive: it can't be inlined
                grammar.VariablesToInline.RemoveAll(s => s == Symbol.NonTerminal(i));
            }
        }

        grammar.Variables.AddRange(expander._auxiliaryVariables);
    }

    private bool ExpandVariable(int index, Variable variable)
    {
        _variableName = variable.Name;
        _repeatCountInVariable = 0;
        var rule = variable.Rule;
        variable.Rule = IRule.Blank;

        if (variable.Kind == VariableType.Hidden && rule is RepeatIRule repeat)
        {
            var inner = ExpandRule(repeat.Content);
            variable.Rule = WrapRuleInBinaryTree(Symbol.NonTerminal(index), inner);
            variable.Kind = VariableType.Auxiliary;
            return true;
        }

        variable.Rule = ExpandRule(rule);
        return false;
    }

    private IRule ExpandRule(IRule rule)
    {
        switch (rule)
        {
            case ChoiceIRule choice:
                return new ChoiceIRule(choice.Elements.Select(ExpandRule).ToList());
            case SeqIRule seq:
                return new SeqIRule(seq.Elements.Select(ExpandRule).ToList());
            case MetadataIRule metadata:
                return new MetadataIRule(metadata.Params.Copy(), ExpandRule(metadata.Rule));
            case RepeatIRule repeat:
                {
                    var inner = ExpandRule(repeat.Content);
                    if (_existingRepeats.TryGetValue(inner, out var existing))
                    {
                        return new SymbolIRule(existing);
                    }

                    _repeatCountInVariable++;
                    var name = $"{_variableName}_repeat{_repeatCountInVariable}";
                    var repeatSymbol = Symbol.NonTerminal(_precedingSymbolCount + _auxiliaryVariables.Count);
                    _existingRepeats[inner] = repeatSymbol;
                    _auxiliaryVariables.Add(new Variable(name, VariableType.Auxiliary, WrapRuleInBinaryTree(repeatSymbol, inner)));
                    return new SymbolIRule(repeatSymbol);
                }

            default:
                return rule;
        }
    }

    private static IRule WrapRuleInBinaryTree(Symbol symbol, IRule rule) =>
        IRule.Choice([IRule.Seq([new SymbolIRule(symbol), new SymbolIRule(symbol)]), rule]);
}

internal sealed class GrammarFlattener
{
    private Production _production = new();
    private readonly Dictionary<string, int> _reservedWordSetIds;
    private readonly List<Precedence> _precedenceStack = [];
    private readonly List<Associativity> _associativityStack = [];
    private readonly List<int> _reservedWordStack = [];
    private readonly List<Alias> _aliasStack = [];
    private readonly List<string> _fieldNameStack = [];

    private GrammarFlattener(Dictionary<string, int> reservedWordSetIds)
    {
        _reservedWordSetIds = reservedWordSetIds;
    }

    public static SyntaxGrammar Flatten(IntermediateGrammar<Symbol, ExternalToken> grammar)
    {
        var reservedIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < grammar.ReservedWordSets.Count; i++)
        {
            reservedIds[grammar.ReservedWordSets[i].Name] = i;
        }

        var flattener = new GrammarFlattener(reservedIds);
        var variables = grammar.Variables.Select(flattener.FlattenVariable).ToList();

        for (var i = 0; i < variables.Count; i++)
        {
            var symbol = Symbol.NonTerminal(i);
            var used = variables.Any(v => v.Productions.Any(p => p.Steps.Any(s => s.Symbol == symbol)));
            foreach (var production in variables[i].Productions)
            {
                if (used && production.Steps.Count == 0)
                {
                    throw new GrammarException($"The rule `{variables[i].Name}` matches the empty string.\nTree-sitter does not support syntactic rules that match the empty string\nunless they are used only as the grammar's start rule.\n");
                }

                if (grammar.VariablesToInline.Contains(symbol) && production.Steps.Any(s => s.Symbol == symbol))
                {
                    throw new GrammarException($"Rule `{variables[i].Name}` cannot be inlined because it contains a reference to itself");
                }
            }
        }

        var reservedWordSets = grammar.ReservedWordSets.Select(s => TokenSet.From(s.ReservedWords)).ToList();
        if (reservedWordSets.Count == 0)
        {
            reservedWordSets.Add(new TokenSet());
        }

        return new SyntaxGrammar
        {
            ExtraSymbols = grammar.ExtraSymbols,
            ExpectedConflicts = grammar.ExpectedConflicts,
            VariablesToInline = grammar.VariablesToInline,
            PrecedenceOrderings = grammar.PrecedenceOrderings,
            ExternalTokens = grammar.ExternalTokens,
            SupertypeSymbols = grammar.SupertypeSymbols,
            WordToken = grammar.WordToken,
            ReservedWordSets = reservedWordSets,
            Variables = variables,
        };
    }

    private SyntaxVariable FlattenVariable(Variable variable)
    {
        var productions = new List<Production>();
        foreach (var rule in ExtractChoices(variable.Rule))
        {
            var production = FlattenRule(rule);
            if (!productions.Any(p => p.StructurallyEquals(production)))
            {
                productions.Add(production);
            }
        }

        return new SyntaxVariable(variable.Name, variable.Kind, productions);
    }

    private Production FlattenRule(IRule rule)
    {
        _production = new Production();
        _aliasStack.Clear();
        _reservedWordStack.Clear();
        _precedenceStack.Clear();
        _associativityStack.Clear();
        _fieldNameStack.Clear();
        Apply(rule, true);
        return _production;
    }

    private bool Apply(IRule rule, bool atEnd)
    {
        switch (rule)
        {
            case SeqIRule seq:
                {
                    var result = false;
                    var lastIndex = seq.Elements.Count - 1;
                    for (var i = 0; i < seq.Elements.Count; i++)
                    {
                        result |= Apply(seq.Elements[i], i == lastIndex && atEnd);
                    }

                    return result;
                }

            case MetadataIRule metadata:
                {
                    var parameters = metadata.Params;
                    var hasPrecedence = false;
                    if (!parameters.Precedence.IsNone)
                    {
                        hasPrecedence = true;
                        _precedenceStack.Add(parameters.Precedence);
                    }

                    var hasAssociativity = false;
                    if (parameters.Associativity is { } associativity)
                    {
                        hasAssociativity = true;
                        _associativityStack.Add(associativity);
                    }

                    var hasAlias = false;
                    if (parameters.Alias is { } alias)
                    {
                        hasAlias = true;
                        _aliasStack.Add(alias);
                    }

                    var hasFieldName = false;
                    if (parameters.FieldName is { } fieldName)
                    {
                        hasFieldName = true;
                        _fieldNameStack.Add(fieldName);
                    }

                    if (Math.Abs(parameters.DynamicPrecedence) > Math.Abs(_production.DynamicPrecedence))
                    {
                        _production.DynamicPrecedence = parameters.DynamicPrecedence;
                    }

                    var didPush = Apply(metadata.Rule, atEnd);

                    if (hasPrecedence)
                    {
                        _precedenceStack.RemoveAt(_precedenceStack.Count - 1);
                        if (didPush && !atEnd)
                        {
                            _production.Steps[_production.Steps.Count - 1].Precedence = _precedenceStack.Count > 0 ? _precedenceStack[_precedenceStack.Count - 1] : Precedence.None;
                        }
                    }

                    if (hasAssociativity)
                    {
                        _associativityStack.RemoveAt(_associativityStack.Count - 1);
                        if (didPush && !atEnd)
                        {
                            _production.Steps[_production.Steps.Count - 1].Associativity = _associativityStack.Count > 0 ? _associativityStack[_associativityStack.Count - 1] : null;
                        }
                    }

                    if (hasAlias)
                    {
                        _aliasStack.RemoveAt(_aliasStack.Count - 1);
                    }

                    if (hasFieldName)
                    {
                        _fieldNameStack.RemoveAt(_fieldNameStack.Count - 1);
                    }

                    return didPush;
                }

            case ReservedIRule reserved:
                {
                    if (!_reservedWordSetIds.TryGetValue(reserved.ContextName, out var id))
                    {
                        throw new GrammarException($"No such reserved word set: {reserved.ContextName}");
                    }

                    _reservedWordStack.Add(id);
                    var didPush = Apply(reserved.Rule, atEnd);
                    _reservedWordStack.RemoveAt(_reservedWordStack.Count - 1);
                    return didPush;
                }

            case SymbolIRule symbol:
                _production.Steps.Add(new ProductionStep(symbol.Symbol)
                {
                    Precedence = _precedenceStack.Count > 0 ? _precedenceStack[_precedenceStack.Count - 1] : Precedence.None,
                    Associativity = _associativityStack.Count > 0 ? _associativityStack[_associativityStack.Count - 1] : null,
                    ReservedWordSetId = _reservedWordStack.Count > 0 ? _reservedWordStack[_reservedWordStack.Count - 1] : 0,
                    Alias = _aliasStack.Count > 0 ? _aliasStack[_aliasStack.Count - 1] : null,
                    FieldName = _fieldNameStack.Count > 0 ? _fieldNameStack[_fieldNameStack.Count - 1] : null,
                });
                return true;
            default:
                return false;
        }
    }

    private static List<IRule> ExtractChoices(IRule rule)
    {
        switch (rule)
        {
            case SeqIRule seq:
                {
                    var result = new List<IRule> { IRule.Blank };
                    foreach (var element in seq.Elements)
                    {
                        var extraction = ExtractChoices(element);
                        var next = new List<IRule>(result.Count * extraction.Count);
                        foreach (var entry in result)
                        {
                            foreach (var extractionEntry in extraction)
                            {
                                next.Add(new SeqIRule([entry, extractionEntry]));
                            }
                        }

                        result = next;
                    }

                    return result;
                }

            case ChoiceIRule choice:
                return choice.Elements.SelectMany(ExtractChoices).ToList();
            case MetadataIRule metadata:
                return ExtractChoices(metadata.Rule).Select(r => (IRule)new MetadataIRule(metadata.Params, r)).ToList();
            case ReservedIRule reserved:
                return ExtractChoices(reserved.Rule).Select(r => (IRule)new ReservedIRule(r, reserved.ContextName)).ToList();
            default:
                return [rule];
        }
    }
}

/// <summary>Builds the NFA of the tokens, tree-sitter's <c>expand_tokens.rs</c>.</summary>
internal sealed class TokenExpander
{
    private readonly Nfa _nfa = new();
    private bool _isSeparator = true;
    private readonly List<int> _precedenceStack = [0];

    public static LexicalGrammar Expand(ExtractedLexicalGrammar grammar)
    {
        var builder = new TokenExpander();
        IRule separatorRule;
        if (grammar.Separators.Count == 0)
        {
            separatorRule = IRule.Blank;
        }
        else
        {
            var separators = new List<IRule>(grammar.Separators) { IRule.Blank };
            separatorRule = IRule.Repeat(IRule.Choice(separators));
        }

        var variables = new List<LexicalVariable>();
        for (var i = 0; i < grammar.Variables.Count; i++)
        {
            var variable = grammar.Variables[i];
            if (variable.Rule.IsEmpty())
            {
                throw new GrammarException($"The rule `{variable.Name}` matches the empty string.\nTree-sitter does not support syntactic rules that match the empty string\nunless they are used only as the grammar's start rule.\n");
            }

            var isImmediateToken = variable.Rule is MetadataIRule { Params.IsMainToken: true };
            builder._isSeparator = false;
            builder._nfa.States.Add(NfaState.Accept(i, GetCompletionPrecedence(variable.Rule)));
            try
            {
                builder.ExpandRule(variable.Rule, builder._nfa.LastStateId);
            }
            catch (RegexParseException e)
            {
                throw new GrammarException($"Error processing rule {variable.Name}: {e.Message}", e);
            }

            if (!isImmediateToken)
            {
                builder._isSeparator = true;
                builder.ExpandRule(separatorRule, builder._nfa.LastStateId);
            }

            variables.Add(new LexicalVariable(variable.Name, variable.Kind, GetImplicitPrecedence(variable.Rule), builder._nfa.LastStateId));
        }

        return new LexicalGrammar(builder._nfa, variables);
    }

    private static int GetImplicitPrecedence(IRule rule) => rule switch
    {
        StringIRule => 2,
        MetadataIRule metadata => metadata.Params.IsMainToken ? GetImplicitPrecedence(metadata.Rule) + 1 : GetImplicitPrecedence(metadata.Rule),
        _ => 0,
    };

    private static int GetCompletionPrecedence(IRule rule) =>
        rule is MetadataIRule { Params.Precedence.Kind: PrecedenceValueKind.Integer } metadata ? metadata.Params.Precedence.Value : 0;

    private bool ExpandRule(IRule rule, int nextStateId)
    {
        switch (rule)
        {
            case PatternIRule pattern:
                {
                    // \w, \s and \d are ASCII classes in tree-sitter
                    var source = pattern.Value
                        .Replace(@"\w", "[0-9A-Za-z_]")
                        .Replace(@"\s", @"[\t-\r ]")
                        .Replace(@"\d", "[0-9]")
                        .Replace(@"\W", "[^0-9A-Za-z_]")
                        .Replace(@"\S", @"[^\t-\r ]")
                        .Replace(@"\D", "[^0-9]");
                    var node = RegexParser.Parse(source, pattern.Flags);
                    return ExpandRegex(node, nextStateId);
                }

            case StringIRule s:
                {
                    var codePoints = CodePoints(s.Value);
                    for (var i = codePoints.Count - 1; i >= 0; i--)
                    {
                        PushAdvance(CharacterSet.FromChar(codePoints[i]), nextStateId);
                        nextStateId = _nfa.LastStateId;
                    }

                    return s.Value.Length > 0;
                }

            case ChoiceIRule choice:
                {
                    var alternatives = new List<int>();
                    var didExpand = false;
                    foreach (var element in choice.Elements)
                    {
                        if (ExpandRule(element, nextStateId))
                        {
                            didExpand = true;
                            alternatives.Add(_nfa.LastStateId);
                        }
                        else
                        {
                            alternatives.Add(nextStateId);
                        }
                    }

                    PushSplits(alternatives);

                    // tree-sitter 0.27: a choice of empty alternatives produces nothing
                    return didExpand;
                }

            case SeqIRule seq:
                {
                    var result = false;
                    for (var i = seq.Elements.Count - 1; i >= 0; i--)
                    {
                        if (ExpandRule(seq.Elements[i], nextStateId))
                        {
                            result = true;
                        }

                        nextStateId = _nfa.LastStateId;
                    }

                    return result;
                }

            case RepeatIRule repeat:
                {
                    _nfa.States.Add(NfaState.Accept(0, 0)); // Placeholder for the split
                    var splitStateId = _nfa.LastStateId;
                    if (ExpandRule(repeat.Content, splitStateId))
                    {
                        _nfa.States[splitStateId] = NfaState.Split(_nfa.LastStateId, nextStateId);
                        return true;
                    }

                    // tree-sitter 0.27 removes the placeholder of a repetition of nothing
                    _nfa.States.RemoveAt(_nfa.States.Count - 1);
                    return false;
                }

            case MetadataIRule metadata:
                {
                    var hasPrecedence = metadata.Params.Precedence.Kind == PrecedenceValueKind.Integer;
                    if (hasPrecedence)
                    {
                        _precedenceStack.Add(metadata.Params.Precedence.Value);
                    }

                    var result = ExpandRule(metadata.Rule, nextStateId);
                    if (hasPrecedence)
                    {
                        _precedenceStack.RemoveAt(_precedenceStack.Count - 1);
                    }

                    return result;
                }

            case BlankIRule:
                return false;
            default:
                throw new GrammarException($"Grammar error: Unexpected rule {rule}");
        }
    }

    private void PushSplits(List<int> alternatives)
    {
        alternatives.Sort();
        var distinct = alternatives.Distinct().Where(i => i != _nfa.LastStateId).ToList();
        foreach (var alternative in distinct)
        {
            PushSplit(alternative);
        }
    }

    private static List<int> CodePoints(string value)
    {
        var result = new List<int>(value.Length);
        for (var i = 0; i < value.Length;)
        {
            i += RegexParser.ReadCodePoint(value, i, out var codePoint);
            result.Add(codePoint);
        }

        return result;
    }

    private bool ExpandRegex(RegexNode node, int nextStateId)
    {
        switch (node)
        {
            case RegexEmpty:
                return false;
            case RegexLiteral literal:
                for (var i = literal.CodePoints.Length - 1; i >= 0; i--)
                {
                    PushAdvance(CharacterSet.FromChar(literal.CodePoints[i]), nextStateId);
                    nextStateId = _nfa.LastStateId;
                }

                return true;
            case RegexClass c:
                PushAdvance(CharacterSet.FromInclusiveRanges(c.Set.Ranges), nextStateId);
                return true;
            case RegexRepeat repeat:
                {
                    var (min, max) = (repeat.Min, repeat.Max);
                    if (min == 0 && max == 1)
                    {
                        return ExpandZeroOrOne(repeat.Content, nextStateId);
                    }

                    if (min == 1 && max < 0)
                    {
                        return ExpandOneOrMore(repeat.Content, nextStateId);
                    }

                    if (min == 0 && max < 0)
                    {
                        return ExpandZeroOrMore(repeat.Content, nextStateId);
                    }

                    if (min == max)
                    {
                        return ExpandCount(repeat.Content, min, nextStateId);
                    }

                    if (max < 0)
                    {
                        return ExpandZeroOrMore(repeat.Content, nextStateId) && ExpandCount(repeat.Content, min, nextStateId);
                    }

                    var result = ExpandCount(repeat.Content, min, nextStateId);
                    for (var i = min; i < max; i++)
                    {
                        if (result)
                        {
                            nextStateId = _nfa.LastStateId;
                        }

                        if (ExpandZeroOrOne(repeat.Content, nextStateId))
                        {
                            result = true;
                        }
                    }

                    return result;
                }

            case RegexConcat concat:
                {
                    var result = false;
                    for (var i = concat.Items.Count - 1; i >= 0; i--)
                    {
                        if (ExpandRegex(concat.Items[i], nextStateId))
                        {
                            result = true;
                            nextStateId = _nfa.LastStateId;
                        }
                    }

                    return result;
                }

            case RegexAlternation alternation:
                {
                    var alternatives = new List<int>();
                    foreach (var alternative in alternation.Alternatives)
                    {
                        alternatives.Add(ExpandRegex(alternative, nextStateId) ? _nfa.LastStateId : nextStateId);
                    }

                    PushSplits(alternatives);
                    return true;
                }

            default:
                throw new GrammarException("Unexpected regular expression node " + node);
        }
    }

    private bool ExpandOneOrMore(RegexNode node, int nextStateId)
    {
        _nfa.States.Add(NfaState.Accept(0, 0)); // Placeholder for the split
        var splitStateId = _nfa.LastStateId;
        if (ExpandRegex(node, splitStateId))
        {
            _nfa.States[splitStateId] = NfaState.Split(_nfa.LastStateId, nextStateId);
            return true;
        }

        _nfa.States.RemoveAt(_nfa.States.Count - 1);
        return false;
    }

    private bool ExpandZeroOrOne(RegexNode node, int nextStateId)
    {
        if (ExpandRegex(node, nextStateId))
        {
            PushSplit(nextStateId);
            return true;
        }

        return false;
    }

    private bool ExpandZeroOrMore(RegexNode node, int nextStateId)
    {
        if (ExpandOneOrMore(node, nextStateId))
        {
            PushSplit(nextStateId);
            return true;
        }

        return false;
    }

    private bool ExpandCount(RegexNode node, int count, int nextStateId)
    {
        var result = false;
        for (var i = 0; i < count; i++)
        {
            if (ExpandRegex(node, nextStateId))
            {
                result = true;
                nextStateId = _nfa.LastStateId;
            }
        }

        return result;
    }

    private void PushAdvance(CharacterSet chars, int stateId) =>
        _nfa.States.Add(NfaState.Advance(chars, stateId, _isSeparator, _precedenceStack[_precedenceStack.Count - 1]));

    private void PushSplit(int stateId) => _nfa.States.Add(NfaState.Split(stateId, _nfa.LastStateId));
}

internal static class DefaultAliasExtractor
{
    private sealed class SymbolStatus
    {
        public List<(Alias Alias, int Count)> Aliases { get; } = [];

        public bool AppearsUnaliased { get; set; }
    }

    public static AliasMap Extract(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar)
    {
        var terminals = Enumerable.Range(0, lexicalGrammar.Variables.Count).Select(_ => new SymbolStatus()).ToArray();
        var nonTerminals = Enumerable.Range(0, syntaxGrammar.Variables.Count).Select(_ => new SymbolStatus()).ToArray();
        var externals = Enumerable.Range(0, syntaxGrammar.ExternalTokens.Count).Select(_ => new SymbolStatus()).ToArray();

        SymbolStatus StatusOf(Symbol symbol) => symbol.Kind switch
        {
            SymbolType.External => externals[symbol.Index],
            SymbolType.NonTerminal => nonTerminals[symbol.Index],
            SymbolType.Terminal => terminals[symbol.Index],
            _ => throw new InvalidOperationException("Unexpected end token"),
        };

        foreach (var variable in syntaxGrammar.Variables)
        {
            foreach (var production in variable.Productions)
            {
                foreach (var step in production.Steps)
                {
                    var status = StatusOf(step.Symbol);
                    if (syntaxGrammar.VariablesToInline.Contains(step.Symbol))
                    {
                        continue;
                    }

                    if (step.Alias is { } alias)
                    {
                        var index = status.Aliases.FindIndex(a => a.Alias.Equals(alias));
                        if (index >= 0)
                        {
                            status.Aliases[index] = (alias, status.Aliases[index].Count + 1);
                        }
                        else
                        {
                            status.Aliases.Add((alias, 1));
                        }
                    }
                    else
                    {
                        status.AppearsUnaliased = true;
                    }
                }
            }
        }

        foreach (var symbol in syntaxGrammar.ExtraSymbols)
        {
            StatusOf(symbol).AppearsUnaliased = true;
        }

        var result = new AliasMap();
        var all = terminals.Select((s, i) => (Symbol.Terminal(i), s))
            .Concat(nonTerminals.Select((s, i) => (Symbol.NonTerminal(i), s)))
            .Concat(externals.Select((s, i) => (Symbol.External(i), s)));
        foreach (var (symbol, status) in all)
        {
            if (status.AppearsUnaliased)
            {
                status.Aliases.Clear();
            }
            else if (status.Aliases.Count > 0)
            {
                // max_by_key((count, -index)): the most frequent alias, the first one on ties
                var best = 0;
                for (var i = 1; i < status.Aliases.Count; i++)
                {
                    if (status.Aliases[i].Count > status.Aliases[best].Count)
                    {
                        best = i;
                    }
                }

                var entry = status.Aliases[best];
                status.Aliases.Clear();
                status.Aliases.Add(entry);
                result[symbol] = entry.Alias;
            }
        }

        foreach (var variable in syntaxGrammar.Variables)
        {
            var positionsToClear = new List<(int, int)>();
            for (var i = 0; i < variable.Productions.Count; i++)
            {
                var production = variable.Productions[i];
                for (var j = 0; j < production.Steps.Count; j++)
                {
                    var step = production.Steps[j];
                    var status = StatusOf(step.Symbol);
                    if (step.Alias is not null && status.Aliases.Count > 0 && step.Alias.Equals(status.Aliases[0].Alias))
                    {
                        var otherProductionsMustUseThisAlias = false;
                        for (var otherI = 0; otherI < variable.Productions.Count; otherI++)
                        {
                            var other = variable.Productions[otherI];
                            if (otherI != i && other.Steps.Count > j && Equals(other.Steps[j].Alias, step.Alias) &&
                                !Equals(result.TryGetValue(other.Steps[j].Symbol, out var a) ? a : null, step.Alias))
                            {
                                otherProductionsMustUseThisAlias = true;
                                break;
                            }
                        }

                        if (!otherProductionsMustUseThisAlias)
                        {
                            positionsToClear.Add((i, j));
                        }
                    }
                }
            }

            foreach (var (productionIndex, stepIndex) in positionsToClear)
            {
                variable.Productions[productionIndex].Steps[stepIndex].Alias = null;
            }
        }

        return result;
    }
}

internal sealed class InlineProcessor
{
    private readonly record struct ProductionStepId(int VariableIndex, int ProductionIndex, int StepIndex);

    private readonly Dictionary<ProductionStepId, List<int>> _productionIndicesByStepId = [];
    private readonly List<Production> _productions = [];

    public static InlinedProductionMap Process(SyntaxGrammar grammar, LexicalGrammar lexicalGrammar)
    {
        foreach (var symbol in grammar.VariablesToInline)
        {
            switch (symbol.Kind)
            {
                case SymbolType.External:
                    throw new GrammarException($"External token `{grammar.ExternalTokens[symbol.Index].Name}` cannot be inlined");
                case SymbolType.Terminal:
                    throw new GrammarException($"Token `{lexicalGrammar.Variables[symbol.Index].Name}` cannot be inlined");
                case SymbolType.NonTerminal when symbol.Index == 0:
                    throw new GrammarException($"Rule `{grammar.Variables[symbol.Index].Name}` cannot be inlined because it is the first rule");
            }
        }

        return new InlineProcessor().Build(grammar);
    }

    private InlinedProductionMap Build(SyntaxGrammar grammar)
    {
        var stepIdsToProcess = new List<ProductionStepId>();
        for (var variableIndex = 0; variableIndex < grammar.Variables.Count; variableIndex++)
        {
            for (var productionIndex = 0; productionIndex < grammar.Variables[variableIndex].Productions.Count; productionIndex++)
            {
                stepIdsToProcess.Add(new ProductionStepId(variableIndex, productionIndex, 0));
                while (stepIdsToProcess.Count > 0)
                {
                    var i = 0;
                    while (i < stepIdsToProcess.Count)
                    {
                        var stepId = stepIdsToProcess[i];
                        var step = ProductionStepForId(stepId, grammar);
                        if (step is not null)
                        {
                            if (grammar.VariablesToInline.Contains(step.Symbol))
                            {
                                var inlined = InlineProductionAtStep(stepId, grammar)
                                    .Select(index => new ProductionStepId(-1, index, stepId.StepIndex))
                                    .ToList();
                                stepIdsToProcess.RemoveAt(i);
                                stepIdsToProcess.InsertRange(i, inlined);
                            }
                            else
                            {
                                stepIdsToProcess[i] = stepId with { StepIndex = stepId.StepIndex + 1 };
                                i++;
                            }
                        }
                        else
                        {
                            stepIdsToProcess.RemoveAt(i);
                        }
                    }
                }
            }
        }

        var result = new InlinedProductionMap();
        result.Productions.AddRange(_productions);
        foreach (var (stepId, indices) in _productionIndicesByStepId)
        {
            var production = ProductionForId(stepId, grammar);
            result.ProductionMap[(production, stepId.StepIndex)] = indices;
        }

        return result;
    }

    private List<int> InlineProductionAtStep(ProductionStepId stepId, SyntaxGrammar grammar)
    {
        if (_productionIndicesByStepId.TryGetValue(stepId, out var cached))
        {
            return cached;
        }

        var stepIndex = stepId.StepIndex;
        var productionsToAdd = new List<Production> { ProductionForId(stepId, grammar).Clone() };
        var i = 0;
        while (i < productionsToAdd.Count)
        {
            var production = productionsToAdd[i];
            if (stepIndex < production.Steps.Count && grammar.VariablesToInline.Contains(production.Steps[stepIndex].Symbol))
            {
                var symbol = production.Steps[stepIndex].Symbol;
                productionsToAdd.RemoveAt(i);
                var replacements = new List<Production>();
                foreach (var p in grammar.Variables[symbol.Index].Productions)
                {
                    var newProduction = production.Clone();
                    var removedStep = newProduction.Steps[stepIndex];
                    newProduction.Steps.RemoveAt(stepIndex);
                    newProduction.Steps.InsertRange(stepIndex, p.Steps.Select(s => s.Clone()));
                    var inserted = newProduction.Steps.GetRange(stepIndex, p.Steps.Count);
                    if (removedStep.Alias is { } alias)
                    {
                        foreach (var insertedStep in inserted)
                        {
                            insertedStep.Alias = alias;
                        }
                    }

                    if (removedStep.FieldName is { } fieldName)
                    {
                        foreach (var insertedStep in inserted)
                        {
                            insertedStep.FieldName = fieldName;
                        }
                    }

                    if (inserted.Count > 0)
                    {
                        var last = inserted[inserted.Count - 1];
                        if (last.Precedence.IsNone)
                        {
                            last.Precedence = removedStep.Precedence;
                        }

                        if (last.Associativity is null)
                        {
                            last.Associativity = removedStep.Associativity;
                        }
                    }

                    if (Math.Abs(p.DynamicPrecedence) > Math.Abs(newProduction.DynamicPrecedence))
                    {
                        newProduction.DynamicPrecedence = p.DynamicPrecedence;
                    }

                    replacements.Add(newProduction);
                }

                productionsToAdd.InsertRange(i, replacements);
                continue;
            }

            i++;
        }

        var result = new List<int>();
        foreach (var production in productionsToAdd)
        {
            var index = _productions.FindIndex(p => p.StructurallyEquals(production));
            if (index < 0)
            {
                _productions.Add(production);
                index = _productions.Count - 1;
            }

            result.Add(index);
        }

        _productionIndicesByStepId[stepId] = result;
        return result;
    }

    private Production ProductionForId(ProductionStepId id, SyntaxGrammar grammar) =>
        id.VariableIndex < 0 ? _productions[id.ProductionIndex] : grammar.Variables[id.VariableIndex].Productions[id.ProductionIndex];

    private ProductionStep? ProductionStepForId(ProductionStepId id, SyntaxGrammar grammar)
    {
        var production = ProductionForId(id, grammar);
        return id.StepIndex < production.Steps.Count ? production.Steps[id.StepIndex] : null;
    }
}
