using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using TreeSpitter.Grammars;

namespace TreeSpitter.Dsl;

/// <summary>
/// Evaluates tree-sitter <c>grammar.js</c> files with Jint and converts the result to a <see cref="Grammar"/>.
/// </summary>
/// <remarks>
/// The DSL functions (<c>grammar</c>, <c>seq</c>, <c>choice</c>, <c>repeat</c>, <c>prec</c>, <c>token</c>, ...) are defined
/// as globals, CommonJS modules (<c>require</c>, <c>module.exports</c>) are supported, including other grammars from
/// <c>node_modules</c> to extend them, and ES modules (<c>export default grammar(...)</c>) are loaded with the module
/// support of Jint.
/// </remarks>
public sealed class GrammarEvaluator
{
    private static readonly Lazy<string> DslSource = new(LoadDslSource);

    private static readonly Regex EsModuleRegex = new(
        @"^\s*(export\s+default\b|export\s*\{|import\s+[\w{*].*\bfrom\s+['""]|import\s+['""])",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly GrammarEvaluatorOptions _options;

    /// <summary>Creates an evaluator with optional options.</summary>
    public GrammarEvaluator(GrammarEvaluatorOptions? options = null)
    {
        _options = options ?? new GrammarEvaluatorOptions();
    }

    /// <summary>Evaluates a <c>grammar.js</c> file.</summary>
    public Grammar EvaluateFile(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        return Evaluate(File.ReadAllText(fullPath), fullPath);
    }

    /// <summary>
    /// Evaluates the source of a <c>grammar.js</c> file. The <paramref name="path"/> is used to resolve the modules it
    /// requires, the current directory is used when it's not set.
    /// </summary>
    public Grammar Evaluate(string source, string? path = null)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var fullPath = Path.GetFullPath(path ?? Path.Combine(Directory.GetCurrentDirectory(), "grammar.js"));

        try
        {
            var result = EsModuleRegex.IsMatch(source)
                ? EvaluateEsModule(source, fullPath)
                : EvaluateCommonJs(source, fullPath);

            return GrammarConverter.FromEvaluationResult(result);
        }
        catch (JavaScriptException e)
        {
            throw new GrammarEvaluationException(FormatJavaScriptError(e), e);
        }
        catch (Acornima.ParseErrorException e)
        {
            throw new GrammarEvaluationException("Syntax error: " + e.Message, e);
        }
    }

    private JsValue EvaluateCommonJs(string source, string fullPath)
    {
        var engine = CreateEngine(configureModules: null, fullPath);
        var loader = new CommonJsLoader(engine, _options);
        var module = loader.LoadMain(source, fullPath);
        return module.Get("exports");
    }

    private JsValue EvaluateEsModule(string source, string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? Path.GetDirectoryName(fullPath)!;
        var engine = CreateEngine(options => options.EnableModules(root, restrictToBasePath: false), fullPath);

        // The CommonJS globals are also available, some grammars mix both
        var loader = new CommonJsLoader(engine, _options);
        loader.InstallGlobals(Path.GetDirectoryName(fullPath)!, fullPath);

        var specifier = "./" + fullPath.Substring(root.Length).Replace('\\', '/');
        engine.Modules.Add(specifier, source);
        var ns = engine.Modules.Import(specifier);
        var exported = ns.Get("default");
        return exported.IsUndefined() ? ns : exported;
    }

    private Engine CreateEngine(Action<Options>? configureModules, string fullPath)
    {
        var engine = new Engine(options =>
        {
            options.LimitRecursion(4096);
            if (_options.Timeout is { } timeout)
            {
                options.TimeoutInterval(timeout);
            }

            configureModules?.Invoke(options);
        });

        engine.SetValue("__tree-spitter_log", new Action<string>(message => _options.Log?.Invoke(message)));
        engine.Execute(
            """
            globalThis.console = {
              log: (...args) => __tree-spitter_log(args.map(a => typeof a === 'string' ? a : JSON.stringify(a)).join(' ')),
              info: (...args) => console.log(...args),
              warn: (...args) => console.log(...args),
              error: (...args) => console.log(...args),
              debug: (...args) => console.log(...args),
            };
            """,
            "console.js");

        var env = new StringBuilder("{");
        var first = true;
        foreach (var entry in _options.Environment)
        {
            if (!first)
            {
                env.Append(',');
            }

            first = false;
            env.Append(JsonString(entry.Key)).Append(':').Append(JsonString(entry.Value));
        }

        env.Append('}');

        engine.Execute(
            "globalThis.process = { env: " + env + ", platform: 'linux', argv: [], cwd: () => " +
            JsonString(Path.GetDirectoryName(fullPath)!) + ", versions: {} };",
            "process.js");

        engine.Execute(DslSource.Value, "tree-sitter-dsl.js");
        return engine;
    }

    internal static string JsonString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
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
                    if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static string FormatJavaScriptError(JavaScriptException e)
    {
        var message = e.Error.IsObject() && e.Error.AsObject().Get("message") is { } m && m.IsString()
            ? m.AsString()
            : e.Message;

        var location = e.Location;
        var source = location.SourceFile;
        if (!string.IsNullOrEmpty(source))
        {
            message += $" ({source}:{location.Start.Line}:{location.Start.Column + 1})";
        }

        return message;
    }

    private static string LoadDslSource()
    {
        using var stream = typeof(GrammarEvaluator).GetTypeInfo().Assembly.GetManifestResourceStream("TreeSpitter.Dsl.dsl.js")
            ?? throw new InvalidOperationException("The DSL resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// The error thrown when a grammar can't be evaluated.
/// </summary>
public sealed class GrammarEvaluationException : Exception
{
    /// <summary>Creates the error with a message.</summary>
    public GrammarEvaluationException(string message) : base(message)
    {
    }

    /// <summary>Creates the error with a message and the exception which caused it.</summary>
    public GrammarEvaluationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
