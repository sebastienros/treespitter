using System.Runtime.InteropServices;

namespace TreeSpitter.Benchmarks;

/// <summary>
/// The native tree-sitter parser on UTF-8 input, called directly (the TreeSitter.DotNet binding only parses strings,
/// as UTF-16). The libraries are the ones of the TreeSitter.DotNet package.
/// </summary>
internal sealed unsafe class NativeUtf8Parser : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TSNode
    {
        public uint Context0;
        public uint Context1;
        public uint Context2;
        public uint Context3;
        public IntPtr Id;
        public IntPtr Tree;
    }

    private static readonly IntPtr Runtime = Load("tree-sitter");
    private static readonly delegate* unmanaged<IntPtr> ParserNew = (delegate* unmanaged<IntPtr>)Export("ts_parser_new");
    private static readonly delegate* unmanaged<IntPtr, void> ParserDelete = (delegate* unmanaged<IntPtr, void>)Export("ts_parser_delete");
    private static readonly delegate* unmanaged<IntPtr, IntPtr, byte> ParserSetLanguage = (delegate* unmanaged<IntPtr, IntPtr, byte>)Export("ts_parser_set_language");
    private static readonly delegate* unmanaged<IntPtr, IntPtr, byte*, uint, IntPtr> ParserParseString = (delegate* unmanaged<IntPtr, IntPtr, byte*, uint, IntPtr>)Export("ts_parser_parse_string");
    private static readonly delegate* unmanaged<IntPtr, void> TreeDelete = (delegate* unmanaged<IntPtr, void>)Export("ts_tree_delete");
    private static readonly delegate* unmanaged<IntPtr, TSNode> TreeRootNode = (delegate* unmanaged<IntPtr, TSNode>)Export("ts_tree_root_node");
    private static readonly delegate* unmanaged<TSNode, IntPtr> NodeString = (delegate* unmanaged<TSNode, IntPtr>)Export("ts_node_string");

    private readonly IntPtr _parser;

    public NativeUtf8Parser(string language)
    {
        var library = Load("tree-sitter-" + language);
        var function = (delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(library, "tree_sitter_" + language.Replace('-', '_'));
        _parser = ParserNew();
        if (ParserSetLanguage(_parser, function()) == 0)
        {
            throw new InvalidOperationException($"The native {language} language isn't compatible with the runtime.");
        }
    }

    /// <summary>Parses UTF-8 input and deletes the tree.</summary>
    public void Parse(byte[] input)
    {
        fixed (byte* bytes = input)
        {
            TreeDelete(ParserParseString(_parser, IntPtr.Zero, bytes, (uint)input.Length));
        }
    }

    /// <summary>Parses UTF-8 input and returns the S-expression of the tree.</summary>
    public string ToSExpression(byte[] input)
    {
        fixed (byte* bytes = input)
        {
            var tree = ParserParseString(_parser, IntPtr.Zero, bytes, (uint)input.Length);
            var text = NodeString(TreeRootNode(tree));
            try
            {
                return Marshal.PtrToStringUTF8(text)!;
            }
            finally
            {
                // tree-sitter allocates with malloc by default
                NativeMemory.Free((void*)text);
                TreeDelete(tree);
            }
        }
    }

    public void Dispose() => ParserDelete(_parser);

    private static IntPtr Load(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".dll" : OperatingSystem.IsMacOS() ? "lib" + name + ".dylib" : "lib" + name + ".so";
        var rid = RuntimeInformation.RuntimeIdentifier;
        foreach (var path in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file),
            Path.Combine(AppContext.BaseDirectory, "runtimes", (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), "native", file),
            Path.Combine(AppContext.BaseDirectory, file),
        })
        {
            if (File.Exists(path))
            {
                return NativeLibrary.Load(path);
            }
        }

        return NativeLibrary.Load(file);
    }

    private static IntPtr Export(string name) => NativeLibrary.GetExport(Runtime, name);
}
