using System.Runtime.CompilerServices;

namespace TreeSpitter;

/// <summary>
/// The access to the parse table of a language, for which the parse loop is specialized: the table is dense for most
/// grammars, and compressed for the largest ones, so that neither pays for the other.
/// </summary>
internal interface IParseTable
{
    /// <summary>The value of a state and a symbol: the index of an action list for a terminal, the next state for a non-terminal.</summary>
    int Lookup(int state, int symbol);
}

/// <summary>A dense parse table: a row of all the symbols per state.</summary>
internal readonly struct DenseParseTable(ushort[] table, int symbolCount) : IParseTable
{
    private readonly ushort[] _table = table;
    private readonly int _symbolCount = symbolCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Lookup(int state, int symbol) => _table[state * _symbolCount + symbol];
}

/// <summary>A parse table compressed in chunks of consecutive symbols, the identical chunks stored once.</summary>
internal readonly struct ChunkedParseTable(ushort[] index, ushort[] chunks, int shift, int chunksPerRow) : IParseTable
{
    private readonly ushort[] _index = index;
    private readonly ushort[] _chunks = chunks;
    private readonly int _shift = shift;
    private readonly int _chunksPerRow = chunksPerRow;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Lookup(int state, int symbol)
    {
        var chunk = _index[state * _chunksPerRow + (symbol >> _shift)];
        return _chunks[(chunk << _shift) | (symbol & ((1 << _shift) - 1))];
    }
}
