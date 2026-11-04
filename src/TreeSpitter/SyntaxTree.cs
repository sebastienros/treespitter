using System.Text;

namespace TreeSpitter;

/// <summary>A position in the input: a zero-based row and a column in bytes.</summary>
public readonly record struct Point(int Row, int Column)
{
    /// <inheritdoc />
    public override string ToString() => $"[{Row}, {Column}]";
}

/// <summary>
/// A syntax tree, the result of a parse. The nodes are stored in arrays owned by the tree, and exposed as
/// <see cref="SyntaxNode"/> values. Positions are byte offsets in the UTF-8 input.
/// </summary>
public sealed class SyntaxTree : IDisposable
{
    private SubtreeData[]? _nodes;
    private int[]? _children;
    private SubtreeArena? _arena;
    private LineIndex? _lines;

    internal SyntaxTree(Language language, byte[] input, int length, SubtreeArena arena, int root)
    {
        Language = language;
        Input = new ReadOnlyMemory<byte>(input, 0, length);
        // The arrays now belong to the tree, until it's disposed
        (_nodes, _children) = arena.Detach();
        _arena = arena;
        Root = root;
    }

    /// <summary>Gets the language of the tree.</summary>
    public Language Language { get; }

    /// <summary>Gets the UTF-8 input that was parsed.</summary>
    public ReadOnlyMemory<byte> Input { get; }

    internal int Root { get; }

    internal SubtreeData[] Nodes => _nodes ?? throw new ObjectDisposedException(nameof(SyntaxTree));

    internal int[] Children => _children ?? throw new ObjectDisposedException(nameof(SyntaxTree));

    /// <summary>Gets the root node.</summary>
    public SyntaxNode RootNode => new(this, Root, 0, 0);

    /// <summary>Gets whether the tree contains syntax errors (ERROR or MISSING nodes).</summary>
    public bool HasError => Nodes[Root].ErrorCost > 0;

    /// <summary>
    /// Releases the memory of the tree to its parser, which reuses it for the next parse. The tree and its nodes can't
    /// be used anymore. Disposing trees is optional: undisposed trees are collected by the GC.
    /// </summary>
    public void Dispose()
    {
        var nodes = _nodes;
        var children = _children;
        var arena = _arena;
        _nodes = null;
        _children = null;
        _arena = null;
        if (nodes is not null && children is not null && arena is not null)
        {
            arena.Return(nodes, children);
        }
    }

    internal Point PointAt(int position)
    {
        _lines ??= new LineIndex(Input.ToArray(), Input.Length);
        var row = _lines.Row(position);
        return new Point(row, _lines.Column(position));
    }

    /// <summary>Returns the tree as an S-expression, like <c>ts_node_string</c>.</summary>
    public string ToSExpression() => RootNode.ToSExpression();

    /// <inheritdoc />
    public override string ToString() => ToSExpression();
}

/// <summary>
/// A node of a <see cref="SyntaxTree"/>: a cheap value that refers to the tree. Hidden nodes (rules whose name starts
/// with an underscore) are skipped by the navigation, like in tree-sitter.
/// </summary>
public readonly struct SyntaxNode : IEquatable<SyntaxNode>
{
    private readonly SyntaxTree? _tree;
    private readonly int _index;
    private readonly ushort _alias;
    private readonly ushort _fieldId;

    internal SyntaxNode(SyntaxTree tree, int index, ushort alias, ushort fieldId)
    {
        _tree = tree;
        _index = index;
        _alias = alias;
        _fieldId = fieldId;
    }

    /// <summary>Gets the tree of the node.</summary>
    public SyntaxTree Tree => _tree ?? throw new InvalidOperationException("The node is null.");

    /// <summary>Gets whether this is the null node, returned when there's no such node.</summary>
    public bool IsNull => _tree is null;

    private ref SubtreeData Data => ref Tree.Nodes[_index];

    /// <summary>Gets the symbol of the node, aliases applied.</summary>
    public ushort Symbol => _alias != 0 ? _alias : Tree.Language.PublicSymbol(Data.Symbol);

    /// <summary>Gets the type of the node: the name of its rule, alias or token.</summary>
    public string Type => Tree.Language.SymbolName(_alias != 0 ? _alias : Data.Symbol);

    /// <summary>Gets whether the node is named, as opposed to a string literal of the grammar.</summary>
    public bool IsNamed => _alias != 0 ? Tree.Language.IsNamed(_alias) : (Data.Flags & SubtreeFlags.Named) != 0;

    /// <summary>Gets whether the node is an extra (like a comment).</summary>
    public bool IsExtra => (Data.Flags & SubtreeFlags.Extra) != 0;

    /// <summary>Gets whether the node is an <c>ERROR</c> node.</summary>
    public bool IsError => Data.Symbol == Language.ErrorSymbol;

    /// <summary>Gets whether the node was inserted by the error recovery (a <c>MISSING</c> node).</summary>
    public bool IsMissing => (Data.Flags & SubtreeFlags.IsMissing) != 0;

    /// <summary>Gets whether the node is or contains a syntax error.</summary>
    public bool HasError => Data.ErrorCost > 0;

    /// <summary>Gets the start of the node, a byte offset.</summary>
    public int StartByte => Data.Start;

    /// <summary>Gets the end of the node, a byte offset.</summary>
    public int EndByte => Data.End;

    /// <summary>Gets the position of the start of the node.</summary>
    public Point StartPoint => Tree.PointAt(Data.Start);

    /// <summary>Gets the position of the end of the node.</summary>
    public Point EndPoint => Tree.PointAt(Data.End);

    /// <summary>Gets the text of the node.</summary>
    public string Text => Encoding.UTF8.GetString(Tree.Input.Span.Slice(Data.Start, Data.End - Data.Start).ToArray());

    /// <summary>Gets the name of the field of the node in its parent, when the node was obtained from its parent.</summary>
    public string? FieldName => _fieldId == 0 ? null : Tree.Language.FieldName(_fieldId);

    /// <summary>Gets the visible children of the node.</summary>
    public IEnumerable<SyntaxNode> Children
    {
        get
        {
            var tree = Tree;
            var list = new List<SyntaxNode>();
            CollectChildren(tree, _index, 0, list);
            return list;
        }
    }

    /// <summary>Gets the named children of the node.</summary>
    public IEnumerable<SyntaxNode> NamedChildren => Children.Where(c => c.IsNamed);

    /// <summary>Gets the number of visible children.</summary>
    public int ChildCount => Data.ChildCount == 0 ? 0 : Data.VisibleChildCount;

    /// <summary>Gets the number of named children.</summary>
    public int NamedChildCount => Data.ChildCount == 0 ? 0 : Data.NamedChildCount;

    /// <summary>Gets the parent of the node, or the null node for the root. This searches the tree from the root.</summary>
    public SyntaxNode Parent
    {
        get
        {
            var tree = Tree;
            var root = tree.RootNode;
            if (_index == root._index)
            {
                return default;
            }

            var current = root;
            while (true)
            {
                var found = false;
                foreach (var child in current.Children)
                {
                    if (child._index == _index)
                    {
                        return current;
                    }

                    if (child.StartByte <= StartByte && child.EndByte >= EndByte && child.ChildCount > 0)
                    {
                        current = child;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return default;
                }
            }
        }
    }

    /// <summary>Gets the first child with a field name, or the null node.</summary>
    public SyntaxNode ChildByFieldName(string name)
    {
        var fieldId = Tree.Language.FieldId(name);
        if (fieldId == 0)
        {
            return default;
        }

        foreach (var child in Children)
        {
            if (child._fieldId == fieldId)
            {
                return child;
            }
        }

        return default;
    }

    /// <summary>Gets the children with a field name.</summary>
    public IEnumerable<SyntaxNode> ChildrenByFieldName(string name)
    {
        var fieldId = Tree.Language.FieldId(name);
        return fieldId == 0 ? [] : Children.Where(c => c._fieldId == fieldId);
    }

    /// <summary>
    /// Collects the visible children of a node: the hidden children are replaced by their own visible children, and
    /// the field names are inherited like in tree-sitter's tree cursor.
    /// </summary>
    private static void CollectChildren(SyntaxTree tree, int index, ushort inheritedField, List<SyntaxNode> result)
    {
        var nodes = tree.Nodes;
        var children = tree.Children;
        var language = tree.Language;
        var stack = new Stack<(int Index, int Child, int Structural, ushort Field)>();
        stack.Push((index, 0, 0, inheritedField));
        while (stack.Count > 0)
        {
            var (parent, i, structural, field) = stack.Pop();
            ref var data = ref nodes[parent];
            if (i >= data.ChildCount)
            {
                continue;
            }

            var child = children[data.FirstChild + i];
            ref var childData = ref nodes[child];
            var isExtra = (childData.Flags & SubtreeFlags.Extra) != 0;
            ushort alias = 0;
            ushort childField = 0;
            if (!isExtra)
            {
                alias = language.AliasAt(data.ProductionId, structural);
                childField = field;
                foreach (var entry in language.FieldMap(data.ProductionId))
                {
                    if (!entry.Inherited && entry.ChildIndex == structural)
                    {
                        childField = entry.FieldId;
                        break;
                    }
                }
            }

            // Continue with the next sibling after this child
            stack.Push((parent, i + 1, isExtra ? structural : structural + 1, field));

            if (alias != 0 || (childData.Flags & SubtreeFlags.Visible) != 0)
            {
                result.Add(new SyntaxNode(tree, child, alias, childField));
            }
            else if (childData.ChildCount > 0 && childData.VisibleChildCount > 0)
            {
                stack.Push((child, 0, 0, childField));
            }
        }
    }

    /// <summary>Returns the node as an S-expression, like tree-sitter's <c>ts_node_string</c>.</summary>
    public string ToSExpression()
    {
        var builder = new StringBuilder();
        SExpressionWriter.Write(builder, Tree, _index, _alias);
        return builder.ToString();
    }

    /// <inheritdoc />
    public bool Equals(SyntaxNode other) => ReferenceEquals(_tree, other._tree) && _index == other._index && _alias == other._alias;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SyntaxNode other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _index * 31 + _alias;

    /// <inheritdoc />
    public override string ToString() => IsNull ? "(null)" : ToSExpression();
}

/// <summary>Writes S-expressions, a port of tree-sitter's <c>ts_subtree__write_to_string</c> without recursion.</summary>
internal static class SExpressionWriter
{
    private struct Frame
    {
        public int Index;
        public bool IsVisible;
        public string? FieldName;
        public int Child;
        public int Structural;
    }

    public static void Write(StringBuilder builder, SyntaxTree tree, int root, ushort rootAlias)
    {
        var nodes = tree.Nodes;
        var children = tree.Children;
        var language = tree.Language;
        var stack = new Stack<Frame>();

        // Opens a node, returns its frame
        Frame Open(int index, ushort alias, bool aliasIsNamed, string? fieldName, bool isRoot)
        {
            ref var data = ref nodes[index];
            var isMissing = (data.Flags & SubtreeFlags.IsMissing) != 0;
            var isVisible = isMissing || (alias != 0 ? aliasIsNamed : (data.Flags & SubtreeFlags.Visible) != 0 && (data.Flags & SubtreeFlags.Named) != 0);
            if (isVisible)
            {
                if (!isRoot)
                {
                    builder.Append(' ');
                    if (fieldName is not null)
                    {
                        builder.Append(fieldName).Append(": ");
                    }
                }

                if (data.Symbol == Language.ErrorSymbol && data.ChildCount == 0 && data.End > data.Start)
                {
                    builder.Append("(UNEXPECTED ");
                    WriteChar(builder, data.LookaheadChar);
                }
                else
                {
                    var name = language.SymbolName(alias != 0 ? alias : data.Symbol);
                    if (isMissing)
                    {
                        builder.Append("(MISSING ");
                        if (aliasIsNamed || (data.Flags & SubtreeFlags.Named) != 0)
                        {
                            builder.Append(name);
                        }
                        else
                        {
                            builder.Append('"').Append(name).Append('"');
                        }
                    }
                    else
                    {
                        builder.Append('(').Append(name);
                    }
                }
            }
            else if (isRoot)
            {
                var name = language.SymbolName(alias != 0 ? alias : data.Symbol);
                if (data.ChildCount > 0)
                {
                    builder.Append('(').Append(name);
                }
                else if ((data.Flags & SubtreeFlags.Named) != 0)
                {
                    builder.Append('(').Append(name).Append(')');
                }
                else
                {
                    builder.Append("(\"").Append(name).Append("\")");
                }
            }

            return new Frame { Index = index, IsVisible = isVisible, FieldName = fieldName, Child = 0, Structural = 0 };
        }

        stack.Push(Open(root, rootAlias, rootAlias != 0 && language.IsVisible(rootAlias), null, true));
        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            ref var data = ref nodes[frame.Index];
            if (frame.Child >= data.ChildCount)
            {
                if (frame.IsVisible)
                {
                    builder.Append(')');
                }

                continue;
            }

            var child = children[data.FirstChild + frame.Child];
            var isExtra = (nodes[child].Flags & SubtreeFlags.Extra) != 0;
            ushort alias = 0;
            var aliasIsNamed = false;
            string? childFieldName = null;
            var structural = frame.Structural;
            if (!isExtra)
            {
                alias = language.AliasAt(data.ProductionId, structural);
                aliasIsNamed = alias != 0 && language.IsNamed(alias);
                childFieldName = frame.IsVisible ? null : frame.FieldName;
                foreach (var entry in language.FieldMap(data.ProductionId))
                {
                    if (!entry.Inherited && entry.ChildIndex == structural)
                    {
                        childFieldName = language.FieldName(entry.FieldId);
                        break;
                    }
                }

                structural++;
            }

            frame.Child++;
            frame.Structural = structural;
            stack.Push(frame);
            stack.Push(Open(child, alias, aliasIsNamed, childFieldName, false));
        }
    }

    private static void WriteChar(StringBuilder builder, int c)
    {
        if (c == -1)
        {
            builder.Append("INVALID");
        }
        else if (c == 0)
        {
            builder.Append("'\\0'");
        }
        else if (c == '\n')
        {
            builder.Append("'\\n'");
        }
        else if (c == '\t')
        {
            builder.Append("'\\t'");
        }
        else if (c == '\r')
        {
            builder.Append("'\\r'");
        }
        else if (c is > 0 and < 128 && !char.IsControl((char)c))
        {
            builder.Append('\'').Append((char)c).Append('\'');
        }
        else
        {
            builder.Append(c.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
