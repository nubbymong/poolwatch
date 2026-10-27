using PoolWatch.Core;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

public class IdentityTests
{
    private static int _pid = 100;
    private static ProcessRecord P(string image, string? path = null, string? cl = null, params string[] services)
    {
        var r = new ProcessRecord { Key = new ProcessKey(_pid++, 1), Image = image, ImagePath = path, CommandLine = cl };
        r.Services.AddRange(services);
        return r;
    }

    private static ProcessIdentity Id(params ProcessRecord[] chain) => ProcessIdentities.Of(chain);
    private const string Home = @"C:\Users\alice";

    [Fact]
    public void StreamDeckPluginNode()
    {
        var sd = P("StreamDeck.exe", @"C:\Program Files\Elgato\StreamDeck\StreamDeck.exe");
        var node = P("node.exe", Home + @"\AppData\Roaming\Elgato\StreamDeck\NodeJS\20.11.1\node.exe",
            $@"""{Home}\AppData\Roaming\Elgato\StreamDeck\NodeJS\20.11.1\node.exe"" ""{Home}\AppData\Roaming\Elgato\StreamDeck\Plugins\com.elgato.cpu.sdPlugin\bin\plugin.js"" -port 28196");
        var id = Id(P("explorer.exe"), sd, node);
        Assert.Equal("Stream Deck (Stream Deck plugin com.elgato.cpu)", id.Role);
        Assert.Equal("Stream Deck", id.Owner.Name);
        Assert.Equal("node.exe · Stream Deck (Stream Deck plugin com.elgato.cpu)", id.Display);
    }

    [Fact]
    public void CodexComputerUseNode()
    {
        var codex = P("codex.exe", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0\app\codex.exe");
        var node = P("node.exe", Home + @"\AppData\Local\OpenAI\Codex\runtimes\cua_node\node.exe", @"node.exe C:\x\cua\server.mjs --port 0");
        var id = Id(P("explorer.exe"), codex, node);
        Assert.Equal("Codex cua_node (server.mjs)", id.Role);
        Assert.Equal(OwnerKind.App, id.Owner.Kind);
        Assert.Equal("Codex", id.Owner.Name);
        Assert.Equal("node.exe · Codex cua_node (server.mjs)", id.Display);
    }

    [Fact]
    public void VitestUnderClaudeCodeSession()
    {
        var claude = P("claude.exe", Home + @"\.local\bin\claude.exe", "claude --resume");
        lock (claude.Context) claude.Context["cwd"] = @"D:\sample-app";
        var bash = P("bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe", "bash -c \"npx vitest run\"");
        var node = P("node.exe", @"C:\Program Files\nodejs\node.exe", @"node D:\sample-app\node_modules\vitest\vitest.mjs run");
        var worker = P("node.exe", @"C:\Program Files\nodejs\node.exe", @"node D:\sample-app\node_modules\tinypool\dist\entry\process.js");
        var id = Id(P("AI Code Conductor.exe"), P("conhost.exe"), claude, bash, node, worker);
        Assert.Equal("tinypool", id.Role);
        Assert.Equal(OwnerKind.ClaudeSession, id.Owner.Kind);
        Assert.Contains("sample-app", id.Owner.Name);
        Assert.Equal($"node.exe · tinypool under Claude Code session sample-app (PID {claude.Pid})", id.Display);
        Assert.Equal("vitest", Id(claude, bash, node).Role);
        Assert.Equal("-c npx", Id(claude, bash).Role);
        Assert.Null(Id(claude, bash, node).Role?.Contains("nodejs") == true ? "bundled" : null); // system node is not a bundle
    }

    [Fact]
    public void WmiLaunchedChain()
    {
        var wmi = P("WmiPrvSE.exe", @"C:\Windows\System32\wbem\WmiPrvSE.exe", null, []);
        var cmd = P("cmd.exe", @"C:\Windows\System32\cmd.exe", @"cmd.exe /c python C:\tools\collect.py");
        var py = P("python.exe", Home + @"\AppData\Local\Programs\Python\Python313\python.exe", @"python C:\tools\collect.py");
        var node = P("node.exe", @"C:\Program Files\nodejs\node.exe", "node probe.js");
        var id = Id(P("svchost.exe", null, null, "DcomLaunch"), wmi, cmd, py, node);
        Assert.True(id.ViaWmi);
        Assert.Equal(OwnerKind.ViaWmi, id.Owner.Kind);
        Assert.Equal("a WMI-launched cmd.exe", id.Owner.Name);
        Assert.Equal("node.exe · probe.js under a WMI-launched cmd.exe (launched via WMI)", id.Display);
        Assert.Equal("collect.py", Id(wmi, cmd, py).Role); // Python under AppData\Local\Programs\Python is a system install
    }

    [Fact]
    public void AiccPowershellAndServices()
    {
        var aicc = P("AI Code Conductor.exe");
        var ps = P("powershell.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"powershell -NoProfile -ExecutionPolicy Bypass -File C:\aicc\resume-picker.ps1");
        var id = Id(aicc, ps);
        Assert.Equal("resume-picker.ps1", id.Role);
        Assert.Equal(OwnerKind.Aicc, id.Owner.Kind);
        var svc = P("svchost.exe", @"C:\Windows\System32\svchost.exe", "svchost -k netsvcs", "Schedule");
        Assert.Equal(OwnerKind.Service, Id(P("services.exe"), svc).Owner.Kind);
        Assert.Equal("git status --porcelain".Split(' ')[1], ProcessIdentities.ArgumentRole("git.exe", @"git -C C:\x -c a=b status --porcelain"));
        Assert.Equal("console host", Id(P("Codex.exe"), P("conhost.exe")).Role);
    }

    [Theory]
    [InlineData(@"C:\Program Files\nodejs\node.exe", null)]
    [InlineData(@"C:\Users\alice\AppData\Local\Programs\Microsoft VS Code\Code.exe", "Microsoft VS Code")]
    [InlineData(@"C:\Program Files\Git\usr\bin\bash.exe", null)]
    [InlineData(@"C:\Users\alice\AppData\Local\OpenAI\Codex\runtimes\cua_node\node.exe", "Codex cua_node")]
    [InlineData(@"C:\Program Files\Docker\Docker\resources\com.docker.backend.exe", "Docker")]
    [InlineData(@"C:\Program Files\GitKraken\gitkraken.exe", "GitKraken")]
    [InlineData(@"C:\ProgramData\chocolatey\bin\node.exe", null)]
    [InlineData(@"C:\Users\alice\AppData\Local\Microsoft\WinGet\Packages\x\rg.exe", null)]
    [InlineData(@"C:\Users\alice\AppData\Local\Microsoft\WindowsApps\python.exe", null)]
    [InlineData(@"C:\Program Files\jdk-21\bin\java.exe", null)]
    [InlineData(@"C:\Users\alice\.nuget\packages\x\tool.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0\app\resources\node.exe", "OpenAI.Codex")]
    public void Bundles(string path, string? expected) => Assert.Equal(expected, ProcessIdentities.BundleOf(path));

    [Fact]
    public void AppIsNeverUnderItself()
    {
        var code = P("Code.exe", @"C:\Users\alice\AppData\Local\Programs\Microsoft VS Code\Code.exe");
        var id = Id(P("explorer.exe"), code);
        Assert.True(id.Owner.IsSelf);
        Assert.Equal("Code.exe", id.Display); // not "Code.exe under VS Code", and no bundle role for a non-runtime
        code.Label = "VS Code renderer";
        Assert.Equal("Code.exe · VS Code renderer", Id(P("explorer.exe"), code).Display);
        var node = P("node.exe", @"C:\Program Files\nodejs\node.exe", "node server.js");
        Assert.False(Id(code, node).Owner.IsSelf);
        Assert.Equal("node.exe · server.js under VS Code", Id(code, node).Display);
    }

    [Fact]
    public void BundleFromTheCommandLineWhenThePathIsUnknown()
    {
        var node = P("node.exe", null, @"""C:\Users\r\AppData\Local\Vendor\runtimes\cua_node\node.exe"" C:\x\server.cjs");
        Assert.Equal("node.exe · Vendor cua_node (server.cjs)", Id(P("explorer.exe"), node).Display);
        Assert.Null(ProcessIdentities.ExeFromCommandLine("node server.js"));
        var bare = P("node.exe", "node.exe", node.CommandLine); // a bare name where a path should be (seen from ETW on the runner)
        Assert.Equal("node.exe · Vendor cua_node (server.cjs)", Id(P("explorer.exe"), bare).Display);
    }

    [Fact]
    public void ArgumentRoleSkipsOptions()
    {
        Assert.Equal("-c git", ProcessIdentities.ArgumentRole("bash.exe", "bash -c -l \"git status\""));
        Assert.Equal("-c npx", ProcessIdentities.ArgumentRole("bash.exe", "bash -lc \"npx vitest\""));
        Assert.Equal("serve.py", ProcessIdentities.ArgumentRole("python.exe", "python -X utf8 -W ignore serve.py --port 1"));
        Assert.Equal("http.server", ProcessIdentities.ArgumentRole("python.exe", "python -X utf8 -m http.server"));
    }
}
