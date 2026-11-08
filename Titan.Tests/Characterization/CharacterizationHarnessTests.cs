using Xunit.Abstractions;
using static Titan.Tests.Characterization.MoveSequences;

namespace Titan.Tests.Characterization;

// The harness has to be able to fail: a different outcome or a different projection on the new side is found, and as
// the shortest sequence that shows it.
[Trait("Category", "Characterization")]
public class CharacterizationHarnessTests
{
    private readonly ITestOutputHelper _output;

    public CharacterizationHarnessTests(ITestOutputHelper output) => _output = output;

    private sealed class Counter
    {
        public int Value { get; set; }
    }

    private sealed record CounterView(int Value);

    private static Side<Counter, CounterView> CounterSide(Func<Counter, string>? addOutcome = null, Func<Counter, CounterView>? project = null) => new(
        () => new Counter(),
        [
            new Move<Counter>("Add", c =>
            {
                c.Value++;
                return addOutcome?.Invoke(c) ?? "ok";
            }),
            new Move<Counter>("Reset", c =>
            {
                c.Value = 0;
                return "ok";
            }),
            new Move<Counter>("Refuse", c => Capture(() => throw new Titan.Domain.Exceptions.BusinessException("INVALID_STATUS", "no"))),
        ],
        project ?? (c => new CounterView(c.Value)));

    [Fact]
    public void Sequences_OverThreeMovesUpToThree_AreAllOfThem()
    {
        List<string> seen = Of(3, 3).Select(s => string.Join("", s)).ToList();

        Assert.Equal(3 + 9 + 27, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal("0", seen[0]);
        Assert.Equal("22", seen[3 + 8]);
        Assert.Equal(39, Count(3, 3));
    }

    [Fact]
    public void Capture_ARefusal_IsItsCode()
    {
        Assert.Equal("throws:INVALID_STATUS", Capture(() => throw new Titan.Domain.Exceptions.BusinessException("INVALID_STATUS", "x")));
        Assert.Equal("throws:ArgumentException", Capture(() => throw new ArgumentNullException("x")));
        Assert.Equal("returned:null", Returning<string?>(() => null));
        Assert.Equal("returned:true", Returning(() => true));
    }

    [Fact]
    public void Compare_TwoEqualSides_FindsNoMismatch()
    {
        CharacterizationReport report = Compare(CounterSide(), CounterSide(), 4);

        Assert.Equal(3 + 9 + 27 + 81, report.SequencesRun);
        report.AssertNoMismatches();
    }

    [Fact]
    public void Compare_ADifferentOutcomeOnTheNewSide_IsFoundAtItsShortestSequence()
    {
        CharacterizationReport report = Compare(CounterSide(), CounterSide(addOutcome: c => c.Value > 1 ? "returned:null" : "ok"), 4);

        _output.WriteLine(report.ToString());
        Assert.True(report.Mismatches > 0);
        Assert.StartsWith("Add > Add  (step 2)", report.FirstMismatches[0]);
        Assert.Throws<Xunit.Sdk.TrueException>(report.AssertNoMismatches);
    }

    [Fact]
    public void Compare_ADifferentProjectionOnTheNewSide_IsFound()
    {
        CharacterizationReport report = Compare(CounterSide(), CounterSide(project: c => new CounterView(c.Value % 2)), 3);

        Assert.True(report.Mismatches > 0);
        Assert.Contains("Add > Add", report.FirstMismatches[0]);
    }

    [Fact]
    public void Compare_SidesWithDifferentMoves_IsRefused()
    {
        Side<Counter, CounterView> fewer = CounterSide() with { Moves = CounterSide().Moves.Take(2).ToList() };

        Assert.Throws<ArgumentException>(() => Compare(CounterSide(), fewer, 2));
    }
}
