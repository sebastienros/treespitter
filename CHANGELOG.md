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
- `Corpus`: reads and runs tree-sitter corpus tests (`test/corpus/*.txt`).
- `tree-spitter` command line tool (`grammar`, `info`, `parse`, `test`).
