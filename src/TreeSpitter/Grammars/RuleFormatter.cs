using System.Text;

namespace TreeSpitter.Grammars;

/// <summary>
/// Formats rules with the tree-sitter DSL syntax.
/// </summary>
internal static class RuleFormatter
{
    public static string Format(Rule rule)
    {
        var builder = new StringBuilder();
        Append(builder, rule);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, Rule rule)
    {
        switch (rule)
        {
            case BlankRule:
                builder.Append("blank()");
                break;
            case EofRule:
                builder.Append("eof()");
                break;
            case StringRule s:
                AppendString(builder, s.Value);
                break;
            case PatternRule p:
                builder.Append('/').Append(p.Value).Append('/').Append(p.Flags);
                break;
            case SymbolRule s:
                builder.Append("$.").Append(s.Name);
                break;
            case SeqRule s:
                AppendCall(builder, "seq", s.Members);
                break;
            case ChoiceRule c:
                if (c.Members.Count == 2 && c.Members[1] is BlankRule)
                {
                    builder.Append("optional(");
                    Append(builder, c.Members[0]);
                    builder.Append(')');
                }
                else
                {
                    AppendCall(builder, "choice", c.Members);
                }

                break;
            case RepeatRule r:
                builder.Append("repeat(");
                Append(builder, r.Content);
                builder.Append(')');
                break;
            case Repeat1Rule r:
                builder.Append("repeat1(");
                Append(builder, r.Content);
                builder.Append(')');
                break;
            case PrecRule p:
                builder.Append(p.Precedence switch
                {
                    PrecedenceKind.Left => "prec.left(",
                    PrecedenceKind.Right => "prec.right(",
                    PrecedenceKind.Dynamic => "prec.dynamic(",
                    _ => "prec(",
                });
                builder.Append(p.Value.ToString()).Append(", ");
                Append(builder, p.Content);
                builder.Append(')');
                break;
            case TokenRule t:
                builder.Append(t.Immediate ? "token.immediate(" : "token(");
                Append(builder, t.Content);
                builder.Append(')');
                break;
            case AliasRule a:
                builder.Append("alias(");
                Append(builder, a.Content);
                builder.Append(", ");
                if (a.Named)
                {
                    builder.Append("$.").Append(a.Value);
                }
                else
                {
                    AppendString(builder, a.Value);
                }

                builder.Append(')');
                break;
            case FieldRule f:
                builder.Append("field(");
                AppendString(builder, f.Name);
                builder.Append(", ");
                Append(builder, f.Content);
                builder.Append(')');
                break;
            case ReservedRule r:
                builder.Append("reserved(");
                AppendString(builder, r.ContextName);
                builder.Append(", ");
                Append(builder, r.Content);
                builder.Append(')');
                break;
        }
    }

    private static void AppendCall(StringBuilder builder, string name, System.Collections.Generic.IReadOnlyList<Rule> members)
    {
        builder.Append(name).Append('(');
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            Append(builder, members[i]);
        }

        builder.Append(')');
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\'':
                    builder.Append("\\'");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('\'');
    }
}
