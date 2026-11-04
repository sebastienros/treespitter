using System.Globalization;
using System.Text;

namespace TreeSpitter.Benchmarks;

/// <summary>The grammars vendored by the tests, and generated inputs.</summary>
internal static class Grammars
{
    /// <summary>The grammars with an input generator.</summary>
    public static readonly string[] Names = ["json", "go", "java", "c"];

    /// <summary>An input of about <paramref name="size"/> chars for a grammar.</summary>
    public static string Input(string name, int size) => name switch
    {
        "json" => Json(size),
        "go" => Go(size),
        "java" => Java(size),
        "c" => C(size),
        _ => FromCorpus(name, size),
    };

    /// <summary>
    /// An input of about <paramref name="size"/> chars made of the inputs of the corpus tests of a grammar: the ones
    /// which parse without errors, alone and one after the other, repeated while the input has no errors. A grammar
    /// whose documents can't follow each other (a JSDoc comment) gets a smaller input.
    /// </summary>
    public static string FromCorpus(string name, int size)
    {
        var parser = new Parser(GeneratedLanguage(name));
        var separator = Separators.GetValueOrDefault(name, "\n");
        var tests = Testing.Corpus.Load(System.IO.Path.Combine(Root(), "grammars", name, "corpus"))
            .Where(t => !t.Skip && t.Attributes.All(a => !a.StartsWith(":language(", StringComparison.Ordinal) || a == $":language({name})" || a == $":language({name.Replace('-', '_')})") && !t.Expected.Contains("ERROR") && !t.Expected.Contains("MISSING"))
            .ToList();
        var block = new StringBuilder();
        foreach (var test in tests)
        {
            var length = block.Length;
            block.Append(test.Input).Append(separator);
            if (HasError(parser, block.ToString()))
            {
                block.Length = length;
            }
        }

        if (block.Length == 0)
        {
            throw new ArgumentException($"No corpus input of the grammar '{name}' parses without errors.", nameof(name));
        }

        var text = block.ToString();
        if (HasError(parser, text + text))
        {
            return text;
        }

        var builder = new StringBuilder();
        while (builder.Length < size)
        {
            builder.Append(text);
        }

        return builder.ToString();
    }

    /// <summary>The separators of the corpus inputs, for the grammars where a new line isn't enough.</summary>
    private static readonly Dictionary<string, string> Separators = new()
    {
        // The tests start with <?php: the PHP code is closed after each one
        ["php"] = "\n?>\n",
    };

    private static bool HasError(Parser parser, string input)
    {
        using var tree = parser.Parse(input);
        return tree.HasError;
    }

    private static readonly Dictionary<string, Language> Languages = [];

    /// <summary>The compiled language of a grammar, compiled once per process.</summary>
    public static Language Language(string name)
    {
        lock (Languages)
        {
            if (!Languages.TryGetValue(name, out var language))
            {
                language = TreeSpitter.Language.FromGrammarFile(Path(name));
                if (ExternalScanner(name) is { } scanner)
                {
                    language.WithExternalScanner(scanner);
                }

                Languages[name] = language;
            }

            return language;
        }
    }

    /// <summary>The external scanner of a grammar: the one of its generated language.</summary>
    public static Func<IExternalScanner>? ExternalScanner(string name) => GeneratedLanguage(name).ExternalScannerFactory;

    /// <summary>
    /// The source-generated language of a grammar, from TreeSpitter.Languages: the class marked with
    /// <c>[GeneratedLanguage("name/grammar.json")]</c>.
    /// </summary>
    public static Language GeneratedLanguage(string name)
    {
        var type = typeof(TreeSpitter.Languages.Json).Assembly.GetTypes().FirstOrDefault(t =>
            t.GetCustomAttributes(typeof(GeneratedLanguageAttribute), false).Cast<GeneratedLanguageAttribute>().Any(a => a.GrammarPath == name + "/grammar.json"))
            ?? throw new ArgumentException($"No generated language '{name}'.", nameof(name));
        return (Language)type.GetProperty("Language")!.GetValue(null)!;
    }

    /// <summary>All the vendored grammars: the directories of the grammars folder with a grammar.json and a corpus.</summary>
    public static string[] All() => Directory.GetDirectories(System.IO.Path.Combine(Root(), "grammars"))
        .Where(d => File.Exists(System.IO.Path.Combine(d, "grammar.json")) && Directory.Exists(System.IO.Path.Combine(d, "corpus")))
        .Select(d => System.IO.Path.GetFileName(d)!)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Runs a comparison with the native parsers, which are tree-sitter 0.26: the runtime reproduces tree-sitter 0.27,
    /// whose error recovery differs, and has a switch to behave like 0.26 for the comparisons.
    /// </summary>
    public static T As026<T>(Func<T> action)
    {
        var property = typeof(Parser).Assembly.GetType("TreeSpitter.Compatibility")!
            .GetProperty("TreeSitter026", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!;
        var old = property.GetValue(null);
        property.SetValue(null, true);
        try
        {
            return action();
        }
        finally
        {
            property.SetValue(null, old);
        }
    }

    /// <summary>The root of the repository.</summary>
    public static string Root()
    {
        for (var directory = AppContext.BaseDirectory; directory is not null; directory = Directory.GetParent(directory)?.FullName)
        {
            if (File.Exists(System.IO.Path.Combine(directory, "TreeSpitter.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("The repository root wasn't found.");
    }

    public static string Path(string name)
    {
        // The benchmarks run from bin/, the grammars are in the repository
        for (var directory = AppContext.BaseDirectory; directory is not null; directory = Directory.GetParent(directory)?.FullName)
        {
            if (File.Exists(System.IO.Path.Combine(directory, "TreeSpitter.slnx")))
            {
                // The grammars which require files of sibling directories keep the layout of their repository
                var path = System.IO.Path.Combine(directory, "grammars", name, "grammar.js");
                return File.Exists(path) ? path : System.IO.Path.Combine(directory, "grammars", name, name, "grammar.js");
            }
        }

        throw new InvalidOperationException("The repository root wasn't found.");
    }

    /// <summary>A JSON document of about <paramref name="size"/> chars.</summary>
    public static string Json(int size)
    {
        var builder = new StringBuilder("[\n");
        for (var i = 0; builder.Length < size; i++)
        {
            builder.Append(i == 0 ? "  " : ",\n  ");
            var price = (i * 1.25).ToString(CultureInfo.InvariantCulture);
            builder.Append($$"""{ "id": {{i}}, "name": "item {{i}}", "price": {{price}}, "tags": ["a", "b\n"], "active": {{(i % 2 == 0 ? "true" : "false")}}, "parent": null }""");
        }

        return builder.Append("\n]\n").ToString();
    }

    /// <summary>A Go file of about <paramref name="size"/> chars.</summary>
    public static string Go(int size)
    {
        var builder = new StringBuilder("package main\n\nimport \"fmt\"\n");
        for (var i = 0; builder.Length < size; i++)
        {
            builder.Append($$"""

                // compute{{i}} sums the even values.
                func compute{{i}}(values []int, limit int) (int, error) {
                	total := 0
                	for i, v := range values {
                		if v%2 == 0 && i < limit {
                			total += v * {{i + 1}}
                		} else if v > limit {
                			return 0, fmt.Errorf("value %d is too large", v)
                		}
                	}
                	p := &Point{X: total, Y: -total}
                	return p.X + len(values)<<1, nil
                }

                """);
        }

        return builder.ToString();
    }

    /// <summary>A Java file of about <paramref name="size"/> chars.</summary>
    public static string Java(int size)
    {
        var builder = new StringBuilder("package demo;\n\nimport java.util.*;\n");
        for (var i = 0; builder.Length < size; i++)
        {
            builder.Append($$"""

                class Widget{{i}} extends Base implements Comparable<Widget{{i}}> {
                    private final List<String> names = new ArrayList<>();
                    private int count = {{i}};

                    public Widget{{i}}(int count) {
                        this.count = count * 2 + 1;
                    }

                    @Override
                    public int compareTo(Widget{{i}} other) {
                        for (int i = 0; i < names.size(); i++) {
                            if (names.get(i).length() > count && !names.isEmpty()) {
                                count += i << 1;
                            } else {
                                return -1;
                            }
                        }
                        String label = "widget " + count;
                        return label.hashCode() ^ other.count;
                    }
                }

                """);
        }

        return builder.ToString();
    }

    /// <summary>A C file of about <paramref name="size"/> chars.</summary>
    public static string C(int size)
    {
        var builder = new StringBuilder("#include <stddef.h>\n");
        for (var i = 0; builder.Length < size; i++)
        {
            builder.Append($$"""

                struct point{{i}} { int x; int y; };

                /* Sums the even values. */
                static int compute{{i}}(const int *values, size_t length, struct point{{i}} *p) {
                    int total = 0;
                    for (size_t i = 0; i < length; i++) {
                        if (values[i] % 2 == 0 && i < 10) {
                            total += values[i] * {{i + 1}};
                        } else if (values[i] > 100) {
                            return -1;
                        }
                    }
                    p->x = total;
                    p->y = -total;
                    return p->x + (int)length << 1;
                }

                """);
        }

        return builder.ToString();
    }
}
