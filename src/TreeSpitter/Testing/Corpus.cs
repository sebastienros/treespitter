using System.Text;
using System.Text.RegularExpressions;

namespace TreeSpitter.Testing;

/// <summary>A test of a tree-sitter corpus file: an input and the expected S-expression.</summary>
public sealed class CorpusTest
{
    /// <summary>Creates a test.</summary>
    public CorpusTest(string file, string name, string input, string expected, IReadOnlyList<string> attributes)
    {
        File = file;
        Name = name;
        Input = input;
        Expected = expected;
        Attributes = attributes;
    }

    /// <summary>Gets the path of the corpus file.</summary>
    public string File { get; }

    /// <summary>Gets the name of the test.</summary>
    public string Name { get; }

    /// <summary>Gets the text to parse.</summary>
    public string Input { get; }

    /// <summary>Gets the expected S-expression.</summary>
    public string Expected { get; }

    /// <summary>The attributes, like <c>:skip</c> or <c>:error</c>.</summary>
    public IReadOnlyList<string> Attributes { get; }

    /// <summary>Gets whether the test has the <c>:skip</c> attribute.</summary>
    public bool Skip => Attributes.Contains(":skip");

    /// <summary>Whether the input is expected to have syntax errors.</summary>
    public bool ExpectsError => Attributes.Contains(":error") || Expected.Contains("(ERROR") || Expected.Contains("(MISSING");

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>The result of running a <see cref="CorpusTest"/>.</summary>
public sealed class CorpusTestResult
{
    /// <summary>Creates a result.</summary>
    public CorpusTestResult(CorpusTest test, bool passed, string actual, string expected)
    {
        Test = test;
        Passed = passed;
        Actual = actual;
        Expected = expected;
    }

    /// <summary>Gets the test.</summary>
    public CorpusTest Test { get; }

    /// <summary>Gets whether the tree matches the expected S-expression.</summary>
    public bool Passed { get; }

    /// <summary>The normalized S-expression of the parsed tree.</summary>
    public string Actual { get; }

    /// <summary>The normalized expected S-expression.</summary>
    public string Expected { get; }
}

/// <summary>
/// Reads and runs the corpus files of tree-sitter grammars (<c>test/corpus/*.txt</c>).
/// </summary>
public static class Corpus
{
    private static readonly Regex HeaderRegex = new(@"^(?<equals>={3,})(?<suffix>[^=\r\n][^\r\n]*)?\r?$", RegexOptions.CultureInvariant);
    private static readonly Regex DividerRegex = new(@"^(?<dashes>-{3,})(?<suffix>[^-\r\n][^\r\n]*)?\r?$", RegexOptions.CultureInvariant);
    private static readonly Regex WhiteSpaceRegex = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex FieldRegex = new(@"[\w-]+: ", RegexOptions.CultureInvariant);
    private static readonly Regex CommentRegex = new(@"^\s*;.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Reads the tests of all the <c>.txt</c> files of a directory, or of a single file.</summary>
    public static IReadOnlyList<CorpusTest> Load(string path)
    {
        var tests = new List<CorpusTest>();
        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.txt", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                tests.AddRange(Parse(System.IO.File.ReadAllText(file), file));
            }
        }
        else
        {
            tests.AddRange(Parse(System.IO.File.ReadAllText(path), path));
        }

        return tests;
    }

    /// <summary>Parses the content of a corpus file.</summary>
    public static IReadOnlyList<CorpusTest> Parse(string content, string file = "")
    {
        var lines = content.Split('\n');
        var tests = new List<CorpusTest>();
        var i = 0;

        while (i < lines.Length)
        {
            var header = HeaderRegex.Match(lines[i]);
            if (!header.Success)
            {
                i++;
                continue;
            }

            var suffix = header.Groups["suffix"].Value.TrimEnd('\r');

            // The name and the attributes, until the closing line of equals
            var nameLines = new List<string>();
            var j = i + 1;
            while (j < lines.Length && !IsHeaderLine(lines[j], suffix))
            {
                nameLines.Add(lines[j].TrimEnd('\r'));
                j++;
            }

            if (j >= lines.Length || nameLines.Count == 0)
            {
                break;
            }

            var bodyStart = j + 1;

            // The body ends at the next header
            var next = bodyStart;
            while (next < lines.Length && !(HeaderRegex.Match(lines[next]) is { Success: true } h && h.Groups["suffix"].Value.TrimEnd('\r') == suffix && next + 2 < lines.Length && HasClosingHeader(lines, next, suffix)))
            {
                next++;
            }

            // The input ends at the last divider of the body
            var divider = -1;
            for (var k = bodyStart; k < next; k++)
            {
                var match = DividerRegex.Match(lines[k]);
                if (match.Success && match.Groups["suffix"].Value.TrimEnd('\r') == suffix)
                {
                    divider = k;
                }
            }

            if (divider >= 0)
            {
                var input = Join(lines, bodyStart, divider);
                var expected = Join(lines, divider + 1, next).Trim();

                // Like tree-sitter, the newline before the divider isn't part of the input
                input = TrimFinalNewLine(input);

                var name = nameLines[0].Trim();
                var attributes = new List<string>();
                for (var k = 1; k < nameLines.Count; k++)
                {
                    var attribute = nameLines[k].Trim();
                    if (attribute.StartsWith(":", StringComparison.Ordinal))
                    {
                        attributes.Add(attribute);
                    }
                }

                tests.Add(new CorpusTest(file, name, input, expected, attributes));
            }

            i = next;
        }

        return tests;
    }

    private static bool IsHeaderLine(string line, string suffix)
    {
        var match = HeaderRegex.Match(line);
        return match.Success && match.Groups["suffix"].Value.TrimEnd('\r') == suffix;
    }

    private static bool HasClosingHeader(string[] lines, int start, string suffix)
    {
        for (var k = start + 1; k < lines.Length && k < start + 8; k++)
        {
            if (IsHeaderLine(lines[k], suffix))
            {
                return true;
            }
        }

        return false;
    }

    private static string Join(string[] lines, int start, int end)
    {
        var builder = new StringBuilder();
        for (var k = start; k < end; k++)
        {
            builder.Append(lines[k]);
            if (k + 1 < end)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private static string TrimFinalNewLine(string input)
    {
        if (input.EndsWith("\r", StringComparison.Ordinal))
        {
            input = input.Substring(0, input.Length - 1);
        }

        return input;
    }

    /// <summary>
    /// Normalizes an S-expression for comparisons: single spaces, no comments, and optionally no field names.
    /// </summary>
    public static string NormalizeSExpression(string sexp, bool stripFields)
    {
        sexp = CommentRegex.Replace(sexp, string.Empty);
        sexp = WhiteSpaceRegex.Replace(sexp, " ").Trim();
        sexp = sexp.Replace("( ", "(").Replace(" )", ")");
        if (stripFields)
        {
            sexp = FieldRegex.Replace(sexp, string.Empty);
        }

        return sexp;
    }

    /// <summary>
    /// Runs a test: parses its input and compares the S-expressions, ignoring the fields when the expected output has none.
    /// Like <c>tree-sitter test</c>, a test with the <c>:error</c> attribute passes when the tree has an error, whatever
    /// its expected output.
    /// </summary>
    public static CorpusTestResult Run(Parser parser, CorpusTest test)
    {
        using var tree = parser.Parse(test.Input);
        var hasFields = FieldRegex.IsMatch(test.Expected);
        var actual = NormalizeSExpression(tree.RootNode.ToSExpression(), stripFields: !hasFields);
        var expected = NormalizeSExpression(test.Expected, stripFields: false);
        var passed = test.Attributes.Contains(":error") ? tree.HasError : actual == expected;
        return new CorpusTestResult(test, passed, actual, expected);
    }
}
