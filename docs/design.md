# TreeSpitter design

TreeSpitter is a C# port of tree-sitter: the parser generator (`tree-sitter generate`) and the parser runtime. The
reference semantics are tree-sitter's, and the port keeps the structure, the names and the order of operations of the
tree-sitter sources (v0.25.10; the runtime is unchanged in v0.26), so that both can be diffed. The goal is to produce
exactly the same trees as tree-sitter, faster.

```
grammar.js ──Jint──▶ Grammar ──prepare──▶ syntax + lexical grammars ──build tables──▶ Language ──▶ Parser (GLR VM)
grammar.json ───────▶                                                                  │
                                                                           Save/Load ──┴──▶ generated C# (tables + lexer)
```

## Grammars

- `Dsl/`: `GrammarEvaluator` runs `grammar.js` with Jint, after `dsl.js` (the tree-sitter DSL: `grammar`, `seq`,
  `choice`, `prec`, `token`, ...), with CommonJS and ES modules for grammars that extend other grammars. The result is the
  same as tree-sitter's `src/grammar.json`.
- `Grammars/`: the grammar AST (`Grammar`, `Rule`), its JSON writer (`Grammar.ToJson`) and `GrammarJsonReader`, which reads
  `grammar.json` without Jint (the source generator uses it).

## Generation (`Generation/`)

A port of `cli/generate/src` of tree-sitter v0.25.10:

| TreeSpitter | tree-sitter |
| --- | --- |
| `Rules.cs` (`IRule`, `Symbol`, `TokenSet`, `Precedence`, `Alias`) | `rules.rs` |
| `Grammars.cs` (`SyntaxGrammar`, `LexicalGrammar`, `Production`, ...) | `grammars.rs` |
| `PrepareGrammar.cs`: `InputGrammarReader`, `SymbolInterner`, `TokenExtractor`, `RepeatExpander`, `GrammarFlattener`, `TokenExpander`, `DefaultAliasExtractor`, `InlineProcessor` | `parse_grammar.rs`, `prepare_grammar/*.rs` |
| `Lexing/RegexParser.cs` + `TokenExpander` | `expand_tokens.rs` and the HIR of regex-syntax |
| `Nfa.cs` (`CharacterSet`, `Nfa`, `NfaCursor`) | `nfa.rs` |
| `VariableInfo.cs` (the fields of the variables) | the part of `node_types.rs` used by the tables |
| `ItemSet.cs` (`ParseItem`, `ParseItemSet`, `ParseItemSetBuilder`) | `build_tables/item.rs`, `item_set_builder.rs` |
| `BuildParseTable.cs` (LR(1) states, precedence and associativity, declared conflicts) | `build_tables/build_parse_table.rs` |
| `TokenConflicts.cs` (`TokenConflictMap`, `CoincidentTokenIndex`) | `token_conflicts.rs`, `coincident_tokens.rs` |
| `BuildTables.cs` (keywords, error state, `Minimizer`, `LexTableBuilder`) | `build_tables.rs`, `minimize_parse_table.rs`, `build_lex_table.rs`, `dedup.rs` |
| `LanguageBuilder.cs` (symbol ids, public symbols, aliases, field maps, lex modes) | `render.rs` |

The behavior of tree-sitter is reproduced, quirks included: for example `x{2,}` matches exactly two `x`, like
tree-sitter (the repetition of `expand_regex` for `{n,}` is disconnected from the loop), and `\w`, `\d`, `\s` are ASCII
classes. Regular expressions are parsed by `RegexParser` (JavaScript syntax with the Rust extensions accepted by
tree-sitter) and turned into NFAs, never forwarded to `System.Text.RegularExpressions`.

## The language: the "bytecode"

`Language` holds the generated tables, the equivalent of the `TSLanguage` of a `parser.c`:

- the parse table, dense: `ParseTable[state * SymbolCount + symbol]` is the index of an action list for a terminal and the
  goto state for a non-terminal (tree-sitter splits large and small states, a dense table is faster to look up);
- the action lists, `ParseActionEntry` values packed in 64 bits (header: count and reusable; actions: shift, reduce with
  symbol, child count, production id and dynamic precedence, accept, recover);
- the lex modes of the states (lex state, external lex state, reserved word set), the alias sequences and field maps of
  the production ids, the symbol names and metadata, the reserved words, the keyword capture token;
- the lexer automata (`LexAutomaton`), main and keywords: per state the accepted token, the transition at the end of the
  input and the transitions as sorted code point ranges, plus a dense table of the ASCII transitions.

`Language.Save()` writes them in a versioned, deflated binary format and `Language.Load()` reads them back.

## The parser runtime

A port of `lib/src/parser.c`, `stack.c` and `subtree.c`, without incremental parsing:

- `ParserEngine<TLexer>`: the parse loop (`ts_parser_parse`), `Lex`, `Advance`, `Shift`, `Reduce`, `Accept`, the error
  recovery (`HandleError`, `Recover`, `RecoverToState`, `DoAllPotentialReductions`, missing tokens) and `CondenseStack`.
  It is generic over the lex function (a struct), so the JIT specializes it for the table lexer or a generated lexer.
- `ParseStack`: the graph-structured stack of the GLR algorithm, with versions, links, merges and `stack__iter`.
- `SubtreeArena`: the nodes. tree-sitter allocates reference-counted subtrees; TreeSpitter stores the nodes as structs in
  an arena (`SubtreeData[]`) and the children as runs of indices in one `int[]`. Nothing is freed during a parse; the
  arrays become the `SyntaxTree`. Nodes have absolute positions (`PaddingStart`, `Start`, `End`) instead of relative
  padding and size, since there's no incremental reuse.
- The input is UTF-8 and positions are byte offsets, like tree-sitter: the error costs count skipped bytes, so the trees
  of erroneous inputs match the native parser on the same UTF-8 bytes (the TreeSitter.DotNet binding parses strings as
  UTF-16, which doubles the costs and can change the recovery of large errors).
- `SyntaxTree` and `SyntaxNode` expose the tree-sitter node model; `SExpressionWriter` ports `ts_subtree__write_to_string`
  without recursion (repetitions are left-deep and tree-sitter's balancing isn't ported).

### Performance

The hot paths don't allocate. Measured optimizations, in the order of their gains:

1. Reductions in place: when the stack has a single version, the entry has a single action (or a reduction next to
   shifts of repetitions, which the loop skips) and the popped path has no merged nodes, the children are popped directly
   into the arena and the parent is pushed on the same version (`ReduceInPlace`, `ParseStack.TryPopSinglePath`).
   tree-sitter creates a version for every reduction and renumbers it.
2. Memory: the capacity of the arena is estimated from the previous parses (no resize), arrays are allocated
   uninitialized, and a disposed `SyntaxTree` gives its arrays back to its parser.
3. Lexer self-loops (`ByteRun`): for each lex state, the ASCII bytes whose transition loops on the state (white space,
   contents of strings and comments, identifiers). Runs are scanned byte by byte, then with a vectorized
   `SearchValues<byte>.IndexOfAnyExcept` after 16 bytes: 6 to 10% on real-world files.
4. Keywords: tree-sitter lexes identifiers with the word token, then runs a keyword automaton from the start of the
   token. When the keyword automaton accepts a finite set of strings, the keyword is looked up instead, with the
   technique of Parlot's `KnownStringLookup`: the length and the first 8 bytes of the token as a `ulong`. The VM uses a
   table keyed by both (`KeywordTable`); generated parsers emit `MatchKeywords`, a switch on the length then switches on
   chunks of the token read as little-endian integers (`CodeGen/KnownBytesLookup`), so a keyword of up to 8 bytes is one
   integer comparison. Keywords that are prefixes of other keywords still run the automaton. The gain is 1 to 3%: the
   automaton already stops after a few bytes for most identifiers.

## Source generation

`CodeGen/CSharpGenerator` writes a partial class with the serialized tables (a `byte[]` in the assembly), a `Language`
property and a lex function struct with the keyword switches. The lexer automata are compiled to C# (a `switch` on the state with a `switch` on the
code point, `goto` transitions, like tree-sitter's `ts_lex`) when the method is small enough for the JIT to optimize it:
past about 2000 basic blocks the JIT compiles it with MinOpts, so large automata (like C's main lexer) use the table
lexer. `TreeSpitter.Generators` is a Roslyn incremental generator for the classes marked `[GeneratedLanguage("…/grammar.json")]`:
it compiles the source of the library (without Jint) and reads the grammars from the `AdditionalFiles`.
`TreeSpitter.Languages` uses it for JSON, Go, Java and C.

## Limitations

- No incremental parsing (`ts_tree_edit`, node reuse) and no included ranges.
- External scanners aren't supported: grammars with `externals` compile, but the external tokens never match.
- The tree API is smaller than tree-sitter's (no queries, no tree cursor).
