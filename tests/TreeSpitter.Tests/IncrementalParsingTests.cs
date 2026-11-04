using System.Text;
using Xunit;

namespace TreeSpitter.Tests;

/// <summary>
/// Incremental parsing: the random edits of tree-sitter's corpus tests (<c>cli/src/tests/corpus_test.rs</c>), and the
/// sharing of the nodes between the trees.
/// </summary>
public sealed class IncrementalParsingTests
{
    /// <summary>
    /// The tests skipped by tree-sitter's corpus tests (<c>corpus_test.rs</c>), whose trees change with the edits: the
    /// heredocs of tree-sitter-bash, whose scanner keeps the heredocs after the ones of its state, so the incremental
    /// parses depend on the nodes they reuse (the native parser gives these trees too).
    /// </summary>
    private static readonly Dictionary<string, string[]> SkippedTests = new()
    {
        ["bash"] = ["Nested Heredocs", "Quoted Heredocs", "Heredocs with weird characters"],
    };

    private const int TrialCount = 6;
    private const int MaxEditCount = 3;

    private static readonly char[] Operators = ['+', '-', '<', '>', '(', ')', '*', '/', '&', '|', '!', ',', '.', '%'];

    private readonly ITestOutputHelper _output;

    public IncrementalParsingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [MemberData(nameof(Fixtures.Grammars), MemberType = typeof(Fixtures))]
    public void RandomEditsAndTheirUndoGiveTheTreesOfAFreshParse(string grammar)
    {
        var parser = Fixtures.Parser(grammar);
        var skipped = SkippedTests.TryGetValue(grammar, out var names) ? names : [];
        var tests = Testing.Corpus.Load(Fixtures.Corpus(grammar)).Where(t => !t.Skip && !skipped.Contains(t.Name)).ToList();
        var failures = new List<string>();
        var sameAsFresh = 0;
        var editedParses = 0;
        for (var testIndex = 0; testIndex < tests.Count; testIndex++)
        {
            var original = Encoding.UTF8.GetBytes(tests[testIndex].Input);
            using var originalTree = parser.Parse(original);
            var expected = originalTree.ToSExpression();
            for (var trial = 0; trial < TrialCount; trial++)
            {
                var random = new Random(testIndex * 1000 + trial);
                var input = original;
                var tree = originalTree.Copy();
                var undo = new Stack<Edit>();
                var editCount = 1 + random.Next(MaxEditCount);
                for (var i = 0; i < editCount; i++)
                {
                    var edit = RandomEdit(random, input);
                    undo.Push(Invert(input, edit));
                    input = Apply(tree, input, edit);
                }

                using var edited = parser.Parse(input, tree);
                tree.Dispose();
                CheckConsistentSizes(edited, input);
                using (var fresh = parser.Parse(input))
                {
                    editedParses++;
                    if (fresh.ToSExpression() == edited.ToSExpression())
                    {
                        sameAsFresh++;
                    }
                }

                while (undo.Count > 0)
                {
                    input = Apply(edited, input, undo.Pop());
                }

                using var undone = parser.Parse(input, edited);
                CheckConsistentSizes(undone, input);
                var actual = undone.ToSExpression();
                if (actual != expected)
                {
                    failures.Add($"{tests[testIndex].Name} (trial {trial})\n  expected: {expected}\n  actual:   {actual}");
                }
            }
        }

        _output.WriteLine($"{grammar}: {editedParses} edited parses, {sameAsFresh} give the tree of a fresh parse");
        foreach (var failure in failures)
        {
            _output.WriteLine(failure);
        }

        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("go")]
    [InlineData("java")]
    [InlineData("c")]
    public void ValidEditsGiveTheTreesOfAFreshParse(string grammar)
    {
        // Edits that keep the input valid: changing digits, and inserting copies of the print statements
        var parser = new Parser(Fixtures.GeneratedLanguage(grammar));
        var input = grammar == "json" ? File.ReadAllBytes(Fixtures.GrammarJson("c")) : Encoding.UTF8.GetBytes(SampleSource(grammar));
        var tree = parser.Parse(input);
        Assert.False(tree.HasError);
        var random = new Random(42);
        for (var i = 0; i < 60; i++)
        {
            Edit edit;
            if (grammar != "json" && i % 3 == 0)
            {
                var lines = Encoding.UTF8.GetString(input).Split('\n');
                var candidates = Enumerable.Range(0, lines.Length).Where(l => lines[l].Contains("print", StringComparison.OrdinalIgnoreCase)).ToList();
                var line = candidates[random.Next(candidates.Count)];
                var start = lines.Take(line).Sum(l => Encoding.UTF8.GetByteCount(l) + 1);
                edit = new Edit(start, 0, Encoding.UTF8.GetBytes(lines[line] + "\n"));
            }
            else
            {
                var digits = Enumerable.Range(0, input.Length).Where(o => input[o] is >= (byte)'1' and <= (byte)'9').ToList();
                var offset = digits[random.Next(digits.Count)];
                edit = new Edit(offset, 1, [(byte)('1' + random.Next(9))]);
            }

            input = Apply(tree, input, edit);
            var edited = parser.Parse(input, tree);
            tree.Dispose();
            tree = edited;
            using var fresh = parser.Parse(input);
            Assert.False(edited.HasError);
            Assert.Equal(fresh.ToSExpression(), edited.ToSExpression());
            Assert.Equal(Describe(fresh.RootNode), Describe(edited.RootNode));
            CheckConsistentSizes(edited, input);
        }

        tree.Dispose();
    }

    [Fact]
    public void TheIncrementalParsingExampleOfTheReadmeWorks()
    {
        var parser = Parser.FromGrammarFile(Fixtures.GrammarJs("json"));
        using var tree = parser.Parse("""{"a": [1, 2]}""");

        tree.Edit(new InputEdit(StartByte: 11, OldEndByte: 11, NewEndByte: 12));
        using var newTree = parser.Parse("""{"a": [1, 20]}""", tree);

        var numbers = newTree.RootNode.NamedChildren.Single().NamedChildren.Single().ChildByFieldName("value").NamedChildren.ToList();
        Assert.Equal(["1", "20"], numbers.Select(n => n.Text));
        Assert.Equal("(document (object (pair key: (string (string_content)) value: (array (number) (number)))))", newTree.ToSExpression());
    }

    [Fact]
    public void AnIncrementalParseReusesTheUnchangedNodes()
    {
        var parser = new Parser(Fixtures.GeneratedLanguage("json"));
        var input = Encoding.UTF8.GetBytes("[" + string.Join(", ", Enumerable.Range(0, 2000).Select(i => $"{{\"id\": {i}, \"name\": \"item {i}\"}}")) + "]");
        using var tree = parser.Parse(input);
        var arena = tree.Arena;
        var oldNodeCount = arena.Count;

        // Change "id": 1000 to "id": 10000
        var position = Encoding.UTF8.GetString(input).IndexOf("\"id\": 1000,", StringComparison.Ordinal) + 10;
        tree.Edit(new InputEdit(position, position, position + 1));
        byte[] newInput = [.. input.Take(position), (byte)'0', .. input.Skip(position)];
        using var edited = parser.Parse(newInput, tree);

        using var fresh = parser.Parse(newInput);
        Assert.Equal(fresh.ToSExpression(), edited.ToSExpression());
        Assert.Same(arena, edited.Arena);

        // Like in tree-sitter, the nodes of the repetitions are rebuilt after the edit (they are fragile), but the
        // elements are reused
        var newNodes = arena.Count - oldNodeCount;
        Assert.True(newNodes * 5 < fresh.Arena.Count, $"{newNodes} new nodes, {fresh.Arena.Count} for a fresh parse");
        var number = edited.RootNode.NamedChildren.First().NamedChildren.ElementAt(1000).NamedChildren.First().ChildByFieldName("value");
        Assert.Equal("10000", number.Text);
        Assert.Equal(position - 4, number.StartByte);
    }

    [Fact]
    public void TheOldTreeIsntChangedByAnIncrementalParse()
    {
        var parser = new Parser(Fixtures.GeneratedLanguage("go"));
        var input = Encoding.UTF8.GetBytes("package main\n\nfunc main() {\n\tx := 1\n\ty := 2\n}\n");
        using var tree = parser.Parse(input);
        var before = Describe(tree.RootNode);

        using var copy = tree.Copy();
        var position = Encoding.UTF8.GetString(input).IndexOf("1", StringComparison.Ordinal);
        copy.Edit(new InputEdit(position, position + 1, position + 3));
        var newInput = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).Replace("x := 1", "x := 100"));
        using var edited = parser.Parse(newInput, copy);

        Assert.Equal(before, Describe(tree.RootNode));
        Assert.Contains("100", edited.RootNode.ToSExpression() + Describe(edited.RootNode));
        Assert.Equal(parser.Parse(newInput).ToSExpression(), edited.ToSExpression());
    }

    [Fact]
    public void AnEditShiftsTheNodesAfterItAndMarksTheNodesTouchingIt()
    {
        var parser = new Parser(Fixtures.GeneratedLanguage("json"));
        using var tree = parser.Parse("[1, 2, 3]");

        // "[1, 2, 3]" -> "[1, 42, 3]"
        tree.Edit(new InputEdit(4, 5, 6));

        var array = tree.RootNode.NamedChildren.Single();
        var numbers = array.NamedChildren.ToList();
        Assert.Equal((1, 2), (numbers[0].StartByte, numbers[0].EndByte));
        Assert.Equal((4, 6), (numbers[1].StartByte, numbers[1].EndByte));
        Assert.Equal((8, 9), (numbers[2].StartByte, numbers[2].EndByte));
        Assert.True(array.HasChanges);
        Assert.True(numbers[1].HasChanges);
        Assert.False(numbers[0].HasChanges);
        Assert.False(numbers[2].HasChanges);
        Assert.Equal(10, tree.RootNode.EndByte);
    }

    [Fact]
    public void ALongSeriesOfEditsDoesntGrowTheMemoryForever()
    {
        var parser = new Parser(Fixtures.GeneratedLanguage("json"));
        var input = Encoding.UTF8.GetBytes("[" + string.Join(", ", Enumerable.Range(0, 300).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]");
        var tree = parser.Parse(input);
        var random = new Random(7);
        var maxNodes = 0;
        for (var i = 0; i < 500; i++)
        {
            // Replace a digit of a number
            int offset;
            do
            {
                offset = random.Next(input.Length);
            }
            while (input[offset] is < (byte)'1' or > (byte)'9');

            input = Apply(tree, input, new Edit(offset, 1, [(byte)('1' + random.Next(9))]));
            var edited = parser.Parse(input, tree);
            tree.Dispose();
            tree = edited;
            maxNodes = Math.Max(maxNodes, tree.Arena.Count);
        }

        using var fresh = parser.Parse(input);
        Assert.Equal(fresh.ToSExpression(), tree.ToSExpression());
        Assert.True(maxNodes < 4 * fresh.Arena.Count + 8192, $"{maxNodes} nodes for {fresh.Arena.Count}");
        tree.Dispose();
    }

    [Fact]
    public void ParsingFromTheSameTreeDoesntGrowTheMemoryForever()
    {
        var parser = new Parser(Fixtures.GeneratedLanguage("json"));
        var input = Encoding.UTF8.GetBytes("[" + string.Join(", ", Enumerable.Range(0, 300).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]");
        using var tree = parser.Parse(input);
        var nodeCount = tree.Arena.Count;
        for (var i = 0; i < 300; i++)
        {
            // Insert a digit in the middle of the array
            using var copy = tree.Copy();
            var newInput = Apply(copy, input, new Edit(input.Length / 2 + i % 7, 0, [(byte)'1']));
            using var edited = parser.Parse(newInput, copy);
            using var fresh = parser.Parse(newInput);
            Assert.Equal(fresh.ToSExpression(), edited.ToSExpression());
        }

        Assert.True(tree.Arena.Count < 3 * nodeCount + 8192, $"{tree.Arena.Count} nodes for {nodeCount}");
    }

    [Fact]
    public void TheCopiesOfATreeCanBeEditedAndReusedFromSeveralThreads()
    {
        var language = Fixtures.GeneratedLanguage("json");
        var input = Encoding.UTF8.GetBytes("[" + string.Join(", ", Enumerable.Range(0, 500).Select(i => $"{{\"id\": {i}}}")) + "]");
        using var tree = new Parser(language).Parse(input);
        var expected = new Parser(language).Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).Replace("\"id\": 250", "\"id\": 2507"))).ToSExpression();
        var position = Encoding.UTF8.GetString(input).IndexOf("\"id\": 250}", StringComparison.Ordinal) + 9;

        Parallel.For(0, 8, _ =>
        {
            var parser = new Parser(language);
            for (var i = 0; i < 20; i++)
            {
                using var copy = tree.Copy();
                var newInput = Apply(copy, input, new Edit(position, 0, [(byte)'7']));
                using var edited = parser.Parse(newInput, copy);
                Assert.Equal(expected, edited.ToSExpression());
            }
        });

        Assert.False(tree.HasError);
    }

    [Fact]
    public void TheOldTreeMustHaveTheLanguageOfTheParser()
    {
        using var tree = new Parser(Fixtures.GeneratedLanguage("json")).Parse("[1]");

        Assert.Throws<ArgumentException>(() => new Parser(Fixtures.GeneratedLanguage("go")).Parse("package main", tree));
    }

    internal readonly record struct Edit(int Position, int DeletedLength, byte[] Inserted);

    /// <summary>tree-sitter's <c>get_random_edit</c>.</summary>
    private static Edit RandomEdit(Random random, byte[] input)
    {
        var choice = random.Next(10);
        if (choice < 2)
        {
            // Insert text at the end
            return new Edit(input.Length, 0, RandomWords(random, 3));
        }

        if (choice < 5)
        {
            // Delete text from the end
            var deleted = Math.Min(random.Next(30), input.Length);
            return new Edit(input.Length - deleted, deleted, []);
        }

        var position = random.Next(input.Length + 1);
        if (choice < 8)
        {
            // Insert at a random position
            return new Edit(position, 0, RandomWords(random, 1 + random.Next(3)));
        }

        // Replace at a random position
        return new Edit(position, random.Next(input.Length - position + 1), RandomWords(random, 1 + random.Next(3)));
    }

    private static byte[] RandomWords(Random random, int maxCount)
    {
        var count = random.Next(maxCount);
        var result = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                result.Add(random.Next(5) == 0 ? (byte)'\n' : (byte)' ');
            }

            if (random.Next(3) == 0)
            {
                result.Add((byte)Operators[random.Next(Operators.Length - 1)]);
            }
            else
            {
                const string alphanumeric = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
                for (var j = random.Next(8); j > 0; j--)
                {
                    result.Add((byte)alphanumeric[random.Next(alphanumeric.Length)]);
                }
            }
        }

        return [.. result];
    }

    private static Edit Invert(byte[] input, Edit edit) =>
        new(edit.Position, edit.Inserted.Length, input.AsSpan(edit.Position, edit.DeletedLength).ToArray());

    /// <summary>Edits the tree, with the points of the edit like tree-sitter's tests, and returns the new input.</summary>
    private static byte[] Apply(SyntaxTree tree, byte[] input, Edit edit)
    {
        byte[] newInput = [.. input.AsSpan(0, edit.Position), .. edit.Inserted, .. input.AsSpan(edit.Position + edit.DeletedLength)];
        tree.Edit(new InputEdit(
            edit.Position, edit.Position + edit.DeletedLength, edit.Position + edit.Inserted.Length,
            PointAt(input, edit.Position), PointAt(input, edit.Position + edit.DeletedLength), PointAt(newInput, edit.Position + edit.Inserted.Length)));
        return newInput;
    }

    private static Point PointAt(byte[] input, int position)
    {
        var row = 0;
        var lineStart = 0;
        for (var i = 0; i < position; i++)
        {
            if (input[i] == '\n')
            {
                row++;
                lineStart = i + 1;
            }
        }

        return new Point(row, position - lineStart);
    }

    /// <summary>tree-sitter's <c>check_consistent_sizes</c>.</summary>
    private static void CheckConsistentSizes(SyntaxTree tree, byte[] input)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        Check(tree.RootNode);
        Assert.True(tree.RootNode.EndByte <= input.Length);

        void Check(SyntaxNode node)
        {
            var start = node.StartByte;
            var end = node.EndByte;
            Assert.True(start <= end, $"{node.Type} starts at {start} after its end {end}");
            Assert.Equal(start, lineStarts[node.StartPoint.Row] + node.StartPoint.Column);
            Assert.Equal(end, lineStarts[node.EndPoint.Row] + node.EndPoint.Column);

            var lastChildEnd = start;
            var someChildHasChanges = false;
            var namedChildCount = 0;
            foreach (var child in node.Children)
            {
                Assert.True(child.StartByte >= lastChildEnd, $"{child.Type} starts at {child.StartByte} before the end of its previous sibling {lastChildEnd}");
                Check(child);
                someChildHasChanges |= child.HasChanges;
                if (child.IsNamed)
                {
                    namedChildCount++;
                }

                lastChildEnd = child.EndByte;
            }

            Assert.Equal(namedChildCount, node.NamedChildCount);
            Assert.True(end >= lastChildEnd);
            if (someChildHasChanges)
            {
                Assert.True(node.HasChanges);
            }
        }
    }

    private static string Describe(SyntaxNode node)
    {
        var builder = new StringBuilder();
        Write(node);
        return builder.ToString();

        void Write(SyntaxNode n)
        {
            builder.Append('(').Append(n.Type).Append(' ').Append(n.StartByte).Append('-').Append(n.EndByte);
            if (n.ChildCount == 0)
            {
                builder.Append(" \"").Append(n.Text).Append('"');
            }

            foreach (var child in n.Children)
            {
                builder.Append(' ');
                Write(child);
            }

            builder.Append(')');
        }
    }

    private static string SampleSource(string grammar) => grammar switch
    {
        "go" => """
            package main

            import "fmt"

            type point struct {
            	x, y int
            }

            func (p point) add(q point) point {
            	return point{p.x + q.x, p.y + q.y}
            }

            func main() {
            	a := point{1, 2}
            	b := point{2, 1}
            	c := a.add(b)
            	fmt.Println(c.x, c.y)
            	for i := 0; i < 12; i++ {
            		fmt.Println(i * 2)
            	}
            }

            """,
        "java" => """
            package example;

            import java.util.List;

            public class Example {
                private final int value = 12;

                public int add(int a, int b) {
                    int c = a + b;
                    int d = c * 2;
                    return d + value;
                }

                public static void main(String[] args) {
                    var e = new Example();
                    System.out.println(e.add(1, 2));
                    for (int i = 0; i < 12; i++) {
                        System.out.println(i);
                    }
                }
            }

            """,
        _ => """
            #include <stdio.h>

            typedef struct point {
                int x, y;
            } point;

            static point add(point p, point q) {
                point r = { p.x + q.x, p.y + q.y };
                return r;
            }

            int main(void) {
                point a = { 1, 2 };
                point b = { 2, 1 };
                point c = add(a, b);
                printf("%d %d\n", c.x, c.y);
                for (int i = 0; i < 12; i++) {
                    printf("%d\n", i * 2);
                }
                return 0;
            }

            """,
    };
}
