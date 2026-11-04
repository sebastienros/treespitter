namespace TreeSpitter.Languages;

/// <summary>
/// The Razor language, from tree-sitter-razor. Its external scanner is the one of tree-sitter-c-sharp, which its
/// <c>src/scanner.c</c> copies.
/// </summary>
[GeneratedLanguage("razor/grammar.json", ExternalScanner = typeof(Scanners.CSharpScanner))]
public static partial class Razor;
