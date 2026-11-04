# Changelog

All notable changes to TreeSpitter are documented here. Versions follow [semantic versioning](https://semver.org);
releases are `v*.*.*` tags, see [docs/release.md](docs/release.md).

## Unreleased

### Added

- `GrammarEvaluator`: evaluates tree-sitter `grammar.js` files with Jint, producing the same grammar as `tree-sitter
  generate`'s `src/grammar.json`. `GrammarConverter` reads and `Grammar.ToJson()` writes `grammar.json` files.
- `LanguageCompiler`: a port of `tree-sitter generate` that compiles a grammar to a `Language`: LR(1) parse tables with
  tree-sitter's conflict resolution and state minimization, lexer automata built from the regular expressions with
  tree-sitter's token conflict analysis, keyword extraction and lex modes.
- `Parser`: a port of the tree-sitter GLR parser, with its error recovery (`ERROR` and `MISSING` nodes), on UTF-8
  input. `SyntaxTree` and `SyntaxNode` expose the tree-sitter node model: named and anonymous nodes, aliases, fields,
  extras, byte offsets, points and S-expressions.
- `Language.Save` and `Language.Load`: the binary format of the compiled tables.
- `CSharpGenerator` and the `TreeSpitter.Generators` source generator: `[GeneratedLanguage("grammar.json")]` classes
  get precompiled parsers, with the tables embedded and the lexer compiled to C#.
- `TreeSpitter.Languages`: precompiled JSON, Go, Java and C parsers.
- Performance: in-place reductions on the single-version path, pooled tree memory (`SyntaxTree.Dispose`), vectorized
  skipping of self-looping lexer states, and keyword lookups by length and first 8 bytes (switches on chunks of the
  token read as integers in generated parsers, like Parlot).
- Incremental parsing: `SyntaxTree.Edit(InputEdit)` (`ts_tree_edit`), `Parser.Parse(input, oldTree)` reusing the
  unchanged nodes of the old tree, `SyntaxTree.Copy()` and `SyntaxNode.HasChanges`, with the lookahead bytes of the
  lexer and tree-sitter's balancing of repetitions. The trees of a lineage share their memory; old trees stay valid.
- `Corpus`: reads and runs tree-sitter corpus tests (`test/corpus/*.txt`).
- `treespitter` command line tool (`grammar`, `info`, `compile`, `generate`, `parse`, `test`).

### Fixed

- The state minimization checks the end of the input for token conflicts like tree-sitter (as the token of index 0):
  the Go and C parse tables had fewer states than tree-sitter's (1301 and 1815 instead of 1442 and 2015), which changed
  the recovery of some syntax errors and the reuse of nodes.
- At the end of the input, the lexers match the `'\0'` transitions that tree-sitter's lexers match there (the
  `ADVANCE_MAP` of a lex state): Go files without a final new line ended with a `MISSING` terminator.
