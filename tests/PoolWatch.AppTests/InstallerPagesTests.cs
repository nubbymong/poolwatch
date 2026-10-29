using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// The setup's own pages (installer/PoolWatch.iss): the words they show, the code that lays them out, and the images
/// they need. The Ready page's icons are unpacked when the wizard starts, and one that is missing is only logged (the
/// page shows without it), so these tests check that every icon the script can ask for is in installer/ready, at the
/// size its name says, for every display scale the script picks from. The pages' code first runs in CI's installer
/// test: its silent install still walks every page, and ci/installer-e2e.ps1 reads where each page's text ended from
/// the setup's log (<see cref="TheInstallerTestReadsTheLinesThePagesLog"/> holds its patterns to the script's lines).
/// </summary>
public sealed class InstallerPagesTests
{
    /// <summary>
    /// The height of Setup's font (9-point Segoe UI) at 100, 125, 150, 175 and 200% display scale. Setup scales by it,
    /// not by the display scale itself: ScaleX(N) is N times this height over 15, to the nearest pixel.
    /// </summary>
    private static readonly int[] FontHeights = [15, 20, 25, 30, 32];

    private static string RepoDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return dir.FullName;
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string Script() => File.ReadAllText(Path.Combine(RepoDir(), "installer", "PoolWatch.iss"));

    /// <summary>One section of the script, from its header to the next section's.</summary>
    private static string Section(string name)
    {
        string script = Script();
        var start = Regex.Match(script, $@"(?m)^\[{name}\]\r?$");
        Assert.True(start.Success, $"[{name}] is not in the script");
        var next = Regex.Match(script[(start.Index + start.Length)..], @"(?m)^\[[A-Za-z]+\]\r?$");
        return next.Success ? script.Substring(start.Index, start.Length + next.Index) : script[start.Index..];
    }

    private static string Entry(string section, string name) =>
        Regex.Match(Section(section), $@"(?m)^{name}=(.*?)\r?$").Groups[1].Value;

    /// <summary>One function or procedure of the script, up to the next one, with every run of white space made one space.</summary>
    private static string Body(string name)
    {
        string script = Script();
        var start = Regex.Match(script, $@"(?m)^(function|procedure) {name}\b");
        Assert.True(start.Success, name + " is not in the script");
        var next = Regex.Match(script[(start.Index + start.Length)..], @"(?m)^(function|procedure) ");
        return Regex.Replace(next.Success ? script.Substring(start.Index, start.Length + next.Index) : script[start.Index..], @"\s+", " ");
    }

    /// <summary>A number constant of the script's code.</summary>
    private static int Number(string constant) => int.Parse(Regex.Match(Script(), $@"(?m)^\s*{constant} = (\d+);").Groups[1].Value);

    /// <summary>A PNG's width and height, read from its header.</summary>
    private static (int Width, int Height) PngSize(string path)
    {
        byte[] head = new byte[24];
        using (var file = File.OpenRead(path)) file.ReadExactly(head);
        Assert.True(head.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), path + " is not a PNG");
        return (BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20)));
    }

    [Fact]
    public void ThePagesSayWhatTheApprovedDesignSays()
    {
        Assert.Equal("no", Entry("Setup", "DisableWelcomePage"));
        Assert.Equal("yes", Entry("Setup", "DisableReadyMemo"));
        Assert.Equal("Welcome to PoolWatch", Entry("Messages", "WelcomeLabel1"));
        Assert.Equal("PoolWatch watches the kernel pool, clears the standby cache before it slows you down, fixes known Windows " +
            "issues and, if you want, trims background work.", Entry("Messages", "WelcomeLabel2"));
        Assert.Equal("Setup installs PoolWatch {#AppVersion} and starts it. A short setup guide then asks how you want it to " +
            "look after this PC.", Entry("CustomMessages", "WelcomeSetup"));
        Assert.Equal("Click Next to continue.", Entry("Messages", "ClickNext"));
        Assert.Equal("Ready to install", Entry("Messages", "WizardReady"));
        Assert.Equal("PoolWatch is ready to install on this PC.", Entry("Messages", "ReadyLabel1"));
        Assert.Equal("Click Install to continue.", Entry("Messages", "ReadyLabel2a"));
        Assert.Equal("Click Install to continue.", Entry("Messages", "ReadyLabel2b"));
        Assert.Equal("Install", Entry("CustomMessages", "ReadyInstallTitle"));
        Assert.Equal("Into %1.", Entry("CustomMessages", "ReadyInstallText"));
        Assert.Equal("Start in the tray", Entry("CustomMessages", "ReadyTrayTitle"));
        Assert.Equal("Next to the clock, as administrator, so it can read the kernel pool.", Entry("CustomMessages", "ReadyTrayText"));
        Assert.Equal("Set up in about 3 minutes", Entry("CustomMessages", "ReadySetupTitle"));
        Assert.Equal("Choose what PoolWatch looks after on this PC.", Entry("CustomMessages", "ReadySetupText"));
        Assert.Equal("You stay in control", Entry("CustomMessages", "ReadySafeTitle"));
        Assert.Equal("Nothing changes without your OK. Anything PoolWatch changes can be put back, and uninstalling puts " +
            "everything back.", Entry("CustomMessages", "ReadySafeText"));
        Assert.Equal("PoolWatch is installed", Entry("Messages", "FinishedHeadingLabel"));
        // LayOutFinishedPage always shows FinishedLabel, where Inno Setup picks one of the two: they must say the same.
        const string finished = "PoolWatch lives in the tray, next to the clock. Its setup guide opens first and takes about 3 minutes.";
        Assert.Equal(finished, Entry("Messages", "FinishedLabel"));
        Assert.Equal(finished, Entry("Messages", "FinishedLabelNoIcons"));
        // The tick box under it, ticked: a postinstall entry without "unchecked".
        Assert.Matches(@"(?m)^Filename: ""\{app\}\\PoolWatch\.exe""; Description: ""Start PoolWatch now""; Flags: postinstall (?!.*unchecked)", Section("Run"));
        // The first step names the folder Setup really installs into, and the Finished page drops "Click Finish to exit Setup.".
        Assert.Contains("StepTexts[0].Caption := FmtMessage(CustomMessage('ReadyInstallText'), [WizardDirValue]);", Body("LayOutReadyPage"));
        Assert.Contains("WizardForm.FinishedLabel.Caption := SetupMessage(msgFinishedLabel) + #13#10;", Body("LayOutFinishedPage"));
    }

    [Fact]
    public void TheWizardLaysOutItsPagesWhenTheyShow()
    {
        string init = Body("InitializeWizard"), changed = Body("CurPageChanged");
        Assert.Contains("LayOutWelcomePage;", init);
        Assert.Contains("CreateReadyPage;", init);
        Assert.Contains("wpReady: LayOutReadyPage;", changed);
        Assert.Contains("wpFinished: LayOutFinishedPage;", changed);
    }

    [Fact]
    public void ThePanelsWordsAreInFixedColoursOnItsFixedBackground()
    {
        // The panel's background is a fixed light green, so its words must not take the page's text colour, which a
        // high-contrast theme can make white: the title in green, the sentence in the design's ink #1F2328.
        Assert.Equal(0x28231F, Convert.ToInt32(Regex.Match(Script(), @"(?m)^\s*InkColor = \$([0-9A-F]{6});").Groups[1].Value, 16));
        Assert.Equal(0xF0F7EE, Convert.ToInt32(Regex.Match(Script(), @"(?m)^\s*SafeBackColor = \$([0-9A-F]{6});").Groups[1].Value, 16));
        string ready = Body("CreateReadyPage");
        var onPanel = Regex.Matches(ready, @"NewLabel\(SafePanel, [^;]*\);").Select(m => m.Value).ToList();
        Assert.Equal(2, onPanel.Count);
        Assert.EndsWith("CustomMessage('ReadySafeTitle'), True, SafeColor);", onPanel[0]);
        Assert.EndsWith("CustomMessage('ReadySafeText'), False, InkColor);", onPanel[1]);
        Assert.Contains("SafePanel.Color := SafeBackColor;", ready);
    }

    [Fact]
    public void EveryIconTheReadyPageCanAskForIsThereAtItsSize()
    {
        string script = Script();
        // The icons the script asks for: the steps' (NewStep's name, at StepIconSize) and the others' (NewIcon's name and size).
        var asked = Regex.Matches(script, @"NewStep\(\d, StepWidth, '(\w+)'").Select(m => (Name: m.Groups[1].Value, Base: Number("StepIconSize")))
            .Concat(Regex.Matches(script, @"NewIcon\([^;']*'(\w+)', (\w+IconSize)").Select(m => (Name: m.Groups[1].Value, Base: Number(m.Groups[2].Value))))
            .Distinct().OrderBy(i => i.Name).ToList();
        Assert.Equal(new[] { "install", "next", "safe", "setup", "tray" }, asked.Select(i => i.Name));

        // The sizes the script picks from (StoredIconSize): Base scaled as Setup scales it at 100 to 200% (Base times the
        // font's height over 15, to the nearest pixel); and the file names it asks for (NewIcon).
        Assert.Equal(FontHeights[0], Number("FontHeightAt100"));
        Assert.Contains("case Level of 0: Result := FontHeightAt100; 1: Result := 20; 2: Result := 25; 3: Result := 30; else Result := 32; end;",
            Body("StoredFontHeight"));
        Assert.Contains("while (Result < Size) and (Level < 4) do begin Level := Level + 1; " +
            "Result := (2 * Base * StoredFontHeight(Level) + FontHeightAt100) div (2 * FontHeightAt100); end;", Body("StoredIconSize"));
        Assert.Contains("Size := ScaleX(Base); Stored := StoredIconSize(Base, Size); FileName := Format('ready-%s-%d.png', [Name, Stored]);",
            Body("NewIcon"));

        string folder = Path.Combine(RepoDir(), "installer", "ready");
        var expected = new List<string>();
        foreach (var (name, size) in asked)
            foreach (int height in FontHeights)
            {
                int pixels = (int)Math.Round(size * height / 15.0, MidpointRounding.AwayFromZero); // ScaleX's MulDiv
                string file = $"ready-{name}-{pixels}.png";
                expected.Add(file);
                Assert.True(File.Exists(Path.Combine(folder, file)), file + " is missing from installer/ready");
                Assert.Equal((pixels, pixels), PngSize(Path.Combine(folder, file)));
            }
        // Nothing else there: every file in the folder is packed into the setup.
        Assert.Equal(expected.Order(), Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).Order());
    }

    [Fact]
    public void TheIconsAreUnpackedFirstAndCanNeverStopTheSetup()
    {
        // First in [Files], and never installed: with solid compression, unpacking one file means unpacking every file
        // above it first.
        string firstSource = Regex.Match(Section("Files"), @"(?m)^Source: .*$").Value.TrimEnd('\r');
        Assert.Equal(@"Source: ""ready\*.png""; Flags: dontcopy", firstSource);
        // Unpacked and loaded only inside NewIcon's try: an icon that cannot be shown is logged and left out.
        string code = Regex.Replace(Script()[Script().IndexOf("[Code]", StringComparison.Ordinal)..], @"\s+", " ");
        Assert.Single(Regex.Matches(code, @"\bExtractTemporaryFile\("));
        Assert.Contains("try if not FileExists(Path) then ExtractTemporaryFile(FileName); Result.PngImage.LoadFromFile(Path); " +
            "except Log('The Ready page''s icon ' + FileName + ' could not be shown: ' + GetExceptionMessage); end;", Body("NewIcon"));
    }

    [Fact]
    public void TheInstallerTestReadsTheLinesThePagesLog()
    {
        // ci/installer-e2e.ps1 finds the pages' log lines by these patterns; each must match the line the script writes.
        string e2e = File.ReadAllText(Path.Combine(RepoDir(), "ci", "installer-e2e.ps1"));
        foreach (var (variable, routine, fields) in new[] { ("welcomeLine", "LayOutWelcomePage", 3), ("readyLine", "LayOutReadyPage", 7) })
        {
            var pattern = Regex.Match(e2e, $@"(?m)^\${variable} = '([^']+)'\r?$");
            Assert.True(pattern.Success, $"${variable} is not in ci/installer-e2e.ps1");
            var format = Regex.Match(Body(routine), @"Log\(Format\('([^']+)'");
            Assert.True(format.Success, routine + " logs no line");
            int n = 0;
            string written = Regex.Replace(format.Groups[1].Value, "%d", _ => (100 + n++).ToString());
            Assert.Equal(fields, n);
            var read = Regex.Match(written, pattern.Groups[1].Value);
            Assert.True(read.Success, $"${variable} does not match what {routine} logs: {written}");
            Assert.Equal(fields + 1, read.Groups.Count);
            Assert.Equal(Enumerable.Range(100, fields).Select(i => i.ToString()), Enumerable.Range(1, fields).Select(i => read.Groups[i].Value));
        }
        // And it is the icons' own log line that it looks for.
        Assert.Contains("'The Ready page''s icon ' + FileName + ' could not be shown: '", Body("NewIcon"));
        Assert.Contains(@"-notmatch ""The Ready page's icon \S+ could not be shown""", e2e);
    }

    [Fact]
    public void TheSideAndCornerImagesAreThereAtTheSizesTheirNamesSay()
    {
        foreach (string directive in new[] { "WizardImageFile", "WizardSmallImageFile" })
        {
            string[] files = Entry("Setup", directive).Split(',');
            Assert.NotEmpty(files);
            foreach (string file in files)
            {
                var size = Regex.Match(file, @"-(\d+)x(\d+)\.png$");
                Assert.True(size.Success, file);
                Assert.Equal((int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value)), PngSize(Path.Combine(RepoDir(), "installer", file)));
            }
        }
    }
}
