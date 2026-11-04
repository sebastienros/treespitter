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

                case "generate-batch":
                    return GenerateBatch(args[1], Option(args, "--parallelism") is { } parallelism ? int.Parse(parallelism, System.Globalization.CultureInfo.InvariantCulture) : 0);

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

    /// <summary>
    /// Generates the precompiled parsers listed in a file, in parallel: one per line, the path of a <c>grammar.json</c>,
    /// the path of the C# file, the namespace, the class name and the fully qualified name of the external scanner (empty
    /// for none), separated by tabs. The classes are static partial classes declared elsewhere, generated like the
    /// classes marked with <c>[GeneratedLanguage]</c> by the source generator: <c>TreeSpitter.Languages</c> builds its
    /// parsers this way, once for its target frameworks.
    /// </summary>
    private static int GenerateBatch(string batchPath, int parallelism)
    {
        var entries = new List<string[]>();
        foreach (var line in File.ReadAllLines(batchPath))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 4)
            {
                Console.Error.WriteLine($"{batchPath}: error TSG003: Expected a grammar, an output, a namespace and a class name, separated by tabs: '{line}'");
                return 1;
            }

            entries.Add(fields);
        }

        // The largest grammars first, so that they don't finish last (the missing ones are reported below)
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        entries = [.. entries.OrderByDescending(e => Size(e[0]))];
        var failures = 0;
        var gate = new object();
        var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism > 0 ? parallelism : Environment.ProcessorCount };
        Parallel.ForEach(entries, options, fields =>
        {
            var (grammarPath, output) = (fields[0], fields[1]);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Like TreeSpitter.Generators.LanguageGenerator
                var grammar = GrammarJsonReader.Read(File.ReadAllText(grammarPath));
                var language = LanguageCompiler.Compile(grammar);
                var source = TreeSpitter.CodeGen.CSharpGenerator.Generate(language, new TreeSpitter.CodeGen.CSharpGeneratorOptions
                {
                    Namespace = fields[2].Length == 0 ? null : fields[2],
                    ClassName = fields[3],
                    Accessibility = null,
                    IsStatic = true,
                    ExternalScanner = fields.Length > 4 && fields[4].Length > 0 ? fields[4] : null,
                });

                // Written whole, so that an interrupted build doesn't leave a file newer than its grammar
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                var temporary = output + ".tmp";
                File.WriteAllText(temporary, source, Encoding.UTF8);
                File.Move(temporary, output, overwrite: true);
                lock (gate)
                {
                    Console.WriteLine($"{grammarPath}: generated {output} in {stopwatch.Elapsed.TotalSeconds:F1} s");
                }
            }
            catch (Exception e) when (e is GrammarException or GrammarFormatException or InvalidOperationException or IOException)
            {
                Interlocked.Increment(ref failures);
                lock (gate)
                {
                    Console.Error.WriteLine($"{grammarPath}: error TSG002: The grammar '{grammarPath}' can't be compiled: {e.Message}");
                }
            }
        });

        return failures == 0 ? 0 : 1;
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
              treespitter generate-batch <file> [--parallelism n]      generates the parsers of the lines of a file, in parallel:
                                                                        grammar.json, file.cs, namespace, class, scanner (tabs)
              treespitter parse <grammar> <file|-> [--time]            prints the syntax tree
              treespitter test <grammar> <corpus> [-v] [--filter text] runs tree-sitter corpus tests
            """);
        return 2;
    }
}
