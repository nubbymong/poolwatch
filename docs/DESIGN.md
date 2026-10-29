# PoolWatch design

How PoolWatch works and why it is built the way it is. The code refers to these sections by number
("DESIGN.md section 7"). What PoolWatch changes on your PC, and how each change is confirmed, is in
[SECURITY.md](../SECURITY.md).

## 1. Why it exists

On a developer PC with a ReFS Dev Drive, the kernel pool grew to about 23 GB (the Task Manager figure) and the
machine slowed, typing included. Working it out by hand took this loop: sample the pool tags, guess which process
was busy, then search AI coding-agent transcripts to find which session had issued the command. It found, in order:

- a desktop app churning its GPU shader disk cache on the Dev Drive;
- an orphaned recursive `find` (`find / -maxdepth 6 -type d -name ...`, started by an agent's tool call that timed
  out, its parent shell gone) crawling every drive for 90 minutes; the pool climbed about 400 MB a minute while it
  ran and stopped growing when it was killed;
- the memory came back only when stale git worktrees were deleted: the pool was holding per-file and per-directory
  file-system metadata for files that had been touched.

The slower, everyday version is a pool that passes 10 GB within a day and forces a restart. PoolWatch automates the
loop above, for the slow climb as well as the sudden burst.

Tags that matter here (from Microsoft's `pooltag.txt`; tags are **case-sensitive**: `Reff` and `RefF` differ). The
built-in table (`src/PoolWatch.Core/Tags/BuiltinTags.cs`) holds these and common NTFS and filter-manager tags:

| Tag | Owner | Meaning |
|---|---|---|
| `FMfn` | fltmgr.sys | NAME_CACHE_NODE (filter manager file-name cache) |
| `File` | nt | File objects (charged to the opening process) |
| `Reff` | refs.sys | FCB_DATA (per-file control block) |
| `RefF` | refs.sys | FCB_INDEX (per-directory control block) |
| `Refx` | refs.sys | General allocation |
| `RLin` | FsLib | Range lock entry |
| `MSPi` `MSeb` `MSb+` `MSde` `MSpa` | refs.sys | Minstore (ReFS metadata store) structures |
| `MmSt` | nt!mm | Section object prototype PTEs (grows with mapped / cached files) |
| `CcVl` | nt!cc | Cache Manager VACB level structures |
| `Nbuf` | netio.sys | NetIO MDL allocations |
| `EtwB` | nt!etw | ETW buffers |
| `AfdB` | afd.sys | AFD data buffer |
| `Toke` | nt!se | Token objects (the tag of the token leak on Windows 11 24H2 and 25H2 builds) |

## 2. Goals and non-goals

1. Continuously and cheaply record pool totals, per-tag usage and per-process activity.
2. Detect abnormal growth, retention and release, per tag.
3. Correlate growth with processes, services, handles and the process lifecycle (start, exit, orphaning), and rank
   suspects with plain-English evidence and a confidence level.
4. On demand or on alert, run a short confirmation trace that attributes allocations to process, driver and stack.
5. Write an incident folder with a bug-report draft: the process, its command line, parent chain, start time, the
   tag, the owning driver, the timeline and the evidence.
6. Optionally, understand AI Code Conductor's process tree and Claude Code sessions well enough to say which session
   (project, account profile, workflow, sub-agent) started an offending process.

Non-goals: no kernel driver; no third-party monitoring tools (PoolMon, RAMMap and the like are neither shipped nor
required); no telemetry, and no network call but the daily update check (on by default, easy to turn off; section
18). PoolWatch reads by default; every change it can make to the system is listed in SECURITY.md. Apart from securing
its own data folder and removing what an older PoolWatch left behind, each needs your action (or a setting you turn
on), and every lasting one is listed under Active changes, where it can be put back (section 13).

## 3. Hard constraints

- **Overhead budget** (steady state on a busy machine, measured by PoolWatch itself and shown in About and every
  report): under 0.5% of one core on average, under 100 MB working set, under 5 MB an hour of disk writes, and its own
  handles and memory flat over hours (section 12 says how the soak judges memory). A confirmation trace may exceed it
  for its bounded length only.
- **Where it writes**: only under its admin-only data folder `%ProgramData%\PoolWatch` (or an explicit headless
  `--out` folder), apart from the few writes SECURITY.md lists, never next to the exe, never through a link, and never
  on a ReFS volume (every Dev Drive is ReFS: its per-file metadata is the pool being measured). 7-day retention by
  default.
- **No WMI in the sampling path** (it is expensive). Native APIs only.
- **PoolWatch must not become a crawler itself.** Every file-system lookup is bounded: explicit paths,
  modified-within windows, file and byte budgets, early exit.
- **Runs elevated** (manifest `requireAdministrator`), and therefore only from under Program Files, such as
  `%ProgramFiles%\PoolWatch`, where its folders and the exe itself must be ones only administrators can change: their
  access rules are read, never written (`InstallGuard`; SECURITY.md says who counts as a standard user). An optional
  "start at logon" scheduled task with highest privileges avoids the daily UAC prompt; it is only ever created for an
  installed copy.
- Windows 11 (the setup also installs on Windows 10 22H2, untested). One x64 build, which Windows on Arm runs through
  its built-in x64 emulation. .NET 10 (`net10.0-windows`, WinForms): a framework-dependent build and a self-contained
  build, which the setup installs.
- **Staying light.** What only the window shows (the suggestions, the known-issue notes, the next-clear line) is
  worked out at most once a minute while the window is visible, never per sample. Incidents and the Pool causes
  rankings keep only what they print; a released tag keeps its ranking until the window hides (a released row shows no
  cause; only Copy summary reads it), and hiding the window releases the rankings and the list. A compacting full
  collection of PoolWatch's own heap, with `EmptyWorkingSet`, runs at 30 s, 90 s and 3 minutes after start-up and when
  the window hides; after that, every 5 minutes, only when the heap has grown 32 MB past what the last full collection
  left. The heap each full collection leaves (its floor) goes into `cache\profile.json`, written atomically every hour
  with the per-stage costs. The storm maps in `findings.jsonl` keep the top 15 (`report.json` has all), and a
  transcript search reads 16 MB at most.

## 4. Collectors

| What | API | Interval |
|---|---|---|
| Per-tag pool usage, alloc / free counts | `NtQuerySystemInformation(SystemPoolTagInformation = 22)` | 10 s |
| Committed pool totals (what Task Manager shows) | PDH `\Memory\Pool Paged Bytes`, `\Memory\Pool Nonpaged Bytes` | 10 s |
| All processes: PID, parent, create time, image, handles, threads, quota pool, I/O counts, private bytes | `NtQuerySystemInformation(SystemProcessInformation = 5)`, one call | 10 s |
| Process start / stop, short-lived ones included: image, command line, parent, user, exit code | real-time ETW session, `Microsoft-Windows-Kernel-Process` (TraceEvent) | events |
| Services hosted per PID | `EnumServicesStatusEx` with `SERVICE_STATUS_PROCESS` | 60 s and on svchost start |
| Handles per process by object type | `NtQuerySystemInformation(SystemExtendedHandleInformation = 64)` + `NtQueryObject(ObjectTypesInformation)` | 5 min, on alert and on Capture now; a scan over 250 ms skips the next |
| Volumes: file system, Dev Drive flag | `GetVolumeInformation`, `FSCTL_QUERY_PERSISTENT_VOLUME_STATE` | start and hourly |

Notes:
- `SystemPoolTagInformation` on 64-bit Windows: `ULONG Count`, then 40-byte entries from offset 8: `Tag[4]` @0,
  `PagedAllocs` @4, `PagedFrees` @8, `PagedUsed` (SIZE_T) @16, `NonPagedAllocs` @24, `NonPagedFrees` @28,
  `NonPagedUsed` (SIZE_T) @32. Tags are keyed by their raw 4 bytes, compared ordinally.
- A process is identified by (PID, create time): PIDs are reused.
- The tag sum runs lower than the committed-pages total (fragmentation, lazy page release). Both are recorded and
  labelled; the window's graph shows the committed figures, the ones Task Manager shows.

## 5. Tag knowledge

For every tag shown, owner and description come from, in order:
1. `pooltag.txt` from the Debugging Tools for Windows, when installed (read at run time, never shipped; at most 8 MB
   of it is read);
2. a scan of the loaded drivers and `%SystemRoot%\System32\drivers\*.sys` for the 4 tag bytes (the classic
   `findstr /m /l` method), cached;
3. the built-in table (section 1).

Object-manager tags map to handle types (`File`, `Even`, `Toke`, `Key `, `Sect` / `MmSt`, `Thre`, `Proc`, `Muta`, ...),
which powers direct attribution.

## 6. Detection

Per tag, over 1-minute, 10-minute, 60-minute and since-boot windows. "What has grown since boot, and what drove it,
hour by hour" is a first-class report (Since boot), from one-minute history.

- **Growth**: by default 200 MB in 10 min, 1 GB in 60 min, or total non-paged pool over 5 GB and still rising (all
  configurable).
- **Retention**: the tag grew during an activity burst and has not fallen within 15 minutes (default) after the
  correlated activity stopped.
- **Release**: a sharp drop. What happened at that moment (process exits, mass deletes) is recorded: it explains what
  was holding the memory. The busiest processes are counted from the sample before the drop began. I/O counters are
  read only at samples, and an exit does not report them, so a process that deletes and exits between two samples is
  counted over the release window up to its last sample instead, and marked as exited with its start time.
- Change points (growth onset and end) feed the correlation.
- **PoolWatch's own cache clears** (`Core/Detection/OwnClears.cs`): a clear's interval runs from the last sample before
  it to the sample 2 minutes later that measures its result; clears less than 2 minutes apart are one interval,
  measured once the last of them is. Whatever the pool does inside it is the clear's result, never a release or growth.
  Every change across a clear is measured on one baseline: the plain change with the clear's drop added back, the
  samples inside the interval left out, and a span that starts inside one starting at its measurement. So growth after
  a clear counts from the level it left and growth before it still counts. Every window is the clock's, and every rule
  keeps its own bytes and words ("grew > 200 MB in 10 min"). The tag rows, the status line's total and its lead, the
  growing and released states, the rates, the growth rules, the non-paged total's rise, an incident's growth and
  release record, and the known-issue evidence all use the one baseline. Reports, the trace answer, the bundle, the
  report suggestion and the issue text say what the clears freed as their result ("PoolWatch's own cache clear at
  06:20:01 freed 350 MB of it and 200 MB fell on its own"), and a retained tag a clear takes under its floor is said
  so ("no longer retained: ... so that memory was the cache's"). While a clear is being measured nothing is raised,
  changed or closed (the maximum-duration close still runs, with the level before the clear), and no known-issue
  growth is known; an incident still rising when a clear ran counts its quiet minutes from the clear's measurement.

Limits of that baseline:

- For a tag a clear dropped, a window that holds a clear's 2 minutes does not count the tag's growth inside them, so a
  leak just over a growth rule that PoolWatch's clears keep trimming can go unreported. At the default 10-second
  sampling that is up to about 4% over the hour rule with hourly clears, and about 8% with clears every 30 minutes (or
  in an hour where a light clear is followed by a full one). A longer sampling interval makes a clear's interval
  longer, so more: about 5% at a sample a minute, up to about 20% at the 300-second maximum. The 10-minute rule and the
  growing state lapse for about 10 minutes after each clear for growth up to about 28% over them at the default
  sampling, and a knowledge pack's rate is judged the same way. For the growth rules, the growing state and the
  non-paged total's rise, a clear never finds more than the same curve holding still through the clear's 2 minutes
  would, apart from the next limit.
- A program's own release inside a clear's 2 minutes is credited to the clear, and so is a tag's own growth there when
  the clear dropped it. A release credited this way is added back, so the tag's growth before and after the clear adds
  up as if that memory had been the cache's. A release read across the clear would read larger by that growth, so a
  release is raised across a clear only on the tag's fall since the clear's measurement: a release that straddles a
  clear is found only by its part after it (its recorded size can still include the part before), and a tag found
  released before the clear stays released while its fall on the baseline still holds the rule's bytes. A release
  gives no tray notice.
- Anything the purge frees after its 2 minutes (a lazy tail) would read as a release of the tags it frees. None has
  been seen either way yet.
- Growth is what a tag allocated, whatever PoolWatch freed of it. Growth before a clear and a refill after it add up in
  one window: a cache that refills about 1 GB an hour after every hourly clear is one long incident, closed at its
  2-hour maximum and re-opened within the hour.
- A window that starts inside a clear's interval is measured from the clear's measurement, so 10 or 60 minutes after a
  clear a finding can lapse for up to 2 minutes (found later, never sooner).
- A retained tag stops being retained when it falls under half its rise, and that rise counts back what the clears
  freed: a clear that frees more than half of it ends the retention, even where the tag still holds what the clear did
  not free.
- Pool causes' row of an earlier incident (Grew "+X", Per hour "X/h") and a report's headline ("grew X in D") count
  back what PoolWatch's clears freed without saying so; the report's Growth line beneath says it. The rows of a tag
  growing or held now, and of its open incident, take their figures from the growth stats (section 11), which say what
  the clears freed.
- Not on the baseline yet: the Advanced view's tag details (apart from their first sentence, from the growth stats)
  and the correlator's ranking, the Since boot report's hourly growth, a capture's window growth, and an incident
  report's allocation shape, charts and CSV files read the raw series.

## 7. Correlation

For each alert, suspects (processes; svchost with its services) are ranked on every signal, each with its evidence:

1. **Direct charge**: the process's quota pool rose, and its handles of the matching object type rose.
2. **Activity correlation**: Spearman rank correlation (plus a lagged version) between the tag's growth and each
   process's I/O rate, and its share of all I/O in the window.
3. **Lifecycle**: started just before onset, exited when growth stopped, or orphaned (parent gone) while busy.
4. **Ancestry**: the full parent chain, with dead ancestors recorded from the ETW start events.
5. **Confidence**: *correlated*, *strongly correlated* (several signals agree) or *confirmed by trace* (section 8).
   PoolWatch never states causation from correlation alone.

## 8. Confirmation trace

Started by Capture now, or by an alert when `CaptureOnAlert` is on (rate-limited: at most one automatic capture an
hour, never while the PC is saturated or in use). Default 60 s, hard cap 5 minutes, one at a time, with the in-box
`wpr.exe` and a generated profile:

- kernel pool allocation and free events with stacks, filtered to the alerting tags, plus process, thread and image
  events;
- optionally `Microsoft-Windows-Kernel-File`, to attribute file churn per process and volume, and a CPU trace with
  stacks (off by default: it lags a busy PC itself);
- the `.etl` is kept in the incident folder and parsed into net bytes per process, tag and driver frame, and top
  directories per process. Kernel functions are named only from local, admin-only symbol folders; no symbol server
  is contacted.

File-system allocations made on worker threads show as System (PID 4); the stacks and the file activity tie them back
to the requesting process.

## 9. AI Code Conductor and Claude Code awareness (optional)

Generic Electron / Chromium understanding first: children are labelled by `--type=` (`renderer`, `gpu-process`,
`utility` plus `--utility-sub-type=`) for any Electron app.

AI Code Conductor (AICC) specifics, used only when it is running:
- the main exe `AI Code Conductor.exe`; its `node.mojom.NodeService` utility processes are background workers (the one
  with by far the most reads is labelled the Tokenomics worker);
- PTY hosts (`conhost.exe`, `OpenConsole.exe`) under AICC, and the session CLIs below them, are "AICC session"
  processes;
- for each session CLI the working directory and environment are read from the process (PEB); the account profile
  comes from `CLAUDE_CONFIG_DIR` / `USERPROFILE` (`...\account-profiles\profile-<id>`), shown as
  "AICC › D:\sample-app (profile-xxxx) › bash › find.exe".

Which Claude Code session issued a command: for a suspect whose ancestry contains `claude.exe`, the transcripts under
`<config dir>\projects\*\*.jsonl` and `...\subagents\**\*.jsonl`, modified within 30 minutes of the process start, are
searched for the command's distinctive core, reading only tool-call lines. The result is the session id, project
folder, workflow and sub-agent, and timestamp. The config dirs searched are the session's own, every profile of the
AICC install it belongs to (or of the `AiccResourcesRoot` setting), and `%USERPROFILE%\.claude`. Bounded: a cap on
files and bytes, and it stops at the first exact hit. PoolWatch never sends anything from a transcript: the update
check sends nothing about it, Report an issue leaves sessions and projects out (section 10), and an export, which keeps
the session id and project names, is shared only by the user.

Git worktree sprawl is flagged as context, not as a crawl: when a release coincides with many deletions under
`.git\worktrees` or a worktree folder, the incident says so.

## 10. Incidents, bug reports and redaction

Each alert or capture creates `incidents\<yyyyMMdd-HHmmss>-<tag>-<raw hex>\` with:
- `report.md`: headline, timeline chart, ranked suspects with evidence and confidence, process details (path,
  version, signer, command line, parent chain, services, start and exit times, AICC / Claude Code session), system
  info (OS build, RAM, volumes with file system and Dev Drive flag, filter drivers) and a draft bug report for the
  owning vendor;
- `report.json`, `timeline.csv`, `processes.csv`, `events.csv`, `timeline.png`, and the `.etl` when a trace was taken.

These files are written as recorded, for you, and each report says so at the top ("NOT REDACTED"). **Export report**
builds a zip for sharing, redacted by default: the user name, home and TEMP paths (long and 8.3 forms), machine name,
domain and SID are replaced by placeholders; values that look like secrets (a password, token or API key on a command
line, an Authorization header, credentials in a URL, well-known token shapes) become `<SECRET>`; and the `.etl` (which
cannot be redacted reliably) is left out. The vendor bundle and copied text (`RedactCopies`, on by default) use the
same redaction. It does not take out Claude Code session ids or project, workflow and sub-agent names. It is
best-effort: read an export before you post it.

**Report an issue** (`Core/Report/IssueReport.cs`, pure; `Ui/ReportIssueDialog.cs`) writes the text of a GitHub issue
from facts PoolWatch already holds: its version and how it was installed (the setup, a zip, or built from source), the
Windows name, display version, build, Insider enrolment and architecture, memory, the number of Dev Drives, the kernel
pool now and its change over the last hour, the automatic clear and its last result, the token-leak guard and its
saved verdict, PoolWatch's own cost, and the 8 largest tags with their plain names; with an incident, its headline,
plain answer and its top three suspects' names and confidence. Never command lines, evidence lines, other apps' paths,
window titles, Claude Code or AI Code Conductor session, project or account names, the services list or the
environment. The redaction is the export's, plus a backstop: other profile folders, other absolute paths (outside
Windows, Program Files and the data folder), shares, GUIDs, volume serials, e-mail, IP and MAC addresses. The preview is
exactly the text, updated as the user types. Open in browser copies the full text and opens GitHub's form with only the
title, version and Windows in its address, at most 255 characters: `explorer.exe`, which opens it so the browser runs
as the user, cuts a longer argument. The user pastes the text. Save as file writes
`exports\issue-<yyyyMMdd-HHmmss>.txt`.

**A guide for the user's AI assistant.** `src/PoolWatch/Assets/AGENTS.md`, embedded in the exe, explains the data
folder's files, the report fields, the known patterns, how to confirm a cause, redaction and how to write a vendor bug
report, with rules for the assistant (read only, change nothing, post nothing). It is written into the data folder at
start when missing or different (compared first), installed next to the exe, and added unredacted at the root of every
export zip and vendor bundle, since it describes PoolWatch and nothing about the PC. It is not at the repository root,
where coding agents would read it as contributor instructions.

## 11. UI

PoolWatch is meant to be simple: say what makes the pool grow, clear the cache, fix what it knows, and help trim
background work. The window shows that and little else; the analysis behind it is the same in both of its views.

**Icons and the tray.** The binoculars everywhere (`TrayIcons`, the `.ico` files in `Assets`): `poolwatch.ico` (16 to
256 px) on every window's title bar and the taskbar, and in the tray the binoculars with a dot in the state's colour,
calm on purpose (`UiData.State`): red only for a real risk (an open incident of the non-paged pool over its limit and
still rising, or an open growth whose cause a trace confirmed); amber while another incident is open or a tag keeps its
memory after the activity that grew it stopped; green otherwise, also while a tag grows for a while (watched, not
alarming); grey binoculars while starting, before the first reading. A test reads every window's source and fails on a
tray-state icon in a title bar. The tray icon's tooltip has the paged and non-paged totals. Its menu: Open PoolWatch,
Run setup again..., Active changes..., "PoolWatch x.y.z is available" (only while a newer release is out and update
notices are on; it opens that release's page, section 18), the tools that are not on the window (Capture now, Since
boot report, Open incident folder, Export vendor bundle, Copy latest incident summary, Mark, the experiments), Remove my
clear task and Remove old clear-cache tasks (only while there is one to remove), Report an issue..., About PoolWatch
(version, overhead, data folder, what PoolWatch changes, credits and licences) and Exit. When a growth incident (or one
of the non-paged pool over its limit) opens, a notice says so in the status sentence's words: "Kernel pool: Open files
(File) is growing." (", with non-paged pool over the limit" for the second), then "PoolWatch is keeping an eye on X."
only at strong evidence or a trace, else "Open PoolWatch for details." (`PoolCauses.IncidentNotice`). A release or a
capture gives no notice. The token-leak watch (section 16) also looks in from the tray every 30 minutes, so it runs
while the window is hidden; its notice, when a check it started found the leak with the guard off, opens the window on
the guard's offer (the Simple view's card, or the switch in Settings in the Advanced view). Until setup was completed
once, every way into the main window opens setup instead (`FirstRunGate`).

**Two views of one window.** The Simple view is the default; a switch in the footer changes to the Advanced view, and
the choice is kept in `ui-state.json` (a file that cannot be read means Simple); setup's level choice sets it too. The
graph is one `PoolChart`, moved between the two layouts, never two charts. Every other window (setup and How PoolWatch
works, Active changes, the two AI agent windows, Report an issue, About, Settings, the Defender instructions) is created
when it opens and disposed when it closes; the main window only hides, and nothing in it refreshes while it is hidden.
Every size is scaled with the display's DPI. The colours come from one palette (`UiColors`), and every level (an
impact, a cost) is drawn by one meter with one word and colour per level (`ImpactScale`); under high contrast the
meter's bars take the text colour of the row they sit on.

Both views share the header and the graph:

- **The header** (`PoolHeader`, built by `HeaderText`, unit-tested): one statement per fact, visual first. "Kernel pool
  now" with the total and its paged and non-paged parts; the trend over the last 10 minutes as an arrow with its
  amount ("243 MB", "up in the last 10 min"; "Steady"; amber for a rise of 100 MB or more, green for such a fall; while
  it cannot say, "collecting the first sample", "the trend shows in a few minutes" or "a cache clear is being
  measured"); the next clear (section 15: "Next clear in 37 min or at 8 GB", "Next clear at 8 GB", "Clearing now",
  "Automatic clearing is off", "Your own clear task clears it", "Clearing needs administrator rights", "Clearing waits
  for the experiment") with a ring for how close it is (the time gone of the schedule, or the pool against the size
  rule); and the last clear's result ("Last at 08:59: freed 914 MB", "Last yesterday at 23:00: freed nothing", "Last at
  08:59: did not run", "No clear yet"). Every amount is rounded one way (`PlainWords.Amount`: whole megabytes below a
  gigabyte, then one decimal), as the tiles, the clear's result and Pool causes round it. A note shows only when there
  is something to watch (`MainForm.HeaderNote`): a likely cause PoolWatch is sure of ("PoolWatch is keeping an eye on
  X."), a tag that grew while the total held, a saturated PC ("Your PC is saturated; busiest: X."), or, while the tray
  is amber or red and nothing else says why, where to look; it takes the tray's colour, red only for a real risk. In the
  Advanced view the header is the smaller one, on one row, on the Services and Tasks tabs, and on Pool causes when the
  full one does not fit on one row (`MainForm.CompactHeader`).
- **The status sentence** (`Core.Util.PoolStatusLine`, unit-tested) is the trend's tooltip, and the next clear's is the
  whole line about clearing (section 15). The sentence says steady ("Kernel pool is steady at 9.6 GB."), growing
  ("Kernel pool grew 709 MB in the last hour.": the committed total now against an hour ago, from 200 MB an hour or 100
  MB in 10 minutes), shrinking (from the same amounts; a fall in the last 10 minutes is said first), a tag growing while
  the total holds ("Kernel pool is steady at 9.6 GB, but File system metadata (Reff, refs.sys) grew 400 MB in the last
  hour."), or too early to tell (under 3 minutes of samples). It names a cause only with strong evidence or a trace
  behind it, in soft words ("PoolWatch is keeping an eye on X."); Pool causes keeps every suspect with its evidence. X
  is the name people know the cause by (`PoolCauses.FriendlyName`): an app's own name, a service's display name
  ("Example Device Service", or "Task Scheduler and 2 other services" when one process runs several), a Claude Code
  session with its folder, else the program without ".exe"; never a PID, a service's short name only once Windows no
  longer lists the service, and nothing for the kernel's own threads, a program started through WMI or a process known
  only by its PID. A change over a span that holds PoolWatch's own cache clear is measured on the one baseline of
  section 6 (the clear's drop added back), and a clear of the last 10 minutes is said as its result ("The cache clear at
  03:56 freed 1.3 GB."), never as the pool shrinking. A move smaller than the shown precision is not said, and it never
  says a tag "grew 0 MB".
- **Clear now**, in the header, empties the standby cache at once. **Settings** is beside it in the Advanced view on
  Pool causes.
- **One graph**: the committed paged and non-paged pool, solid lines, a legend with the current values, round axis
  steps. Its span (`PoolChart.Span`) is the last 6 hours in the Simple view and the window picked in the Advanced view
  (the last hour, 6 hours or 24 hours), and its title says so ("Kernel pool, last 24 hours"), whether or not PoolWatch
  has run that long: the time axis always spans the whole window, and the time before PoolWatch started is a band
  labelled "Before PoolWatch started (14:05)". The last 70 minutes are the raw samples and the rest minute averages
  (the history keeps 25 hours of minutes). The lines break where two points are more than 10 minutes apart (the PC
  asleep), and a point alone between two breaks is a dot. Every text position is measured from the font, so the labels
  cannot overlap the title at any display scaling, and the time axis takes as many evenly spaced round clock times as
  fit. Each cache clear in the span has a small mark at the top of the plot and a faint line; pointing at a mark shows
  "Cache clear at 09:12 (automatic): freed 4.7 GB" ("Cache clear yesterday at 23:00 ..." before today, or "being
  measured"), and anywhere else the chart's help text. It repaints the marks only when the list of clears changed.

**The Simple view** (`SimpleView`) adds, under the header:

- **Four tiles** (`TileText`, over the graph's own 6 hours, from the growth stats below): Growth per hour (with
  PoolWatch's own clears added back: "1.3 GB", "without the clears, last 6 h"; "known in 22 min" until 30 minutes are
  measured; "since 08:05" while PoolWatch has run for less than the window), Cleared ("7.1 GB", "6 clears, last 6 h"),
  Net change ("up 307 MB") and Memory in use ("41 GB", "of 127 GB, 71 GB cache", in whole gigabytes, as RAM is sold).
  They take one row, or two on a narrow window.
- **the graph**, which keeps at least 120 logical pixels.
- **Get more from PoolWatch**: at most three cards, those that fit. First the offers (section 16) that one click puts
  right, each with its button and Hide (30 days); then the feature cards (`Discovery`: See what grows the pool, Trim
  background services, Mark a slow moment), each with a link and Hide (for good, kept in `setup.json`). They are
  worked out at most once a minute while the window is visible in this view, and right after a button or Hide; a card
  list is rebuilt only when what it says changed, so nothing flickers. A button runs its change off the UI thread; its
  answer stays a minute above the cards after its card has gone, in red when it failed.
- **the footer**: Active changes (section 13) with a count of the changes in effect, the Advanced view switch and,
  while a newer release is out and update notices are on, "PoolWatch x.y.z is available. See what's new", whose link
  opens that release's page (section 18).

**The Advanced view** adds the 1 h / 6 h / 24 h choice and Show RAM on the graph, the **Growth and clears** panel beside
it and three tabs under it, with the same footer and the switch on. It has no cards. On the Services and Tasks tabs the
graph's row gives its room to the tab (`MainForm.AdvancedRows`); the graph is back on Pool causes.

- **The time window choice** (`SpanChoice`, three segments in one outline; 6 hours at first, kept while PoolWatch
  runs) sets the graph's span, the panel's window and Pool causes' four window columns.
- **Growth and clears** (`GrowthPanel`): the rate with its caption; Grew, Cleared (N clears), Fell on its own and Net
  change, signed with a true minus; and how long the window was measured ("Measured over 18 h of the 24: the rest was
  not sampled, for example while the PC was asleep." for any gap between samples: a sleep, PoolWatch held up, or the
  clock moved), which becomes the panel's tooltip when the panel is too low for it. Before any data each row says "not
  known yet". Refreshed with each new sample while the window shows.
- **Show RAM**, a switch in the graph's legend in this view only (`Settings.ShowRam`, kept in `settings.json`, not in
  the Advanced grid: on for a new install and for a `settings.json` saved without it; a saved value is kept): RAM in
  use, the cache (standby) and free as muted stacked columns behind the pool's lines and grid, a column a minute, 5
  minutes or a quarter hour as the span is, on a right axis from 0% to 100% of the PC's physical RAM; the column under
  the pointer is outlined and its tooltip gives the GB ("13:25: in use 14.2 GB, cache 12.5 GB, free 5.3 GB (of 32 GB
  RAM)").
- **Settings** is a short form in plain words, in three columns: Start PoolWatch when I sign in (the scheduled task,
  changed at once) and update notices (`CheckForUpdates`); automatic cache clearing (`AutoClearEnabled`,
  `AutoClearEveryHours`, `AutoClearPoolGb`), named as its three rules (On a schedule, At a pool size, Whichever comes
  first, recommended), with the line under it saying first that a clear takes a few seconds and video or the mouse may
  stutter while it runs, then the 30-minute spacing; deeper captures (`DeeperCapture`, with a line saying when it has no
  effect); the token-leak guard (`TokenGuardEnabled`: a line under the box with its trade-off, that it lasts until
  sign-out and that PoolWatch turns it on again each time it starts; the rest in its tooltip, one sentence a line); and,
  when the Codex app is installed with the launcher and the shortcut is not on the desktop, an offer to create the
  Codex shortcut again (`ShortcutOffer`). Its values are checked before saving (`SimpleSettings`, unit-tested: ranges,
  and an automatic clear needs a schedule or a pool size), and the form's fields are bound in `SimpleSettingsFields`,
  unit-tested too. **Advanced...** opens every setting in the property grid; both edit the same settings and share one
  Save. Save puts the automatic clear, the token-leak guard and update notices into effect at once (section 14), only
  those the form changed; every other setting applies at PoolWatch's next start, and the form says which. Each opening
  starts from `settings.json`, so a change saved for the next start (deeper capture, an Advanced value) shows again and
  is never written back over, with the three at-once settings as they run (with `--config`, from the running settings,
  and Save then keeps nothing for the next start: section 14). Both forms are created each time they open.
- **Pool causes** is one list merging what were four tabs: open incidents and those of the last 7 days; tags the
  detector marks growing, retained or released that no open incident covers; live processes whose handles rose 5,000
  or whose charged pool rose 32 MB in the hour, or that were orphaned while PoolWatch watched and still do 500 I/O
  operations a second; and, from the last 24 hours, spawn storms, child accumulation, git roots, stray `.git` folders,
  token-leak and filter-driver findings (warning or alert) and every cache clear's result. What is happening now comes
  first. The columns: What is growing (the tag in words, with its driver), Grew, Cleared, Net, Per hour, Likely cause,
  Confidence (in words), Impact and When. A row of a tag growing or held now, and of its open incident, takes Grew,
  Cleared, Net and Per hour from the growth stats over the picked window when they count that tag
  (`PoolCauses.WithWindow`; the legend row says which window, "Grew, Cleared, Net change and Per hour of tags now: last
  6 hours"); every other row keeps its own figures (a process's rise in pool charge, an earlier incident's growth, what
  a clear or a release freed), and only Net is ever signed. **Impact** is the level of the tag's growth per hour with
  PoolWatch's clears added back over that window (`ImpactRules.FromGrowth`: section 16 has the scale), drawn with its
  word whole at any scaling; a held tag is None, and the cell is empty until the rate is measured. The selected row's
  details are a title, a line saying what PoolWatch already does about it ("Handled: the standby cache is emptied every
  hour, or at 8 GB.", "Handled: the token-leak guard is on, so Windows does not leak these."), the incident report's
  summary, a tag's top three suspects with their evidence and the recent notable events (a tag's begin with the
  window's figures as a sentence), a process's counts, chain and command line, or a finding's text, with Export report
  (the selected row's report folder, else the newest incident's, redacted by default: section 10), Open report folder
  and Copy summary. A known issue that matches (section 17) adds its note under the details of its tags and of their
  incidents. A click on a column header sorts by it (numbers by value, the confidence by strength, When by time, names
  ignoring case; a cell with no number last either way; ties keep the list's own order), a second click reverses it,
  and Windows' own sort arrow shows on that header (`ListSort`, the one rule of every sortable list); a selected row
  stays selected. A line above the list says what the colours mean: pink for an open incident or an alert finding,
  yellow for a tag growing or held now, a process piling up, or a warning finding. A tag row's cause comes from its
  ranking (the correlator over the last hour, tens of milliseconds), built on a pool thread, one at a time
  (`TagRankings`, `RankPlan`): the selected row's tag at once, every listed tag once a few seconds apart, then each
  refreshed after 5 minutes (the selected one after 30 s), never two within 30 s. Until a tag has a ranking its cause
  cell is blank and its details say "Ranking the suspects…". The details box stays a native text box, so its text can
  be selected and copied.
- **Services** (`ServicesTab`, `ServicesTabView`, `ServiceRows`) lists the background services you may turn off, with
  their live cost, in two groups: **Windows services** (those the catalogue knows Windows' own start type for), each
  with its source line (`ServiceSourceText`: "Microsoft's virtual-desktop optimisation tool turns this off", "The CIS
  security checklist for company PCs turns this off", both, or "No official source"; never "safe to turn off") and the
  group's one caveat ("These sources were written for virtual desktops and company PCs. Check what you lose first."),
  and **From other apps** ("No official source: decide with your AI agent"); then, with Show protected, the protected
  ones, which are never changed. The columns: Service, You lose, Cost (the processor cost now, on the one meter), State
  and Source; a row's tooltip has its note and the whole source line. Show not running and Show protected start off
  (a ticked service always shows), and Find filters. The list (`ServiceListView`) draws its group headers itself (a
  ListView cannot put a tick box on a group): a fold arrow, a tick for all, some or none of the group's shown services,
  and its name; screen readers get the same as the row's text. The ticks are kept by the list, so a fold, a filter, a
  sort or a refresh keeps them. Space and the right-click menu (Tick selected, Untick selected) tick or untick every
  selected row at once (a selected header stands for its group); Left, Right and Enter fold a header; Ctrl+A selects
  all. Above the list: the profile row (section 13), **Decide with your AI agent** and **Import answer**. Below it: a
  green line ("Anything you turn off can be put back from Active changes."), **Put back...** (put back the selected
  service, set the selected service to its Windows default, put all back), Refresh and, on the right, the primary
  **Turn off N services**. The questions before a service change (with the profile take-out of section 13) live in
  `ServicesTab`.
- **Tasks and startup apps** (`TasksTab`, `TasksTabView`, `TaskRows`, `TaskActions`) lists the startup apps, the
  non-Microsoft scheduled tasks and the apps busy in the background, ranked by what each costs now (`TaskCost.Rank`:
  its cores, a little for page faults; one running but not measured yet ranks above what does not run). Four segments
  (All, Startup apps, Scheduled tasks, Busy in the background), Find, and Show not running and Show Microsoft, both off
  at first. Cost now is the latest sampling window (`ProcessLoad.CostNow`), a busy app's its 30-minute average
  (`Diagnostics.CostOfProcess`); a program that runs through a shared launcher (cmd, PowerShell) cannot be told apart,
  so its cell says "not measurable" and its details say why. Under the list, a detail panel for the selected row: what
  it runs (the full command, scrolling in its own box, selectable), when it starts, what it costs, and its actions: the
  switch ("Don't start at sign-in" / "Start at sign-in again", the `StartupApproved` switch Task Manager uses; "Turn
  this task off" / "Turn this task back on", Task Scheduler's Enable flag), recorded before it is made (section 13),
  never for Microsoft's own entries; Open file location; and, in red, **End it now** (`TaskActions.End`, `RunEnd`),
  offered only for a running program whose every process was matched by its own full path, never when one of them is a
  core, security or service-host process (`EndProcessPolicy`), after one question that names it, each process ended
  through a handle checked against its start time. The list reads again every 5 minutes on a refresh, at once on Refresh
  and after a switch or an AI agent's Apply (`TaskReadClock`); the switch and End it now wait while it reads. The lists
  never scroll sideways.

**The AI agent windows** (`AgentPromptWindow`, `AgentReviewWindow`, `AgentDecisions`; the files through `AgentFileIo`;
the prompt and the answer in `Core/Services/Agent`). For the services of other apps there is no official source, so
the user decides with an AI agent of their own. **Decide with your AI agent** reads what runs on this PC
(`AgentItemList.Build`: the services the tab offers, the startup apps and the non-Microsoft tasks, with their
publishers and costs) and shows a window with three steps, what the agent will see and what is never in it, and every
word of `poolwatch-services.md` (`AgentPrompt.Build`, at most 96 KB of UTF-8). The prompt is redacted as it is built,
with Report an issue's redaction and a backstop of its own: names, drive paths (`<PATH>`), SIDs, e-mail addresses and
accounts (`<ACCOUNT>`) are taken out, each word read whole or not at all, and a name over 4,096 characters is
`<TOO LONG>`; a redaction that cannot finish in time makes no prompt.
**Save the prompt...** writes it only where the user picks, **Copy to clipboard** copies it. **Import answer** reads
the file the user picks (at most 64 KB, never through a link), builds the list again from the live PC, and reads the
answer strictly (`AgentAnswer.Read`, version 1: `turnOff` and `keep`, each with `services`, `startup` and `tasks`
lists of names, and `why`, one reason per name; anything else refuses the whole answer, with why). A review window
then lists what Apply turns off, what PoolWatch refuses and why (a protected service, anything not on this PC now,
Microsoft's own startup apps and tasks, a name on this PC under the list's other name), and what is left as it is (up
to 50 a part, then counted); an item whose name another of its kind shares shows its place or folder on its own line.
Words from the answer are drawn as plain text, never a link. **Apply** (`AgentReviewForm.Apply`, `AgentApply`) asks
about services of other apps PoolWatch does not recognise by name (up to 15, then counted), then turns off the services
through the tab's own path and the startup apps and tasks through `ChangeRecords.Toggle`, each recorded first, with
"AI agent answer" as their source (`AgentAnswer.Via`); one service gone since the review does not stop the rest, and the
result gives up to ten reasons for what was not changed. The Tasks tab then reads again.

**Growth and clear stats** (`Core/Growth`, `GrowthLedger`). Fed each sample right after the history, under the same
lock, at about 10 microseconds a sample. It counts the committed pool total and each tag of 4 MB or more (at most 256
tags at once: a tag is counted from the first sample it holds that much, and its slot is let go once it is small again
and no kept minute holds it), on the baseline of section 6: across a clear's interval the drop is the clear's (Cleared),
and every other change, a rise across the interval included, is the series' own. Each minute keeps that own change,
signed, and what the clears freed, for 25 hours; a tag's minute is kept only once it holds 64 KB of change (the rest is
carried to a later minute), so a tag's window is true to 64 KB. A window (the last hour, 6 hours or 24 hours, from the
minute it starts in) adds its minutes up and only then says whether the series grew or fell (`GrowthCounts.Of`): Grew
and Fell on its own are never both non-zero, a pool that wobbles between samples shows neither at any sampling interval,
and Net change = Grew - Cleared - Fell is the level change over the time measured. Measured time is the sum of the gaps
between samples: a change across a sleep or other gap (more than the sampling interval plus 2 minutes) or a clock set
back is neither counted nor timed, and the next sample starts again from where the series is then; a clear left open
across a sleep still keeps its drop, as the detector says. A clock set back also drops the minutes that lie wholly after
the new time (the minute it falls in keeps its counts, so a window counts at most the step's length twice), and a clear
already counted is not counted again, even when the minute that counted it was dropped; a clear still being measured
when the clock is set back is not counted at all. The rate is (Grew - Fell) per hour measured, said once 30 minutes are
measured ("known in 22 min" before), so the rate times the hours measured, less Cleared, is the net change. Its caption
says whether the window holds a clear of PoolWatch's ("Without the clears it would grow", or "would fall") or none ("It
grows", "It falls"); before the rate is known, and for a rate under a byte an hour ("nothing"), it says no direction
("Growth per hour"). Durations are to the nearest minute, the "so far" line rounded down. It holds typically 1 to 3 MB,
at most about 5 MB (256 tags changing every minute for 25 hours), and is never written to disk; `cache\profile.json`
reports its size. Limits: a clear whose purge call fails only after its own 2-minute measurement is still counted as
cleared (the status sentence's hour leaves it out), and a tag's counts start when it first holds 4 MB or when PoolWatch
starts.

**The RAM overlay** (`Core/Growth/RamHistory`). The cache clearer already reads the memory lists once a minute
(`SystemMemoryListInformation`); each reading is kept as one point a minute: the standby list as the cache, the free
and zeroed lists as free, and in use as what those two leave of the PC's physical RAM (`GlobalMemoryStatusEx`, also
the incident reports' RAM). The points sit in a fixed ring of 1,500 (25 hours, about 47 KB), in memory only, and the
graph averages them into its columns. The Simple view's Memory in use tile reads the latest point.

**Tag names** (`PlainWords.Tag`) are everyday words with the tag code and its driver in one pair of brackets: "Open
files (File, Windows kernel)". Common tags have a fixed name; another tag uses a short pooltag.txt description without
its bracketed aside, and otherwise reads "Pool tag Abcd (foo.sys)". A tag row's details start with the tag code, the
driver and the full technical description.

**Setup** (`Ui/Setup`: `SetupWizardForm`, "Set up PoolWatch"; its state in `SetupSession`, without a window, so it is
tested) is a window of its own with seven steps on a rail: Welcome (the mission, the four pillars, and the level
choice, Simple or Advanced, which sets the app's view; both run the same steps), Your PC ("How your PC is impacted":
each known cause with its impact level from `ImpactRules.FromFacts`, section 16, and "Fix in step N", "Trim in the app"
or "Still watching"), Safety (three promises, in green), Standby cache (On a schedule, At a pool size, Whichever comes
first (recommended, preselected on every PC at a first run), Decide later; a small chart of the kernel pool; the
stutter line; and, with a RAMMap clear task of the user's own, the note that choosing a rule replaces it), Fixes (start
when you sign in, Defender performance mode with its instructions, the Codex shortcut as a Yes/No, and the token-leak
guard when the check found the leak, its "Why?" open with what you will notice and how to undo it, with the pages it
cites), Services (for later, in the app: setup changes no services) and Ready (what setup set, the update-notice box,
where PoolWatch lives). "How PoolWatch works" (`HowItWorksForm`) opens from every step's footer. The window is laid out
from its fonts at any scaling, cut to the working area with the page scrolling, opens on the screen of the window it
opens over (else the one with the pointer), and is built once: a change updates its rows in place, so a press never
rebuilds the page. Switches, choice cards and the Yes/No report their checked state to screen readers. Setup reads the
PC as it opens ("Looking at your PC..."), then, on a Windows build that needs it, runs the token-leak check (usually
about 20 seconds) once; the CI UI test never runs it. Steps 4 and 5 wait for the read before Next ("Next is ready in a
moment."), at most a minute in all (`EnvironmentProbe.HungAfter`): after that setup goes on with the facts PoolWatch had
and shows the read when it ends. Nothing is changed until Next on the step that shows it: the Standby cache step writes
the chosen rule (`LiveSettings.Apply`) and, exactly when the step shows the note, replaces the user's task first (from
the same scan; the rule is written right after); the Fixes step's Next registers the sign-in task, creates the shortcut
and turns the guard on through the same code as the cards. A Fixes row that appears while the step shows (the read or
the leak check ended) holds Next for a second (`NewRowWait`), so a Next pressed as it arrives never applies what the
user had no time to see. A change that fails stays on the step with why, and Next again goes on without it. Each change
is on the timeline as made in setup and listed under Active changes. Until setup was completed once (`setup.json`), it
opens on its own at every start, the sign-in start included, the main window stays hidden, and it has no close button
(PoolWatch exiting or Windows shutting down still closes it); the Ready step's Open PoolWatch completes it, applies the
level choice, starts the update check and shows the main window. Opened again from the tray (**Run setup again...**), it
can be cancelled, and picking Simple there switches an open window to the Simple view at once.

The lag chart, the since-start chart and the raw numbers block (commit, memory lists, lag, CPU and page faults, the
token-leak guard's state) are not on the window. The data is still recorded in the data folder (`totals.csv`,
`lag.csv`, `load.csv`, `findings.jsonl`), and the Since boot report and the exports have it.

## 12. Build and test

The code builds anywhere the .NET 10 SDK runs (`EnableWindowsTargeting`); it runs only on Windows.

- Unit tests (xUnit, `tests/PoolWatch.Tests`): parsing against byte fixtures, tag lookup, detection on synthetic
  series, ranking, transcript lookup on fixture files, redaction, the recommendation rules, the known-issue packs. They
  also run on Linux.
- App-layer tests (`tests/PoolWatch.AppTests`, Windows only): every system change goes through an injected fake; no
  test changes a service, task, registry value or ACL, writes a shortcut outside its own TEMP folder, or ends a process.
- End-to-end on the Windows CI runner (elevated): PoolWatch runs headless while `tools/Leaker` reproduces a pattern
  (a File-handle leak, file churn with release, an orphaned child, the confirmation trace, spawn storms and more), and
  the generated reports are checked.
- The UI test opens the tray app on the runner, checks each window's texts and layout (the header and the buttons that
  show, the three tabs, each card's words in plain language with no setting's own name, the tray icon's colour against
  the open incidents, and Pool causes listing its rows again after the window hid), and takes the screenshots the README
  and the user guide use (in `docs/screenshots`; the README's with and without picture is an illustration): the Simple
  view (`1-simple-view.png`), the Advanced view with the top cause selected (`2-advanced-view.png`), Services
  (`3-services.png`), Tasks and startup apps with its top row selected (`3b-tasks-and-startup-apps.png`), Settings and
  its grid (`4-settings.png`, `5-settings-advanced.png`), each of setup's seven steps once it has read the PC
  (`6-setup-1-welcome.png` to `6-setup-7-ready.png`) and How PoolWatch works (`6-setup-how-it-works.png`), Active
  changes (`7-active-changes.png`), Report an issue (`8-report-issue.png`) and About (`9-about.png`). It also takes the
  window with an update notice for a sample release (`10-update-notice.png`), which it checks but those pages do not
  show (the test never asks GitHub). Nothing it presses changes anything, setup runs no token-leak check there, and it
  never applies a service profile at start.
- The installer test compiles the setup, installs it silently, makes a real change with the installed exe, starts the
  tray app, uninstalls, and checks that PoolWatch exited through its exit signal, the change was put back and the
  files, shortcut, sign-in task and Uninstall entry are gone; a second pass keeps the changes.
- On GitHub's Windows 11 Arm runners: the unit tests, and the x64 build, laid out as the setup installs it, running the
  self-test, the CSV checks and the end-to-end and diagnostics scenarios under emulation.
- A 45-minute soak checks the overhead budget under a busy-PC load, headless and with the window open (walking the
  Simple view and the Advanced view's three tabs, one a minute: `MainForm.StartSoakCycle`), judged as steady state from
  15 minutes in until 10 seconds before PoolWatch's own end of run (the start-up peak, and the end-of-run screenshots or
  exit reports, are reported on their own lines, not judged). Flat memory is judged through the heap PoolWatch's full
  collections leave (the floors in its profile.json, from the same window; a slope of private memory over every sample
  would depend on where the window cuts the sawtooth between collections): their trend, under 5 MB an hour, when at
  least three floors span 15 minutes, and, in every run, private memory's steady maximum, which must stay under 100 MB.
  The soak and an overhead check run on demand and before every release.

## 13. Change ledger and Active changes

**Active changes** is one list over several sources, because some changes already have a store of their own and a
second copy would be a second truth (`Engine/Changes`, `ChangeLedger`). The window (`ActiveChangesForm`) shows up to
three groups (`ChangeGroup`; an empty one is left out): the features PoolWatch turned on (the sign-in task, the
automatic clear, the token-leak guard, the Codex shortcut), each with Turn off (the shortcut: Remove); what it turned
off (services, scheduled tasks, startup apps, a clear task of the user's own), each with Put back; and "Other changes
PoolWatch made" (a service set to its Windows default, a scheduled task or startup app that was off and that PoolWatch
turned on), each with Put back too. A row that cannot be put back, or that the uninstaller leaves, says so, and the
footer then says that uninstalling puts the rest back ("except where marked"). Its data file keeps its older name,
`applied-changes.json`, so an installed copy's records stay readable.

| Kind | Source | Put back |
|---|---|---|
| Start at sign-in | Task Scheduler (the `\PoolWatch` task exists) | removes the task |
| Automatic clear, token-leak guard | the running settings | turns it off (section 14) |
| Services | `service-changes.json` | restores the saved start type (and, once you confirm, takes a service it turns on out of the profile applied at start: "Service profiles" below) |
| Your own clear task that PoolWatch removed | `applied-changes.json` | puts it back from its saved definition |
| Scheduled tasks and startup apps | `applied-changes.json` | switches them back |
| The Codex shortcut | `applied-changes.json` | deletes it, only if its SHA-256 still matches what PoolWatch wrote |

`applied-changes.json` holds only the changes with no store of their own. It is admin-only and in the data folder, never
under `--out`; at most 1 MB and 500 records; written to a temporary file and moved; one thread-safe instance per data
folder, so every window and the tray write through one object. The list of changes is read, as well as written,
through the link guard, and read again before every change and every listing, never written over from an old copy.
Every record is read as untrusted (bounded, every field validated) and checked against the live system right before it
is acted on: a shortcut must be a `.lnk` directly in the user's Desktop or Start-menu Programs folder; a task
definition must parse with DTDs prohibited, be at most 64 KB, carry no access rules of its own, have only actions that
run RAMMap's memory clear by full path with no environment variable, hold only plain text in every part it checks (no
comment, CDATA, processing instruction or element) and no control character in a path or an option (a tab may separate
the options), and name a root-folder task that does not exist now; a task path must not start with `\Microsoft` or
hold a `/`. A source that cannot be read is listed as a row that cannot be put back, saying so.

Removing your own clear task: every definition is saved and read back before the first task is deleted; when one cannot
be, none is removed. A delete that fails is taken to have removed nothing; after a delete that removed nothing,
PoolWatch checks that the task is gone, and if not, the automatic clear stays off. Taking a record back acts only on the
record that removal saved, so two removals of one task never leave it with no record. Putting a task back turns
PoolWatch's automatic clear off first, so the two never both run, and registers the definition with the account it ran
as. One that ran with a saved password, or that PoolWatch cannot recreate, is saved to `removed-tasks\<name>.xml` for
import in Task Scheduler instead. One whose RAMMap a standard user could change (by the access rules of its folders
or of the file, judged as `InstallGuard` judges PoolWatch itself), whose rules cannot be read, that is not there now or
that is itself a link, is put back only after a warning the user accepts in the app; the warning names every such
program, the yes holds only for the warning shown, and it is checked again on the record that is registered. The
uninstaller never puts it back (it saves it for import).

A switch of a scheduled task or a startup app on the Tasks and startup apps tab, or by an AI agent's answer, is recorded
before it is made: when the record cannot be saved nothing is switched, and a switch that fails takes its record back.
The switches and Put back's switch back run one at a time. When `service-changes.json` cannot be read, the Services tab
says so and Put back says why, and no service is changed whose original cannot be saved, or read (a start type the
service control manager could not tell would be an original that Put back refuses). Turn off (also the path of an AI
agent's answer), a profile applied at start, Put back and Set to its Windows default take one operation lock in
`ServicesController`, so a put-back never runs in the middle of a change. Turn off sets a service to Manual where the
catalogue says so, else Disabled, but never looser than it is now: a service already Disabled stays Disabled. It stops a
service only when it sets Disabled; one set to Manual keeps running until it stops by itself or the PC restarts: Turn
off's question lists those apart from the ones it stops and disables (`ServicesPageText.ToManual`), and the State
column reads one as PoolWatch left it ("set to Manual, running (turned off by PoolWatch ...)"), never as switched back
on. Windows does not stop a service while one that depends on it runs, nor start one while a service it needs is
Disabled, so of the services one change turns off, one that depends on another is stopped first, and of those one put
back restores, the other comes back first (`ServicePlan.InDependencyOrder`, from the dependencies Windows lists for each
service; Put all back in Active changes takes that order from the services source, `IChangeSource.PutBackOrder`).
`ServiceControl.Change` sets the start type first and then asks for the stop, so a service already Disabled cannot be
started again in between (by a trigger, say); when the start type cannot be set, no stop is asked for and nothing
changed. A service that is starting, resuming, pausing or stopping is waited for to settle, up to 20 seconds, and a stop
Windows took is waited for up to 20 seconds more; two pure functions decide each step (`ServiceControl.Looked` for the
state, `ServiceControl.Answered` for Windows' answer to the stop, 1062 counting as stopped, and a first 1061, from a
service that has just begun to pause, say, looked at again once). A service whose stop Windows refused (one that depends
on it runs, which PoolWatch never stops, since nobody chose it), that did not settle or stop in time, or whose state
Windows could not give keeps its new start type: its result says why and when it stops, never done (it names the running
dependents only when Windows refused for them, error 1051, and says "still running" only of one that runs: one still
starting, stopping, resuming, pausing or paused when the wait ended is "not stopped yet", in that state,
`NotDoneCause.NotSettled`, and one that was paused when Windows refused its stop is "still paused", `NotDone.Paused`), and its record keeps the process it ran on in (`ServiceChange.NotStoppedPid`; 0 when that was
not known, and then any process counts). While it runs on in that process, the State column says "set to Disabled, still
running" (while it is in one of those states, "not known to have stopped") and the self-repair check does not warn
(`ServicePlan.KeptRunning`); once it is seen STOPPED (`ServiceView.NeitherRunningNorStopped` false), in another process
or at another start type, it is read as any other. The check skips a service from the save of its Turn off's record
until that mark is saved, as it skips one being put back. Putting back only a service whose needed one is still Disabled (Windows error 1068) names the needed one and says
to put it back too (`ServicePlan.NotStartedText`). Turn off, like the run at start, leaves a service that is already as it would leave it (`ServicePlan.IsOff`: at the
start type it sets, and seen STOPPED when that is Disabled) as it is and records nothing for it, so Active changes lists
only what changed. A Disabled service that runs, is paused, is starting, stopping, resuming or pausing, or whose state
Windows could not give is not off, so turning it off again asks for its stop again. Turn off's question lists only the services it
changes and says how many ticked ones are off already. The one exception is a service whose record no longer matches it
(`ServicePlan.RecordDrifted`: neither as PoolWatch left it nor back at its original, so `GateRestore` refuses it): Turn
off (`ServicePlan.OffAlready`) records it again with the first original, so the refusal's advice "turn it off again on
the Services tab, then Put back" and the self-repair warning's "Put back works again after that" hold. The run at start
leaves such a service as it is. The services' texts name only buttons the tab has: a test reads every string of the
Services and Active changes code for an old button's name.

**Service profiles** (`Core/Services/ServiceProfiles.cs`, `Engine/ProfileStore.cs`, `Engine/ProfileAtStart.cs`). A
profile is a name and the services it keeps off, kept in `settings.json` (`serviceProfiles`: the profiles, the one
applied at start, and its delay of 1 to 15 minutes, 3 by default). The built-in "Telemetry only" and "Agentic coding
PC" are not stored: they tick the catalogue's telemetry services, or the services it marks for the preset that also have
an official source (`ServicePlan.InAgenticPreset`), that are not off yet. Saving,
renaming and deleting go through `LiveSettings.Apply` (section 14); deleting changes no service. A new profile past 50
is refused (the save dialog says so before Save), since reading would leave it out. A choice at start that names no
profile (written by hand, or its profile left out when read) applies nothing, and a save or a rename onto that name
drops it unless the save dialog's box is ticked, so a profile never applies at start without that box. A profile holds
only services the catalogue lists that are not protected (counted as for an Insider PC, since enrolment is not known
when settings are read). Every read of `settings.json` cleans the profiles and fails closed, each with a log line at
start: a profile not in the form PoolWatch writes, with a bad or taken name, beyond 50 profiles or listing more than 200
services or something that is not a service name is left out; a protected service, or one the catalogue does not list,
is dropped from its profile; and a choice of profile or a delay that cannot be read applies nothing at start.

Applying at start runs only in the tray app, and not in CI's UI test, not with `--config` (a file read as written, which
anyone may have written) and not without administrator rights (no original could be recorded); each of those says so in
the log when a profile is chosen. After the delay, on a worker thread, it waits for its turn as one change of services
(`ServicesController.OneChange`, the operation lock) and only then reads the running settings again
(`ProfileAtStart.Narrowed`). An edit made since PoolWatch started only narrows the run: a profile deleted or no longer
chosen is not applied, another profile chosen (or the chosen one renamed) is applied from the next start, and of the
chosen one only the services it listed at start and still lists are turned off (one added meanwhile waits for the next
start, and the log says so; one the profile listed already when PoolWatch started is still turned off by this run, even
after a No to "Turn them off now?", which says so while the run waits (`ServicesController.WaitingAtStart`, set by
`ProfileAtStart.Start` until the run ends)). It then calls `ServicesController.ApplyProfile`, Turn off's own path:
while `service-changes.json` cannot be read it changes nothing; otherwise each service of the profile that is installed,
on the catalogue, passes Turn off's gate (`ServicePlan.BuildApply`: never a protected service, the Insider rules
included) and is not already as Turn off leaves it (`ServicePlan.IsOff`) has its original recorded first and goes
through the live gate, and every other one is left as it is, with why in the log. Each change is a log line and a
finding, with "profile NAME, at start" as its source, and a row in Active changes. The tray's notice says what the run
turned off and what it could not, with the log file's path for why (when it would pass the 250 characters a notice
shows: fewer names, then "The profile applied at start" for the profile's name, then no "Click here"; the log's path and
"says why." stay whole, and no word is cut), and a click on it opens Active changes (`BalloonClick`: each notice its own action, run once, so a click
never runs an older notice's). The self-repair warning for a service turned back on names the profile only when it lists
the service and would turn it off as it is now (`ServicePlan.OnAt`). Nothing asks the user at start: ticking "Apply this
profile when PoolWatch starts" in the save dialog is the consent, and a save that makes a profile apply at start while
some of its services are on offers to turn exactly those off now, through Turn off's own question, recorded as the
profile's change. Putting back (in Active changes, or with Put back... on the Services tab: one, all, or to its Windows
default) a service the at-start profile lists warns first when the service is on afterwards
(`ServicePlan.OnAfterRestore`: never for a Windows-default record, whose put-back sets Disabled again, nor for an
original that is as Turn off leaves a service; `ServicePlan.OnAfterDefault` for a Windows default) and, once confirmed,
takes the service out of that profile (`ProfileStore.KeepOn`: from `ServicesSource.Undo` for a row that showed the
warning and a record put back on, and from `ProfileStore.RestoreKeepingOn` and `DefaultKeepingOn` for what the restore
put back on), so the next start does not turn it off again. Only the services the question named come out:
`ServicesTab.DoRestore`, `RestoreSelected` and `RestoreSelectedToDefault` pass them on (`named`, `confirmed`), and a
service the restore turns on that its question did not name (turned off after it was asked, by the run at start or
`--service-off`) stays in the profile, with a result line that says so. The take-out runs inside the restore's own
operation (the `thenHeld` step of `ServicesController.Restore` and `RestoreToDefault`), so the run at start, which reads
the profile inside its own, never turns off again a service a restore has just taken out. Put all back says the warning
once for all such rows (`ActiveChange.AtStart`) and asks no box for each. A service a restore turned on is unticked on
the Services tab (`ServicesController.TurnedOn`), so the next Turn off does not turn it off again. `--service-restore`
and the uninstaller never change profiles, so the profile turns such a service off again at the next start unless it is
then as Turn off leaves it (`ServicePlan.IsOff`). The old per-user `settings.json` (a file a standard user could write)
is taken over without profiles, and the background move of the old data folder no longer copies `settings.json`: only
the checked settings migration at start takes it.

**Put all back** asks once, listing every change and every caveat (the warning of the profile applied at start once, at
the end, for all its services); every other change with a warning is then asked about on its own, and only the changes
the question showed are put back, in a fixed order: the shortcut, startup apps, scheduled tasks, services, the
automatic clear, a removed clear task, the token-leak guard, the sign-in task. It never stops at a failure, and its
results list what was not done first. Listing and putting back touch COM, the service manager and the registry, so they
run off the UI thread; the window reads the list only when it opens and after a change, and each change writes a line
on the timeline.

Not listed: cache clears that already ran (the cache refills by itself), programs the user ended, PoolWatch's own data
folder, the older PoolWatch clear-cache tasks it removes by itself (its own unused leftovers), and task or startup
switches made before this version (they are only in `findings.jsonl`).

## 14. Live settings

A saved setting takes effect at PoolWatch's next start, except three: the automatic clear and the token-leak guard
apply at once when setup, a card, Settings' Save or Active changes changes them, and update notices when setup or
Settings does, or the next-clear line, Active changes and the update notice would be wrong until a restart.
Show RAM and the service profiles (section 13) are saved the same way whenever the window changes them.
`LiveSettings.Apply` loads `settings.json`, changes it, normalises it, writes it to a temporary file and moves it over
the old one (not with `--config`, which is read-only); a failed write throws and changes nothing. Then it changes the
running settings object (the sampler reads the automatic clear's values once per sample; a bool or double write is
atomic on x64) and raises `Applied`, so an open Settings form's copy follows and never saves a stale value over a live
change. Turning the guard on starts its sign-in check once, only when it was off. `LiveSettings.Saved` reads what the
next start will read, `settings.json`, for each opening of Settings to start from (section 11); with `--config` that
file is never written, so it returns nothing and Settings starts from the running settings. Settings' Save writes every
setting for the next start through `LiveSettings.SaveForNextStart`, with the service profiles as `settings.json` holds
them then (the Services tab owns them, so an older copy never puts back a profile changed since). With `--config` it
writes nothing at all, so a `--config` file's settings, its service profiles included, never become PoolWatch's own:
Save then puts only the three at-once settings into effect and says that nothing was kept for the next start.

## 15. The automatic cache clear

- **The next clear.** `ClearPolicy.Next` takes the inputs of the rule that clears (`Due`: the settings, the clear
  history, PoolWatch's start time, the experiment flag) plus the clear helper's status, so the Simple view's line
  cannot disagree with what will happen; a test walks every combination minute by minute. The line says when the next
  clear runs ("in 23 min", "when the kernel pool reaches 9 GB"), or why none will ("Automatic clearing is off.", "...
  paused: your own clear task (RAMMap) does this job.", "... needs PoolWatch to run as administrator.", "... waits
  until the experiment ends."), then what the last one did ("Last clear at 09:12 freed 4.7 GB.", "No cache clear yet.",
  "... is being measured.", "... did not run: <reason>.").
- **Escalation.** In "auto" mode, an automatic light (low-priority standby) clear that, measured 2 minutes later,
  freed less than max(512 MB, 10% of the pool before it) is followed right away by the full standby purge: once, inside
  the 30-minute spacing, never twice in a row, and not at all when the last measured full purge freed little too. The
  last one, not an average, so a PC whose light clears used to help gets the full clear at the first light one that does
  not. The line then says both: "Last clear at 09:12 (light) freed nothing, so PoolWatch ran a full clear at 09:14: it
  freed 5.1 GB." One pure rule (`ClearPolicy.Automatic`) that the sampler calls, constant time.
- **PoolWatch's own impact.** The window's changes over a span that holds PoolWatch's clear are measured on the one
  baseline of section 6 (the clear's drop added back, the 2 minutes until its measurement left out), and none is said
  while it is measured; the drop is said as the clear's result. The detector, the incidents and the known-issue
  evidence (section 17) use the same baseline, so a clear's drop, and what the pool does in its 2 minutes, are never
  read as a change; growth after it counts from the level it left.
- **What a clear is like.** A clear is one call that can free gigabytes of the standby list at once: it takes a few
  seconds, and video or the mouse may stutter while it runs. PoolWatch keeps the existing rules (every N hours, or
  when the pool reaches N GB, each on its own: 0 hours clears only by size, 0 GB only on the schedule; a light clear
  first in "auto" mode) and says so once wherever clearing is turned on: the line under automatic clearing in
  Settings, setup's Standby cache step (`SetupText.Stutter`), and the Simple view's card that turns the hourly clear on
  (`SimpleSettings.ClearStutter`, `RunsAClear`).
- **Whether clearing helps here.** Read from every measured clear, by pool tag family (ReFS, NTFS, and the file cache
  every file system shares): `cache\clears.json` keeps, for each clear that ran, the tags it freed (at least 1 MB each,
  at most 12). Setup recommends "Whichever comes first" on every PC and runs no test clear. While clearing is off
  ("Decide later"), the Simple view offers the hourly clear on a PC with a ReFS volume or a Dev Drive, on an NTFS-only
  PC once a clear showed that it helps, or once file-system records that a clear frees grew in the last day
  (`ClearAdvice.ForSimpleView`).

## 16. Environment detection and suggestions

**Detection** (`Engine/Setup/EnvironmentProbe`) is one scan of explicit reads, never a crawl, on a worker thread and one
at a time: the Windows name, display version and build, the Insider channel, the processor (`IsWow64Process2`, so an
x64 process on Arm says Arm), memory, the volumes with their file system and Dev Drive flag and each Dev Drive's trusted
flag, AI tools (the desktop apps' Store packages, Claude Code, AI Code Conductor, and a few fixed paths for VS Code,
Cursor and the Codex and Claude CLIs), a RAMMap clear task of the user's own, the sign-in task, the optional services
running, and Microsoft Defender's real-time protection and Dev Drive performance mode through WMI (never its exclusion
list). It runs at the first start, from the tray menu, and when the main window opens with facts more than a day old or
from another Windows build (one registry read decides); never on a timer or per sample. A read that fails counts as not
known. The result is cached in `cache\environment.json` (no user names, no paths but drive roots) and read back as
untrusted.

**Whether the token leak applies.** Builds before 24H2 (26100) do not have it. On 24H2 and later, a saved verdict for
this exact build decides; else the live foreground lock timeout: 0 means something already works around it, not
reported means unknown, and otherwise setup runs the check (`TokenLeakGuard.TestOnly`: the guard's two helper runs,
usually about 20 seconds, no change to the workaround, the verdict saved for the next sign-in check). The CI UI test
never runs it.

**The token-leak watch** (`Core/Recommendations/TokenLeakWatch`). When setup found no leak, or could not tell, PoolWatch
keeps watching: with the guard off, once Windows was updated since the last saved verdict (or none was saved), it runs
the same check-only test by itself, once per build in a session (`TokenLeakWatch.ShouldCheck`), from the window while
it shows and from the tray every 30 minutes once setup was completed (`TrayApp.TokenLeakWatchEvery`); and the leak's
own known issue matching the evidence (section 17) counts as seeing it (`TokenLeakWatch.Seen`). Either way the Simple
view offers the guard; nothing here turns it on.

**Impact levels** (`Core/Impact/ImpactRules`, pure; one test pins the scale). Measured: the growth per hour with
PoolWatch's own clears added back (`GrowthWindow.PerHour`, never the net change), as a day: None under 10 MB an hour,
Low under 5 GB a day, Medium under 10 GB a day, High from 10 GB a day; a fall is None, and there is no level until 30
minutes are measured. GB is 2^30 bytes. Setup's first-run levels come from the facts (`ImpactRules.FromFacts`): a ReFS
volume or a Dev Drive, High; Defender's real-time protection on with a Dev Drive it checks first (performance mode off,
or the drive not trusted: `ScannedFirst`, the rule the Defender advice uses too), Medium, and with no Dev Drive at all,
Low; the Codex desktop app installed, Low; services PoolWatch knows running, Low; the token leak found, High; anything
unreadable counts as not found. What PoolWatch measures for a cause wins (`Combine`).

**One model of suggestions** (`Core/Recommendations`, pure, unit-tested on Linux). What the PC is set up with is decided
in setup (section 11), not drip-fed later: the Simple view's offers (`RecommendationRules.Current`) are only what
PoolWatch found after setup that one click puts right, at most three by priority, each only on evidence PoolWatch
already holds in memory or in the cached facts, and none hidden in the last 30 days: the token-leak guard (a check's
verdict for this build, the leak's findings in the last day, or its known issue's evidence), the hourly clear while
clearing is off (with section 15's evidence), and reporting an incident of the last 7 days whose cause has at least
some evidence. The rules' full list (`OnEvidence`: also replacing a clear task, checking the build for the leak, start
at sign-in, reviewing services that cost real processor time, Defender's performance mode for a Dev Drive whose
file-system records grew or while Defender was busy, and the Codex shortcut when PoolWatch named the app in a growth)
is what the rules' and the known issues' tests read. After the offers come the feature cards (section 11). Building
the inputs reads memory only. Texts are short (a title of 60 characters, a reason of 240, a button of 16) and tested for
setting names and jargon. The Defender advice is instructions only: PoolWatch never changes Defender settings and runs
none of its commands. A suggestion that a known issue explains takes that issue's reason (section 17).

## 17. Known issues (knowledge packs)

One JSON file per known issue in `knowledge/`, embedded in PoolWatch.Core and read once on first use, as untrusted: at
most 32 KB, strict JSON, nested at most 8 deep, every field checked against its limits, links only to github.com,
learn.microsoft.com and support.microsoft.com; fields PoolWatch does not know are ignored (so a newer pack loads in an
older PoolWatch), and a pack that fails a check is left out with the reason in the log. The format is in
[knowledge-packs.md](knowledge-packs.md).

The evidence is gathered from memory under the history lock, only while the window is visible and at most once a
minute: the cached PC facts, the live processes (Store packages by their folder name), the live foreground lock timeout,
each pack tag's growth over the last hour on the baseline of section 6 (at least 10 minutes; none while a clear is
measured), and the measured clears of the last day with the tags they freed. Matching is pure. A matched issue is a
note in the Advanced view next to its pool tags; the note says what PoolWatch already does about the issue, or names
the suggestion that offers the fix while it is offered. The issue also gives a suggestion the rules already make its
reason; it never adds or removes a suggestion. An issue that something in effect stops is not named: the token-leak
issue is hidden while the Windows setting that stops it is on, whoever set it (the guard being on is not enough, since
Windows can refuse the setting), because what grows then has another cause.

## 18. Update check

Information only (`Engine/UpdateCheck.cs`, `Core/Updates/Releases.cs`), in the tray app only, while the setting is on
(setup's last step, on by default; Settings). It starts once setup has been completed: at start-up when it was
completed before, otherwise when setup finishes; its last step has the switch. One one-shot timer that looks at
the clock at least every six hours: the first check 3 to 6 minutes after it starts (or a day after the saved last
check), then a day plus up to an hour after each check, by the clock, so a PC that sleeps still checks daily. While
the setting is off nothing is sent and the check stays due.

A check is one GET of the latest release from
`https://api.github.com/repositories/1435312349/releases/latest`: the repository by its number, which stays with it
through a rename or a transfer, so whoever holds the name later cannot answer for PoolWatch. It has no credentials, no
cookies and no redirects, the User-Agent `PoolWatch/<version>` and `Accept: application/vnd.github+json`, a 15-second
limit and a 1 MiB answer limit (refused unread when its length says so). Only a stable SemVer tag counts (pre-releases
and drafts are never offered), and only when its page is exactly that release's page,
`https://github.com/nubbymong/poolwatch/releases/tag/<tag>`, so a notice never names one release and opens another,
and never a download. The tag is compared with the running version by SemVer precedence; a newer release is announced
once per version, also across restarts. The window's footer says "PoolWatch x.y.z is available. See what's new" and
the tray menu "PoolWatch x.y.z is available"; the link and the menu item open the page through the same allow-list as
every other link. Both read the release each time they are drawn (a release taken back gives the older one, or none)
and show nothing while the setting is off. Nothing is downloaded or run. The last check and the latest stable release
seen live in `cache\update-check.json`, at most 4 KB, read back as untrusted. Failures (offline, the rate limit, a
refused or malformed answer) are quiet: the first is logged as one line, the next only after a check works again. The
first check loads the HTTP and TLS libraries once (a few MB); the timer wakes at most four times a day and does no I/O
then.

## 19. Installer, uninstall and the exit signal

**The setup** is made with Inno Setup 6.7.3 (downloaded in CI from a pinned address and checked against its SHA-256)
from the self-contained build: .NET included, the self-contained exe installed as `PoolWatch.exe`, with the Codex
shortcut's launcher, `AGENTS.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md` and the runtime's own licence files. It installs
to `%ProgramFiles%\PoolWatch` only, with the permissions of Program Files, which `InstallGuard` expects: there is no
folder page, and a setup started with `/DIR=` elsewhere stops before copying anything. One x64 build; on Windows on Arm
it installs too, and runs under Windows' x64 emulation. An upgrade first runs the installed `PoolWatch.exe
--stop-running` (an older one without the option is closed by Inno Setup's own step). "Start PoolWatch now" starts it
with the setup's administrator rights, and PoolWatch's own setup appears at that first start. In a release, the setup,
its uninstaller and the launcher are signed with the same certificate as PoolWatch.

**Uninstall.** The uninstaller's work runs at Inno Setup's `usAppMutexCheck` step: after its "Are you sure?" and before
any file goes, where an abort cancels cleanly. By default it puts back everything PoolWatch changed that it can (the
list's `uninstallLeaves` says beforehand which it cannot, and why). It first asks PoolWatch for its list
(`PoolWatch.exe --changes-dump` into the data folder as `uninstall-list.json`, read only when PoolWatch wrote it, exit
0, and deleted once read; otherwise it touches nothing in the data folder). Started by hand, it shows what it will put
back, what it cannot (with why) and the token-leak guard's sign-out caveat, and asks: Put them back (recommended, the
default) or Keep my changes; a question that cannot be shown means putting back. A silent uninstall and `/DELETEDATA`
put back without asking; `/KEEPCHANGES` keeps the changes. Names in its boxes are shortened to fit; the log keeps them
whole. Then it runs `PoolWatch.exe --uninstall-cleanup [--keep-changes]`,
hidden. That stops PoolWatch through the exit signal and holds its one-instance mutex (so nothing starts meanwhile)
while it removes the sign-in task and the shortcut and, unless asked to keep the changes, runs the ledger's Put all back
(section 13). It prints and writes
`uninstall-summary.txt`, each change "Put back", "Could not put back" (with why) or "Kept". Exit 7 means PoolWatch is
still running (interactive: Retry or Cancel; silent: the uninstall stops), 8 that something was not put back. No file
is removed while a PoolWatch still holds its mutex. Started by hand, it then says what it put back ("PoolWatch put back
each of these changes:"), with the sign-out caveat, also after a `/DELETEDATA` uninstall. Deleting the data folder is
offered only when every change was put back (default No; silent: only with `/DELETEDATA`), so a later PoolWatch can
still put back what is recorded there.

**The exit signal** (`Engine/ExitSignal.cs`) is an auto-reset named event, `Global\PoolWatch-Exit`, with an admin-only
DACL (Administrators and SYSTEM), created by the tray app and by a headless run. Listening is one registered wait on the
thread pool's shared wait thread; nothing runs until it is set. Setting it is the normal clean exit: open incidents are
closed and written, and the token-leak guard's failsafe runs. `--stop-running` and the cleanup set it every 250 ms and
wait up to 60 s for the one-instance mutex (which an exiting PoolWatch leaves abandoned; the cleanup takes it either
way). A name that already exists, which PoolWatch did not create, is not used: the same exposure the one-instance mutex
has.

**The Codex shortcut** ("ChatGPT (no shader cache)" on the desktop; the Codex desktop app's program is `ChatGPT.exe`).
A packaged Store app cannot be activated from an elevated process, so the shortcut runs a
launcher script, `chatgpt-no-shader-cache.ps1`, unelevated, through
`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File`. With `Bypass`,
PowerShell does not check the script's signature when it runs: what protects the script is the admin-only install
folder it sits in. The script finds the Codex or ChatGPT desktop package, its application and its main process itself
(no fixed names or paths), starts the app through Windows' app activation with `--disable-gpu-shader-disk-cache`,
brings it to the front if it already runs that way, and asks the user to quit it first if it runs without (it never
closes it). PoolWatch writes the shortcut safely from its elevated process into a folder the user can change: it
builds the bytes in the data folder, checks them with its own `.lnk` parser, then writes them as a new file, never
through a link; the Desktop and each folder above it up to the profile must be real folders, except OneDrive's
cloud-file folders, and the written file's final path must be the intended one. Its SHA-256 goes into the ledger, and
Remove in Active changes and the uninstaller delete it only if it still matches.

## 20. Known limitations

- **Over-the-shoulder elevation.** PoolWatch runs as the account that approved the administrator prompt. When a
  standard user elevates with another administrator's password, the per-user facts (AI tools in the profile, Store
  packages) and the Desktop that gets the shortcut are that administrator's.
- **What Active changes does not list** (section 13): cache clears that already ran, programs ended, PoolWatch's own
  data folder, the old PoolWatch clear-cache tasks it removes by itself, and task or startup switches made before this
  version.
- **No defence against .NET start-up variables** yet (SECURITY.md, Known limitations): the first item after 1.0.
- **The token-leak guard's setting outlives Turn off**: turning the guard off stops PoolWatch setting it again, but the
  live setting lasts until the user signs out.
- **A service profile applied at start asks nothing at start.** Its consent is the box ticked when it was saved. The
  uninstaller does not change profiles, so a data folder kept through an uninstall applies the profile again under a
  later PoolWatch. `--service-restore` never takes a service out of it (section 13).
- **The growth and clear stats** (section 11) count a clear whose purge fails only after its 2-minute measurement as
  cleared, and drop the minutes wholly after the new time of a clock set back (at most the step's length is counted
  twice); a clear still being measured when the clock is set back is not counted.
- **Known issues only explain.** A pack gives a reason to a suggestion the rules already make; an issue with no
  built-in action (the NTFS records one) shows only as a note in the Advanced view.
- **Windows on Arm and Windows 10 22H2 are not tested on real hardware.** On Arm the x64 build runs under Windows'
  emulation, which only CI's Arm jobs (section 12) are set up to check; Windows 10 22H2 has no check at all.
