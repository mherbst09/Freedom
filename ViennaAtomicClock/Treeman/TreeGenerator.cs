namespace ViennaAtomicClock.Treeman;

/// <summary>
/// Produces the branch sequence of the tree. Fairness rules (all from <see cref="GenerationRules"/>):
/// a branch-free start, no long runs of branches on one side, never a left branch directly next to a right one,
/// and not too many empty segments in a row. The branch chance rises slowly with the difficulty level.
/// Because only the segment that slides down to the player is dangerous, every sequence leaves the other side
/// free – there is always a way out.
/// </summary>
public sealed class TreeGenerator
{
    private readonly GenerationRules _rules;
    private SeededRandom _random;
    private int _generated;
    private Side _previous = Side.None;
    private int _sameSideRun;
    private int _emptyRun;

    public TreeGenerator(GenerationRules rules, ulong seed)
    {
        _rules = rules;
        _random = new SeededRandom(seed);
    }

    public ulong Seed { get; private set; }

    public void Reset(ulong seed)
    {
        Seed = seed;
        _random = new SeededRandom(seed);
        _generated = 0;
        _previous = Side.None;
        _sameSideRun = 0;
        _emptyRun = 0;
    }

    /// <summary>Branch chance for a difficulty level (0 = start).</summary>
    public double BranchChance(int level) =>
        Math.Min(_rules.MaxBranchChance, _rules.StartBranchChance + level * _rules.BranchChanceIncreasePerLevel);

    /// <summary>The next segment from the bottom up.</summary>
    public Side Next(int level)
    {
        Side side;
        if (_generated < _rules.SafeStartSegments) side = Side.None;
        else
        {
            var branch = _emptyRun >= _rules.MaxEmptyInRow || _random.NextDouble() < BranchChance(level);
            side = branch ? (_random.NextDouble() < 0.5 ? Side.Left : Side.Right) : Side.None;
            if (side != Side.None)
            {
                // Too many on one side: switch sides – unless the switch would need a gap first.
                if (side == _previous && _sameSideRun >= _rules.MaxSameSideInRow) side = side.Opposite();
                if (_rules.RequireGapBetweenSides && _previous != Side.None && side != _previous) side = Side.None;
                if (side == _previous && _sameSideRun >= _rules.MaxSameSideInRow) side = Side.None;
            }
        }

        _generated++;
        if (side == Side.None)
        {
            _emptyRun++;
            _sameSideRun = 0;
        }
        else
        {
            _sameSideRun = side == _previous ? _sameSideRun + 1 : 1;
            _emptyRun = 0;
        }
        _previous = side;
        return side;
    }

    /// <summary>Checks a sequence (bottom first) against the rules – used by the tests and for debugging.</summary>
    public static bool IsFair(IReadOnlyList<Side> sequence, GenerationRules rules, out string? problem)
    {
        problem = null;
        var same = 0;
        var empty = 0;
        for (var i = 0; i < sequence.Count; i++)
        {
            var side = sequence[i];
            if (i < rules.SafeStartSegments && side != Side.None) { problem = $"branch in safe segment {i}"; return false; }
            if (side == Side.None)
            {
                empty++;
                same = 0;
                if (i >= rules.SafeStartSegments && empty > rules.MaxEmptyInRow + 1) { problem = $"{empty} empty in a row at {i}"; return false; }
                continue;
            }
            empty = 0;
            var previous = i > 0 ? sequence[i - 1] : Side.None;
            if (rules.RequireGapBetweenSides && previous != Side.None && previous != side) { problem = $"left/right without gap at {i}"; return false; }
            same = previous == side ? same + 1 : 1;
            if (same > rules.MaxSameSideInRow) { problem = $"{same} branches on one side at {i}"; return false; }
        }
        return true;
    }
}
