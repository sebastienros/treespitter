namespace TreeSpitter.Languages;

/// <summary>The TypeScript language, from tree-sitter-typescript.</summary>
[GeneratedLanguage("typescript/grammar.json", ExternalScanner = typeof(Scanners.TypeScriptScanner))]
public static partial class TypeScript;

/// <summary>The TSX language (TypeScript with JSX), from tree-sitter-typescript.</summary>
[GeneratedLanguage("tsx/grammar.json", ExternalScanner = typeof(Scanners.TypeScriptScanner))]
public static partial class Tsx;
