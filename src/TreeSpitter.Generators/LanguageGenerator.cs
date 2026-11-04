using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TreeSpitter.CodeGen;
using TreeSpitter.Generation;
using TreeSpitter.Grammars;

namespace TreeSpitter.Generators;

/// <summary>
/// Generates the precompiled parsers of the classes marked with <c>[GeneratedLanguage("path/grammar.json")]</c>: the
/// grammar is read from the additional files, compiled to parse tables and lexer automata, and emitted as C#.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class LanguageGenerator : IIncrementalGenerator
{
    private const string AttributeName = "TreeSpitter.GeneratedLanguageAttribute";

    private static readonly DiagnosticDescriptor GrammarNotFound = new(
        "TSG001", "Grammar file not found", "The grammar file '{0}' of '{1}' isn't an additional file of the project", "TreeSpitter", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor GrammarError = new(
        "TSG002", "Invalid grammar", "The grammar '{0}' can't be compiled: {1}", "TreeSpitter", DiagnosticSeverity.Error, true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntax, _) =>
            {
                var symbol = (INamedTypeSymbol)syntax.TargetSymbol;
                var path = syntax.Attributes[0].ConstructorArguments.Length > 0 ? syntax.Attributes[0].ConstructorArguments[0].Value as string : null;
                var ns = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString();
                string? scanner = null;
                foreach (var argument in syntax.Attributes[0].NamedArguments)
                {
                    if (argument.Key == "ExternalScanner" && argument.Value.Value is INamedTypeSymbol type)
                    {
                        scanner = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    }
                }

                return new Target(ns, symbol.Name, symbol.IsStatic, path ?? "", scanner, syntax.TargetNode.GetLocation().SourceTree?.FilePath ?? "", syntax.TargetNode.Span);
            });

        var grammars = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => new GrammarFile(Normalize(file.Path), file.GetText(cancellationToken)?.ToString() ?? ""))
            .Collect()
            .Select(static (files, _) => new EquatableArray<GrammarFile>(files));

        context.RegisterSourceOutput(targets.Combine(grammars), static (output, pair) => Generate(output, pair.Left, pair.Right));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void Generate(SourceProductionContext output, Target target, EquatableArray<GrammarFile> files)
    {
        var wanted = Normalize(target.GrammarPath).TrimStart('.', '/');
        GrammarFile? file = null;
        foreach (var candidate in files.Items)
        {
            if (candidate.Path == wanted || candidate.Path.EndsWith("/" + wanted, StringComparison.Ordinal))
            {
                file = candidate;
                break;
            }
        }

        var location = Location.Create(target.FilePath, target.Span, default);
        if (file is null)
        {
            output.ReportDiagnostic(Diagnostic.Create(GrammarNotFound, location, target.GrammarPath, target.ClassName));
            return;
        }

        string source;
        try
        {
            var grammar = GrammarJsonReader.Read(file.Value.Content);
            var language = LanguageCompiler.Compile(grammar);
            source = CSharpGenerator.Generate(language, new CSharpGeneratorOptions
            {
                Namespace = target.Namespace,
                ClassName = target.ClassName,
                Accessibility = null,
                IsStatic = target.IsStatic,
                ExternalScanner = target.ExternalScanner,
            });
        }
        catch (Exception e) when (e is GrammarException or GrammarFormatException or InvalidOperationException)
        {
            output.ReportDiagnostic(Diagnostic.Create(GrammarError, location, target.GrammarPath, e.Message));
            return;
        }

        var hintName = (target.Namespace is null ? "" : target.Namespace + ".") + target.ClassName + ".g.cs";
        output.AddSource(hintName, SourceText.From(source, Encoding.UTF8));
    }

    private readonly record struct Target(string? Namespace, string ClassName, bool IsStatic, string GrammarPath, string? ExternalScanner, string FilePath, Microsoft.CodeAnalysis.Text.TextSpan Span);

    private readonly record struct GrammarFile(string Path, string Content);

    private readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>
        where T : IEquatable<T>
    {
        public ImmutableArray<T> Items { get; } = items;

        public bool Equals(EquatableArray<T> other) => Items.AsSpan().SequenceEqual(other.Items.AsSpan());

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                foreach (var item in Items)
                {
                    hash = hash * 31 + item.GetHashCode();
                }

                return hash;
            }
        }
    }
}
