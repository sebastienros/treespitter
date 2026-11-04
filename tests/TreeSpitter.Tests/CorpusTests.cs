using TreeSpitter.Testing;
using Xunit;

namespace TreeSpitter.Tests;

/// <summary>
/// Runs the corpus tests of the vendored tree-sitter grammars. The tests which are known to fail are listed in the
/// known-failures.txt file of each grammar: a new failure fails the build, a test which starts passing has to be removed
/// from the list.
/// </summary>
public sealed class CorpusTests
{
    private readonly ITestOutputHelper _output;

    public CorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> JsonTests() => new(TreeSpitter.Testing.Corpus.Load(Fixtures.Corpus("json")).Select(t => t.Name));

    [Theory]
    [MemberData(nameof(JsonTests))]
    public void Json(string name)
    {
        var test = TreeSpitter.Testing.Corpus.Load(Fixtures.Corpus("json")).Single(t => t.Name == name);
        var result = TreeSpitter.Testing.Corpus.Run(Fixtures.Parser("json"), test);

        Assert.Equal(result.Expected, result.Actual);
    }

    [Theory]
    [MemberData(nameof(Fixtures.Grammars), MemberType = typeof(Fixtures))]
    public void TheVirtualMachineMatchesTheKnownResults(string grammar) => MatchesTheKnownResults(grammar, Fixtures.Parser(grammar));

    [Theory]
    [MemberData(nameof(Fixtures.Grammars), MemberType = typeof(Fixtures))]
    public void TheGeneratedParsersMatchTheKnownResults(string grammar) => MatchesTheKnownResults(grammar, new Parser(Fixtures.GeneratedLanguage(grammar)));

    private void MatchesTheKnownResults(string grammar, Parser parser)
    {
        var knownFailuresPath = Path.Combine(Fixtures.GrammarDirectory(grammar), "known-failures.txt");
        var knownFailures = File.Exists(knownFailuresPath)
            ? File.ReadAllLines(knownFailuresPath).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet()
            : [];

        var newFailures = new List<string>();
        var newPasses = new List<string>();
        var passed = 0;
        var tests = TreeSpitter.Testing.Corpus.Load(Fixtures.Corpus(grammar)).Where(t => !t.Skip).ToList();

        foreach (var test in tests)
        {
            var key = Path.GetFileNameWithoutExtension(test.File) + ": " + test.Name;
            var result = TreeSpitter.Testing.Corpus.Run(parser, test);
            if (result.Passed)
            {
                passed++;
                if (knownFailures.Contains(key))
                {
                    newPasses.Add(key);
                }
            }
            else if (!knownFailures.Contains(key))
            {
                newFailures.Add(key);
                _output.WriteLine($"FAILED {key}\n  expected: {result.Expected}\n  actual:   {result.Actual}");
            }
        }

        _output.WriteLine($"{grammar}: {passed}/{tests.Count} corpus tests pass");
        foreach (var key in newPasses)
        {
            _output.WriteLine("now passing, remove from known-failures.txt: " + key);
        }

        Assert.Empty(newFailures);
        Assert.Empty(newPasses);
    }
}
