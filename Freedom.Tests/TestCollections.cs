using Xunit;

namespace Freedom.Tests;

/// <summary>
/// Tests that measure real time (click rates, frame budgets) run alone, after the parallel tests – otherwise
/// CPU-heavy tests (FFmpeg, LibreOffice, disk scans) running at the same time make them fail at random.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
