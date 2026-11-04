namespace TreeSpitter;

/// <summary>
/// The version of tree-sitter whose behavior the runtime reproduces. The runtime is a port of tree-sitter 0.27, whose
/// error recovery differs from 0.26; the benchmarks compare the trees with the native parsers of TreeSitter.DotNet,
/// which bundles 0.26, and set <see cref="TreeSitter026"/> to check everything but these changes.
/// </summary>
internal static class Compatibility
{
    /// <summary>Gets or sets whether the error recovery is the one of tree-sitter 0.26.</summary>
    public static bool TreeSitter026 { get; set; }
}
