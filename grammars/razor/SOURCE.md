Vendored from https://github.com/tris203/tree-sitter-razor at commit fe46ce5ea7d844e53d59bc96f2175d33691c61c5 (MIT License, see LICENSE), the version of the native parser of TreeSitter.DotNet 1.3.0.

Files: grammar.js, src/grammar.json (as grammar.json) and test/corpus (as corpus/). The external scanner, src/scanner.c, is the one of tree-sitter-c-sharp (only the names of its functions differ): its port is src/TreeSpitter.Languages/Scanners/CSharpScanner.cs.

grammar.js requires `tree-sitter-c-sharp/grammar`, the npm package `tree-sitter-c-sharp` (`^0.23.1`, resolved to 0.23.1 by the pnpm-lock.yaml of tree-sitter-razor): node_modules/tree-sitter-c-sharp/grammar.js and its LICENSE are the ones of https://github.com/tree-sitter/tree-sitter-c-sharp at the tag v0.23.1 (commit 362a8a41b265056592a0c3771664a21d23a71392, MIT License), which reproduces src/grammar.json.
