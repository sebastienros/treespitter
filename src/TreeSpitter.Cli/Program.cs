using System.Diagnostics;
using System.Text;
using TreeSpitter.Dsl;
using TreeSpitter.Generation;
using TreeSpitter.Grammars;
using TreeSpitter.Testing;

namespace TreeSpitter.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            return Usage();
        }

        try
        {
            switch (args[0])
            {
                case "grammar":
                    Console.WriteLine(LoadGrammar(args[1]).ToJson());
                    return 0;

                case "info":
                    return Info(args[1]);

                case "parse" when args.Length >= 3:
                    return Parse(args[1], args[2], args.Contains("--time"));

                case "compile":
                    return Compile(args[1], Option(args, "-o") ?? Path.ChangeExtension(Path.GetFileName(args[1]), ".tsl"));

                case "generate":
                    return Generate(args[1], Option(args, "-o"), Option(args, "--namespace"), Option(args, "--class"));

                case "test" when args.Length >= 3:
                    return Test(args[1], args[2], args.Contains("-v") || args.Contains("--verbose"), Option(args, "--filter"));

                default:
                    return Usage();
            }
        }
        catch (Exception e) when (e is GrammarEvaluationException or GrammarException or IOException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static Grammar LoadGrammar(string path)
        => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? GrammarConverter.FromJsonFile(path)
            : new GrammarEvaluator(new GrammarEvaluatorOptions { Log = Console.Error.WriteLine }).EvaluateFile(path);

    private static Language LoadLanguage(string path)
    {
        var stopwatch = Stopwatch.StartNew();
        var grammar = LoadGrammar(path);
        var evaluated = stopwatch.Elapsed;
        var warnings = new List<string>();
        var language = LanguageCompiler.Compile(grammar, warnings);
        foreach (var warning in warnings)
        {
            Console.Error.WriteLine("warning: " + warning);
        }

        Console.Error.WriteLine($"grammar evaluated in {evaluated.TotalMilliseconds:F0} ms, compiled in {(stopwatch.Elapsed - evaluated).TotalMilliseconds:F0} ms");
        return language;
    }

    private static int Parse(string grammarPath, string inputPath, bool time)
    {
        var parser = new Parser(LoadLanguage(grammarPath));
        var input = inputPath == "-" ? Encoding.UTF8.GetBytes(Console.In.ReadToEnd()) : File.ReadAllBytes(inputPath);

        var stopwatch = Stopwatch.StartNew();
        var tree = parser.Parse(input);
        stopwatch.Stop();

        Console.WriteLine(tree.RootNode.ToSExpression());
        if (time)
        {
            Console.Error.WriteLine($"parsed {input.Length} bytes in {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        }

        return tree.HasError ? 1 : 0;
    }

    private static int Compile(string grammarPath, string output)
    {
        var language = LoadLanguage(grammarPath);
        File.WriteAllBytes(output, language.Save());
        Console.Error.WriteLine($"wrote {output}: {language.StateCount} parse states, {language.LexStateCount} lex states");
        return 0;
    }

    private static int Generate(string grammarPath, string? output, string? ns, string? className)
    {
        var language = grammarPath.EndsWith(".tsl", StringComparison.OrdinalIgnoreCase) ? Language.Load(File.ReadAllBytes(grammarPath)) : LoadLanguage(grammarPath);
        var source = TreeSpitter.CodeGen.CSharpGenerator.Generate(language, new TreeSpitter.CodeGen.CSharpGeneratorOptions
        {
            Namespace = ns,
            ClassName = className ?? ToClassName(language.Name) + "Language",
        });

        if (output is null)
        {
            Console.Write(source);
        }
        else
        {
            File.WriteAllText(output, source);
            Console.Error.WriteLine($"wrote {output}");
        }

        return 0;
    }

    private static string ToClassName(string name)
    {
        var builder = new StringBuilder();
        var upper = true;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return builder.ToString();
    }

    private static int Info(string grammarPath)
    {
        var language = LoadLanguage(grammarPath);
        Console.WriteLine($"name: {language.Name}");
        Console.WriteLine($"states: {language.StateCount}");
        Console.WriteLine($"symbols: {language.SymbolCount}");
        Console.WriteLine($"tokens: {language.TokenCount}");
        Console.WriteLine($"aliases: {language.AliasCount}");
        Console.WriteLine($"fields: {language.FieldCount}");
        Console.WriteLine($"production ids: {language.ProductionIdCount}");
        Console.WriteLine($"lex states: {language.LexStateCount} (keywords: {language.KeywordLexStateCount})");
        Console.WriteLine($"parse actions: {language.ParseActionCount}");
        return 0;
    }

    private static int Test(string grammarPath, string corpusPath, bool verbose, string? filter)
    {
        var parser = new Parser(LoadLanguage(grammarPath));
        var tests = Corpus.Load(corpusPath)
            .Where(t => !t.Skip && (filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        int passed = 0, failed = 0;
        var stopwatch = Stopwatch.StartNew();
        string? currentFile = null;

        foreach (var test in tests)
        {
            if (test.File != currentFile)
            {
                currentFile = test.File;
                Console.WriteLine(Path.GetFileName(currentFile) + ":");
            }

            var result = Corpus.Run(parser, test);
            if (result.Passed)
            {
                passed++;
                Console.WriteLine("  ✓ " + test.Name);
            }
            else
            {
                failed++;
                Console.WriteLine("  ✗ " + test.Name);
                if (verbose)
                {
                    Console.WriteLine("    expected: " + result.Expected);
                    Console.WriteLine("    actual:   " + result.Actual);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed ({tests.Count} tests, {stopwatch.Elapsed.TotalSeconds:F1} s)");
        return failed == 0 ? 0 : 1;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              treespitter grammar <grammar.js|grammar.json>            prints the grammar as JSON (like src/grammar.json)
              treespitter info <grammar>                               prints the size of the generated tables
              treespitter compile <grammar> [-o file.tsl]              writes the compiled tables (Language.Save)
              treespitter generate <grammar|file.tsl> [-o file.cs] [--namespace ns] [--class name]
                                                                        generates the C# source of a precompiled parser
              treespitter parse <grammar> <file|-> [--time]            prints the syntax tree
              treespitter test <grammar> <corpus> [-v] [--filter text] runs tree-sitter corpus tests
            """);
        return 2;
    }
}
