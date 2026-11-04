namespace TreeSpitter.Dsl;

/// <summary>
/// Options of <see cref="GrammarEvaluator"/>.
/// </summary>
public sealed class GrammarEvaluatorOptions
{
    /// <summary>
    /// The values of <c>process.env</c> visible to the grammar. Empty by default, such that the result doesn't depend on the
    /// environment of the process.
    /// </summary>
    public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Additional directories where <c>require('module')</c> looks for packages, after the <c>node_modules</c> directories
    /// of the grammar's directory and of its parents.
    /// </summary>
    public IList<string> ModuleSearchPaths { get; } = new List<string>();

    /// <summary>Receives the messages logged with <c>console.log</c>, <c>console.warn</c> and <c>console.error</c>.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The maximum duration of the evaluation. No limit by default.</summary>
    public TimeSpan? Timeout { get; set; }
}
