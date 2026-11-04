# TreeSpitter design

TreeSpitter is a C# port of tree-sitter: the parser generator (`tree-sitter generate`) and the parser runtime. The
reference semantics are tree-sitter's, and the port keeps the structure, the names and the order of operations of the
tree-sitter sources (v0.27.1), so that both can be diffed. The goal is to produce exactly the same trees as tree-sitter,
faster.

The native parsers the benchmarks compare with (TreeSitter.DotNet 1.3.0) are tree-sitter 0.26.3, whose error recovery
differs from 0.27 (the order of the reductions tried when recovering, the recovery of invalid lookaheads in the error
state, nested error repetitions and their costs, the state of missing leaves). `Compatibility.TreeSitter026` restores the
behavior of 0.26 for these changes only: the benchmarks set it to compare the trees, everything else is checked against
the native parsers, and the trees of 0.27 are checked by the corpus tests.

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

A port of `crates/generate/src` of tree-sitter v0.27.1 (the generator was ported from v0.25.10 and updated):

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
classes. Two quirks matter for the trees of erroneous inputs and the reuse of nodes:

- When the minimization merges states, the end of the input is checked for conflicts with the tokens of the other state
  as if it were the lexical token of index 0 (`token_conflicts` uses the index of `Symbol::end()`): this keeps states
  apart, the parse tables have the states of tree-sitter (32, 1442, 1385 and 2015 for the JSON, Go, Java and C grammars).
- At the end of the input the lookahead of tree-sitter's lexers is 0. The generated `ts_lex` matches it with the
  transitions rendered in the `ADVANCE_MAP` of a lex state (its leading single-character transitions, when they have at
  least 8 ranges), while the other conditions on `'\0'` check `!eof`. `LexAutomaton` takes such a transition as the
  transition at the end of the input when the state has no end-of-input action: the terminators of tree-sitter-go
  (`choice('\n', ';', '\0')`) match at the end of a file without a final new line. Regular expressions are parsed by `RegexParser` (JavaScript syntax with the Rust extensions accepted by
tree-sitter) and turned into NFAs, never forwarded to `System.Text.RegularExpressions`.

### The cost of the generation

The large grammars (C++, Julia, Razor, Verilog, C#) have 60,000 to 100,000 LR(1) states before the minimization, and
millions of entries. The port keeps the order of operations of tree-sitter, which decides the numbering of the states and
the resolution of the conflicts, but not always its data structures, which are not the same in C# (no `IndexMap`, no
cheap moves, a garbage collector):

- `TransitiveClosure` collects the items added by the closure of the non-terminals by their rank in the order of
  `ParseItem.CompareTo` (computed once) instead of inserting them in a sorted list: the items of a kernel are after their
  first step (or are the start item) and sort after them, and an entry keeps the first item inserted among the items that
  compare equal, like tree-sitter. The additions of a non-terminal are added once per closure, then only their following
  tokens when these change.
- The item sets which don't become states (the closures and most successor item sets) are recycled by a
  `ParseItemSetPool`. The states are looked up by a packed key: the class of each item (the items equal by
  `ParseItem.Equals`), its reserved word set and the bits of its lookaheads.
- The preceding symbols and auxiliary symbols of the states are immutable linked lists, shared instead of copied. A
  `ParseTableEntry` stores its first action, `OrderedMap` has a compact index of open addressing and is allocated at its
  size, and the enums are compared as numbers (`Enum.CompareTo(object)` boxes them).
- `CoincidentTokenIndex` stores bit matrices instead of the lists of the states of each pair of tokens, which hold
  hundreds of millions of entries for the large grammars: the only questions asked of these lists are whether a pair has
  a state, and whether the word token is valid in all of them.
- The minimization compares the entries of two states by merging their sorted tokens, with the conflicting tokens of each
  token as bits and the shapes of the action lists (the actions without their shifted states) as numbers. In
  `SplitStateIdGroups`, a state with the same tokens (and reserved words) as an earlier state of its group isn't compared
  with the states after it: between such states the split condition is the inequality of the groups of their successors,
  so it would split the same states as the earlier state, none. The groups are split exactly like tree-sitter, without
  comparing all the pairs of states of the large cores.
- The token conflicts depend only on the lexical grammar: they are computed before the parse table, when their garbage is
  collected without the large heap of the states. The NFA closures mark the visited states instead of searching the
  lists of states, and the lex table merges the token sets with the incompatible terminals of each terminal as bits.

The compilations of the large grammars are 2.4 to 4.6 times faster than with the data structures of tree-sitter (C++
and Julia from 52 s to 11.5 s with `treespitter compile`, the 27 grammars of the source generator from 320 s to 72 s),
and the tables are the same: the `.tsl` files of the 27 grammars are compared byte for byte.

## The language: the "bytecode"

`Language` holds the generated tables, the equivalent of the `TSLanguage` of a `parser.c`:

- the parse table: the value of a state and a symbol is the index of an action list for a terminal and the goto state for
  a non-terminal. It is dense (`ParseTable[state * SymbolCount + symbol]`, faster to look up than tree-sitter's large and
  small states) up to 2^21 entries, and compressed above (C++, C#, Razor, TypeScript, TSX, Verilog...): the rows are cut
  in chunks of 16 symbols (more when the 16-bit chunk indices don't fit) and the identical chunks are stored once, which
  takes Verilog (20534 states, 1223 symbols) from 50 MB to 7.5 MB. The parse loop is generic over the kind of table
  (`IParseTable`), so the dense tables don't pay for the compressed ones;
- the action lists, `ParseActionEntry` values packed in 64 bits (header: count and reusable; actions: shift, reduce with
  symbol, child count, production id and dynamic precedence on 16 bits like tree-sitter, accept, recover);
- the lex modes of the states (lex state, external lex state, reserved word set), the alias sequences and field maps of
  the production ids, the symbol names and metadata, the reserved words, the keyword capture token;
- the lexer automata (`LexAutomaton`), main and keywords: per state the accepted token, the transition at the end of the
  input and the transitions as sorted code point ranges, plus a dense table of the ASCII transitions.

`Language.Save()` writes them in a versioned, deflated binary format (the parse tables as byte planes, the low bytes then the
high bytes, which deflate better) and `Language.Load()` reads them back. The external scanner is code: a loaded language
gets it with `WithExternalScanner`.

## The parser runtime

A port of `lib/src/parser.c`, `stack.c` and `subtree.c`:

- `ParserEngine<TLexer, TTable>`: the parse loop (`ts_parser_parse`), `Lex`, `Advance`, `Shift`, `Reduce`, `Accept`, the
  error recovery (`HandleError`, `Recover`, `RecoverToState`, `DoAllPotentialReductions`, missing tokens) and
  `CondenseStack`. It is generic over the lex function and the parse table (structs), so the JIT specializes it for the
  table lexer or a generated lexer, and for a dense or a compressed table.
- `ParseStack`: the graph-structured stack of the GLR algorithm, with versions, links, merges and `stack__iter`.
- `SubtreeArena`: the nodes. tree-sitter allocates reference-counted subtrees; TreeSpitter stores the nodes as structs in
  an arena (`SubtreeData[]`) and the children as runs of indices in one `int[]`. Nothing is freed during a parse, and
  nodes aren't changed once a tree is built. Positions are relative like in tree-sitter (padding, size and lookahead
  bytes), so that subtrees can be shared by the trees of successive incremental parses; `SyntaxNode` computes the
  absolute positions while navigating.
- The input is UTF-8 and positions are byte offsets, like tree-sitter: the error costs count skipped bytes, so the trees
  of erroneous inputs match the native parser on the same UTF-8 bytes (the TreeSitter.DotNet binding parses strings as
  UTF-16, which doubles the costs and can change the recovery of large errors).
- `SyntaxTree` and `SyntaxNode` expose the tree-sitter node model; `SExpressionWriter` ports `ts_subtree__write_to_string`
  without recursion.

### External scanners

The external scanners of the grammars are C code (`src/scanner.c`): `TreeSpitter.Languages` has C# ports of them
(`Scanners/`), which keep the structure, the names and the comments of the C code so that the two can be diffed, and the
C semantics that change the tokens (signed `char`, wrapping unsigned arithmetic, truncations, `memcpy` layouts).

- `IExternalScanner` is `scan`, `serialize` and `deserialize`, and `Lexer` the `TSLexer` of the scanners (`Lookahead`,
  `Advance`, `MarkEnd`, `GetColumn`, `IsAtEnd`, `IsAtIncludedRangeStart`, `ResultSymbol`). A parser creates a new scanner
  for each parse, like `ts_parser_parse`, since the state that an empty `deserialize` doesn't reset must not leak to the
  next parse.
- The serialized states are the bytes of the C serializers: the runtime compares them to merge stack versions, to cache
  tokens and to reuse nodes, so a different layout changes the trees. They are stored in the arena with the external
  leaves that change them, and the stack heads and the nodes keep their last external token like tree-sitter.
- The tokens which read their column are marked, and an edit invalidates them like `ts_subtree_edit` when it has the
  points of tree-sitter's `TSInputEdit` (`InputEdit` takes them optionally): until the end of their line, or after it
  when the edit shifted the columns. Without points, an edit invalidates all the nodes after it which read their column.
- `WideChar` ports the `iswspace`, `iswalpha`... of tree-sitter's Wasm standard library (musl's tables), the portable
  behavior of the scanners. Native parsers call the C library of their platform, whose wide character functions depend
  on the locale: in the C locale of the TreeSitter.DotNet binding on Linux, they only match ASCII, so the trees of the
  Ruby scanner differ from the native ones on non-ASCII heredoc words and interpolations.
- The lexers match invalid UTF-8 like tree-sitter's generated lexers: the lookahead is -1, which the negated character
  classes of `render.rs` (`lookahead != '"' && ...`) accept, so the invalid bytes are part of strings and comments.

### Incremental parsing

`SyntaxTree.Edit` and `Parser.Parse(input, oldTree)` port `ts_tree_edit` and the reuse of the parser:

- An arena is shared by the trees of a lineage: an incremental parse appends its nodes to the arena of the old tree and
  references the reused subtrees, the old tree stays valid. The arena is reference counted by its trees and goes back to
  its parser when they are all disposed. Parses and edits of the trees of an arena take its lock.
- `Edit` is `ts_subtree_edit` with path copying: the nodes touching the edit are copied with their new padding and size
  and marked as changed, the other trees sharing them aren't changed. Since nodes are immutable, `SyntaxNode` values of
  an edited tree, its copies (`Copy`, which shares the nodes) and older trees stay valid.
- The lexer records how far it read after each token (`ts_lexer_finish`: the lookahead character, plus 4 bytes after an
  invalid UTF-8 sequence), and nodes summarize the lookahead bytes of their children, so that edits in this range
  invalidate them.
- The parse loop ports `ReusableNode`, `ts_parser__reuse_node`, `ts_parser__can_reuse_first_leaf` and
  `ts_parser__breakdown_lookahead`. The reuse helpers aren't inlined in the parse loop: they would slow down the parses
  from scratch.
- Balancing (`ts_parser__balance_subtree`, `ts_subtree_compress`): tree-sitter rebalances the binary trees of the
  repetitions after each parse, which changes the nodes that edits invalidate and that parses reuse. TreeSpitter
  balances a tree when it's first edited, copied or reused instead, with copy on write: a tree which is only read doesn't
  pay for it (about 8% of a parse of the JSON benchmark). Only the nodes created by the parse of the tree are balanced (tree-sitter
  checks that the reference count is 1), and only the subtrees flagged as containing unbalanced repetitions are visited.
- Like in tree-sitter, the nodes of a repetition are fragile (their reduction shares its entry with the shift of the
  repetition) and aren't reused: an edit rebuilds the repetition nodes after it, but reuses its elements.
- Compaction: the arena of a long series of incremental parses holds the nodes of the old trees. When the arena is more
  than twice the size of the tree it was created for (plus 4096 nodes), the next incremental parse copies the old tree
  to a new arena first.

The benchmarks check the incremental parses against the native ones (`incremental-check`): the random edits of
tree-sitter's corpus tests (`get_random_edit`) are applied to both trees, and the trees of the incremental parses, then
of the parses after undoing the edits, are compared, with the points of the edits like tree-sitter's tests. They are
identical for the corpus tests of the 27 grammars, including the cases where the native incremental parse differs from
a fresh parse.

### Performance

The hot paths don't allocate. Measured optimizations, in the order of their gains:

1. Reductions in place: when the stack has a single version, the entry has a single action (or a reduction next to
   shifts of repetitions, which the loop skips) and the popped path has no merged nodes, the children are popped directly
   into the arena and the parent is pushed on the same version (`ReduceInPlace`, `ParseStack.TryPopSinglePath`).
   tree-sitter creates a version for every reduction and renumbers it.
2. Memory: the capacity of the arena is estimated from the previous parses (no resize), arrays are allocated
   uninitialized, and the arena of disposed trees goes back to its parser.
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

The 46 methods marked `[MethodImpl(MethodImplOptions.AggressiveInlining)]` (the table lookups, the accessors of the
arena and the stack, the UTF-8 decoding, the lexer transitions) are worth it: without the attribute, the parses of the
generated parsers are 1 to 3% slower on 8 grammars, and the incremental parses up to 6.5% (Go and Java). The JIT inlines
the smallest ones anyway; none was measured to be harmful, each one's effect being below the noise of the benchmarks.

The grammars with many ambiguities and the error recovery have their own costs:

- When two versions of the stack meet, `select_tree` compares their trees (`ts_subtree_compare`) down to the leaves while
  they are equal. The versions share their prefixes, so the comparison skips the pairs of identical subtrees: C++ parses
  22% faster.
- The error recovery records a summary of the stack of each paused version and pops slices: the lists are pooled (they
  allocated 1.3 MB per parse of a PHP input with errors). The rows spanned by skipped trees (their error cost) come from
  an index of the line starts which is built lazily, up to the positions asked, and reused by the parses of the arena:
  it was allocated for the whole input at each parse with an error (89 KB per incremental parse of a Go file of 100 KB).

## Source generation

`CodeGen/CSharpGenerator` writes a partial class with the serialized tables (a `byte[]` in the assembly), a `Language`
property and a lex function struct with the keyword switches. The lexer automata are run by the table lexer, like the
ones of the VM. They used to be compiled to C# (a `switch` on the state with a `switch` on the code point and `goto`
transitions, like tree-sitter's `ts_lex`), which was not faster than the table lexer (its ASCII transitions are one
array lookup, and its self-loops are vectorized), within 3% either way on the 13 grammars whose lexers the JIT
optimized, and 22 to 30% slower for JavaScript, TypeScript and Rust, whose lexer methods were too large to be optimized
(the JIT compiles the methods of more than about 2000 basic blocks with MinOpts). `TreeSpitter.Generators` is a Roslyn
incremental generator for the classes marked `[GeneratedLanguage("…/grammar.json")]`: it compiles the source of the
library (without Jint) and reads the grammars from the `AdditionalFiles`.

`TreeSpitter.Languages` marks its classes with `[GeneratedLanguage]` too (the tests find the languages and their
scanners with it), but doesn't use the source generator: Roslyn runs the outputs of a generator one after the other, at
each compilation of the project and for each target framework, which compiled the 27 grammars twice at each build. A
target of the project (`TreeSpitterGenerateLanguages`) runs `treespitter generate-batch` instead: the grammars listed by
the `TreeSpitterGrammar` items of the project (with the class and the external scanner of their language) are compiled
in parallel, with the server garbage collector, to the same C# files as the source generator's, in
`obj/<configuration>/generated` for the target frameworks. The builds of the target frameworks call the target on the
project without target framework, which MSBuild builds once, and the target only compiles the grammars whose
`grammar.json` is newer than their file (all of them when the tool or the project changed). A development build can
generate only some grammars (`TreeSpitterGrammars=json,go dotnet build`), and `TreeSpitterGenerateParallelism` limits the
grammars compiled at the same time (all the processors by default).

## Limitations

- No included ranges, and no changed ranges (`ts_tree_get_changed_ranges`) between the trees of an incremental parse.
- The `eof()` rule of tree-sitter 0.27 isn't supported: the generator reports it.
- The external scanners of other grammars than the ones of `TreeSpitter.Languages` have to be ported to C#
  (`IExternalScanner`).
- The tree API is smaller than tree-sitter's (no queries, no tree cursor).
