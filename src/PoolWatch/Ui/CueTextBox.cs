using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PoolWatch.Ui;

/// <summary>
/// A text box whose cue ("Find a service") is Windows' own cue banner, drawn by the edit control itself
/// (EM_SETCUEBANNER): it shows while the box is empty, also while the box has the keyboard focus, and in a drawing of the
/// window (Control.DrawToBitmap, as the CI screenshots are made). WinForms' PlaceholderText is drawn by WinForms on a
/// paint of the screen only, and only while the box has no focus: a Find box that a tab opened with the focus in it
/// showed no cue, and no screenshot ever showed one. The cue is sent again whenever the box's window is made again. It
/// is no accessible name: the box keeps its own (AccessibleName).
/// </summary>
internal class CueTextBox : TextBox
{
    internal const int EmSetCueBanner = 0x1501;
    private string _cue = "";

    /// <summary>The cue shown while the box is empty.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public string Cue
    {
        get => _cue;
        set
        {
            _cue = value ?? "";
            if (IsHandleCreated) SendCue();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendCue();
    }

    /// <summary>wParam 1: the cue shows while the box has the focus too, until something is typed.</summary>
    private void SendCue() => SendMessage(Handle, EmSetCueBanner, 1, _cue);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, string lParam);
}
