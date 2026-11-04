namespace TreeSpitter.Languages;

/// <summary>The PHP language, from tree-sitter-php: PHP code with the text around its <c>&lt;?php</c> tags.</summary>
[GeneratedLanguage("php/grammar.json", ExternalScanner = typeof(Scanners.PhpScanner))]
public static partial class Php;
