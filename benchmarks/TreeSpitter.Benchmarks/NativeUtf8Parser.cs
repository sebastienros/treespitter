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

    [StructLayout(LayoutKind.Sequential)]
    private struct TSPoint
    {
        public uint Row;
        public uint Column;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TSInputEdit
    {
        public uint StartByte;
        public uint OldEndByte;
        public uint NewEndByte;
        public TSPoint StartPoint;
        public TSPoint OldEndPoint;
        public TSPoint NewEndPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TSLogger
    {
        public IntPtr Payload;
        public delegate* unmanaged<IntPtr, int, byte*, void> Log;
    }

    private static readonly IntPtr Runtime = Load("tree-sitter");
    private static readonly delegate* unmanaged<IntPtr, TSLogger, void> ParserSetLogger = (delegate* unmanaged<IntPtr, TSLogger, void>)Export("ts_parser_set_logger");
    private static readonly delegate* unmanaged<IntPtr, TSInputEdit*, void> TreeEdit = (delegate* unmanaged<IntPtr, TSInputEdit*, void>)Export("ts_tree_edit");
    private static readonly delegate* unmanaged<IntPtr, IntPtr> TreeCopy = (delegate* unmanaged<IntPtr, IntPtr>)Export("ts_tree_copy");
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

    /// <summary>The destination of the logs of the parsers, when set.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Logs the parse to <see cref="Log"/>.</summary>
    public void EnableLogging(bool enabled) =>
        ParserSetLogger(_parser, new TSLogger { Payload = IntPtr.Zero, Log = enabled ? &OnLog : null });

    [UnmanagedCallersOnly]
    private static void OnLog(IntPtr payload, int type, byte* buffer)
    {
        if (type == 0)
        {
            Log?.Invoke(Marshal.PtrToStringUTF8((IntPtr)buffer)!);
        }
    }

    /// <summary>Parses UTF-8 input, incrementally with an edited old tree.</summary>
    public NativeTree ParseTree(byte[] input, NativeTree? oldTree)
    {
        fixed (byte* bytes = input)
        {
            return new NativeTree(ParserParseString(_parser, oldTree?.Handle ?? IntPtr.Zero, bytes, (uint)input.Length));
        }
    }

    public void Dispose() => ParserDelete(_parser);

    /// <summary>A native tree.</summary>
    internal sealed class NativeTree : IDisposable
    {
        public NativeTree(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; private set; }

        public NativeTree Copy() => new(TreeCopy(Handle));

        /// <summary>Applies an edit, with the points computed from the old and the new input.</summary>
        public void Edit(byte[] oldInput, byte[] newInput, int startByte, int oldEndByte, int newEndByte)
        {
            var edit = new TSInputEdit
            {
                StartByte = (uint)startByte,
                OldEndByte = (uint)oldEndByte,
                NewEndByte = (uint)newEndByte,
                StartPoint = PointAt(oldInput, startByte),
                OldEndPoint = PointAt(oldInput, oldEndByte),
                NewEndPoint = PointAt(newInput, newEndByte),
            };
            TreeEdit(Handle, &edit);
        }

        /// <summary>Edits the tree with the points of the edit, which an editor knows: nothing is computed.</summary>
        public void Edit(InputEdit edit)
        {
            var native = new TSInputEdit
            {
                StartByte = (uint)edit.StartByte,
                OldEndByte = (uint)edit.OldEndByte,
                NewEndByte = (uint)edit.NewEndByte,
                StartPoint = ToNative(edit.StartPoint!.Value),
                OldEndPoint = ToNative(edit.OldEndPoint!.Value),
                NewEndPoint = ToNative(edit.NewEndPoint!.Value),
            };
            TreeEdit(Handle, &native);

            static TSPoint ToNative(Point point) => new() { Row = (uint)point.Row, Column = (uint)point.Column };
        }

        public string ToSExpression()
        {
            var text = NodeString(TreeRootNode(Handle));
            try
            {
                return Marshal.PtrToStringUTF8(text)!;
            }
            finally
            {
                NativeMemory.Free((void*)text);
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                TreeDelete(Handle);
                Handle = IntPtr.Zero;
            }
        }

        private static TSPoint PointAt(byte[] input, int position)
        {
            var row = 0u;
            var lineStart = 0;
            for (var i = 0; i < position && i < input.Length; i++)
            {
                if (input[i] == '\n')
                {
                    row++;
                    lineStart = i + 1;
                }
            }

            return new TSPoint { Row = row, Column = (uint)(position - lineStart) };
        }
    }

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
