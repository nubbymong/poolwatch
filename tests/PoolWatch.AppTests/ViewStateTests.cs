using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>ui-state.json: the view the owner chose survives a restart; a missing or bad file means the Simple view.</summary>
public sealed class ViewStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-view-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Log _log;

    public ViewStateTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        _log = new Log(Path.Combine(_root, "data", "view.log"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string File_ => Path.Combine(_paths.DataRoot, ViewState.FileName);

    [Fact]
    public void TheChosenViewSurvivesARestart()
    {
        var state = new ViewState(_paths, _log);
        Assert.False(state.Advanced); // the Simple view by default
        state.Remember(false);
        Assert.False(File.Exists(File_)); // written only when it changed

        state.Remember(true);
        Assert.True(new ViewState(_paths, _log).Advanced);
        state.Remember(false);
        Assert.False(new ViewState(_paths, _log).Advanced);
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[\"advanced\"]")]
    [InlineData("{\"view\": \"Advanced\"}")]
    [InlineData("{\"view\": true}")]
    [InlineData("{\"mode\": \"advanced\"}")]
    public void ABadFileMeansTheSimpleView(string json)
    {
        File.WriteAllText(File_, json);
        Assert.False(new ViewState(_paths, _log).Advanced);
    }

    [Fact]
    public void AnOversizeFileIsNotRead()
    {
        File.WriteAllText(File_, "{\"view\": \"advanced\", \"pad\": \"" + new string('x', 2000) + "\"}");
        Assert.False(new ViewState(_paths, _log).Advanced);
        Assert.True(ViewState.IsAdvanced("{\"view\": \"advanced\"}"));
    }
}
