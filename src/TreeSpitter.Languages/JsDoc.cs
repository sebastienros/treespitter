namespace TreeSpitter.Languages;

/// <summary>The language of JSDoc comments, from tree-sitter-jsdoc.</summary>
[GeneratedLanguage("jsdoc/grammar.json", ExternalScanner = typeof(Scanners.JsDocScanner))]
public static partial class JsDoc;
