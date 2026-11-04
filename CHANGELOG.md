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
  get precompiled parsers, with the tables embedded and the keywords matched by code.
- `TreeSpitter.Languages`: precompiled parsers of the 27 grammars of TreeSitter.DotNet 1.3.0 (Agda, Bash, C, C++, C#, CSS,
  Embedded Template, Go, Haskell, HTML, Java, JavaScript, JSDoc, JSON, Julia, OCaml, OCaml type, PHP, Python, QL, Razor,
  Ruby, Rust, Scala, TSX, TypeScript, Verilog), with their external scanners ported to C#.
- External scanners: `IExternalScanner` and the `TSLexer` API on `Lexer` (`Lookahead`, `Advance`, `MarkEnd`,
  `GetColumn`, `IsAtIncludedRangeStart`, ...), `Language.WithExternalScanner`, and the `ExternalScanner` type of
  `[GeneratedLanguage]`. The scanner states are compared and reused like tree-sitter's. `WideChar` has the wide character
  functions of tree-sitter's Wasm standard library.
- `InputEdit` takes the points of the edit, like `TSInputEdit`: the nodes depending on their column are invalidated like
  `ts_subtree_edit`.
- Performance: in-place reductions on the single-version path, pooled tree memory (`SyntaxTree.Dispose`), vectorized
  skipping of self-looping lexer states, and keyword lookups by length and first 8 bytes (switches on chunks of the
  token read as integers in generated parsers, like Parlot).
- Incremental parsing: `SyntaxTree.Edit(InputEdit)` (`ts_tree_edit`), `Parser.Parse(input, oldTree)` reusing the
  unchanged nodes of the old tree, `SyntaxTree.Copy()` and `SyntaxNode.HasChanges`, with the lookahead bytes of the
  lexer and tree-sitter's balancing of repetitions. The trees of a lineage share their memory; old trees stay valid.
- `Corpus`: reads and runs tree-sitter corpus tests (`test/corpus/*.txt`).
- `treespitter` command line tool (`grammar`, `info`, `compile`, `generate`, `parse`, `test`).

### Changed

- `TreeSpitter.Generators` references Microsoft.CodeAnalysis.CSharp 4.3.1 (from 4.14), the oldest version with the APIs it
  uses: the generator loads from Visual Studio 2022 17.3 and the .NET 6.0.4xx SDK (instead of 17.14 and 9.0.3xx).
- The generator and the runtime are ports of tree-sitter v0.27.1 (from v0.25.10): identical reductions aren't
  conflicts, right associativity wins mixed precedence ties, the indirect recursion check runs on the flattened
  productions, unused rules are the ones unreachable from the start rule, the word rule, the extras, the externals and
  the reserved words, `token.immediate` content is a token, inlined supertypes are dropped and terminal supertypes
  rejected, unnecessary conflicts are reported; the error recovery orders the candidate reductions like tree-sitter's
  lookahead iterator, continues the recovery of invalid lookaheads in the error state, nests error repetitions and
  refunds their extent cost. The language format is version 4 (the lookahead order of the small states, the compressed
  parse tables, 16-bit dynamic precedences).
- The parse tables of more than 2^21 entries are compressed in chunks (Verilog takes 7.5 MB instead of 50 MB), and the
  serialized tables are smaller (`TreeSpitter.Languages`, 1.46 MB with 8 grammars before, 1.08 MB after).
- Performance: the comparison of the trees of merged versions skips their shared subtrees (C++ 22% faster), the error
  recovery reuses its lists, and the lines of the input are indexed only as far as the error costs need them. The state
  minimization of the generator compares the successors of the states without lookups (10 to 26% faster compilations).
- The generator is 2.4 to 4.6 times faster on the large grammars (C++ and Julia from 52 s to 11.5 s, the 27 grammars of
  `TreeSpitter.Languages` from 320 s to 72 s per target framework), with the same tables: the closures collect their items
  by rank, the item sets of the existing states are recycled and the states are looked up by packed keys, the entries of
  the parse table are compact, the coincident tokens are bit matrices, the minimization merges the sorted tokens of two
  states and doesn't compare the states equivalent to an earlier state of their group, the token conflicts are computed
  before the parse table, and the enums are compared without boxing (see "The cost of the generation" in
  docs/design.md).
- `TreeSpitter.Languages` compiles its 27 grammars with the new `treespitter generate-batch` command instead of the
  source generator: in parallel, once for its two target frameworks instead of twice, and only the grammars which
  changed (the generated sources are the same). With the generator before the change above, a clean build of the
  solution took 231 to 238 s instead of 387 to 636 s, and a build after a change of a scanner or a grammar 66 to 76 s
  instead of 342 s; with both changes, a clean build takes 150 s.
- Regular expressions use Unicode simple case folding for the `i` flag, fold the leaves of a class before the
  negations, negate `\p{name!=value}` and reject non-ASCII byte classes in `(?-u:...)`.

### Fixed

- The state minimization checks the end of the input for token conflicts like tree-sitter (as the token of index 0):
  the Go and C parse tables had fewer states than tree-sitter's (1301 and 1815 instead of 1442 and 2015), which changed
  the recovery of some syntax errors and the reuse of nodes.
- At the end of the input, the lexers match the `'\0'` transitions that tree-sitter's lexers match there (the
  `ADVANCE_MAP` of a lex state): Go files without a final new line ended with a `MISSING` terminator.
- The lexers match invalid UTF-8 (lookahead -1) with the negated character classes, like tree-sitter's generated
  lexers: the invalid bytes are part of strings and comments instead of errors.
- Regular expressions support the set operations of regex-syntax in classes (`&&`, `--`, `~~`): Julia's identifiers.
- Dynamic precedences are 16-bit like in tree-sitter (Haskell's `prec.dynamic(-1000, ...)` was +24).
- Corpus tests with `:language(name)` run with that grammar only, and `:error` tests pass when the tree has an error,
  like `tree-sitter test`.
