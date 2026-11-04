Vendored from https://github.com/tree-sitter/tree-sitter-haskell at commit 0975ef72fc3c47b530309ca93937d7d143523628 (MIT License, see LICENSE), the version of the native parser of TreeSitter.DotNet 1.3.0.

Files: grammar.js and the files of grammar/ it requires (grammar/*.js, at the same path), src/grammar.json (as grammar.json) and test/corpus (as corpus/). The external scanner, src/scanner.c and the src/unicode.h it includes, is ported to C# in src/TreeSpitter.Languages/Scanners/HaskellScanner.cs and HaskellScanner.Unicode.cs.
