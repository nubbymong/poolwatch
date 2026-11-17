using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>The answers to the urgent clear's question after an upgrade (<see cref="UrgentQuestion"/>).</summary>
internal enum UrgentAnswer { TurnOn, LeaveOff, Later }

/// <summary>The urgent clear's question after an upgrade. Not built yet: it asks nothing and writes nothing.</summary>
internal sealed class UrgentQuestion(LiveSettings live, Action<string> log, Action<UrgentQuestionWindow>? show = null)
{
    public const string TurnOnLabel = "Turn on", LeaveOffLabel = "Leave off", LaterLabel = "Decide later";

    /// <summary>The window open now; null when none.</summary>
    public UrgentQuestionWindow? Open => null;

    /// <summary>At the tray app's start: asks nothing yet.</summary>
    public bool AtStart(bool setupCompleted, bool smoke)
    {
        _ = (live, log, show, setupCompleted, smoke);
        return false;
    }

    /// <summary>An answer: writes nothing yet.</summary>
    public void Answered(UrgentAnswer? answer) => _ = answer;

    /// <summary>The log's line for an answer: none yet.</summary>
    public static string LogLine(UrgentAnswer? answer) => "";

    /// <summary>Closes the window open now, as PoolWatch exits.</summary>
    public void Close() { }
}

/// <summary>The urgent clear's question's window. Not built yet.</summary>
internal sealed class UrgentQuestionWindow : Form
{
    public UrgentQuestionWindow(long alertBytes, Action<UrgentAnswer?> answered) => _ = (alertBytes, answered);

    /// <summary>The words shown (the tests).</summary>
    internal string Words => "";

    /// <summary>The buttons' words, left to right (the tests).</summary>
    internal IReadOnlyList<string> Buttons => [];

    /// <summary>The button of an answer (the tests).</summary>
    internal Button ButtonFor(UrgentAnswer answer) => throw new NotImplementedException(answer.ToString());

    /// <summary>An answer pressed.</summary>
    internal void Press(UrgentAnswer answer) => _ = answer;

    /// <summary>The window closed.</summary>
    internal void Dismissed() { }

    /// <summary>Whether it takes the focus as it shows (the tests).</summary>
    internal bool TakesFocusAsItShows => true;

    /// <summary>The extended window style it is made with (the tests).</summary>
    internal int ExtendedStyle => CreateParams.ExStyle;
}
