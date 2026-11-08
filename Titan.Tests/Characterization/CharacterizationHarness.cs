using System.Diagnostics;
using System.Text;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Characterization;

// Characterization harness (titan-ops architecture/measurement.md#measurement). When an aggregate changes shape, the
// behaviour an outside observer sees must not change. This runs every sequence of moves up to a bounded length on the
// aggregate as it was and on the aggregate as it is, and compares, after every move, the outcome of the move and a
// projection of what a caller can read.
//
// Run it with one command:
//   dotnet test Titan.Tests/Titan.Tests.csproj --filter "Category=Characterization"
// (add --logger "console;verbosity=detailed" to see the sequence counts; TITAN_CHARACTERIZATION_LENGTH=5 runs the deeper,
// slower bound once before a change ships).
//
// The "old side" is the frozen copy in Old/. The "new side" is the live aggregate in Titan.Domain, reached through an
// adapter (a Side) written once per aggregate; a change that reshapes the aggregate re-points only the new side's calls
// and projection, never the move names, the projected fields or the old side. A move returns a short outcome token, so a refusal and
// its code are part of what is compared. The Old/ folder is deleted when the characterized change has shipped to prd.

/// One named move on an aggregate. The old side and the new side offer the same names in the same order.
internal sealed record Move<TAggregate>(string Name, Func<TAggregate, string> Apply);

/// One side of the comparison: how to build the aggregate, the moves that can be made on it, and the projection of what
/// an observer reads. TView is a record of strings, numbers and bools so that two projections compare by value.
internal sealed record Side<TAggregate, TView>(
    Func<TAggregate> Start,
    IReadOnlyList<Move<TAggregate>> Moves,
    Func<TAggregate, TView> Project);

/// What an observer sees after one move.
internal sealed record Step<TView>(string Move, string Outcome, TView View);

internal static class CharacterizationDepth
{
    /// Each aggregate picks the longest sequence that runs in a few seconds; TITAN_CHARACTERIZATION_LENGTH raises it for
    /// a one-off deeper run before a change ships.
    public static int For(int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable("TITAN_CHARACTERIZATION_LENGTH"), out int length) && length > 0
            ? length
            : fallback;
}

internal static class MoveSequences
{
    /// Every sequence of 1 to maxLength moves, as indexes into the alphabet, shortest first. A sequence is replayed from
    /// a fresh aggregate, so the count is the sum of alphabetSize^length.
    public static IEnumerable<int[]> Of(int alphabetSize, int maxLength)
    {
        for (int length = 1; length <= maxLength; length++)
        {
            int[] pick = new int[length];
            while (true)
            {
                yield return (int[])pick.Clone();
                int position = length - 1;
                while (position >= 0 && ++pick[position] == alphabetSize)
                {
                    pick[position] = 0;
                    position--;
                }
                if (position < 0)
                {
                    break;
                }
            }
        }
    }

    public static long Count(int alphabetSize, int maxLength)
    {
        long total = 0;
        long ofLength = 1;
        for (int length = 1; length <= maxLength; length++)
        {
            ofLength *= alphabetSize;
            total += ofLength;
        }
        return total;
    }

    private const string RefusedPrefix = "throws:";

    /// A refusal is an outcome, not a failure of the harness: a business refusal by its code, a bad argument by its
    /// kind. Any other exception is a bug in the aggregate or the adapter and ends the run.
    public static string Capture(Func<string> move)
    {
        try
        {
            return move();
        }
        catch (BusinessException refused)
        {
            return $"{RefusedPrefix}{refused.Code}";
        }
        catch (ArgumentException)
        {
            return $"{RefusedPrefix}ArgumentException";
        }
    }

    public static string Doing(Action move) => Capture(() =>
    {
        move();
        return "ok";
    });

    /// "returned:true", "returned:null", "returned:Create": what a method answered, as text.
    public static string Returning<T>(Func<T> move) => Capture(() => $"returned:{Format(move())}");

    private static string Format(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        _ => value.ToString() ?? "null",
    };

    /// Replays every sequence on both sides in step and compares each step. A sequence is counted as a mismatch only
    /// when its last step is the first to differ: a longer sequence that repeats a shorter mismatch is the same finding.
    public static CharacterizationReport Compare<TOld, TNew, TView>(
        Side<TOld, TView> old, Side<TNew, TView> current, int maxLength, int keepMismatches = 5)
    {
        string[] oldNames = old.Moves.Select(move => move.Name).ToArray();
        string[] newNames = current.Moves.Select(move => move.Name).ToArray();
        if (!oldNames.SequenceEqual(newNames))
        {
            throw new ArgumentException($"The sides offer different moves: [{string.Join(", ", oldNames)}] and [{string.Join(", ", newNames)}].");
        }

        var report = new CharacterizationReport(oldNames.Length, maxLength, keepMismatches);
        var clock = Stopwatch.StartNew();
        foreach (int[] sequence in Of(oldNames.Length, maxLength))
        {
            Replay(old, current, sequence, oldNames, report);
        }
        report.Elapsed = clock.Elapsed;
        return report;
    }

    private static void Replay<TOld, TNew, TView>(
        Side<TOld, TView> old, Side<TNew, TView> current, int[] sequence, string[] names, CharacterizationReport report)
    {
        report.SequenceRun();
        TOld before = old.Start();
        TNew after = current.Start();
        for (int index = 0; index < sequence.Length; index++)
        {
            Move<TOld> oldMove = old.Moves[sequence[index]];
            Move<TNew> newMove = current.Moves[sequence[index]];
            string oldOutcome = oldMove.Apply(before);
            string newOutcome = newMove.Apply(after);
            bool isLastStep = index == sequence.Length - 1;
            bool aRefusalEndsTheRequest = oldOutcome.StartsWith(RefusedPrefix) || newOutcome.StartsWith(RefusedPrefix);
            if (aRefusalEndsTheRequest)
            {
                // A refused request saves nothing, so the state the aggregate was left in never exists: only the
                // refusal itself is compared and the sequence ends here.
                if (oldOutcome != newOutcome && isLastStep)
                {
                    report.Mismatch(sequence, names, oldOutcome, newOutcome);
                }
                return;
            }
            var seenBefore = new Step<TView>(oldMove.Name, oldOutcome, old.Project(before));
            var seenAfter = new Step<TView>(newMove.Name, newOutcome, current.Project(after));
            if (seenBefore == seenAfter)
            {
                continue;
            }
            if (isLastStep)
            {
                report.Mismatch(sequence, names, seenBefore, seenAfter);
            }
            return;
        }
    }
}

internal sealed class CharacterizationReport
{
    private readonly int _keep;
    private readonly List<string> _examples = [];

    public CharacterizationReport(int alphabetSize, int maxLength, int keep)
    {
        AlphabetSize = alphabetSize;
        MaxLength = maxLength;
        _keep = keep;
    }

    public int AlphabetSize { get; }
    public int MaxLength { get; }
    public long SequencesRun { get; private set; }
    public long Mismatches { get; private set; }
    public TimeSpan Elapsed { get; set; }

    /// The shortest mismatching sequences first, in the order the sequences are enumerated.
    public IReadOnlyList<string> FirstMismatches => _examples;

    public void SequenceRun() => SequencesRun++;

    public void Mismatch(int[] sequence, string[] names, object before, object after)
    {
        Mismatches++;
        if (_examples.Count < _keep)
        {
            _examples.Add($"{string.Join(" > ", sequence.Select(i => names[i]))}  (step {sequence.Length})\n    old: {before}\n    new: {after}");
        }
    }

    public string Summary =>
        $"{SequencesRun} sequences ({AlphabetSize} moves, up to {MaxLength} long), {Mismatches} mismatches, {Elapsed.TotalSeconds:F1}s";

    public override string ToString()
    {
        var text = new StringBuilder(Summary);
        foreach (string example in _examples)
        {
            text.Append("\n  ").Append(example);
        }
        return text.ToString();
    }

    public void AssertNoMismatches() => Assert.True(Mismatches == 0, ToString());
}
