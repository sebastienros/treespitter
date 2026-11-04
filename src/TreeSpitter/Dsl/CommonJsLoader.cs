using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime.Interop;

namespace TreeSpitter.Dsl;

/// <summary>
/// A minimal CommonJS module system: <c>require</c>, <c>module</c>, <c>exports</c>, <c>__filename</c> and
/// <c>__dirname</c>, with the node resolution of relative paths and <c>node_modules</c> packages.
/// </summary>
internal sealed class CommonJsLoader
{
    private static readonly string[] Extensions = [".js", ".cjs", ".json"];

    private readonly Engine _engine;
    private readonly GrammarEvaluatorOptions _options;
    private readonly Dictionary<string, ObjectInstance> _cache = new(StringComparer.Ordinal);
    private JsValue? _wrapperFactory;

    public CommonJsLoader(Engine engine, GrammarEvaluatorOptions options)
    {
        _engine = engine;
        _options = options;
    }

    /// <summary>Runs the main module and returns its <c>module</c> object.</summary>
    public ObjectInstance LoadMain(string source, string fullPath)
    {
        InstallGlobals(Path.GetDirectoryName(fullPath)!, fullPath);
        return Execute(source, fullPath);
    }

    /// <summary>Defines a global <c>require</c> function resolving modules from the specified directory.</summary>
    public void InstallGlobals(string directory, string fullPath)
    {
        _engine.SetValue("require", CreateRequire(directory));
        _engine.SetValue("__filename", fullPath);
        _engine.SetValue("__dirname", directory);
    }

    private ClrFunction CreateRequire(string directory)
    {
        var require = new ClrFunction(_engine, "require", (_, arguments) =>
        {
            var specifier = arguments.Length > 0 && arguments[0].IsString()
                ? arguments[0].AsString()
                : throw new GrammarEvaluationException("require() expects a module name.");

            return Require(directory, specifier);
        });

        return require;
    }

    private JsValue Require(string directory, string specifier)
    {
        var path = Resolve(directory, specifier)
            ?? throw new GrammarEvaluationException($"Cannot find module '{specifier}' from '{directory}'.");

        if (_cache.TryGetValue(path, out var cached))
        {
            return cached.Get("exports");
        }

        var source = File.ReadAllText(path);

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var module = CreateModuleObject();
            module.Set("exports", new JsonParser(_engine).Parse(source));
            _cache[path] = module;
            return module.Get("exports");
        }

        return Execute(source, path).Get("exports");
    }

    private ObjectInstance Execute(string source, string path)
    {
        var module = CreateModuleObject();
        _cache[path] = module;

        // A shebang line isn't valid JavaScript, it's turned into a comment to keep the line numbers
        if (source.StartsWith("#!", StringComparison.Ordinal))
        {
            source = "//" + source.Substring(2);
        }

        // The wrapper is on the first line such that the line numbers of the errors match the file
        var function = _engine.Evaluate(
            "(function (exports, require, module, __filename, __dirname) {" + source + "\n})",
            path);

        var directory = Path.GetDirectoryName(path)!;
        _engine.Call(
            function,
            module.Get("exports"),
            [module.Get("exports"), CreateRequire(directory), module, path, directory]);

        return module;
    }

    private ObjectInstance CreateModuleObject()
    {
        _wrapperFactory ??= _engine.Evaluate("(function () { return { exports: {} }; })");
        return _engine.Call(_wrapperFactory, JsValue.Undefined, []).AsObject();
    }

    private string? Resolve(string directory, string specifier)
    {
        if (specifier.StartsWith("./", StringComparison.Ordinal) ||
            specifier.StartsWith("../", StringComparison.Ordinal) ||
            specifier == "." || specifier == ".." ||
            Path.IsPathRooted(specifier))
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, specifier));
            return ResolveFile(candidate) ?? ResolveDirectory(candidate);
        }

        for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var candidate = Path.Combine(current, "node_modules", specifier);
            if ((ResolveFile(candidate) ?? ResolveDirectory(candidate)) is { } found)
            {
                return found;
            }
        }

        foreach (var searchPath in _options.ModuleSearchPaths)
        {
            var candidate = Path.GetFullPath(Path.Combine(searchPath, specifier));
            if ((ResolveFile(candidate) ?? ResolveDirectory(candidate)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? ResolveFile(string candidate)
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }

        foreach (var extension in Extensions)
        {
            if (File.Exists(candidate + extension))
            {
                return candidate + extension;
            }
        }

        return null;
    }

    private string? ResolveDirectory(string candidate)
    {
        if (!Directory.Exists(candidate))
        {
            return null;
        }

        var packageJson = Path.Combine(candidate, "package.json");
        if (File.Exists(packageJson))
        {
            var package = new JsonParser(_engine).Parse(File.ReadAllText(packageJson));
            if (package.IsObject() && package.AsObject().Get("main") is { } main && main.IsString())
            {
                var mainPath = Path.GetFullPath(Path.Combine(candidate, main.AsString()));
                if ((ResolveFile(mainPath) ?? ResolveIndex(mainPath)) is { } found)
                {
                    return found;
                }
            }
        }

        return ResolveIndex(candidate);
    }

    private static string? ResolveIndex(string directory) => ResolveFile(Path.Combine(directory, "index"));
}
