namespace TreeSpitter.Languages;

/// <summary>The Bash language, from tree-sitter-bash.</summary>
[GeneratedLanguage("bash/grammar.json", ExternalScanner = typeof(Scanners.BashScanner))]
public static partial class Bash;
