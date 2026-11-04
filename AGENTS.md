# TreeSpitter repository instructions

- Read [docs/design.md](docs/design.md) before changing the generator or the parser. The reference semantics are
  tree-sitter's: `src/TreeSpitter/Generation` is a port of `tree-sitter generate` (v0.27.1, `crates/generate`), the parser
  runtime is a port of `lib/src/parser.c`, `stack.c` and `subtree.c`, and the trees must match the grammars' corpus
  tests and the native parsers. When in doubt, compare with the tree-sitter sources rather than guessing, and keep the
  port close to them (same order of operations, same names), so the two can be diffed.
- The pipeline is `grammar.js` → Jint → `Grammar` (src/TreeSpitter/Dsl, src/TreeSpitter/Grammars) → prepared grammar
  and NFA → LR(1) parse table and lexer DFAs (src/TreeSpitter/Generation) → `Language` (the "bytecode") → `Parser`
  (the GLR virtual machine, `ParserEngine<TLexer, TTable>`), or a source-generated language which embeds the tables. Regular expressions are converted to
  NFAs; never forward matching to `System.Text.RegularExpressions`.
- The input is UTF-8 and positions are byte offsets, like tree-sitter. `Language` instances are immutable and shared;
  per-parse state lives in the parser engine (one per `Parser`), never in `Language`.
- Performance is the main goal. The hot paths (lexing, `ParserEngine.Advance`, `Reduce`, `ParseStack.Push`/`PopCount`,
  `SubtreeArena.NewNode`) must not allocate per token: nodes live in arenas of structs. Measure every parser change with
  `benchmarks/TreeSpitter.Benchmarks` (`quick` for iterations, BenchmarkDotNet with `[MemoryDiagnoser]` for the
  numbers of a pull request) and put the numbers in the pull request.
- Prefer `ReadOnlySpan<byte>` to `byte[]`: methods which only read bytes take a `ReadOnlySpan<byte>`, and constant byte
  tables are `static ReadOnlySpan<byte> Name => [...]` properties, which the compiler points at the data of the
  assembly (no allocation, no copy). This only holds for byte-sized elements (`byte`, `sbyte`, `bool`): a span of a
  larger type allocates an array at each access on `netstandard2.0`, so those stay `static readonly` arrays. Arrays
  remain where the bytes are stored (the input of a tree, the state of the lexer).
- The `netstandard2.0` target is for .NET Framework hosts; keep it building and behaving like `net10.0` (polyfills come
  from Meziantou.Polyfill). The build is warning-free (`TreatWarningsAsErrors`) and public APIs are documented
  (`CS1591` is an error).
- Layout: `src/` (shipped: the library and the `treespitter` tool), `grammars/` (vendored tree-sitter grammars and
  corpora, used by the tests and the benchmarks), `tests/` (xunit.v3 on Microsoft.Testing.Platform, sentence-style test
  names, plain `Assert`), `benchmarks/`, `docs/`.
- The corpus tests of the vendored grammars must match `grammars/*/known-failures.txt`: remove the entries a change
  fixes, never add entries to hide a regression. Preserve the vendored files byte for byte with their `LICENSE` and
  `SOURCE.md`, and update `THIRD-PARTY-NOTICES.txt` with them.
- Code style is `.editorconfig`: 4-space C#, `var` everywhere, braces always, file-scoped namespaces, `_camelCase`
  fields. Run `dotnet format TreeSpitter.slnx --severity warn --verify-no-changes` before opening a pull request.
- Work on a branch, open a pull request against `main`, never push to `main` directly. Do not publish packages
  manually; CI does that on tags and on `main`.
- Never describe skipped or unrun checks as passing.
