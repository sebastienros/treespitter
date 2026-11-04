using System.IO.Compression;
using System.Text;

namespace TreeSpitter;

public sealed partial class Language
{
    private const uint Magic = 0x4C505354; // "TSPL"
    private const int FormatVersion = 1;

    /// <summary>
    /// Serializes the language to its binary format: the parse table, the lexer automata and the metadata, compressed.
    /// <see cref="Load(byte[])"/> reads it back without the grammar, which is how generated parsers embed their tables.
    /// </summary>
    public byte[] Save()
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(Name);
            writer.Write(SymbolCount);
            writer.Write(AliasCount);
            writer.Write(TokenCount);
            writer.Write(ExternalTokenCount);
            writer.Write(StateCount);
            writer.Write(FieldCount);
            writer.Write(ProductionIdCount);
            writer.Write(MaxAliasSequenceLength);
            writer.Write(KeywordCaptureToken);
            writer.Write(MaxReservedWordSetSize);
            WriteStrings(writer, SymbolNames);
            WriteBytes(writer, SymbolFlags);
            WriteUShorts(writer, PublicSymbolMap);
            WriteStrings(writer, FieldNames);
            WriteUShorts(writer, ParseTable);
            writer.Write(ParseActions.Length);
            foreach (var action in ParseActions)
            {
                writer.Write(action.Value);
            }

            WriteUShorts(writer, LexStates);
            WriteUShorts(writer, ExternalLexStates);
            WriteUShorts(writer, ReservedWordSetIds);
            WriteUShorts(writer, AliasSequences);
            WriteUShorts(writer, FieldMapSlices);
            writer.Write(FieldMapEntries.Length);
            foreach (var entry in FieldMapEntries)
            {
                writer.Write(entry.FieldId);
                writer.Write(entry.ChildIndex);
                writer.Write(entry.Inherited);
            }

            WriteUShorts(writer, ReservedWords);
            WriteUShorts(writer, SupertypeSymbols);
            WriteLexAutomaton(writer, MainLexTable);
            WriteLexAutomaton(writer, KeywordLexTable);
            WriteStrings(writer, ExternalTokenNames);
            WriteUShorts(writer, ExternalScannerSymbolMap);
            writer.Write(ExternalScannerStates.Length);
            foreach (var value in ExternalScannerStates)
            {
                writer.Write(value);
            }
        }

        return output.ToArray();
    }

    /// <summary>Reads a language serialized by <see cref="Save"/>.</summary>
    public static Language Load(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        using var input = new MemoryStream(data, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(deflate, Encoding.UTF8);
        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("The data isn't a serialized TreeSpitter language.");
        }

        var version = reader.ReadInt32();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported language format version {version}, expected {FormatVersion}.");
        }

        var language = new Language
        {
            Name = reader.ReadString(),
            SymbolCount = reader.ReadInt32(),
            AliasCount = reader.ReadInt32(),
            TokenCount = reader.ReadInt32(),
            ExternalTokenCount = reader.ReadInt32(),
            StateCount = reader.ReadInt32(),
            FieldCount = reader.ReadInt32(),
            ProductionIdCount = reader.ReadInt32(),
            MaxAliasSequenceLength = reader.ReadInt32(),
            KeywordCaptureToken = reader.ReadUInt16(),
            MaxReservedWordSetSize = reader.ReadInt32(),
        };

        language.SymbolNames = ReadStrings(reader)!;
        language.SymbolFlags = ReadBytes(reader);
        language.PublicSymbolMap = ReadUShorts(reader);
        language.FieldNames = ReadStrings(reader);
        language.ParseTable = ReadUShorts(reader);
        var rawActions = new ulong[reader.ReadInt32()];
        ReadBulk(reader, rawActions, sizeof(ulong));
        var actions = new ParseActionEntry[rawActions.Length];
        for (var i = 0; i < actions.Length; i++)
        {
            actions[i] = new ParseActionEntry(rawActions[i]);
        }

        language.ParseActions = actions;
        language.LexStates = ReadUShorts(reader);
        language.ExternalLexStates = ReadUShorts(reader);
        language.ReservedWordSetIds = ReadUShorts(reader);
        language.AliasSequences = ReadUShorts(reader);
        language.FieldMapSlices = ReadUShorts(reader);
        var entries = new FieldMapEntry[reader.ReadInt32()];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new FieldMapEntry(reader.ReadUInt16(), reader.ReadByte(), reader.ReadBoolean());
        }

        language.FieldMapEntries = entries;
        language.ReservedWords = ReadUShorts(reader);
        language.SupertypeSymbols = ReadUShorts(reader);
        language.MainLexTable = ReadLexAutomaton(reader);
        language.KeywordLexTable = ReadLexAutomaton(reader);
        language.ExternalTokenNames = ReadStrings(reader)!;
        language.ExternalScannerSymbolMap = ReadUShorts(reader);
        var states = new bool[reader.ReadInt32()];
        for (var i = 0; i < states.Length; i++)
        {
            states[i] = reader.ReadBoolean();
        }

        language.ExternalScannerStates = states;
        return language;
    }

    private static void WriteStrings(BinaryWriter writer, string?[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value is not null);
            if (value is not null)
            {
                writer.Write(value);
            }
        }
    }

    private static string?[] ReadStrings(BinaryReader reader)
    {
        var values = new string?[reader.ReadInt32()];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadBoolean() ? reader.ReadString() : null;
        }

        return values;
    }

    private static void WriteBytes(BinaryWriter writer, byte[] values)
    {
        writer.Write(values.Length);
        writer.Write(values);
    }

    private static byte[] ReadBytes(BinaryReader reader) => reader.ReadBytes(reader.ReadInt32());

    private static void WriteUShorts(BinaryWriter writer, ushort[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static ushort[] ReadUShorts(BinaryReader reader)
    {
        var values = new ushort[reader.ReadInt32()];
        ReadBulk(reader, values, sizeof(ushort));
        return values;
    }

    /// <summary>Reads an array of little-endian primitives at once.</summary>
    private static void ReadBulk<T>(BinaryReader reader, T[] values, int size)
        where T : struct
    {
        if (values.Length == 0)
        {
            return;
        }

        var bytes = reader.ReadBytes(values.Length * size);
        if (bytes.Length != values.Length * size)
        {
            throw new EndOfStreamException();
        }

        if (!BitConverter.IsLittleEndian)
        {
            for (var i = 0; i < bytes.Length; i += size)
            {
                Array.Reverse(bytes, i, size);
            }
        }

        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
    }

    private static void WriteInts(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static int[] ReadInts(BinaryReader reader)
    {
        var values = new int[reader.ReadInt32()];
        ReadBulk(reader, values, sizeof(int));
        return values;
    }

    private static void WriteLexAutomaton(BinaryWriter writer, LexAutomaton automaton)
    {
        WriteInts(writer, automaton.Accept);
        WriteInts(writer, automaton.EofTarget);
        WriteInts(writer, automaton.TransitionStart);
        WriteInts(writer, automaton.RangeStart);
        WriteInts(writer, automaton.RangeEnd);
        WriteInts(writer, automaton.RangeTarget);
    }

    private static LexAutomaton ReadLexAutomaton(BinaryReader reader) =>
        new(ReadInts(reader), ReadInts(reader), ReadInts(reader), ReadInts(reader), ReadInts(reader), ReadInts(reader));
}
