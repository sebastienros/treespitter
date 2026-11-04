using System.Text;
using BenchmarkDotNet.Attributes;

namespace TreeSpitter.Benchmarks;

/// <summary>
/// Incremental parsing against the native tree-sitter parsers: each operation edits the tree of the previous version of
/// the input (a character inserted in the middle of an identifier or a number, or removed) and parses the new version
/// with it, like an editor does on each keystroke. The full parses of the same input are the baselines.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class IncrementalBenchmarks
{
    private Parser _parser = null!;
    private NativeUtf8Parser _native = null!;
    private byte[] _original = [];
    private byte[] _edited = [];
    private int _position;
    private SyntaxTree _tree = null!;
    private NativeUtf8Parser.NativeTree _nativeTree = null!;
    private bool _treeIsEdited;
    private bool _nativeTreeIsEdited;

    // The edits with their points, like an editor gives them: the insertion of the character and its removal
    private InputEdit _insertion;
    private InputEdit _removal;

    /// <summary>The languages compared.</summary>
    public static IEnumerable<string> Languages => Grammars.Names;

    /// <summary>The language of the benchmark.</summary>
    [ParamsSource(nameof(Languages))]
    public string Language { get; set; } = "";

    /// <summary>The approximate size of the input, in chars.</summary>
    [Params(100_000)]
    public int Size { get; set; }

    /// <summary>Finds an edit in the middle of the input which keeps it valid, and checks that both parsers agree.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _parser = new Parser(Grammars.GeneratedLanguage(Language));
        _native = new NativeUtf8Parser(Language);
        _original = Encoding.UTF8.GetBytes(Grammars.Input(Language, Size));
        for (_position = _original.Length / 2; _position < _original.Length; _position++)
        {
            // Inside a word: insert a copy of its character
            if (!char.IsLetterOrDigit((char)_original[_position]) || !char.IsLetterOrDigit((char)_original[_position - 1]))
            {
                continue;
            }

            _edited = [.. _original.AsSpan(0, _position), _original[_position], .. _original.AsSpan(_position)];
            using var tree = _parser.Parse(_edited);
            using var originalTree = _parser.Parse(_original);
            if (!tree.HasError && !originalTree.HasError)
            {
                break;
            }
        }

        var start = IncrementalCheck.PointAt(_original, _position);
        var after = new Point(start.Row, start.Column + 1);
        _insertion = new InputEdit(_position, _position, _position + 1, start, start, after);
        _removal = new InputEdit(_position, _position + 1, _position, start, after, start);
        _tree = _parser.Parse(_original);
        _nativeTree = _native.ParseTree(_original, null);

        // Two edits each, both ways: the incremental trees are the fresh ones, and the native ones
        Grammars.As026(() =>
        {
            for (var i = 0; i < 4; i++)
            {
                Incremental();
                NativeIncremental();
                var input = _treeIsEdited ? _edited : _original;
                if (_tree.ToSExpression() != _parser.Parse(input).ToSExpression() || _tree.ToSExpression() != _nativeTree.ToSExpression())
                {
                    throw new InvalidOperationException($"The incremental {Language} trees differ.");
                }
            }

            return 0;
        });
    }

    /// <summary>Releases the trees and the native parser.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _tree.Dispose();
        _nativeTree.Dispose();
        _native.Dispose();
    }

    /// <summary>Parses the input from scratch with the native parser.</summary>
    [Benchmark(Baseline = true)]
    public void NativeFull() => _native.Parse(_original);

    /// <summary>Edits the native tree and parses the new input with it.</summary>
    [Benchmark]
    public void NativeIncremental()
    {
        var newInput = _nativeTreeIsEdited ? _original : _edited;
        _nativeTree.Edit(_nativeTreeIsEdited ? _removal : _insertion);
        var tree = _native.ParseTree(newInput, _nativeTree);
        _nativeTree.Dispose();
        _nativeTree = tree;
        _nativeTreeIsEdited = !_nativeTreeIsEdited;
    }

    /// <summary>Parses the input from scratch with the generated parser.</summary>
    [Benchmark]
    public void Full()
    {
        using var tree = _parser.Parse(_original);
    }

    /// <summary>Edits the tree and parses the new input with it, with the generated parser.</summary>
    [Benchmark]
    public void Incremental()
    {
        var newInput = _treeIsEdited ? _original : _edited;
        _tree.Edit(_treeIsEdited ? _removal : _insertion);
        var tree = _parser.Parse(newInput, _tree);
        _tree.Dispose();
        _tree = tree;
        _treeIsEdited = !_treeIsEdited;
    }
}
