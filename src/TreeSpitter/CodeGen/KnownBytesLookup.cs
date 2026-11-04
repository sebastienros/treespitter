using System.Globalization;
using System.Text;

namespace TreeSpitter.CodeGen;

/// <summary>
/// Generates the code that recognizes a known set of byte strings, the technique of Parlot's
/// <c>SourceGeneration/KnownStringLookup</c>: a switch on the length, then switches on chunks of the input read as
/// little-endian integers (8, 4, 2 or 1 bytes, masked), choosing at each node the chunk that splits the remaining
/// candidates best, and comparing the remaining chunks of the last candidate. A word of up to 8 bytes is recognized
/// with one switch on its length and one comparison of a <see cref="ulong"/>, without hashing or comparing spans.
/// </summary>
internal static class KnownBytesLookup
{
    /// <summary>
    /// Emits the body of a method whose parameter <c>input</c> is a <c>ReadOnlySpan&lt;byte&gt;</c>, returning
    /// <paramref name="match"/> for the index of the word that is the input, or <paramref name="failure"/>.
    /// </summary>
    public static string Generate(IReadOnlyList<byte[]> words, Func<int, string> match, string failure, string resultType = "int")
    {
        var output = new StringBuilder();
        var helpers = new StringBuilder();
        var number = 0;
        var candidates = words.Select((bytes, index) => new Candidate(bytes, index))
            .GroupBy(c => Convert.ToBase64String(c.Bytes), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();
        var lengthGroups = candidates.GroupBy(c => c.Bytes.Length).OrderBy(g => g.Key).ToArray();

        // Large vocabularies are split in one method per length, to keep the methods small for the JIT
        var useLengthHelpers = candidates.Length > 32 && lengthGroups.Length > 1 && candidates.Sum(c => c.Bytes.Length) > 1024;
        var inlineCandidates = 0;
        output.Append("switch (input.Length)\n{\n");
        foreach (var group in lengthGroups)
        {
            output.Append(FormattableString.Invariant($"case {group.Key}:\n{{\n"));
            if (useLengthHelpers && (group.Count() > 4 || inlineCandidates + group.Count() > 32))
            {
                output.Append(FormattableString.Invariant($"return MatchLength{group.Key}(input);\n}}\n"));
                var dispatch = output;
                output = helpers;
                output.Append(FormattableString.Invariant($"static {resultType} MatchLength{group.Key}(global::System.ReadOnlySpan<byte> input)\n{{\n"));
                if (!Emit(group.ToArray(), new bool[group.Key]))
                {
                    output.Append(FormattableString.Invariant($"return {failure};\n"));
                }

                output.Append("}\n");
                output = dispatch;
                continue;
            }

            inlineCandidates += group.Count();
            if (!Emit(group.ToArray(), new bool[group.Key]))
            {
                output.Append("break;\n");
            }

            output.Append("}\n");
        }

        output.Append(FormattableString.Invariant($"}}\nreturn {failure};\n"));
        output.Append(helpers);
        return output.ToString();

        // Emits the tests of candidates of the same length, the bytes known equal for all of them being "proven".
        // Returns whether the emitted code always returns.
        bool Emit(Candidate[] remaining, bool[] proven)
        {
            if (remaining.Length > 1)
            {
                // The chunks that are the same for all the candidates are checked once
                proven = (bool[])proven.Clone();
                var common = new List<string>();
                for (var width = 8; width >= 1; width /= 2)
                {
                    for (var offset = 0; offset + width <= proven.Length; offset++)
                    {
                        if (Enumerable.Range(offset, width).Any(i => proven[i]) ||
                            remaining.Select(c => Value(c.Bytes, offset, width)).Distinct().Count() != 1)
                        {
                            continue;
                        }

                        common.Add(Test(remaining[0], offset, width));
                        Prove(proven, offset, width);
                    }
                }

                if (common.Count > 0)
                {
                    output.Append(FormattableString.Invariant($"if (!({string.Join(" && ", common)})) return {failure};\n"));
                }
            }

            if (remaining.Length == 1)
            {
                var tests = new List<string>();
                for (var offset = 0; offset < proven.Length;)
                {
                    if (proven[offset])
                    {
                        offset++;
                        continue;
                    }

                    var width = 8;
                    while (offset + width > proven.Length || Enumerable.Range(offset, width).Any(i => proven[i]))
                    {
                        width /= 2;
                    }

                    tests.Add(Test(remaining[0], offset, width));
                    offset += width;
                }

                var result = match(remaining[0].Index);
                output.Append(tests.Count == 0 ? $"return {result};\n" : $"if ({string.Join(" && ", tests)}) return {result};\n");
                return tests.Count == 0;
            }

            // The chunk that splits the candidates in the most balanced partitions
            var bestOffset = -1;
            var bestWidth = 0;
            var bestLargest = remaining.Length;
            var bestDistinct = 0;
            for (var width = remaining.Length <= 32 ? 8 : 1; width >= 1; width /= 2)
            {
                for (var offset = 0; offset + width <= proven.Length; offset++)
                {
                    if (Enumerable.Range(offset, width).Any(i => proven[i]))
                    {
                        continue;
                    }

                    var partitions = remaining.GroupBy(c => Value(c.Bytes, offset, width)).ToArray();
                    var largest = partitions.Max(g => g.Count());
                    if (partitions.Length > 1 && (largest < bestLargest || largest == bestLargest && partitions.Length > bestDistinct))
                    {
                        bestOffset = offset;
                        bestWidth = width;
                        bestLargest = largest;
                        bestDistinct = partitions.Length;
                    }
                }
            }

            if (bestOffset < 0)
            {
                foreach (var candidate in remaining)
                {
                    if (Emit([candidate], proven))
                    {
                        return true;
                    }
                }

                return false;
            }

            var local = $"chunk{number++}";
            output.Append(FormattableString.Invariant($"var {local} = {Load(bestOffset, bestWidth)};\n"));
            output.Append(FormattableString.Invariant($"switch ({local})\n{{\n"));
            var nextProven = (bool[])proven.Clone();
            Prove(nextProven, bestOffset, bestWidth);
            foreach (var group in remaining.GroupBy(c => Value(c.Bytes, bestOffset, bestWidth)))
            {
                output.Append(FormattableString.Invariant($"case {Constant(group.Key)}:\n{{\n"));
                if (!Emit(group.ToArray(), nextProven))
                {
                    output.Append("break;\n");
                }

                output.Append("}\n");
            }

            output.Append("}\n");
            return false;
        }

        static string Test(Candidate candidate, int offset, int width) =>
            $"{Load(offset, width)} == {Constant(Value(candidate.Bytes, offset, width))}";
    }

    /// <summary>The bytes of the input at an offset, as a little-endian integer.</summary>
    private static string Load(int offset, int width) => width switch
    {
        1 => $"(ulong)input[{offset}]",
        2 => $"(ulong)global::System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(input.Slice({offset}))",
        4 => $"(ulong)global::System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input.Slice({offset}))",
        _ => $"global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(input.Slice({offset}))",
    };

    private static ulong Value(byte[] bytes, int offset, int width)
    {
        ulong value = 0;
        for (var i = 0; i < width; i++)
        {
            value |= (ulong)bytes[offset + i] << (i * 8);
        }

        return value;
    }

    private static void Prove(bool[] proven, int offset, int width)
    {
        for (var i = offset; i < offset + width; i++)
        {
            proven[i] = true;
        }
    }

    private static string Constant(ulong value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture) + "UL";

    private sealed record Candidate(byte[] Bytes, int Index);
}
