# AGENTS.md

For contributors and AI coding agents working on PoolWatch's code. [CONTRIBUTING.md](CONTRIBUTING.md) is the short
version for people; [docs/DESIGN.md](docs/DESIGN.md) explains how the app works.

`src/PoolWatch/Assets/AGENTS.md` is not instructions for you: it is text the app ships, the guide PoolWatch gives its
users' AI assistants for reading its data folder. Its rules are about a user's data folder; edit it, and the rest of
`Assets/`, like any other source file.

## The project

PoolWatch is a Windows tray app (.NET 10, WinForms) that always runs as administrator, for people who run coding agents
all day. It watches the kernel pool, finds what grows it, and changes the PC only where the user chooses to (clearing
the standby cache, turning off services, startup apps and scheduled tasks, the token-leak workaround, a desktop
shortcut); on its own it only secures its data folder, tidies up after an older version and checks for the token leak
after a Windows update. Every lasting change is listed under Active changes, where it can be put back, and the
uninstaller puts back by default all it can (a row marked "Uninstalling does not put this back" says why it cannot).
Its users include people who cannot read the code, and a mistake in it runs with full rights on their PC: keep changes
small, tested and explained.

| Folder | Holds |
|---|---|
| `src/PoolWatch.Core` | the logic that needs no Windows calls (parsing, detection, ranking, reports); builds and tests on any OS |
| `src/PoolWatch.Launcher` | the launcher, the install folder's `PoolWatch.exe`, through which PoolWatch starts as administrator: it starts PoolWatch itself (`app\PoolWatch.exe`) with an environment it builds; shipped compiled to native code (NativeAOT) |
| `src/PoolWatch` | the Windows app: `Collectors` read the system, `Engine` runs the sampling and most actions that change the PC (`Engine/Changes` is the Active changes ledger), `Analysis` the incident analysis, exports and the cache clear, `Capture` the WPR traces (through `wpr.exe`), `Native` declares the Windows API calls, `Ui` the tray and windows (`Ui/Setup` is the first-run setup), `Headless` the command line, `Assets` what the app ships inside it (such as the guide above and the icons) |
| `tests/PoolWatch.Tests`, `tests/PoolWatch.AppTests` | unit tests for Core (any OS) and for the app (Windows, with fakes) |
| `knowledge` | the known issues PoolWatch recognises, as data ([docs/knowledge-packs.md](docs/knowledge-packs.md)) |
| `installer` | the Inno Setup script and what it installs |
| `ci`, `tools/Leaker` | scripts for the CI runners, and the leak simulator they drive |

## Build and test

Build the whole solution, then what the `build` job in `.github/workflows/ci.yml` runs (the two test projects and the
three publishes the release is made from). The other jobs (`e2e`, `diag`, `ui`, `installer`, `arm64`) start from that
job's build; `linux-unit-tests` and `arm64-unit-tests` run the Core tests on their own runners; `soak` and `overhead`
run on demand and on a release tag; `sign` and `publish` run only on a `v*` tag.

```
dotnet build PoolWatch.slnx -c Release            # must report 0 warnings
dotnet test tests/PoolWatch.Tests -c Release
dotnet test tests/PoolWatch.AppTests -c Release   # Windows only
dotnet publish src/PoolWatch/PoolWatch.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o publish/fd/app
dotnet publish src/PoolWatch/PoolWatch.csproj -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/sc/app
dotnet publish src/PoolWatch.Launcher/PoolWatch.Launcher.csproj -c Release -r win-x64 -p:PublishAot=true -p:RestoreLockedMode=false -o publish/launcher
```

- Each build is laid out as it is installed: PoolWatch itself in `app\`, and the launcher, which the native publish
  names `PoolWatch.exe`, above it (CI copies it into each build).

- The launcher's native publish needs the C++ build tools (CI's Windows runners have them) and restores without the
  lock file: the native compiler comes as packages at the SDK's own known versions (as the self-contained build's runtime
  packs do), which no lock file records; on Windows NuGet checks each package's signature as it restores. Its unit tests
  (in `tests/PoolWatch.AppTests`) use its normal build, which needs neither.
- Nothing in the build turns a warning into an error, so read the build's summary: a change that adds a warning is
  not done.
- `tests/PoolWatch.Tests` also runs on Linux and natively on Windows on Arm; `tests/PoolWatch.AppTests` runs on Windows
  only (on Arm, as the x64 build under emulation). The Linux job also compiles the Windows app.
- Every change comes with a test that fails without it. Tests never change the real PC: each system change goes
  through an interface with a fake (`ILogonTasks`, `IServiceBackend`, `IRootOps` and `IClearTaskStore` show the
  pattern), file tests stay in their own TEMP folder, and no test reads or changes a real ACL: a test that needs a
  security descriptor builds it in memory from SDDL (as `AdminFolderRulesTests` does) and never applies it.
- Tests never show a window or a dialog on the desktop: a test builds a form or control without showing it, never
  calls `Show`, `ShowDialog` or `MessageBox.Show`, and answers the app's questions through an interface (as
  `IProfileDialogs` and the AI agent windows' `IAgentFiles` do). `tests/PoolWatch.AppTests/NoDialogs.cs` makes an
  exception inside a window throw in the test instead of opening WinForms' error dialog; keep it. A test run that leaves
  a window open is a failing run.
- A test that lays out a window and checks the result belongs to the `WholeWindowLayout` collection
  (`tests/PoolWatch.AppTests/WholeWindowLayout.cs`), which runs on its own, not beside other tests: never-shown windows
  laid out in parallel gave false layout failures. Check the controls of a never-shown window with
  `WindowFitTests.OwnVisible`, or every control reads as hidden and the check finds nothing.
- Package versions are locked (`packages.lock.json`; CI restores in locked mode). Agree a new NuGet package in an issue
  first; after changing a reference, run `dotnet restore --force-evaluate` on Windows and commit the lock files
  (elsewhere the Windows app's lock files come out different, and CI's locked restore fails). A package the app or the
  launcher ships also needs its row in THIRD-PARTY-NOTICES.md at the new version, and its licence checked again
  (`ThirdPartyNoticesTests` holds the table to the lock files).
- Do not run `ci/*.ps1`, `tools/Leaker` or the installer on your own PC: they change services, scheduled tasks and the
  standby cache, and Leaker grows the kernel pool and loads the CPU on purpose. CI runs them on throwaway runners; read
  the run's artifacts (logs, reports, screenshots) instead. An AI agent does not start PoolWatch itself unless its
  user asks. An elevated build runs only from Program Files; `--allow-any-location` is for CI's runners only, and
  `--ui-smoke` (the CI UI test) skips that check too.

## Code that runs elevated

The attacker that matters is a standard user, or malware running as the user, trying to make PoolWatch run their code
or change the wrong thing ([SECURITY.md](SECURITY.md), "Security model").

- Write and delete only through the write policy, never through a link: `PathGuard.CheckReal` in the data folder (or a
  headless `--out` folder), and anywhere else a link check of its own: `NoFollow`, checking the open handle's final
  path (the desktop shortcut, a prompt saved from an AI-agent window), or `PathGuard.FirstLinkBelow` (an older
  version's helper files in Program Files).
- Start programs by full path with `UseShellExecute = false`, never by a bare name or a shell association. Open web
  pages only through `UiActions.OpenWebPage`, which accepts only addresses PoolWatch knows or built itself
  (`Credits.IsAllowedLink`: the credits, `Credits.ReferencePages` matched whole, a release page, the advisory page, a
  new-issue address it built).
- Treat anything a standard user can write as hostile: read it bounded, skip links, never load code from it. Check a
  stored record (`applied-changes.json`, `service-changes.json`) against the live system again right before acting on
  it.
- A new change to the PC needs the user's explicit action (a click, or a setting the user turned on, such as a service
  profile applied at start) and a row in SECURITY.md's table (the few already there that happen on their own, such as
  securing the data folder, say so in its table). If it lasts, it is recorded so Active changes can put it back (and
  the uninstaller with it), and it is recorded before it is made.
- No network connections except the daily update check (`src/PoolWatch/Engine/UpdateCheck.cs`, on by default and easy
  to turn off). Nothing about the PC is sent anywhere. A new connection, or a change to this one, needs the security
  review below.
- Stay light: under 0.5% of one core and a working set under 100 MB on a busy PC (the CI soak and overhead checks
  enforce it). Nothing new on the sampling path. A new window or dialog is created when opened and disposed when
  closed (the main window only hides, and stops refreshing while hidden); a new scan runs on demand or on a slow timer,
  never on every sample.

### Security-sensitive code, and what each part must keep

| Area | Where | What a change must keep |
|---|---|---|
| Active changes and the change ledger | `src/PoolWatch/Engine/Changes/` (`ChangeLedger`, `ChangeRecords`, `ChangeSources`, `ActiveChange`), `ServicesController.cs`, `src/PoolWatch/Ui/ActiveChangesForm.cs` | Every record is read as untrusted, bounded and validated, and checked against the live system right before a put-back or the uninstaller acts on it. A switch is recorded before it is made, nothing is switched when the record cannot be saved, a failed switch takes its record back, and switches run one at a time. Put all back puts back only what the window showed, as it showed it. The data file keeps its name, `applied-changes.json`, so an installed copy's records stay readable. |
| Putting back a clear task of the user's own | `src/PoolWatch/Engine/Changes/ClearTaskReplacer.cs`, `src/PoolWatch/Engine/CacheHelper.cs` | Only RAMMap memory-clear actions by full path from one folder, plain text in every checked part, no control character in a path or an option, the Task Scheduler root, no task of that name, never one that needs a saved password, registered with the account it ran as. A RAMMap a standard user could change (or one that is missing, a link, or whose rules cannot be read) needs the user's yes to the warning shown, checked on the record that is registered; the uninstaller never gives one. |
| Where PoolWatch may run from | `src/PoolWatch/Engine/InstallGuard.cs`, `src/PoolWatch/Engine/AdminFolderRules.cs` (also compiled into the launcher), `src/PoolWatch/Engine/AlwaysElevated.cs`; `LogonTaskPolicy.cs` | Under Program Files, and every folder from the program's up to Program Files, the rules for files in its own folder and the own rules of the program, of every other file and folder in its own folder (`ChangeableInItsFolder`), of its launcher (`WhyNotStartFrom`: each launcher name that is there, and the usual one when none is) and of every other file below the program's folder with the folders it is in (`FilesBelow`: its DLLs in `app\amd64\`; a folder whose files cannot be listed fails closed) must be ones no one but an administrator can change (every account and group counts, every user's own SID included, except SYSTEM, Administrators, TrustedInstaller, "Local account and member of Administrators group", and the members of Administrators, accounts or groups, whose every program runs as administrator by the PC's UAC settings, `AlwaysElevated`; the launcher counts only its own account, when every program of it runs as administrator: its token holds Administrators unsplit and is a sign-in in person, or, for a batch, service or network sign-in, UAC's settings say so, `ElevatedAccounts.OfThisProcess`; SECURITY.md). A folder link on the way is judged where it really leads, up to Program Files. Rules that cannot be read, a missing program, a program or anything in its folder that is a link, and a folder link that leads outside Program Files fail closed. Rules are only ever read. Only `--ui-smoke`, the token-leak test's helper and `--allow-any-location` skip the check (`InstallGuard.Applies`): another needs this review. The logon task is trusted only when it is exactly the task PoolWatch makes (`LogonTaskPolicy.Judge`: one action, this PoolWatch's launcher by its literal path, `--tray`, no start folder, this account, interactive, highest privileges). |
| Starting as administrator | `src/PoolWatch.Launcher/` (the launcher: `EnvironmentBlock`, `CommandLine`, `WindowsPaths`, `Native`, `Program.WhyNotStart`, its `/DEPENDENTLOADFLAG` link flag), `src/PoolWatch/Engine/LaunchGate.cs` (the layout, and its check first in `Program.Main`), both `app.manifest` files, `StartupHookSupport` in `src/PoolWatch/PoolWatch.csproj`, `LogonTask` in `src/PoolWatch/Ui/TrayIcons.cs`, `LogonTaskCheck` and `LogonTaskPolicy.Judge` in `LogonTaskPolicy.cs`, `UserEnvironment.cs`, the layout, the starts and `StopBeforeLauncher` in `installer/PoolWatch.iss`, the launcher's publish and link-map steps in `.github/workflows/ci.yml` | The install folder's `PoolWatch.exe` is the launcher (both zips also carry it as `PoolWatch-selfcontained.exe`, and the setup puts it there where that file is) and PoolWatch itself is `app\PoolWatch.exe`; a file under either name counts as the launcher only with `app\PoolWatch.exe`'s product version (`LaunchGate.Judge`); only the launcher asks for administrator rights (the program's manifest stays `asInvoker`), and it stays native code (NativeAOT: no .NET runtime, no start-up hooks, no loadable GC, no diagnostic server) that loads its DLLs from System32 only. It finds its own path through the kernel, and starts PoolWatch only from a folder in Program Files, only once it has administrator rights itself, and only when neither it nor the program nor their folders can be changed by anyone who is not an administrator (the program's own rules, `AdminFolderRules.cs`, a folder link followed). It passes on none of its own environment's variables: only its allowlist, each value from Windows' own folders, the machine's registry or the profile's folders (the profile list's profile folder and the account's own AppData settings, each read as written with only Windows' own values filled in, never through a call that fills a variable in from its environment), a Path of Windows' own folders, never a name with one of .NET's start-up prefixes, and the arguments exactly as given (`CommandLine`, round-trip tested). The starts PoolWatch and its setup make to run PoolWatch as administrator (the logon task, the Start menu entry, the setup's and the uninstaller's steps) go through it, and the setup never starts an older PoolWatch to stop it. The one start PoolWatch makes of itself, the token-leak test's helper (`app\PoolWatch.exe --token-leak-test`), does not: it inherits the clean environment of the PoolWatch that starts it. `app\PoolWatch.exe` started any other way hands its start over before it does any work, except with the switches that skip the location check (`--allow-any-location`, `--ui-smoke`, `--token-leak-test`), which run as they are once elevated. The logon task check changes a task only on the owner's answer, and a headless run never changes one. What a program of the account will see (a task's command line, its TEMP) is read from the account's environment as data (`UserEnvironment`), never applied to PoolWatch. |
| The update check | `src/PoolWatch/Engine/UpdateCheck.cs`, `src/PoolWatch.Core/Updates/Releases.cs` | One GET to `https://api.github.com/repositories/1435312349/releases/latest`, the repository by its number (it survives a rename; a name can be taken over), no credentials, cookies or redirects, a bounded answer, stable releases only, a page under the repository's own releases. It never downloads or runs anything, and never shows or logs a release's name or notes. |
| Report an issue and exports | `src/PoolWatch.Core/Report/IssueReport.cs`, `src/PoolWatch.Core/Incidents/Exporter.cs` (`Redactor`) | An issue never carries a command line, an evidence line, another app's path, a window title, a session, project or account name, the services list or the environment, and its address holds only the title, version and Windows. Export redaction stays on by default. A new field in either needs a redaction test. Transcripts are read on the PC only and never sent. |
| Native calls | `src/PoolWatch/Native/` | Declared signatures match the Windows headers; buffers are bounded. |
| The installer and the uninstall clean-up | `installer/`, `--stop-running`, `--uninstall-cleanup`, `--changes-dump` | Installs to `%ProgramFiles%\PoolWatch` only; stops PoolWatch through its exit signal, never a kill (only a token-leak test helper left running from its exe is ended: by PoolWatch through a handle checked against that exe, and by the script only when nothing else of PoolWatch runs, through a handle opened before the process is read again and checked), and closes no other program (Inno Setup's own close, `CloseApplications`, stays off, and its filter matches no file of the setup's); looks at no named object (PoolWatch's are in its private namespace, `src/PoolWatch/Engine/AdminNamespace.cs`); removes no file after a cleanup that failed (any exit but 0, 8 or 12); replaces or removes no file in place while it is in use, asked of each file itself (the setup's `CanReplace`): such a file is moved into place, or deleted, at the next restart through Inno Setup's `RestartReplace`, never its restart flag, and a setup run while such work is waiting decides so before Inno Setup's own check for an unfinished install looks at Windows' list (`ProgramDir`), so the setup and the uninstaller never restart the PC or ask for a restart (`RestartIfNeededByRun=no`); decides by a file in use only once it has stayed in use a few seconds (`FreeSoon`, `WhenToReplace`); sets every old file aside before writing any new one and puts them all back when it does not finish, with the Start menu entry, the uninstaller's program it replaced and the Apps and Features entry (`SetAllAside`, `DeinitializeSetup`), puts an old file another program holds back as a copy or under a second name of the same file (`PutBack`, `CreateHardLinkW`), and when a file this version has cannot be taken back (`TakeBack`), or an old one cannot be put back even so, every old file not yet back goes back at the next restart, the launcher's files last (`PutAllBack`), so until that restart the install folder holds no old file beside a new one, and no file is lost (an old file Windows will not take for the restart stays set aside, and a new one it will not take to delete stays after it too; when that new one stays under its own name, the launcher's files stay set aside as well, and every old file does when it is at one of the launcher's names, so nothing starts from the mix: `Refused`, `LeftNew`, `LauncherLeftNew`; the setup says so); this version's uninstaller removes nothing until then (`RunCleanup`) unless Windows refused some of that work (the setup then says to run it again before uninstalling), an uninstaller made before the launcher has its record, or else its messages file, or else its program, set aside until then when the setup can, and when it cannot (another program keeps all three from that, or Windows refuses) the setup says not to uninstall before the restart and what that uninstaller would do (`HoldOldUninstaller`); what the setup says starts PoolWatch until the restart is read from the install folder (`UntilRestart`); when a setup that finishes at the restart cannot undo a new file's move, it reads Windows' list for what that move replaces (`ReadRestartWork`, `FilledAtRestart`), keeps that file (a copy, a second name, or, failing both, the file renamed aside now, unless another program keeps that from being done too: `Unstage`) and leaves no file that starts PoolWatch at the launcher's names after the restart unless Windows refuses that too (`KeepLauncherOut`, `SetAsideAtRestart`); starts PoolWatch only through a launcher of its program's product version (`LauncherPath`); writes the uninstaller in a folder of its own when one of the install folder's is in use at its look, so that look stops the setup only for `/DIR`; names every file in use and what has it open on its last page and in its log, which it always writes (`SetupLogging`), and the uninstaller in its log when it is started with `/LOG`, as Apps and Features starts it (`UninstallLogging`); puts back every change by default (it asks only when started by hand, putting back being the default, and a question it cannot show means putting back); reads PoolWatch's list of changes only when PoolWatch wrote it (exit 0), and otherwise touches nothing in the data folder; deletes the data folder only when asked and every change was put back. `UninstallerTests` reads the uninstaller's named functions. |
| First-run setup's changes | `src/PoolWatch/Ui/Setup/SetupSession.cs` (`StandbyCacheSetup`, `ApplyCache`, `ApplyFixes`, `NextHeldFor`), `WindowsSetupSystem` in `src/PoolWatch/Ui/SetupContext.cs`, `FirstRunGate` in `src/PoolWatch/Ui/TrayApp.cs` | A change runs only on Next of the step that shows it, as the step shows it: the cache rule on the Standby cache step, the switches and Yes/No on Fixes. Your RAMMap task is replaced only when the step shows the note that says so, decided from the same scan. A Fixes row that appears while the step shows is not applied by a Next pressed as it arrives (Next waits `NewRowWait` first). The scan setup waits for is bounded (a minute), so a hung read never keeps the first run, which is there to be finished (closing it asks first), from finishing. |
| The AI agent's prompt and answer | `src/PoolWatch.Core/Services/Agent/` (`AgentItems`, `AgentPrompt`, `AgentAnswer`), `AgentFileIo` in `src/PoolWatch/Ui/AgentFiles.cs`, `AgentApply` in `src/PoolWatch/Ui/AgentDecisions.cs`, `AgentReviewForm.Apply` in `src/PoolWatch/Ui/AgentReviewWindow.cs`, `ServicesController.RefusedAlone` | Nothing changes without the user's Apply in the review, and only items the answer matched in the list read again from the live PC; each change goes through the paths that record it first (the Services tab's turn-off, `ChangeRecords.Toggle` for startup apps and tasks); services of other apps PoolWatch does not recognise get a question that names them (up to 15, counting the rest). The prompt is written, and the answer read, only where the user picked, never through a link; the answer is read bounded (`AgentAnswer.MaxBytes`) and strictly, never throws, and its words are shown only as plain text (a Label, never a link). The prompt's redaction gives up rather than let a word through unredacted. |
| Ending a program | `TaskActions.End` and `TaskActions.RunEnd` in `src/PoolWatch/Ui/TaskActions.cs`, wired by `TasksTab.EndSelected`; `EndProcessPolicy` in `src/PoolWatch.Core/Services/EntryCost.cs`; `src/PoolWatch/Engine/ProcessEnder.cs` | Only processes matched by their own full path; nothing of it is ended when any process is one PoolWatch never ends (Windows' own, security, a service host); one question that names it; each process ended through a handle checked against its start time. Microsoft's own startup apps and tasks are never switched (`TaskActions.Toggle`). |
| The token-leak watch | `src/PoolWatch.Core/Recommendations/TokenLeakWatch.cs`, `RecommendationActions.WatchTokenLeak`, `TrayApp.WatchTokenLeak` | It starts only the check-only test (never with the guard on, and once per Windows build per session), never turns the guard on, and its notice only opens the guard's offer. |
| The release path | `.github/workflows/ci.yml` (the `build` steps that make the exe, then `sign` and `publish`) and any workflow's triggers or permissions | No job a pull request can reach holds a secret or a write token; actions stay pinned to commits. |
| A service profile applied at start (a change with no click when it runs) | `src/PoolWatch/Engine/ProfileAtStart.cs` (`ForApp`, called from `Ui/UiRunner.cs`), `ServicesController.ApplyProfile` and `OneChange` in `src/PoolWatch/Engine/ServicesController.cs`, `ProfileAtStart.Narrowed`, `src/PoolWatch.Core/Services/ServiceProfiles.cs` (reading profiles from `settings.json`), `src/PoolWatch/Engine/ProfileStore.cs`, `src/PoolWatch/Ui/ProfileDialogs.cs` (the save dialog's box, the consent), `LiveSettings.SaveForNextStart` in `src/PoolWatch/Engine/LiveSettings.cs` (Settings' Save), `ServicePlan.OffStart` and `ServicePlan.IsOff` in `src/PoolWatch.Core/Services/ServicePlan.cs`, and what takes a service out of the profile after a restore: `ServicesSource.Undo` in `src/PoolWatch/Engine/Changes/ChangeSources.cs`, `ProfileStore.RestoreKeepingOn` and `ProfileStore.DefaultKeepingOn`, with `ServicePlan.OnAfterRestore` and `ServicePlan.OnAfterDefault` deciding which services stay on; and the questions before a take-out: `ServicesTab.DoRestore`, `RestoreSelected` and `RestoreSelectedToDefault` in `src/PoolWatch/Ui/ServicesTab.cs` (the services they name, which they pass on as `named` or `confirmed`), `ProfileText.RestoreKeepsOn` and `KeepsOn` in `src/PoolWatch/Ui/ProfileText.cs`, and `ActiveChangesForm.PutBackOne`, `ActiveChangesView.PutBackOne`, `PutAllBackQuestion` and `PutAllBackPlan` in `src/PoolWatch/Ui/ActiveChangesForm.cs` (the `confirmed` they pass) | It runs only in the tray app, elevated, from PoolWatch's own `settings.json` (never with `--config`, never in the UI smoke), and only for the profile the user chose in the save dialog, whose box starts unticked for a new profile. Profiles are read leniently and fail closed: a profile not in PoolWatch's form is left out, a protected or unrecognised service is ignored, and a choice or delay that cannot be read applies nothing. The run reads the profile only in its own turn under the controller's operation lock, and never turns off a service the chosen profile did not list when PoolWatch started (an edit since only narrows it). Each service goes through Apply's own path under that lock: `ServicePlan.BuildApply`'s gate, its original recorded first (nothing changes while the record cannot be read), then the live gate; a Disabled service is never loosened. A profile is never applied at start from a file a standard user could write (a `--config` file): with `--config` nothing writes `settings.json`, Settings' Save included, and that Save keeps the profiles as `settings.json` holds them. A restore takes a service out of the profile only after the user confirmed a question that names it (a service the question did not name stays in the profile, and the result says so), only when the service is on afterwards, and inside the restore's own operation (the `thenHeld` step of `ServicesController.Restore` and `RestoreToDefault`); `--service-restore` and the uninstaller never change profiles. |
| Every other elevated action, and code that reads what a standard user can write | In `src/PoolWatch/Engine/`: `ServiceControl.cs`, `TasksAndStartup.cs`, `ProcessEnder.cs`, `TokenLeakGuard.cs`, `ChatGptShortcut.cs`, `LiveSettings.cs`, `DataRootGuard.cs`, `AppPaths.cs`, `NoFollow.cs`, `ExitSignal.cs`, `AdminNamespace.cs`, `LeftoverHelpers.cs`. Also the cache purge (`src/PoolWatch/Analysis/CacheClearer.cs`, `src/PoolWatch.Core/Cache/StandbyCache.cs`), the trace (`src/PoolWatch/Capture/CaptureRunner.cs`), the logon task (`src/PoolWatch/Ui/TrayIcons.cs`), web pages (`UiActions.OpenWebPage`, `Credits.IsAllowedLink`, `Credits.ReferencePages`), the services catalogue and its protected-service gate (`src/PoolWatch.Core/Services/ServiceCatalogue.cs`, `services-catalogue.json`), what the suggestions are built from (`src/PoolWatch/Engine/Setup/RecommendationInputsBuilder.cs`), `src/PoolWatch.Core/Util/PathGuard.cs`, `src/PoolWatch.Core/Model/RetentionClock.cs` (how long retention has kept what it deletes), and the readers: `TranscriptSearch` (Claude Code transcripts), `ShellLink` (the `.lnk` parser), `PoolTagTxt` in `src/PoolWatch.Core/Tags/TagKnowledge.cs` (`pooltag.txt`), `src/PoolWatch.Core/Util/SymbolPaths.cs` (symbol folders), `src/PoolWatch/Collectors/PebReader.cs` (other programs' current folder and environment, which `AiccModel` and `TranscriptSearch` parse), `src/PoolWatch/Engine/Setup/EnvironmentProbe.cs` (the user's registry and profile paths), and `KnowledgeBase` (built in, still read as untrusted) | The rules in "Code that runs elevated" above: the user's explicit action, a record made before a lasting change, the write policy and no link followed, programs by full path, bounded reads, and no code loaded from where a standard user can write. |

A change to any of these needs a separate security review before it merges: a reviewer that did not write the change
(another person, or an AI agent that is not the author) tries to break it, and the pull request records what was tried
and found. An AI agent never reviews its own change. Each guard the change adds has a test that fails without it. Not
sure whether a change touches one of them? Treat it as if it does.

PoolWatch starts as administrator only through its launcher, the install folder's `PoolWatch.exe`, which gives it an
environment built from Windows' own values, so .NET's start-up variables set for the user do not reach it (SECURITY.md,
Security model). PoolWatch's inner program, `app\PoolWatch.exe`, started as administrator by an administrator's own
hand (from their own elevated console, or **Run as administrator** on that file) has read them before its own code
runs: it starts again through the launcher, but do not claim more than that (SECURITY.md, Known limitations). A new
way to start PoolWatch as administrator goes through the launcher and needs the review above.

## Changing detection (`src/PoolWatch.Core/Detection`)

PoolWatch clears the cache itself, so it must know its own impact ([docs/DESIGN.md](docs/DESIGN.md), section 6):

- A drop after PoolWatch's own clear is that clear's result, never a release or a fall, for the total and for each tag.
  A clear's interval runs from the last sample before it to its measurement 2 minutes later (`OwnClears`).
- Growth is counted across clears on one baseline (`ClearSpans.Stitch`): the clear's drop is added back and the samples
  inside its interval are left out, so growth after a clear counts from the level it left and growth before it still
  counts. Every rule keeps the clock's window and its own bar: do not measure only from the level after a clear (that
  hides a leak the clears keep trimming), and do not scale or cut a window.
- While a clear is being measured, nothing is raised, changed or closed, apart from the incidents' maximum-duration
  close (2 hours by default), which still runs, with the level before the clear.
- With no clear in a window, every finding stays exactly as it was. `tests/PoolWatch.Tests/OwnClearTests.cs` holds
  this (`WithNoClearInTheWindowEveryFindingAlertAndIncidentIsExactlyAsBefore`,
  `EveryRuleFindsWhatItFindsOnTheSameCurveHoldingStillThroughTheClearAndNeverMore`); a new rule gets the same tests.
- The incidents (`Core/Incidents/IncidentManager.cs`), the known-issue evidence (`Core/Knowledge/KnowledgeEvidence.cs`)
  and the status line's totals (`src/PoolWatch/Ui/UiSnapshot.cs`) read the same baseline, and the texts that say what
  a clear freed follow it (`IncidentWriter`, `TraceAttribution`, `BundleBuilder`, `RecommendationInputsBuilder`,
  `ReportIssueDialog`). The growth and clear stats (`Core/Growth/GrowthLedger.cs`, which also feed Pool causes' four
  window columns) count on the same baseline but keep their own copy of a clear's interval, held equal to
  `OwnClears.SpansIn` by `GrowthLedgerTests`. Change them together, and update the limits in DESIGN.md sections 6 and
  11 when a number changes.

## Privacy: this repository is public

Everything pushed here is published: code, docs, commit messages, branch names, issues and pull requests.

- Never commit personal data: user or machine names, home paths, SIDs, e-mail addresses, IP or MAC addresses, tokens,
  or anything from an unredacted incident folder or a `capture.etl`. Test data and examples use made-up names
  ("Example Device Service"), never a product or service from a real PC.
- Screenshots come from the CI runner (the `ui` job's artifacts, named as `MainForm.Screenshots` and `UiRunner` save
  them), never from your own PC, and the maintainer looks at each one before it is committed. An illustration with
  made-up numbers, such as the README's with and without picture (`docs/screenshots/compare-*.svg`), is allowed only
  when its alt text and caption say so.
- The AI agent's prompt is built from what runs on the user's PC: a change to what it holds needs a redaction test,
  as a new field in an export or an issue does.
- An unfixed vulnerability goes to private reporting (SECURITY.md), never into an issue, a pull request, a commit
  message or a branch name. Describe an attack in prose; never commit a working exploit.

## Branches, reviews and releases

- Branch from `beta` and open the pull request against `beta`. `main` holds releases only: `beta` is merged into
  `main`, and a `v*` tag on `main` builds, signs and publishes the release.
- Pull requests are squash-merged once CI is green. The title becomes the commit message: a plain, capitalised
  sentence with no `feat:`-style prefix (see `git log`).
- A change users will notice gets a line under "Unreleased" in [CHANGELOG.md](CHANGELOG.md).

**Before the release pull request merges into `main`** (`main` is the default branch, so its README is the public
front page from that moment), and again on the commit to be tagged:

1. `dotnet build PoolWatch.slnx -c Release` reports 0 warnings.
2. Both test projects pass: `tests/PoolWatch.Tests` and `tests/PoolWatch.AppTests`.
3. Every CI job is green, including `linux-unit-tests`, `arm64-unit-tests` and every leg of the `arm64` matrix. The
   tag's `sign` job needs all of them, and the soak and overhead checks, which run on the tag.
4. Every screenshot the README and the user guide show comes from the `ui` job's artifacts, from a run after the last
   change to any window they show, and the maintainer has looked at each. An illustration, such as the with and
   without picture at the top of the README, says so in its alt text and its caption. `docs/screenshots` holds no file
   that nothing links to.
5. Every change since the last release to a part listed under "Security-sensitive code" had its separate security
   review, and the pull request records what was tried and what was found.
6. The README, the user guide, the technical page and SECURITY.md's table describe what the tag ships, and the release
   file names they give carry its version. CHANGELOG.md has the version's entry, dated the day of the tag, and that
   entry goes into the release's notes (the `publish` job's generated notes only list the merged pull requests).

## Writing for users

Everything a user reads is in plain words: no internal setting names (`AutoClearEnabled`), no pool tag without a
plain name, no exclamation marks. Say how sure PoolWatch is, and never present a correlation as the cause.
