namespace PoolWatch.AppTests;

/// <summary>
/// The tests that lay a window out and check where its controls landed run here, one class at a time, after the tests
/// that run in parallel. In a full parallel run such a check failed now and then (About, Settings, the profile save
/// dialog), with controls placed where their parent's size says they cannot be, or a window measured taller than it is;
/// run on their own, also beside a full run loading the PC, they never did. So it is another test in the same process,
/// not the PC's load: these windows are never shown, so no later layout pass corrects a measure taken while other
/// classes create, lay out and dispose windows and fonts at 100-200% on other threads. The app has one UI thread.
/// </summary>
[CollectionDefinition(nameof(WholeWindowLayout), DisableParallelization = true)]
public sealed class WholeWindowLayout;
