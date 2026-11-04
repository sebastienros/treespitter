# TreeSpitter

**tree-sitter grammars on .NET, without native code, faster than tree-sitter.**

TreeSpitter is a C# port of [tree-sitter](https://tree-sitter.github.io): `grammar.js` files are evaluated with
[Jint](https://github.com/sebastienros/jint), compiled to LR(1) parse tables and lexer automata exactly like
`tree-sitter generate` does, and run by a port of the tree-sitter GLR parser, error recovery included. Languages can
also be precompiled by a source generator, which embeds their compiled tables in an assembly: they load in
milliseconds instead of being compiled when the program runs (seconds, 40 s for C++).

```
grammar.js ──Jint──▶ grammar AST ──▶ parse tables + lexer automata (bytecode) ──▶ GLR virtual machine ──▶ syntax tree
                                                        └──▶ source generator ──▶ precompiled parser
```

The trees are the trees of tree-sitter. `TreeSpitter.Languages` ships the 27 grammars of the TreeSitter.DotNet package,
with their external scanners ported to C#: their corpus tests pass, and the benchmarks check that the trees are identical
to the native parsers' on every corpus input and on random edits of them, real-world files and files with syntax errors
included.

## Performance

TreeSpitter against the native tree-sitter parsers of [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet)
1.3.0 (tree-sitter 0.26), on generated inputs. `Native` goes through the binding (strings, as UTF-16), `Native UTF-8`
calls tree-sitter directly on the UTF-8 bytes; the TreeSpitter parsers parse the UTF-8 bytes and dispose their trees,
like the native benchmarks.

| Input | Native | Native UTF-8 | TreeSpitter VM | TreeSpitter generated | Speed-up |
| --- | ---: | ---: | ---: | ---: | ---: |
| JSON, 10 KB | 1.18 ms | 1.16 ms | 0.328 ms | 0.327 ms | 3.6× |
| Go, 10 KB | 1.63 ms | 1.59 ms | 0.405 ms | 0.415 ms | 3.8× |
| Java, 10 KB | 1.47 ms | 1.52 ms | 0.305 ms | 0.321 ms | 4.7× |
| C, 10 KB | 1.42 ms | 1.44 ms | 0.261 ms | 0.253 ms | 5.7× |
| JSON, 100 KB | 11.7 ms | 11.9 ms | 3.19 ms | 3.07 ms | 3.9× |
| Go, 100 KB | 16.4 ms | 16.8 ms | 3.98 ms | 4.02 ms | 4.2× |
| Java, 100 KB | 14.7 ms | 14.8 ms | 3.06 ms | 3.10 ms | 4.8× |
| C, 100 KB | 14.1 ms | 13.9 ms | 2.48 ms | 2.47 ms | 5.6× |

The speed-up is the generated parser against the native parser on the same UTF-8 input. A parse of these inputs
allocates about 120 bytes when the trees are disposed (the memory of a disposed tree goes back to its parser).

All the grammars of `TreeSpitter.Languages`, on inputs made of their corpus tests (the ones without errors, repeated
while the input stays valid: Agda and Ruby can't be repeated, and the JSDoc and OCaml type documents are a single
comment or type), with the `quick` mode of the benchmarks (best of 2 runs):

| Grammar | Input | Native | TreeSpitter VM | TreeSpitter generated | Speed-up |
| --- | ---: | ---: | ---: | ---: | ---: |
| Agda | 5 KB | 817 µs | 181 µs | 178 µs | 4.6× |
| Bash | 100 KB | 43.42 ms | 16.09 ms | 15.73 ms | 2.8× |
| C | 100 KB | 14.01 ms | 2.47 ms | 2.47 ms | 5.7× |
| C# | 123 KB | 36.75 ms | 9.28 ms | 9.32 ms | 3.9× |
| C++ | 125 KB | 29.76 ms | 7.88 ms | 7.89 ms | 3.8× |
| CSS | 103 KB | 14.07 ms | 2.64 ms | 2.67 ms | 5.3× |
| Embedded template | 100 KB | 11.05 ms | 2.40 ms | 2.31 ms | 4.8× |
| Go | 100 KB | 16.21 ms | 3.95 ms | 4.01 ms | 4.0× |
| HTML | 102 KB | 36.50 ms | 10.66 ms | 10.36 ms | 3.5× |
| Haskell | 102 KB | 3.94 ms | 846 µs | 866 µs | 4.5× |
| JSON | 100 KB | 11.45 ms | 3.06 ms | 3.07 ms | 3.7× |
| Java | 100 KB | 14.38 ms | 3.03 ms | 3.02 ms | 4.8× |
| JavaScript | 104 KB | 23.26 ms | 5.49 ms | 5.20 ms | 4.5× |
| Julia | 102 KB | 34.23 ms | 6.14 ms | 6.14 ms | 5.6× |
| OCaml | 101 KB | 32.90 ms | 5.69 ms | 5.30 ms | 6.2× |
| PHP | 118 KB | 31.20 ms | 8.14 ms | 8.17 ms | 3.8× |
| Python | 106 KB | 28.88 ms | 6.74 ms | 6.73 ms | 4.3× |
| QL | 103 KB | 19.73 ms | 3.76 ms | 3.83 ms | 5.2× |
| Razor | 104 KB | 20.87 ms | 5.87 ms | 5.86 ms | 3.6× |
| Ruby | 15 KB | 3.58 ms | 788 µs | 760 µs | 4.7× |
| Rust | 103 KB | 20.91 ms | 4.32 ms | 4.44 ms | 4.7× |
| Scala | 111 KB | 35.57 ms | 11.78 ms | 11.58 ms | 3.1× |
| TSX | 112 KB | 23.76 ms | 5.46 ms | 5.24 ms | 4.5× |
| TypeScript | 111 KB | 23.93 ms | 5.57 ms | 5.35 ms | 4.5× |
| Verilog | 103 KB | 23.56 ms | 7.06 ms | 7.07 ms | 3.3× |

Real-world files: tree-sitter's own `parser.c` and `query.c`, and the 237 KB pretty-printed `grammar.json` of
tree-sitter-c:

| File | Native | Native UTF-8 | TreeSpitter generated | Speed-up |
| --- | ---: | ---: | ---: | ---: |
| `grammar.json` (JSON, 237 KB) | 13.2 ms | 13.2 ms | 2.95 ms | 4.5× |
| `parser.c` (C, 79 KB) | 7.71 ms | 7.85 ms | 1.68 ms | 4.7× |
| `query.c` (C, 149 KB) | 16.0 ms | 15.7 ms | 3.77 ms | 4.2× |

Incremental parsing: a character inserted in the middle of an identifier or a number of the inputs above, or removed,
then the new input parsed with the edited tree, like an editor does on each keystroke. Both parsers get the points of
the edits, which an editor knows (`quick-incremental`, best of 2 runs):

| Grammar | Native full parse | Native incremental | TreeSpitter full parse | TreeSpitter incremental | Speed-up |
| --- | ---: | ---: | ---: | ---: | ---: |
| Bash | 47.73 ms | 59.20 ms | 15.73 ms | 25.23 ms | 2.3× |
| C | 13.84 ms | 187 µs | 2.40 ms | 108 µs | 1.7× |
| C# | 37.53 ms | 7.42 ms | 9.21 ms | 3.59 ms | 2.1× |
| C++ | 29.45 ms | 5.75 ms | 7.96 ms | 2.08 ms | 2.8× |
| CSS | 13.92 ms | 1.23 ms | 2.60 ms | 667 µs | 1.8× |
| Embedded template | 11.31 ms | 8.40 ms | 2.33 ms | 3.20 ms | 2.6× |
| Go | 16.03 ms | 139 µs | 3.89 ms | 76 µs | 1.8× |
| HTML | 36.88 ms | 419 µs | 10.37 ms | 346 µs | 1.2× |
| Haskell | 3.85 ms | 3.26 ms | 891 µs | 876 µs | 3.7× |
| JSON | 11.25 ms | 314 µs | 3.07 ms | 183 µs | 1.7× |
| Java | 14.32 ms | 58 µs | 2.98 ms | 32 µs | 1.8× |
| JavaScript | 23.58 ms | 5.80 ms | 5.25 ms | 2.11 ms | 2.7× |
| Julia | 35.01 ms | 4.60 ms | 5.88 ms | 2.37 ms | 1.9× |
| OCaml | 34.26 ms | 3.57 ms | 5.49 ms | 2.06 ms | 1.7× |
| PHP | 31.46 ms | 3.72 ms | 8.07 ms | 1.88 ms | 2.0× |
| Python | 30.07 ms | 6.31 ms | 6.77 ms | 2.76 ms | 2.3× |
| QL | 20.11 ms | 1.61 ms | 3.82 ms | 835 µs | 1.9× |
| Razor | 20.53 ms | 3.96 ms | 5.85 ms | 2.30 ms | 1.7× |
| Rust | 20.70 ms | 2.40 ms | 4.28 ms | 1.20 ms | 2.0× |
| Scala | 36.45 ms | 3.41 ms | 11.14 ms | 2.97 ms | 1.1× |
| TSX | 22.94 ms | 2.08 ms | 5.36 ms | 1.15 ms | 1.8× |
| TypeScript | 24.38 ms | 2.41 ms | 5.31 ms | 1.29 ms | 1.9× |
| Verilog | 23.27 ms | 4.88 ms | 7.17 ms | 2.76 ms | 1.8× |

The incremental parses are 1.7 to 3.7 times faster than the native ones, except Scala's and HTML's (1.1 and 1.2).
BenchmarkDotNet gives the same for the generated inputs of JSON, Go, Java and C: 182, 76, 32 and 115 µs per incremental
parse, against 322, 156, 58 and 193 µs for the native parser (1.7 to 2.1 times faster).
Earlier versions of this table measured the native parser with the computation of the points of its edits, a scan of
the input, which made it look 1.5 to 3 times slower than it is.

Preparing a language, which tree-sitter does ahead of time with `tree-sitter generate` and a C compiler:

| Grammar | Evaluate `grammar.js` | Compile the tables (`tree-sitter generate`) | Load precompiled tables |
| --- | ---: | ---: | ---: |
| JSON | 1.2 ms | 1.2 ms | 0.03 ms |
| Go | 4.8 ms | 0.46 s | 0.85 ms |
| C | 7.3 ms | 1.39 s | 1.81 ms |

The largest grammars take longer to compile (C# 22 s, Razor 28 s, Verilog 29 s, C++ 40 s), which the source generator
does at build time, and their precompiled tables load in 8 to 25 ms.

[benchmarks/TreeSpitter.Benchmarks](benchmarks/TreeSpitter.Benchmarks), BenchmarkDotNet (in process), .NET 10, Linux,
Intel Xeon 2.7 GHz. The allocations of the native parsers are made by tree-sitter and aren't counted.

Where the time goes, and what made it fast, is described in [docs/design.md](docs/design.md): reductions in place on the
single-version path, arenas of structs instead of reference-counted subtrees, pooled tree memory, and vectorized
skipping of the self-looping lexer states (white space, comments, strings).

## Install

```bash
dotnet add package TreeSpitter            # the library: grammar.js → parser
dotnet add package TreeSpitter.Languages  # the precompiled parsers of 27 grammars
dotnet add package TreeSpitter.Generators # the source generator, for your own grammars
dotnet tool install -g TreeSpitter.Cli    # the treespitter command
```

The library targets .NET 10 and .NET Standard 2.0 (for .NET Framework hosts). Its assemblies are strong-named with a
deliberately non-secret development key, [TreeSpitter.Development.snk](TreeSpitter.Development.snk), so signed hosts
can reference them; the key supplies an assembly identity, not production signing or package authenticity.

## Quick start

Compile a grammar at runtime, from a `grammar.js` (or `grammar.json`) file:

```csharp
using TreeSpitter;

var parser = Parser.FromGrammarFile("tree-sitter-json/grammar.js");
using var tree = parser.Parse("""{ "name": "treespitter", "tags": [1, 2] }""");

Console.WriteLine(tree.RootNode.ToSExpression());
// (document (object (pair key: (string (string_content)) value: (string (string_content)))
//                   (pair key: (string (string_content)) value: (array (number) (number)))))

var pair = tree.RootNode.NamedChildren.First().NamedChildren.First();
Console.WriteLine(pair.ChildByFieldName("key").Text);   // "name"
Console.WriteLine(pair.StartPoint);                     // [0, 2]
```

The input is UTF-8, positions are byte offsets like in tree-sitter: `Parse(byte[])`, `Parse(ReadOnlyMemory<byte>)` and
`ParseFile(path)` parse bytes directly, `Parse(string)` encodes the string first. A `Parser` isn't thread-safe; a
`Language` is immutable and can be shared. Disposing a tree gives its memory back to its parser (once all the trees sharing it, see below, are disposed).

Nodes have the tree-sitter model: `Type`, `Symbol`, `IsNamed`, `IsExtra`, `IsError`, `IsMissing`, `HasError`,
`HasChanges`, `Children`, `NamedChildren`, `ChildByFieldName`, `FieldName`, `Parent`, `StartByte`/`EndByte`,
`StartPoint`/`EndPoint` and `Text`. Syntax errors produce `ERROR` and `MISSING` nodes, with the error recovery of
tree-sitter.

### Incremental parsing

Like `ts_tree_edit` and `ts_parser_parse` with an old tree: describe the edit of the input to the tree, then parse the
new input with the edited tree. The unchanged nodes are reused, the new tree shares them, and the old tree stays valid.

```csharp
using var tree = parser.Parse("""{"a": [1, 2]}""");

// {"a": [1, 2]} -> {"a": [1, 20]}: one byte inserted at offset 11
tree.Edit(new InputEdit(StartByte: 11, OldEndByte: 11, NewEndByte: 12));
using var newTree = parser.Parse("""{"a": [1, 20]}""", tree);
```

`Edit` shifts the nodes after the edit and marks the nodes touching it (`HasChanges`); `Copy()` returns a cheap copy
of a tree to edit while keeping the original. The trees of the incremental parses are the trees of tree-sitter's
incremental parses: the benchmarks compare them on the random edits of tree-sitter's corpus tests.

### Precompiled parsers

The parsers of `TreeSpitter.Languages` are compiled at build time: `Agda`, `Bash`, `C`, `Cpp`, `CSharp`, `Css`,
`EmbeddedTemplate`, `Go`, `Haskell`, `Html`, `Java`, `JavaScript`, `JsDoc`, `Json`, `Julia`, `OCaml`, `OCamlType`,
`Php`, `Python`, `Ql`, `Razor`, `Ruby`, `Rust`, `Scala`, `Tsx`, `TypeScript` and `Verilog`.

```csharp
using TreeSpitter.Languages;

using var tree = Go.CreateParser().ParseFile("main.go");
```

Their assembly is 6.0 MB for the 27 grammars (the deflated tables are most of it), the same on every platform: the
native libraries of TreeSitter.DotNet are 66 MB per platform.

For your own grammars, add `TreeSpitter.Generators` and the `grammar.json` of the grammar (from `tree-sitter generate`, or
`treespitter grammar grammar.js`) as an additional file:

```xml
<ItemGroup>
  <PackageReference Include="TreeSpitter" />
  <PackageReference Include="TreeSpitter.Generators" PrivateAssets="all" />
  <AdditionalFiles Include="grammars/my-language/grammar.json" />
</ItemGroup>
```

```csharp
[TreeSpitter.GeneratedLanguage("my-language/grammar.json", ExternalScanner = typeof(MyLanguageScanner))]
public static partial class MyLanguage;

var tree = MyLanguage.CreateParser().Parse(source);
```

The generated class embeds the tables (`Language.Save()`), loads them once (`Language.Load()`, from the data of the
assembly, without a copy), and matches the keywords with code. What it saves is the preparation of the language: the
evaluation of `grammar.js` and the compilation of the tables (a few seconds for most grammars, up to 40 s and 2 GB of
memory for C++, C# and Razor), done at build time. The parses are as fast as the ones of a language compiled at
runtime, which runs the same tables. `treespitter generate` writes the same source
to a file.

A grammar with `externals` needs its external scanner (`src/scanner.c`) ported to an `IExternalScanner`: `Scan`,
`Serialize` and `Deserialize` with the `TSLexer` API on `Lexer` (`Lookahead`, `Advance`, `MarkEnd`, `GetColumn`,
`ResultSymbol`...), and the `iswalpha`-like functions of `WideChar`. The scanners of `TreeSpitter.Languages`
(`src/TreeSpitter.Languages/Scanners`) are examples. A language compiled at runtime gets its scanner with
`language.WithExternalScanner(() => new MyLanguageScanner())`.

### Grammars

```csharp
using TreeSpitter.Dsl;
using TreeSpitter.Generation;

var grammar = new GrammarEvaluator().EvaluateFile("tree-sitter-go/grammar.js");
File.WriteAllText("grammar.json", grammar.ToJson());     // the same file as tree-sitter generate's src/grammar.json
var language = LanguageCompiler.Compile(grammar);        // throws GrammarException for unresolved conflicts
File.WriteAllBytes("go.tsl", language.Save());           // the compiled tables, for Language.Load
```

## Command line

```
treespitter grammar <grammar.js|grammar.json>                     prints the grammar as JSON (like src/grammar.json)
treespitter info <grammar>                                        prints the size of the generated tables
treespitter compile <grammar> [-o file.tsl]                       writes the compiled tables
treespitter generate <grammar|file.tsl> [-o file.cs] [--namespace ns] [--class name]
                                                                   generates the C# source of a precompiled parser
treespitter parse <grammar> <file|-> [--time]                     prints the syntax tree
treespitter test <grammar> <corpus> [-v] [--filter text]          runs tree-sitter corpus tests
```

## Grammar support

The whole DSL is supported: `seq`, `choice`, `optional`, `repeat`, `repeat1`, `prec` (`left`, `right`, `dynamic`,
named `precedences`), `token`, `token.immediate`, `alias`, `field`, `extras`, `word`, `inline`, `supertypes`,
`conflicts`, `reserved`, and grammars extending other grammars through `require` or `import`. The generator is a port
of `tree-sitter generate` v0.27.1 and reports the same errors (unresolved conflicts, empty rules, ...), and the parser is a
port of the v0.27.1 runtime. The parse tables have the states of the `parser.c` files of the grammars. The `eof()` rule
of 0.27 isn't supported yet.

| Grammar | Corpus tests passing | Parse states | Symbols | Lex states | External scanner |
| --- | ---: | ---: | ---: | ---: | --- |
| [agda](https://github.com/tree-sitter/tree-sitter-agda) | 75 / 75 | 12818 | 223 | 689 | AgdaScanner |
| [bash](https://github.com/tree-sitter/tree-sitter-bash) | 100 / 100 | 7571 | 280 | 950 | BashScanner |
| [c](https://github.com/tree-sitter/tree-sitter-c) | 85 / 85 | 2015 | 360 | 324 |  |
| [c-sharp](https://github.com/tree-sitter/tree-sitter-c-sharp) | 154 / 155 | 7822 | 524 | 414 | CSharpScanner |
| [cpp](https://github.com/tree-sitter/tree-sitter-cpp) | 197 / 197 | 11551 | 565 | 772 | CppScanner |
| [css](https://github.com/tree-sitter/tree-sitter-css) | 40 / 40 | 442 | 142 | 437 | CssScanner |
| [embedded-template](https://github.com/tree-sitter/tree-sitter-embedded-template) | 7 / 7 | 29 | 31 | 52 |  |
| [go](https://github.com/tree-sitter/tree-sitter-go) | 67 / 67 | 1442 | 214 | 165 |  |
| [haskell](https://github.com/tree-sitter/tree-sitter-haskell) | 725 / 725 | 12862 | 537 | 175 | HaskellScanner |
| [html](https://github.com/tree-sitter/tree-sitter-html) | 20 / 20 | 94 | 41 | 77 | HtmlScanner |
| [java](https://github.com/tree-sitter/tree-sitter-java) | 108 / 108 | 1385 | 320 | 194 |  |
| [javascript](https://github.com/tree-sitter/tree-sitter-javascript) | 116 / 116 | 1870 | 261 | 279 | JavaScriptScanner |
| [jsdoc](https://github.com/tree-sitter/tree-sitter-jsdoc) | 17 / 17 | 99 | 43 | 246 | JsDocScanner |
| [json](https://github.com/tree-sitter/tree-sitter-json) | 6 / 6 | 32 | 25 | 44 |  |
| [julia](https://github.com/tree-sitter/tree-sitter-julia) | 63 / 63 | 8267 | 243 | 373 | JuliaScanner |
| [ocaml](https://github.com/tree-sitter/tree-sitter-ocaml) | 79 / 79 | 8273 | 440 | 356 | OCamlScanner |
| [ocaml-type](https://github.com/tree-sitter/tree-sitter-ocaml) | 8 / 8 | 7196 | 439 | 358 | OCamlScanner |
| [php](https://github.com/tree-sitter/tree-sitter-php) | 140 / 140 | 3371 | 431 | 292 | PhpScanner |
| [python](https://github.com/tree-sitter/tree-sitter-python) | 117 / 117 | 2788 | 273 | 150 | PythonScanner |
| [ql](https://github.com/tree-sitter/tree-sitter-ql) | 57 / 57 | 2019 | 188 | 57 |  |
| [razor](https://github.com/tris203/tree-sitter-razor) | 71 / 71 | 18646 | 607 | 708 | CSharpScanner |
| [ruby](https://github.com/tree-sitter/tree-sitter-ruby) | 290 / 290 | 5989 | 347 | 350 | RubyScanner |
| [rust](https://github.com/tree-sitter/tree-sitter-rust) | 148 / 148 | 3825 | 351 | 196 | RustScanner |
| [scala](https://github.com/tree-sitter/tree-sitter-scala) | 143 / 143 | 21252 | 353 | 191 | ScalaScanner |
| [tsx](https://github.com/tree-sitter/tree-sitter-typescript) | 111 / 111 | 5994 | 393 | 294 | TypeScriptScanner |
| [typescript](https://github.com/tree-sitter/tree-sitter-typescript) | 111 / 111 | 5878 | 376 | 219 | TypeScriptScanner |
| [verilog](https://github.com/tree-sitter/tree-sitter-verilog) | 70 / 70 | 20534 | 1202 | 706 |  |

The C# test that fails (`Switch statements`, `case int when i < 5:`) is parsed like tree-sitter 0.27.1 parses it: its
generator shifts on a precedence tie with right-associative reductions where 0.26, which generated the native parser
and the corpus, reduced.

Limitations: no included ranges, no changed ranges between trees, no queries. The external scanners of other grammars
have to be ported to C#.

## Build

The library targets `netstandard2.0` and `net10.0` and is written in C# 15, which needs the .NET 11 SDK (the .NET 10
SDK compiles up to C# 14); the tests and the tool run on .NET 10. `global.json` selects the SDK and opts `dotnet test`
into Microsoft.Testing.Platform.

```bash
dotnet build
dotnet test --solution TreeSpitter.slnx
dotnet run -c Release --project benchmarks/TreeSpitter.Benchmarks -- --filter '*'
dotnet run -c Release --project benchmarks/TreeSpitter.Benchmarks -- quick        # a quick comparison, without BenchmarkDotNet
```

Sources follow the root [`.editorconfig`](.editorconfig); check the formatting with
`dotnet format TreeSpitter.slnx --severity warn --verify-no-changes`. The design is described in
[docs/design.md](docs/design.md), releases in [docs/release.md](docs/release.md).

## License

[MIT](LICENSE). TreeSpitter ports tree-sitter (MIT), and the repository vendors tree-sitter grammars and sources; their
licenses are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
