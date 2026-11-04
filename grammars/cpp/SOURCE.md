Vendored from https://github.com/tree-sitter/tree-sitter-cpp at commit 12bd6f7e96080d2e70ec51d4068f2f66120dde35 (MIT License, see LICENSE), the version of the native parser of TreeSitter.DotNet 1.3.0.

Files: grammar.js, src/grammar.json (as grammar.json) and test/corpus (as corpus/, with its c/ directory). The external scanner, src/scanner.c, is ported to C# in src/TreeSpitter.Languages/Scanners/CppScanner.cs.

grammar.js requires `tree-sitter-c/grammar`, the npm package `tree-sitter-c` (`^0.23.6`, resolved to 0.23.6 by the package-lock.json of tree-sitter-cpp): node_modules/tree-sitter-c/grammar.js and its LICENSE are the ones of https://github.com/tree-sitter/tree-sitter-c at the tag v0.23.6 (commit ec69f91b23dd8630d68710b911d77127146ff7ef, MIT License), which reproduces src/grammar.json.
