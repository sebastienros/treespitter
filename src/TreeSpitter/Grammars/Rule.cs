namespace TreeSpitter.Grammars;

/// <summary>
/// The kind of a <see cref="Rule"/>, matching the <c>type</c> property of the rules in a tree-sitter <c>grammar.json</c> file.
/// </summary>
public enum RuleKind
{
    /// <summary><c>blank()</c>, a <see cref="BlankRule"/>.</summary>
    Blank,
    /// <summary><c>eof()</c>, an <see cref="EofRule"/>.</summary>
    Eof,
    /// <summary>A string literal, a <see cref="StringRule"/>.</summary>
    String,
    /// <summary>A regular expression, a <see cref="PatternRule"/>.</summary>
    Pattern,
    /// <summary>A reference to a rule, a <see cref="SymbolRule"/>.</summary>
    Symbol,
    /// <summary><c>seq()</c>, a <see cref="SeqRule"/>.</summary>
    Seq,
    /// <summary><c>choice()</c> and <c>optional()</c>, a <see cref="ChoiceRule"/>.</summary>
    Choice,
    /// <summary><c>repeat()</c>, a <see cref="RepeatRule"/>.</summary>
    Repeat,
    /// <summary><c>repeat1()</c>, a <see cref="Repeat1Rule"/>.</summary>
    Repeat1,
    /// <summary><c>prec()</c>, a <see cref="PrecRule"/>.</summary>
    Prec,
    /// <summary><c>prec.left()</c>, a <see cref="PrecRule"/>.</summary>
    PrecLeft,
    /// <summary><c>prec.right()</c>, a <see cref="PrecRule"/>.</summary>
    PrecRight,
    /// <summary><c>prec.dynamic()</c>, a <see cref="PrecRule"/>.</summary>
    PrecDynamic,
    /// <summary><c>token()</c>, a <see cref="TokenRule"/>.</summary>
    Token,
    /// <summary><c>token.immediate()</c>, a <see cref="TokenRule"/>.</summary>
    ImmediateToken,
    /// <summary><c>alias()</c>, an <see cref="AliasRule"/>.</summary>
    Alias,
    /// <summary><c>field()</c>, a <see cref="FieldRule"/>.</summary>
    Field,
    /// <summary><c>reserved()</c>, a <see cref="ReservedRule"/>.</summary>
    Reserved,
}

/// <summary>
/// A node of the grammar AST, the result of evaluating the tree-sitter DSL (<c>seq</c>, <c>choice</c>, <c>repeat</c>, ...).
/// </summary>
public abstract record Rule
{
    /// <summary>Gets the kind of the rule, the <c>type</c> property of the rules in a <c>grammar.json</c> file.</summary>
    public abstract RuleKind Kind { get; }

    /// <summary>
    /// Returns the rule formatted with the tree-sitter DSL syntax, e.g. <c>seq('(', $.expression, ')')</c>.
    /// </summary>
    public sealed override string ToString() => RuleFormatter.Format(this);

    internal static int CombineHashes(IEnumerable<Rule> rules)
    {
        unchecked
        {
            var hash = 17;
            foreach (var rule in rules)
            {
                hash = hash * 31 + rule.GetHashCode();
            }

            return hash;
        }
    }
}

/// <summary><c>blank()</c>: matches nothing.</summary>
public sealed record BlankRule : Rule
{
    /// <summary>The <c>blank()</c> rule.</summary>
    public static readonly BlankRule Instance = new();

    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Blank;
}

/// <summary><c>eof()</c>: matches the end of the input.</summary>
public sealed record EofRule : Rule
{
    /// <summary>The <c>eof()</c> rule.</summary>
    public static readonly EofRule Instance = new();

    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Eof;
}

/// <summary>A string literal: an anonymous token.</summary>
public sealed record StringRule(string Value) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.String;
}

/// <summary>A regular expression literal.</summary>
public sealed record PatternRule(string Value, string Flags = "") : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Pattern;
}

/// <summary>A reference to another rule, <c>$.name</c>.</summary>
public sealed record SymbolRule(string Name) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Symbol;
}

/// <summary><c>seq(...)</c>.</summary>
public sealed record SeqRule(IReadOnlyList<Rule> Members) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Seq;

    /// <inheritdoc />
    public bool Equals(SeqRule? other) => other is not null && Members.SequenceEqual(other.Members);

    /// <inheritdoc />
    public override int GetHashCode() => CombineHashes(Members) ^ 0x5e9;
}

/// <summary><c>choice(...)</c>, and <c>optional(x)</c> which is <c>choice(x, blank())</c>.</summary>
public sealed record ChoiceRule(IReadOnlyList<Rule> Members) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Choice;

    /// <inheritdoc />
    public bool Equals(ChoiceRule? other) => other is not null && Members.SequenceEqual(other.Members);

    /// <inheritdoc />
    public override int GetHashCode() => CombineHashes(Members) ^ 0xc401;
}

/// <summary><c>repeat(x)</c>: zero or more.</summary>
public sealed record RepeatRule(Rule Content) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Repeat;
}

/// <summary><c>repeat1(x)</c>: one or more.</summary>
public sealed record Repeat1Rule(Rule Content) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Repeat1;
}

/// <summary>The flavor of a precedence rule.</summary>
public enum PrecedenceKind
{
    /// <summary><c>prec(n, x)</c></summary>
    Default,

    /// <summary><c>prec.left(n, x)</c></summary>
    Left,

    /// <summary><c>prec.right(n, x)</c></summary>
    Right,

    /// <summary><c>prec.dynamic(n, x)</c></summary>
    Dynamic,
}

/// <summary>
/// A precedence value, either a number or a name declared in the <c>precedences</c> of the grammar.
/// </summary>
public readonly record struct PrecedenceValue
{
    /// <summary>Creates a numeric precedence.</summary>
    public PrecedenceValue(int number)
    {
        Number = number;
        Name = null;
    }

    /// <summary>Creates a named precedence, declared in the <c>precedences</c> of the grammar.</summary>
    public PrecedenceValue(string name)
    {
        Number = 0;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>Gets the numeric precedence, 0 for a named precedence.</summary>
    public int Number { get; }

    /// <summary>Gets the name of a named precedence, <c>null</c> for a numeric precedence.</summary>
    public string? Name { get; }

    /// <summary>Gets whether the precedence is a name.</summary>
    public bool IsNamed => Name is not null;

    /// <summary>Converts a number to a precedence.</summary>
    public static implicit operator PrecedenceValue(int number) => new(number);

    /// <summary>Converts a name to a precedence.</summary>
    public static implicit operator PrecedenceValue(string name) => new(name);

    /// <inheritdoc />
    public override string ToString() => Name is null ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "'" + Name + "'";
}

/// <summary><c>prec</c>, <c>prec.left</c>, <c>prec.right</c> and <c>prec.dynamic</c>.</summary>
public sealed record PrecRule(PrecedenceKind Precedence, PrecedenceValue Value, Rule Content) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => Precedence switch
    {
        PrecedenceKind.Left => RuleKind.PrecLeft,
        PrecedenceKind.Right => RuleKind.PrecRight,
        PrecedenceKind.Dynamic => RuleKind.PrecDynamic,
        _ => RuleKind.Prec,
    };
}

/// <summary><c>token(x)</c> and <c>token.immediate(x)</c>.</summary>
public sealed record TokenRule(Rule Content, bool Immediate = false) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => Immediate ? RuleKind.ImmediateToken : RuleKind.Token;
}

/// <summary><c>alias(x, name)</c>.</summary>
public sealed record AliasRule(Rule Content, string Value, bool Named) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Alias;
}

/// <summary><c>field(name, x)</c>.</summary>
public sealed record FieldRule(string Name, Rule Content) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Field;
}

/// <summary><c>reserved(context, x)</c>.</summary>
public sealed record ReservedRule(string ContextName, Rule Content) : Rule
{
    /// <inheritdoc />
    public override RuleKind Kind => RuleKind.Reserved;
}
