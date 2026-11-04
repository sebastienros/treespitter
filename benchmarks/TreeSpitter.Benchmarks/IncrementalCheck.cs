using System.Text;

namespace TreeSpitter.Benchmarks;

/// <summary>
/// Compares the incremental parses with the native ones: the random edits of the corpus tests of tree-sitter are applied
/// to the trees of both parsers, and the trees of the incremental parses compared.
/// </summary>
internal static class IncrementalCheck
{
    private static readonly char[] Operators = ['+', '-', '<', '>', '(', ')', '*', '/', '&', '|', '!', ',', '.', '%'];

    public readonly record struct Edit(int Position, int DeletedLength, byte[] Inserted);

    public static void Run(string language, int trials)
    {
        var parser = new Parser(Grammars.Language(language));
        using var native = new NativeUtf8Parser(language);
        var tests = Testing.Corpus.Load(Path.Combine(Grammars.Root(), "grammars", language, "corpus")).Where(t => !t.Skip).ToList();
        int parses = 0, freshDifferences = 0, editedDifferences = 0, undoneDifferences = 0, nativeEditedVsFresh = 0, undoneVsOriginal = 0;
        for (var testIndex = 0; testIndex < tests.Count; testIndex++)
        {
            var original = Encoding.UTF8.GetBytes(tests[testIndex].Input);
            using var originalTree = parser.Parse(original);
            using var originalNative = native.ParseTree(original, null);
            if (originalTree.ToSExpression() != originalNative.ToSExpression())
            {
                freshDifferences++;
                Report("fresh", tests[testIndex].Name, 0, original, originalNative.ToSExpression(), originalTree.ToSExpression());
                continue;
            }

            for (var trial = 0; trial < trials; trial++)
            {
                var random = new Random(testIndex * 1000 + trial);
                var input = original;
                using var tree = originalTree.Copy();
                using var nativeTree = originalNative.Copy();
                var undo = new Stack<Edit>();
                var editCount = 1 + random.Next(3);
                for (var i = 0; i < editCount; i++)
                {
                    var edit = RandomEdit(random, input);
                    undo.Push(new Edit(edit.Position, edit.Inserted.Length, input.AsSpan(edit.Position, edit.DeletedLength).ToArray()));
                    input = Apply(tree, nativeTree, input, edit);
                }

                parses++;
                using var edited = parser.Parse(input, tree);
                using var nativeEdited = native.ParseTree(input, nativeTree);
                using (var nativeFresh = native.ParseTree(input, null))
                {
                    if (nativeFresh.ToSExpression() != nativeEdited.ToSExpression())
                    {
                        nativeEditedVsFresh++;
                    }
                }

                if (edited.ToSExpression() != nativeEdited.ToSExpression())
                {
                    editedDifferences++;
                    Report("edited", tests[testIndex].Name, trial, input, nativeEdited.ToSExpression(), edited.ToSExpression());
                }

                while (undo.Count > 0)
                {
                    input = Apply(edited, nativeEdited, input, undo.Pop());
                }

                using var undone = parser.Parse(input, edited);
                using var nativeUndone = native.ParseTree(input, nativeEdited);
                if (undone.ToSExpression() != nativeUndone.ToSExpression())
                {
                    undoneDifferences++;
                    Report("undone", tests[testIndex].Name, trial, input, nativeUndone.ToSExpression(), undone.ToSExpression());
                }

                if (nativeUndone.ToSExpression() != originalNative.ToSExpression())
                {
                    undoneVsOriginal++;
                }
            }
        }

        Console.WriteLine($"{language}: {tests.Count} tests ({freshDifferences} with different fresh trees, skipped), {parses} edited parses: " +
            $"{editedDifferences} edited and {undoneDifferences} undone trees differ from native; " +
            $"native: {nativeEditedVsFresh} edited trees differ from a fresh parse, {undoneVsOriginal} undone trees differ from the original");
    }

    /// <summary>Logs the native parses of a trial, with the decisions about the reuse of the nodes.</summary>
    public static void Trace(string language, string testName, int trial)
    {
        var parser = new Parser(Grammars.Language(language));
        using var native = new NativeUtf8Parser(language);
        var tests = Testing.Corpus.Load(Path.Combine(Grammars.Root(), "grammars", language, "corpus")).Where(t => !t.Skip).ToList();
        var testIndex = tests.FindIndex(t => t.Name == testName);
        var original = Encoding.UTF8.GetBytes(tests[testIndex].Input);
        using var tree = parser.Parse(original);
        using var nativeTree = native.ParseTree(original, null);
        var random = new Random(testIndex * 1000 + trial);
        var input = original;
        var undo = new Stack<Edit>();
        var editCount = 1 + random.Next(3);
        for (var i = 0; i < editCount; i++)
        {
            var edit = RandomEdit(random, input);
            undo.Push(new Edit(edit.Position, edit.Inserted.Length, input.AsSpan(edit.Position, edit.DeletedLength).ToArray()));
            input = Apply(tree, nativeTree, input, edit);
        }

        Console.WriteLine("==== edited parse, native");
        NativeUtf8Parser.Log = m => Console.WriteLine("  " + m);
        native.EnableLogging(true);
        using var nativeEdited = native.ParseTree(input, nativeTree);
        native.EnableLogging(false);
        using var edited = parser.Parse(input, tree);
        while (undo.Count > 0)
        {
            input = Apply(edited, nativeEdited, input, undo.Pop());
        }

        Console.WriteLine("==== undone parse, native");
        native.EnableLogging(true);
        using var nativeUndone = native.ParseTree(input, nativeEdited);
        native.EnableLogging(false);
        using var undone = parser.Parse(input, edited);
        Console.WriteLine("native: " + nativeUndone.ToSExpression());
        Console.WriteLine("ours:   " + undone.ToSExpression());
    }

    private static void Report(string kind, string name, int trial, byte[] input, string expected, string actual)
    {
        Console.WriteLine($"  {kind} tree of '{name}' (trial {trial}) differs, input: {Encoding.UTF8.GetString(input).Replace("\n", "\\n")}");
        Console.WriteLine($"    native: {expected}");
        Console.WriteLine($"    ours:   {actual}");
    }

    private static byte[] Apply(SyntaxTree tree, NativeUtf8Parser.NativeTree nativeTree, byte[] input, Edit edit)
    {
        byte[] newInput = [.. input.AsSpan(0, edit.Position), .. edit.Inserted, .. input.AsSpan(edit.Position + edit.DeletedLength)];
        tree.Edit(new InputEdit(
            edit.Position, edit.Position + edit.DeletedLength, edit.Position + edit.Inserted.Length,
            PointAt(input, edit.Position), PointAt(input, edit.Position + edit.DeletedLength), PointAt(newInput, edit.Position + edit.Inserted.Length)));
        nativeTree.Edit(input, newInput, edit.Position, edit.Position + edit.DeletedLength, edit.Position + edit.Inserted.Length);
        return newInput;
    }

    /// <summary>The point of a position, with the column in bytes, like the points given to the native parser.</summary>
    public static Point PointAt(byte[] input, int position)
    {
        var row = 0;
        var lineStart = 0;
        for (var i = 0; i < position && i < input.Length; i++)
        {
            if (input[i] == '\n')
            {
                row++;
                lineStart = i + 1;
            }
        }

        return new Point(row, position - lineStart);
    }

    /// <summary>tree-sitter's <c>get_random_edit</c>, as in the tests.</summary>
    public static Edit RandomEdit(Random random, byte[] input)
    {
        var choice = random.Next(10);
        if (choice < 2)
        {
            return new Edit(input.Length, 0, RandomWords(random, 3));
        }

        if (choice < 5)
        {
            var deleted = Math.Min(random.Next(30), input.Length);
            return new Edit(input.Length - deleted, deleted, []);
        }

        var position = random.Next(input.Length + 1);
        if (choice < 8)
        {
            return new Edit(position, 0, RandomWords(random, 1 + random.Next(3)));
        }

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
}
