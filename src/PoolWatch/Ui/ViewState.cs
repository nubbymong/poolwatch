using System.Text.Json;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// Which view the main window opens in, kept in ui-state.json in the data root: the Simple view (the default) or the
/// Advanced view. The file is read once, as untrusted: a missing, oversize or malformed one, or any other value, means the
/// Simple view. It is written only when the owner switches, to a temporary file first; a write that fails is logged, and
/// the window keeps showing the view that was chosen.
/// </summary>
internal sealed class ViewState
{
    public const string FileName = "ui-state.json";
    private const int MaxBytes = 1024;
    private const string AdvancedValue = "advanced", SimpleValue = "simple";

    private readonly AppPaths _paths;
    private readonly Log _log;

    public ViewState(AppPaths paths, Log log)
    {
        _paths = paths;
        _log = log;
        Advanced = Read();
    }

    /// <summary>Always in the data root (never under --out): the view is the owner's choice on this PC, not one run's.</summary>
    private string StateFile => _paths.Guard.CheckReal(Path.Combine(_paths.DataRoot, FileName));

    /// <summary>The main window opens in the Advanced view.</summary>
    public bool Advanced { get; private set; }

    /// <summary>The owner switched views: kept for the next start (written only when it changed).</summary>
    public void Remember(bool advanced)
    {
        if (advanced == Advanced) return;
        Advanced = advanced;
        try
        {
            string file = StateFile;
            File.WriteAllText(_paths.Guard.CheckReal(file + ".tmp"), JsonSerializer.Serialize(new { view = advanced ? AdvancedValue : SimpleValue }));
            File.Move(_paths.Guard.CheckReal(file + ".tmp"), file, overwrite: true);
        }
        catch (Exception ex) { _log.Info($"{FileName} not written: {ex.Message}"); }
    }

    private bool Read()
    {
        try
        {
            var file = new FileInfo(StateFile);
            return file.Exists && file.Length <= MaxBytes && IsAdvanced(File.ReadAllText(file.FullName));
        }
        catch (Exception ex)
        {
            _log.Info($"{FileName} not read: {ex.Message}");
            return false;
        }
    }

    /// <summary>True only for a JSON object whose "view" is "advanced"; anything else is the Simple view.</summary>
    internal static bool IsAdvanced(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("view", out var view) &&
                   view.ValueKind == JsonValueKind.String && view.GetString() == AdvancedValue;
        }
        catch (JsonException) { return false; }
    }
}
