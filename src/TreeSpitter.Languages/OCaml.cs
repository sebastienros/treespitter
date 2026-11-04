namespace TreeSpitter.Languages;

/// <summary>The OCaml language (implementations), from tree-sitter-ocaml.</summary>
[GeneratedLanguage("ocaml/grammar.json", ExternalScanner = typeof(Scanners.OCamlScanner))]
public static partial class OCaml;

/// <summary>The OCaml type language (a type expression), from tree-sitter-ocaml.</summary>
[GeneratedLanguage("ocaml-type/grammar.json", ExternalScanner = typeof(Scanners.OCamlScanner))]
public static partial class OCamlType;
