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
        _ => throw new ArgumentException($"No input for the grammar '{name}'.", nameof(name)),
    };

    private static readonly Dictionary<string, Language> Languages = [];

    /// <summary>The compiled language of a grammar, compiled once per process.</summary>
    public static Language Language(string name)
    {
        lock (Languages)
        {
            if (!Languages.TryGetValue(name, out var language))
            {
                language = TreeSpitter.Language.FromGrammarFile(Path(name));
                Languages[name] = language;
            }

            return language;
        }
    }

    /// <summary>The source-generated language of a grammar.</summary>
    public static Language GeneratedLanguage(string name) => name switch
    {
        "json" => TreeSpitter.Languages.Json.Language,
        "go" => TreeSpitter.Languages.Go.Language,
        "java" => TreeSpitter.Languages.Java.Language,
        "c" => TreeSpitter.Languages.C.Language,
        _ => throw new ArgumentException($"No generated language '{name}'.", nameof(name)),
    };

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
                return System.IO.Path.Combine(directory, "grammars", name, "grammar.js");
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
