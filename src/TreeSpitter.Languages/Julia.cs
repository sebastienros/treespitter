namespace TreeSpitter.Languages;

/// <summary>The Julia language, from tree-sitter-julia.</summary>
[GeneratedLanguage("julia/grammar.json", ExternalScanner = typeof(Scanners.JuliaScanner))]
public static partial class Julia;
