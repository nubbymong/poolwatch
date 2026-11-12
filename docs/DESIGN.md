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
  for its bounded length only. Steady state starts 15 minutes after PoolWatch starts, the window the soak judges
  (section 12). Before that is start-up: the first scans, the windows built, the JIT and the start-up trims. In a CI
  soak with the window open PoolWatch used 1.4% of one core between 30 s and 15 minutes and 0.46% after; in the CI UI
  test About was drawn about 90 s in, right after the test built, drew and saved every window and before PoolWatch's
  next trim, and the 138 MB of working set it showed then was that start-up. About judges the budget once steady state
  has lasted 5 minutes, with its CPU, its highest working set and its disk writes, and before that says only when it
  will (`SelfMeter`); the rolling line in every report ("PoolWatch itself (last 10 min): CPU 0.21% of one core, 64 MB
  now, 1.2 MB/h written": the CPU and the writes over the window, the working set as it is now) warns that PoolWatch
  is over budget only over a window that is all steady state. Both read as About opens, so windows built just before it
  count: at the end of the CI UI soak, drawn after the 8 windows (16 screenshots) that walk made in about 4 s, About
  read 127 to 131 MB of working set, where the steady peak was 86 to 96 MB, and called it over budget. The CI tests now
  draw About first, and once any window of PoolWatch's other than the main one closes, its memory is given back
  (below).
- **Report folders inside the write budget.** On a PC doing agentic coding (the owner's: about 200,000 process starts
  and 9,400 recorded events an hour, 7 to 10 incidents open at once in a pool episode) the incident and storm folders
  wrote 140 to 225 MB an hour in 1.0.0-beta.2: every open incident's whole folder (events.csv alone 1.2 to 1.3 MB) every
  5 minutes, and a folder for every spawn storm (about 3,000 a day, most of them Git Bash fork copies). They now share
  one write budget (`Core.Incidents.WriteBudget`, section 10), the report folders' share of the 5 MB an hour beside the
  day's CSV files' 2.5 MB with Detailed recording (below; with Reports only there are no day files and no storm
  folders): what can wait is written while the budget has room, 1 MB an hour with a burst
  of 256 KB, first an open incident's `report.json` and `report.md` (what an export and an AI assistant read; the
  window's details and Copy summary of an open incident are built from the incident in memory, so they are never behind
  it, and Export report writes its folder whole first), the one
  written longest ago first, then its events' rows while no report is due, and a storm folder's update, an open
  incident's chart and CSVs only while the budget has room to spare; what a reader needs at once (an incident opening,
  re-opening or closing, a trace attached, a storm's first and last folder write, the end of a run) is always written
  and spends it too, its debt at most the burst (a quarter of an hour's refill), so ten incidents opening together hold
  the refreshes back that long and no longer. A file that did not change is not written again, an open incident's
  events.csv and spawns.csv are appended to, and each write adds at most 100 of an incident's events; an incident's
  closing write replaces its events.csv once per close, whole, with both ends of its window (the first 500 events
  from the window's start as it is measured then, which the first part follows, and its latest
  500, section 10: about 0.29 MB a long incident at the owner's event mix and command-line lengths, 0.14 MB of it the
  latest 500; `BothEndsTests`), a write that must be made like the rest of the closing write. That is the cost of
  keeping both ends (the owner's choices, 2026-11-10: both ends, then 500 at each end rather than 1,000, whose close
  wrote 0.46 to 0.58 MB). Ten closing in one hour after an hour of growth write 2.8 MB of events.csv (291 KB each), and
  5.8 MB with the rest of their closing writes (7.5 MB at 1,000 each end), over the 5 MB an hour for that hour. Through
  a day of long incidents closing about 3 an hour (the maintainer's rate: 16 held after 4 hours), events.csv writes
  0.94 MB an hour (303 KB an incident), all but 0.02 MB of it writes that must be made, and the report folders 2.24 MB
  an hour in all (1.90 MB of it writes that must be made), over their 2 MB share by 0.24 MB (1,000 at each end: 1.83
  and 3.11 MB). That is 0.78 MB an hour more than 976632c, which kept the first 1,000 alone, wrote in the same day
  (1.46 MB: its events.csv 0.18 MB an hour, 58 KB an incident, the rest of the first part waiting for a budget that
  seldom had room for it; the folders' other files the same, about 1.3 MB an hour in both): to write no more than that,
  each end would have to be about 100 events. With the day's CSV files (2.10 to 2.16 MB an hour, below) and
  findings.jsonl (0.3 to 0.6 MB an hour), Detailed recording writes about 4.6 to 5.0 MB an hour before the log and the
  caches (those three together; `poolwatch.log` and the files under `cache\` are not in that figure, nor measured
  here), at the 5 MB budget rather than inside it; Reports only writes the report folders' 2.2 MB and none of the
  day's (`BothEndsTests`, 19 incidents 18 minutes apart over 6 hours; 976632c's and 9f3efdb's figures from a run of the
  same test beside them on the same PC). `WriteBudgetTests`
  measures this
  at the write check every file goes through: 10 growth incidents opening and open for an hour beside a storm-heavy hour
  (three sessions bursting every 2.5 minutes, a new program storming every 2 minutes, Git Bash fork copies), at the
  owner's event rate and command-line lengths, write 1.7 MB in that hour (1.0 MB of it the openings and the storm
  folders' first writes, which must be made), under its 2 MB share; each open incident's report there is at most an
  hour behind (an incident's report is about 90 KB, ten of them refreshed every 5 minutes would be 10 MB an hour).
  It also measures the CPU of that work on its thread, and asserts under 18 s in the hour, the 0.5% budget. The rest of
  the overhead on the owner's PC (2.9% of one core and 254 MB in 1.0.0-beta.2, `cache\profile.json`: the ranking, the
  allocations, and the day's CSV files at about 4.7 MB an hour with the handle scans' write of every one of them) is
  outside the report folders: the ranking and the memory are the next item, the day's files and the handle scan the
  items after it. What the budget spent and held back is in About (beside
  PoolWatch's own overhead: what the report folders wrote; what of it a reader needed at once, written whatever the
  budget; what could wait, within or over what the budget allows it so far (`WriteBudget.CanWaitAllowance`: the refill,
  the burst it starts with and one burst more, as such a write is made with any room left and debited whole); how many
  times a write was held for a later sample; the open incidents whose report is behind now, the oldest since when, and
  of them those whose last write failed; and those whose chart and CSV files are 30 minutes old or more) and in
  `cache\profile.json`'s `writeBudget`, so a deferral is never silent and nothing is said to be within the budget that
  is not.
- **CPU and memory under agentic load.** On the owner's PC (`cache\profile.json`, 1.0.0-beta.2 after 7 hours of
  agentic load, about 236,000 process starts an hour) PoolWatch spent 2.4% of one core (`tick (all)` 1.46%, of it
  `analysis.incidents` 0.43% and `diagnostics` 0.36%), allocated 15.5 MB a sample and 2.3 MB more in its ETW callbacks
  (49 GB in the 7 hours, the GC's own CPU on top) and held 170 MB of heap, its floor after each full collection rising
  from 14 MB to 120-200 MB. Most of the CPU was the suspect ranking every open incident got every 30 s (10 open at
  once: 2,000 suspects each, 12 rank correlations a suspect); most of the heap what grew with the hours: every
  process's points for 70 minutes at 48 bytes, compact records of every short command of a 2-hour incident window, the
  registry's exited processes for a day with their kilobyte scripts, each incident's 5,000 events and every owner's 20
  members for 8 hours, 26 incidents at once, the minutes' every busy process for 25 hours. Now:
  - an open incident's episode (onset, end, growth: what its row and the tray read) is measured every 30 s, its
    suspects ranked only before a rewrite of its report (each report holds the ranking of the moment it is written),
    every 2 minutes while the write budget has room (a new top suspect is written then) and at least every 10 (the
    cause shown from memory); a counter that is 0 in most samples is ranked without sorting its zeros (the ranking is
    the same to the bit);
  - a process's points are runs: a point equal to the one before (an idle process's) takes no space, a new one 28
    bytes (its I/O counters packed as a rise from the oldest held), exactly as added; a handle scan is held as flat
    arrays (18 scans of 700 processes with 15 types: 2.1 MB, 9.4 MB as the dictionaries they come in); the raw rows of
    tags share their list of tags with the row before; a minute older than 2 hours keeps its tags and totals and its
    processes are summed into its hour, the 10 busiest by name and the hour's total, which is all the since-boot report
    reads of them (its report is the same, `CpuMemoryBoundsTests`; the oldest hour, partly past the 25 hours, is shared
    out over all of it);
  - a Git Bash fork copy holds its parent's command line; a short command past the newest 3,000 of an incident's window
    stays as a compact record only when a ranking can name it (a snapshot listed it, or it is the ancestor of one) or a
    trace is recording, and only within the raw history (less 10 minutes); an exited process past the raw history and
    20 minutes more becomes a named record (name, services, label, times, a command line only when short) for the
    rest of its day, at most 10,000, the first named going first (on the owner's load 3 to 4 hours past the 90 minutes;
    the since-boot report names an hour's processes as it sums it, 2 to 3 hours after they ran); each PID's newest
    instance and image are kept for the records held only; the string pool holds at most 4,096 strings;
  - an incident keeps the first 500 events by time from its window's start (taken again from the 90-minute buffer
    when the start is measured somewhere else, section 10), and the latest 500
    up to its window's end (500 more before those while it is
    open, so an end of its growth measured up to about 3 minutes earlier still leaves 500; and, while it can still take
    them into its window, the events after its end, at most 4,000), and its events.csv says how many it left out between
    them and when (with Detailed recording the day's events.csv has every one of the time it was on; 5,000 from the start held 3.2 MB more in
    the test below at 6 hours than 1,000, and about 29 MB more with all 32 incidents at the cap; an open incident's
    latest are the newest, which the 90-minute buffer holds too, so with 10 incidents open for 3 hours at the owner's
    rate they hold 0.16 MB, 0.31 MB with 1,000 at each end; a closed one lets go of its latest part an hour after it
    closed, when it can no longer re-open and its events.csv holds both ends as written, so for the rest of its 6
    hours it holds its first part alone: 19 long incidents closed over 6 hours hold 0.34 MB of latest parts, those of
    the last hour, where keeping them for the 6 hours held 9.2 MB with 1,000 at each end, `BothEndsTests`)
    and the members of its first three owners only (its reports are byte-identical); at most 20 closed incidents
    stay in memory; the summarizer asks the worktree-delete expression only of a command line with "tree" in it, and
    the detector reads each tag's series into one array, each row's place of a tag found once per shared list.
  An incident open longer than the raw history keeps its onset and start level instead of moving them to the
  history's first sample (its growth no longer shrank as the history moved on, its events.csv no longer lost its rows
  as "before the window"), and counts PoolWatch's own clears that freed part of it after they left the history, as
  measured then, in its growth and in what they freed (the same as a history holding the whole incident gives).
  `AgentLoadTests` replays a day of that load (26 hours) through every per-sample part of the core: about 600 process
  starts a sample with Git Bash's fork copies, 600 long-running processes (560 of them idle in a sample, so their
  points are long runs; a real PC's are busier), 10 incidents growing all day and 40 more every 4 hours. On 9144b20's
  code that world took 2.6% of one core and 40 MB a sample over 4 hours, its heap after a full collection 45 MB at half
  an hour and 101 MB at 4 hours, still rising (this change as first reviewed: 54 MB at 4 hours, about 70 MB at 24).
  Now, with both ends of 500 kept and the first part following the window's start (a Release build, every figure
  from one run): 0.170% of one core and 3.96 MB a
  sample: incidents 0.118% and 2.4 MB, registry 0.0138% and 0.46 MB, detector 0.0097% and 30 KB, summarizer 0.0093%
  and 0.59 MB, the diagnostics' core parts 0.0140% (spawns 0.0078%), history 0.0047% and the incidents' event intake
  0.0005%; the heap 35.4 MB at 4 hours and 36 to 40 MB from 6 hours to the day's end (averaging 37.6 MB), a slope of
  about 0.14 MB an hour as the minutes' tags fill their 25 hours, the hours' incidents moving it up and down by about
  2 MB. Run before, one after another on the same PC: 976632c, which kept the first 1,000 alone, 0.125% of one core,
  3.8 MB a sample (incidents 0.090% and 2.3 MB), the heap 36 MB at 4 hours and 36 to 40 MB after (averaging 38.0);
  9f3efdb, 1,000 at each end, 0.136% and 0.149%, 4.06 MB a sample (incidents 0.096 and 0.105%, 2.6 MB), the heap 38 MB
  at 4 hours and 37 to 43 MB after (averaging 39.4 and 40.1); 500 at each end, run after 9f3efdb, 0.157% and 0.166%.
  Those totals differ from run to run, and not with the code: against 976632c's run, the two runs at 500 measured the
  parts this change does not touch higher too (the registry by 36 to 47%, the detector by 31 to 37%, history by 33%,
  the diagnostics' parts by 42 to 65%, the summarizer by 14 to 24%; the incidents part by 23 to 28%), and so did the
  run above (the registry by about 54%, the detector by 45%, history by 30%, the spawn tracker by 63%, the summarizer
  by 33%; the incidents part by 31%). Seven runs of the code as it was before the first part followed the window's
  start measured 0.114% to 0.154% in all, in the lowest of them every part at or below 976632c's, and 3.96 to 3.97 MB
  a sample; run once more in the same hour as the run above, that code measured 0.178% (its incidents part 0.123%),
  and one more run of this code 0.168%. Following the start costs nothing these runs can tell from the
  difference between one run and the next: the event intake, where each follow takes the buffer again, measured
  0.0005% to 0.0006% against 0.0005%, and with `BothEndsTests`' ten open incidents 0.7 to 0.9 µs an event against
  0.6. Nor does what came after it (section 10: the latest part and its counts taken again when the window's end is
  measured somewhere else, a closed incident's writes that must be made giving its file every row, in time order):
  two runs of that code, four hours later the same day, measured 0.142% and 0.153% (incidents 0.102% and 0.110%,
  the event intake 0.0004% and 0.0003%), 3.97 MB a sample, the heap 35.5 MB at 4 hours and 35.8 to 39.7 MB from 6
  hours to the day's end (averaging 37.6 to 37.7), and the report folders wrote what they did (below). 500 at each end
  holds about 2 MB less than 1,000 (hours 6 to 26 average 37.6 MB against 39.4 and 40.1; the events the incidents
  hold: about 54,000 against 75,000, and 30,000 with the first 1,000 alone), about what 976632c held. Each of these
  runs had `BothEndsTests` and `WriteBudgetTests` in its process; alone in its process, `AgentLoadTests` reads about
  2 MB more at every hour (37 MB at 4 hours, 37.6 to 41.8 MB from 6 hours to the day's end), for this code and for
  the code before it alike. It
  asserts a 48 MB
  ceiling, a slope over hours 6 to 26 under 0.25 MB an hour, under 3 MB more in the last 6 hours than in hours 6 to 12,
  under 0.45% of one core (three times what it measures, for a slower runner; the old 30 s ranking alone is 1.3%) and
  under 6 MB a sample. What was held at 26 hours before both ends were kept, part by part (each cleared in turn, not
  measured again since): the raw history's process runs 4.8 MB and tag rows 2.7, the
  90 minutes of events 3.8, 20 closed incidents 4.5, the registry's full records 6.0, compact 2.5, named 2.5 and
  per-PID 2.2, the handle scans 0.6, the string pool 1.0, the spawn tracker, census and tokens 3.4; the other 7 MB
  (the minutes among them) clearing part by part does not split.
  The target for that heap is not met. PoolWatch's own trim empties its working set at 75 MB only while the heap is at
  most half of it (`MemoryTrim`), 37.5 MB; in the CI soak with the window open the working set peaked at 84 MB beside
  a 15 MB heap (about 69 MB that is not heap), which leaves about 30 MB of heap under the 100 MB budget, the app's own
  share (the window, the tag knowledge, the CSV files' state) included; this load's parts alone hold 36 to 40 MB. On
  the owner's PC the memory outside the heap is larger still: 118.5 MB private at 32 s beside a 13.9 MB heap, 285 MB of
  working set beside 196 MB committed by the GC at 7 hours; what holds it is not in `profile.json` and is outside this
  part. The CPU, projected for the owner's PC from its beta.2 profile (each part changed here scaled by what it took in
  the world before and after, the rest as measured there), is not within the 0.5% either:

  | Part (owner's PC, % of one core) | beta.2 | projected | from |
  |---|---|---|---|
  | `analysis.incidents` (ranking, episodes; writes are `incident.write`) | 0.43 | 0.014 | world 2.50 → 0.082 (as first measured, the basis of every row here; later runs measured 0.084 to 0.123 with the run, which projects 0.014 to 0.021) |
  | `analysis.detector` | 0.11 | 0.050 | world 0.0145 → 0.0068 |
  | `tick.summarize` | 0.053 | 0.027 | world 0.0127 → 0.0064 |
  | `tick.registry`, `analysis.history` | 0.047 | 0.047 | unchanged in the world |
  | `diagnostics` (core parts in the world 0.008) | 0.36 | up to 0.36 | app-side parts not measured; now `diagnostics.*` by part |
  | `tick.processes` (the kernel's process query) | 0.21 | 0.21 | not changed |
  | handle scan, `incident.write`, CSV files | 0.23 | about 0.06 | the scan's query 0.04 as measured; its write 16 ms of CPU a scan and the day's files under 0.01% (`HandleCsvWriterTests`, `DailyCsvBudgetTests`, below); `incident.write` within the write budget |
  | ETW callbacks (`etw.start`, `etw.stop`, `enrich`) | 0.15 | 0.15 | not changed |
  | the window (`ui.*`) | 0.08 | 0.08 | not changed |
  | the other stages (labels, trims, services, pool tags) | 0.02 | 0.02 | not changed |
  | outside the stages (the GC, timers, the thread pool) | 0.73 | about 0.35 | allocations about 18 → about 8 MB a sample, ETW callbacks included |
  | **all** | **2.42** | **about 1.3 to 1.5** | |

  So the CPU still over budget on that PC is the diagnostics' app-side parts (load and PDH reads, services, the cache
  clear, storm folders, lag, marks, findings; `diagnostics.*` in the next `profile.json` say which), the process
  query, the ETW callbacks (3.4 KB allocated a start), the handle scan's query and the GC's share; under the CI soak's
  lighter load PoolWatch keeps to it (0.46% with the window open).
- **Recording is a choice.** On the owner's PC 1.0.0-beta.2 kept about 860 MB of day folders and 16,057 incident
  folders, written whether or not anyone would read them. The day's files and the spawn-storm folders are now written
  only with **Detailed recording** (`Settings.RecordMode`, set in setup's Records step and in Settings, read at each
  write so it applies from the next sample); the default, **Reports only**, writes a report folder each time the pool
  grows (and the reports of a crash or restart and of an experiment, as before) and no record of the day: beside the
  reports it keeps the log, the settings, the caches, `setup.json`, each boot's baseline and summary (`boots\`) and, at
  each exit, the Since boot report and `boots.md` made from them, while storms are still found, shown and noticed from
  memory. Most people need only the reports; a leak that builds up over days, or a developer asking for more, needs the
  day. People upgrading from 1.0.0-beta.2 get Reports only and are asked once to choose, by the Records step alone,
  which opens at each start until they press Done (or save a change of the record mode or the cleanup in Settings).
  The budget below is judged in the worst case, Detailed recording,
  which the CI's soak, overhead and agentic-load runs use (`recordMode` in their settings files); the CI UI test runs at
  the default and checks that it writes none of the day's files and no spawn-storm folder, while a short burst of
  commands it starts is found as a storm and listed in Pool causes with no folder to open.
- **The day's CSV files inside their share** (Detailed recording). On the owner's PC in 1.0.0-beta.2 the day's folder took 112 MB a day:
  `events.csv` 2.2 MB an hour, `tags.csv` 0.95, `processes.csv` 0.75, `services.csv` 0.31, `handles.csv` 0.19. Their
  share is now 2.5 MB an hour, which with the report folders' 1 MB leaves 1.5 MB for `findings.jsonl` (0.3 to 0.6 MB an
  hour there), the log and the caches. Outside verbose mode (`CsvSink`, AGENTS.md's file guide says the same) each day's
  files begin with the whole picture (every process, every tag over 1 MB, the whole services list, every handle row) and
  then hold what changed: a tag's row when its paged or non-paged use moved 1 MB, at least every 10 minutes, and once as
  it falls under 1 MB; a process once it has run a minute (a shorter one is in `events.csv`), then while it is among the
  50 busiest by I/O (any at all), its handles moved by 100, or it is PoolWatch; a service when its host changed (PID 0:
  stopped); a process's handles of a type when their count changed, and once more, with its count then, as it leaves
  that selection while it runs (the per-type totals and the System process's every scan); an `Exit` or `Orphaned` row
  without the command line its `Start` row gave that day (a `Start` row without one is not matched); and the event
  summary's minute rollups merged over 5 minutes (`EventSummarizer.RollupClosed`; the incidents still get the minute
  ones). AGENTS.md says file by file what a reader may carry forward from a row. A file whose kept rows were dropped
  (past the bound on rows no file could take) gives the whole picture again at its next write.
- **The handle scan's write off the tick.** In the owner's beta.2 one handle scan's CSV stage took 41.9 s of wall time,
  24 s of it the sampler thread's CPU, while every file of the day, written at each sample, never took more than 0.2 s
  (`tick.csv`): what took the time was not found, as that build had no finer stages. So a scan's two files are written on
  a thread of their own (`HandleCsvWriter`, through a sink of its own): the tick hands the scan over and goes on; at most
  one scan waits while one is written, and one handed over meanwhile replaces it, counted (`profile.json`'s
  `handleScansNotWritten`) and logged. The write is profiled in parts (`csv.handles.rows`, `csv.handles.commit`), and one
  of 5 s or more is logged with its thread's kernel and user CPU, the GCs and their pause during it and PoolWatch's page
  faults, so the next slow one says where its time went. It records at most 2,000 of the processes' counts, the
  largest, and a scan with more says how many it left out (a pid -2 row).
  `HandleCsvWriterTests` measures an owner-sized census (the System process with 1.4 million handles and 600 processes,
  2 million in all, 820 rows): 16 ms of CPU for the day's first scan and 2 ms for the next, asserted under 250 ms, and
  the tick's hand-over under 1 ms.
- **The day's CSV files, measured.** `DailyCsvBudgetTests` measures the day's files at the sink's opener under the owner's shape for 4 hours
  (samples of about 330 processes with 25 short commands caught in each, 1,100 tags of which 180 over 1 MB and 10
  growing, the owner's hour of events.csv rows at their lengths with 66 minute rollups a minute, the services changing
  25 times an hour, handle scans with a third of their counts moving): 4.6 to 5.1 MB an hour before, 2.10 to 2.16 MB an
  hour now (`events.csv` 1.20, `processes.csv` 0.43, `tags.csv` 0.21, `handles.csv` 0.19, the rest 0.10), asserted under
  2.5 MB in each hour; the sink's own CPU there is under 1 s in the 4 hours (under 0.01% of one core), its allocations
  70 MB an hour (142 before). The CI's agentic-load check (section 12) holds the whole app to the 5 MB.
- **Where it writes**: only under its admin-only data folder `%ProgramData%\PoolWatch` (or an explicit headless
  `--out` folder), apart from the few writes SECURITY.md lists, never in its install folder, never through a link, and never
  on a ReFS volume (every Dev Drive is ReFS: its per-file metadata is the pool being measured). 7-day retention by
  default.
- **No WMI in the sampling path** (it is expensive). Native APIs only.
- **PoolWatch must not become a crawler itself.** Every file-system lookup is bounded: explicit paths,
  modified-within windows, file and byte budgets, early exit.
- **Runs elevated, started through its launcher** (the install folder's `PoolWatch.exe`, manifest
  `requireAdministrator`; PoolWatch itself is `app\PoolWatch.exe` below it, manifest `asInvoker`), and therefore only
  from under Program Files, such as `%ProgramFiles%\PoolWatch`, where its folders, the program, the launcher, every
  file and folder in `app` and every other file below it (its DLLs in `app\amd64\`) must be ones only administrators
  can change, and a folder link on the way is judged where it leads: their access rules are read, never written
  (`InstallGuard`; every account and group that is not an administrator counts: SECURITY.md). The launcher, compiled
  to native code (NativeAOT), judges its own folder and the program by the same rules, and starts PoolWatch with an
  environment it builds from Windows' own values (section 19), so .NET's start-up variables in the user's environment
  never reach the PoolWatch that runs; it has the name and the place the program had before 1.0, so everything that
  starts the install folder's `PoolWatch.exe` starts it, and `app\PoolWatch.exe` started any other way hands its start
  to it first thing (`LaunchGate`; the CI and test switches aside). An optional "start at logon" scheduled task with
  highest privileges, which starts the launcher, avoids the daily UAC prompt; it is only ever created for an installed
  copy, and at every start an existing one is trusted only when it is exactly the task PoolWatch makes
  (`LogonTaskPolicy.Judge`).
- Windows 11 (the setup also installs on Windows 10 22H2, untested). One x64 build, which Windows on Arm runs through
  its built-in x64 emulation. .NET 10 (`net10.0-windows`, WinForms): a framework-dependent build and a self-contained
  build, which the setup installs.
- **Staying light.** What only the window shows (the suggestions, the known-issue notes, the next-clear line) is
  worked out at most once a minute while the window is visible, never per sample. Incidents and the Pool causes
  rankings keep only what they print; a released tag keeps its ranking until the window hides (a released row shows
  no cause; only Copy summary reads it). Hiding the window releases every page's data that its first refresh after
  showing works out again (`MainForm.ReleaseWhileHidden`): the rankings and the Pool causes list, the graph's points
  and clears and its RAM columns (the Advanced view's, with Show RAM on; that refresh reads the graph and the header
  even without a new sample), and the Tasks and startup apps rows with what each costs, in the tab and in its list,
  and the detail panel's text (a read of them that ends while the window is hidden works out no row). What no refresh
  rebuilds stays: the Services list (read again only after a change, Refresh or a change of PoolWatch's record, and it
  holds your ticks), the tasks and startup apps as read (every 5 minutes while their tab shows), the Simple view's
  cards (made again only when what they say changes) and the short texts of the header and the tiles. Opening the
  Advanced view on a tab reads only that tab. A switch of view or tab is laid out once, with the header that changes
  with it (`MainForm.LayOutOnce`): the smaller header of the Services tabs moves everything under it, and laid out one
  change at a time a switch laid the whole view out twice. A new sample lays the header out again only when a text its
  size is measured from changed (the ring of the next clear moves every sample and only repaints). A compacting full
  collection of PoolWatch's own heap, with `EmptyWorkingSet`, runs at 30 s, 90 s and 3 minutes after start-up, when
  the window hides, and when any other window of PoolWatch's stops showing (About, Report an issue, Active changes,
  Settings and its Advanced grid, setup, and every other: `OtherWindows`, which counts them each time the UI thread
  goes idle, as WinForms raises no event when any window closes). Building a window touches pages that stay in the
  working set after it has gone: in a probe, 10 windows built, shown and closed added 38 MB of working set, 27 MB of it
  private and 11 MB shared (private memory grew 20 MB). After start-up, every 5 minutes, a compacting collection alone (no
  empty) runs only when the heap has grown 32 MB past what the last full collection left. Between those, a sample
  empties the working set alone once it has reached 75 MB (three quarters of the budget), at least 2 minutes after the
  last empty, and only while the heap is at most half of it (`MemoryTrim`). With the window open, the working set
  holds the pages its views and tabs touched since the last empty: in the CI UI soak with nothing emptied after
  start-up, 67 to 91 MB with the window of 1.0.0-preview.1 and 78 to 101 MB with 1.0.0's, while private memory and the
  heap stayed the same within 2 MB. The
  likely reason (not measured by cause) is pages the larger window touches and that stay resident: the working set
  less private memory, steady median, was 14.0 MB with preview.1's window and 22.9 MB with 1.0.0's, and under 1 MB
  headless in both. Each such empty is judged 10 seconds after it, whatever the sample interval: back at 75 MB or more
  by then (memory in use, which only faults straight back in), it did not hold and the wait before the next doubles,
  up to an hour; under 75 MB, it held and the wait is 2 minutes again (never less: at most 30 an hour). Memory in use
  that comes back more slowly than that counts as held and is still emptied every 2 minutes. The heap each full
  collection leaves (its floor) and the count of empties that did not hold go into `cache\profile.json`, written
  atomically every hour with the per-stage costs. The storm maps in `findings.jsonl` keep the top 15 (`report.json`
  has all), and a transcript search reads 16 MB at most.

## 4. Collectors

| What | API | Interval |
|---|---|---|
| Per-tag pool usage, alloc / free counts | `NtQuerySystemInformation(SystemPoolTagInformation = 22)` | 10 s |
| Committed pool totals (what Task Manager shows) | `GetPerformanceInfo` (`KernelPaged`, `KernelNonpaged`, times `PageSize`) | 10 s |
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
- Each sample is stamped on two clocks (`MonitorEngine.TickTime`), read once as its tick begins: the system clock, for
  the times shown and written, and `Environment.TickCount64`, which only goes forward (`Sample.TickMs`). The sampler's
  clock (`MonitorEngine.SamplerClock`) holds the whole schedule: each tick begins with `SamplerClock.Begin`, which
  reads the two times and gives the tick its `ThisTick` (only beginning a tick makes one), with which it stamps its
  sample and asks whether each slower job is due (a test holds that each job's work is under its own job's question);
  each job's interval (`SamplerClock.Every`: the settings' for services, handles and volumes, read at each tick) and
  its first and next due times are the clock's own, so the tick names a job and hands no time or interval of its own.
  The slower collectors above, the restart of the process-event session when something stopped it (first due a minute
  after the first tick), retention and the hourly `cache\profile.json` (first due an hour after the engine is made)
  fall due on the second alone, and so does how long load has lasted before "PC is saturated" (`SaturationDetector`,
  on `LoadPoint.TickMs`, a parameter of its own of a record class with no constructor without parameters, so no load
  point is made without it): setting the system clock back never holds them back until it catches up, nor does
  setting it forward run them early.
- So are, on the forward time kept beside each time of day (`Core.Model.Moment`: a sample's `TickMs`, `History.Ms`,
  `Alert.Ms`, `ClearRecord.TickMs`, an incident's, a registry record's, a kept event's and a finding's): the history's
  raw window, minutes and handle scans; the detector's windows, rates and release spacing, and a tag's allocation rates
  across PoolWatch's own clears; the incidents' event keep, quiet period, maximum duration, re-rank and rewrite
  intervals, the protected window (its bounds, 10 minutes each side of the incidents' windows, and the 15 minutes a
  closed incident stays in it), the 6 hours a closed one is kept, a re-open's hour, alert runs, retention and the
  ranking's window; the events and short commands picked for an incident's window and the merge of its own earlier
  events into them, a release's samples, exits and busy processes, the tag's growth during a trace, the durations a
  report says (the growth's and its trace's), the samples its chart and its allocation rates read, the span before
  the onset those rates are set against and the sample its counters cover the growth from; the correlator's handle
  scans and a process's start, exit, orphaning and running time beside a growth, and its tolerances (the interval
  between samples); whether a trace row without a create time belongs to a suspect that had not exited when the trace
  began, and which instance of its PID (or of its parent's PID) such a row is: the one alive when the row first
  allocated, the trace's own times read onto the forward clock from the recording's start; whether a traced process
  whose start and exit PoolWatch held was a short command; the growth ledger
  (section 11); the automatic clear's
  spacing, schedule, back-off and 2-minute measurement; the experiments' phases; the mark inbox; the boot summary and
  its retry; the registry's 4 seconds before a process missing from the snapshots counts as exited, its 30 seconds
  before a child counts as an orphan, whether a parent exited before its child started, and how long it keeps exited
  records; the tag and process CSV rows; the version, crash, stray-git and filter checks, the clearer's memory lists and
  helper status, and the worker labels; the event summary's held starts and rollup window, and the exit of a start it
  held (a start it listed as long-running has its exit listed; it keeps at most 20,000 such listings, the earliest let
  go first); a spawned child's start and lifetime (the registry's,
  when it holds them), the spawn storms and their report folders (the re-open hour, the hourly limit on new ones and
  their refreshes), the report folders' write budget, the git root cache and the child accumulation; the filter
  timeline's pairing of
  a filter load with a package activation, its minute between two entries of one package and the two minutes it keeps
  them (each event-log record is read onto the forward clock as it arrives); the hold on alerts for PoolWatch's own
  trace buffers; a trace's length and the CPU recording's (their starts and ends are read on both clocks); the known
  issues' clears of the last day; how long a hidden suggestion stays hidden (30 days from the moment it was hidden,
  kept on both clocks); and the app's caches, waits and schedules: the device map, the object type names, the event-log
  state's save, drive answers, parents' working directories, the lag probe's resume gap and compositor retry, the waits
  for captures and transcript searches at the end of a run, a headless run's length, the self-test's wait, the
  incident list's re-read, the Active changes count's and the Tasks tab's reads, the setup scan's
  24 hours, the update check's day, the background tag ranking's spacing, the windows a tag's live views rank over and
  the span, events and trace they show, and the runs of the processes they list while the registry holds them (from
  the registry's start to its exit, or to the exit event's own time before it marks the exit), a process's rise and
  rates in the Pool causes view and the process list and whether it became an orphan after PoolWatch first saw it, an
  incident's growth rate there (an open
  one's so far, a closed one's from its onset to its end; one read back from its folder after a restart is timed by its
  times of day, all it keeps) and in its lead, the status line's 10-minute and hour
  changes, the header's note of PoolWatch's own clear, the last day of findings and incidents the suggestions and the
  Pool causes list read, the week of incidents they list and the one offered for a report, and the Simple view's minute.
  Tests that set the system clock back or forward between the two times an interval compares are the guard of these
  (`ForwardClockTests`, `ForwardClockWindowTests`, `ForwardClockSiteTests`, `ForwardClockScheduleTests`,
  `TagViewClockTests` and each part's own). A scan of the code (`SystemClockScanTests`) is a cheap extra: it finds a few
  ways of writing the system clock into an interval, names each place it finds, and does not try to recognise every way
  of writing code.
- A time Windows gives on the system clock (a process's create time; the kernel's exit time in a process event) is read
  onto the forward clock when PoolWatch reads it (`Moment.MsOf`), so a system clock changed between that time and its
  reading still moves it: for a process event that arrives as it happens, by at most a sample (a kernel process event is
  dated as it arrives less the wait the performance counter measured for it, at most 30 s, since TraceEvent's own time
  of it is reckoned from the system time its session started at). When that wait is out of those bounds (an event
  delivered later or stamped on another counter) or there is no counter, an exit is dated as it arrives, and a start's
  create-time estimate is TraceEvent's own time, off by as much as the system clock was set since the session started
  and by the two clocks' drift, unless Windows' create time is within 5 s of it and is taken instead. A process's
  create time read later (a process first seen in a snapshot; the exit of a process whose start the event summary did
  not hold, or whose listing as long-running it let go) is moved by as much as the clock was changed since the process
  started, as Windows keeps a process's start only as a system time (`GetProcessTimes`).
  A tag's live view times the run of a process whose exit it lists and which the registry no longer holds from that
  create time to the exit's time of day, both system times, so a clock changed while it ran moves that run by as much.
- Times of day stay times of day where nothing else exists: what is shown and written (the graph and a report's chart
  draw their points at their times of day); the graph's shown window; the crash check's look-back in the event log and
  the age of the newest filter manager record (event
  records and their queries are on the system time, below); and the records read back from files after a restart:
  `clears.json`, the incident folders, the setup scan's `cache\environment.json`, the update check's last check and the
  hidden suggestions in `setup.json`. Each of those is read onto the forward clock as it is loaded, and its age goes on
  from there on the forward clock (the automatic clear's spacing and back-off after a clear from before the restart, and
  a suggestion hidden before it, among them). That is a choice: within one start of Windows `Environment.TickCount64`
  goes on across a PoolWatch restart, but telling such a restart from one after a reboot needs an identifier of the boot
  that no clock setting moves, and the boot id PoolWatch keeps (`boots\`) is the boot's time to the minute, worked out
  from the system clock. So a system clock changed between such a record and the restart that reads it back moves the
  record's age by as much.
- Retention is the one place where a clock change could delete data, so it never judges by the system clock
  (`Core.Model.RetentionClock`, kept in `cache\retention.json`). Each retention check lists what it may delete, a daily
  data folder, an incident or an export (and a file directly in their folders), each with its stamp
  (`RetentionClock.Stamp`, `AppPaths.StampOf`): for a file its last-write time and length; for a folder its own
  last-write time and the name, length and last-write time of every file and folder in it at any depth, each length read
  from the file itself (a folder's listing may not yet show the rows the CSV sink adds to a file it holds open), and
  nothing read through a link: a link in a folder is left out with what is behind it, a folder that is itself a link (a
  junction in a daily folder's place, say) is stamped by its own last-write time alone, and a file that is a link by its
  own time and length; retention never deletes such a thing, as its delete refuses a link (`PathGuard.CheckReal`). The
  sink adds rows to the fixed-name files of the day's folder, which leaves the folder's own last-write time as it was,
  so the stamp, not that time, is what tells that the folder was written: after a clock set back to a day whose folder
  is still there, or set forward for a moment so that a later day's folder is made early, the rows that go into such a
  folder count from the check that sees them. One seen for the first time as it is now (a name not seen before, or a
  stamp other than the one it was seen with) starts at 0 at that check, and its time goes on from there on the PC's
  working-time timer (QueryUnbiasedInterruptTime, `Native.WorkingTime`); it is deleted once that time is over the
  retention period and a thousandth of it (the timer may run a little fast). Its stamp took the value it has at the last
  write into it before the check that first saw it, so the time counted is never more than the time since it or anything
  in it was last written, whatever the system clock said then (a clock set back and forward again between two ticks
  included) or did since, and a folder the sink is writing changes at every check, so it is never deleted under it. The
  timer goes on only while Windows runs and is awake: unlike `Environment.TickCount64` it stands still while the PC
  sleeps or hibernates, and a Shut down with Fast Startup on hibernates Windows' own session (Microsoft Learn,
  "Interrupt Time" and "Distinguishing Fast Startup from Wake-from-Hibernation"), so neither the time asleep or off nor
  a clock changed meanwhile counts. Across a restart of PoolWatch the timer now less the timer at the last save is
  added, never below 0: within one start of Windows that is the time Windows was awake meanwhile (so a crash loses
  nothing), and after Windows started again at most the time since that start. What it cannot count keeps data longer
  than the setting, never shorter: the time from the last write into a thing to the check that first sees it as it is
  (at most 6 hours of it while PoolWatch runs), the time the PC was asleep, hibernated or off, the time since the last
  save when Windows started again after PoolWatch stopped without saving, and, the first time this version runs or when
  its saved state cannot be read, the whole age of what is there then. A stamp is only compared with the one seen
  before, to tell that the thing changed: a write that leaves every name, length and last-write time in it as it was (a
  file written over with as many bytes, with the clock at the very time it was last written) is no change to it. A
  record is let go once a check that listed its folder whole no longer finds the thing; one whose stamp cannot be read
  (any part of it) is kept, as are the records of a folder that could not be listed. In a folder that cannot be
  listed (when Windows refuses the listing, say), each thing with a record is still judged by its own path there
  (a plain name only: `AppPaths.RecordedPath`), so what is due in it still goes, and the record of one no longer at
  its path goes; a thing not recorded yet is first seen once the folder can be listed. A due folder is deleted by
  `AppPaths.DeleteTree`, which never follows a link (a link in it is deleted itself) and, unlike .NET's recursive
  delete, never looks the folder up in the folder above it, so its files still go while that folder above cannot be
  listed. When a folder cannot be deleted whole (a file in it in use, say), what is left stays due while it holds
  only what was in it then, so a later check tries it again at once instead of counting it from 0 (in that run of
  PoolWatch; after a restart it is seen anew, so kept longer). A folder that cannot be listed, a thing that cannot
  be read whole and one that cannot be removed are each logged once until that can be done again. A save is at most
  4 MB, leaving out the records first seen latest (those things are seen anew after the next start, so kept longer).
  Each check logs one line: how many things it judged and how many it removed, by folder, with the first five names
  (`AppPaths.RetentionSummary`; the owner's log had 1,137 lines a day, one per folder removed). A check lists each
  folder once for its folders and files and reads each file's time and length in one query of the file itself.
  The first run of this version, with no saved clock, counts everything from 0, and no seeding of the records can be
  safe: what there is to seed from is each thing's last-write time, a system-clock time, and a clock set back before a
  write makes a file written a minute ago look a week old, so a record seeded from it would delete that file early
  (the PC's working-time timer itself starts again at every boot, so it holds no age from before this boot either).
  Counting from 0 keeps data longer, never shorter; on the owner's PC, upgrading to beta.2 kept 14,416 storm folders
  a week longer, which retention then removes as it would any other.
- The filter manager's events in the System log are read from the newest record the last run saw. That record's age is
  read on the system clock, as Windows gives it on no other: an event record is stamped with the system time (its
  `TimeCreated` `SystemTime`), and an event query's `timediff(@SystemTime)` is measured from the system time now
  (Microsoft Learn, "Consuming Events", the XPath 1.0 limitations of event queries). When the system clock was set back
  since that record, the age is negative, and PoolWatch reads every such record since Windows started instead.

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
hour by hour" is a first-class report (Since boot), from one-minute history (25 hours): each minute's tags and each
busy process's I/O in it; past 2 hours a minute's processes are summed into its hour, the 10 busiest kept by name and
the hour's total, so each hour's three named processes and their shares are those its minutes gave (the oldest hour,
partly past the 25 hours, is shared out over all of it).

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
  2-hour maximum and re-opened within the hour (while it is among the 20 closed incidents held in memory, section 10).
  Once open longer than the raw history's 70 minutes, the clears that left the history still count, as each was
  measured: its growth and what the clears freed are those a history holding the whole incident would give.
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
5. **Confidence**: *weak evidence* (only active at the time), *some evidence* (one signal agrees), *strong evidence*
   (a direct charge, or several signals agree) or *confirmed by a trace* (section 8). These are Pool causes' Confidence
   column's words, and every place that names a confidence uses them (`PlainWords.Confidence`): the details, the plain
   answer and the headline, report.md, Copy summary (an incident's, a tag's and the tray's), a vendor bundle's
   incidents.md and Report an issue. report.json's `confidence` keeps the names (`StronglyCorrelated`), which are all
   PoolWatch reads back. A report an earlier build wrote keeps its older words wherever it wrote them ("strongly
   correlated", "correlated", "weak signal", "confirmed by trace", "(strong)", "(some)", "(weak)"): in report.md (for
   example its title, plain answer, owner lines, suspects table and Actual line) and in report.json's headline and plain
   answer, which the details, Copy summary and Report an issue show as written; its suspects' confidence they read from
   the names, in the column's words.
   PoolWatch never states causation from correlation alone.

**Token objects and process starts** (`Core/Tokens/TokenCorrelator`, the `TokenSpawn` finding). After each bucket (a
minute by default) PoolWatch asks whether Toke's outstanding allocations rise with the process starts. It judges its
whole window (an hour by default) at the tracking bar (a rank correlation of 0.6, with enough starts and tokens per
start). Besides it, it judges a window from each bucket that is at or below every later Toke reading, which is what a
fresh start of PoolWatch at that bucket would judge: the newest 32 of them, and of a run of equal readings only the
newest two. Each of those windows is one more chance for a few buckets of a rise that ignores the starts to line up with
them, so such a window must hold at least 10 buckets and clear more than the bar: the one-sided 99.9% lower bound of
its correlation (Fisher's z) must reach 0.6, which takes a correlation of 0.9559 over 10 buckets, 0.9292 over 14 and
0.8723 over 27. The 10 holds also when the Advanced settings ask fewer buckets for a verdict (4 at the least): over n
buckets the starts fall in Toke's exact order by chance once in n! (1 in 720 over 6), and the bound lets that order
through over any n above 3. Judged at the bar itself, these windows raised a false warning in 29 to 37 of 40 eight-hour
test runs of each of four kinds of rise that ignore the starts; with the bound but over as few buckets as a verdict
needs, in up to 17 of 40 at 4 buckets, 5 at 5 and 1 at 6. With the bound and the 10, none does, at 4, 5, 6 or 10
buckets per verdict, and the whole window alone raises the same few it always did (one in 0, 1, 0 and 1 of the 40 runs
of each kind at 10 buckets; 0, 1, 0 and 3 at 4).

The verdict is never later than the whole window alone would give it: the whole window is judged first, at the bar, at
every bucket. A leak whose own buckets clear the bound as soon as they number and rise enough for a verdict (each start
leaves its token, and nothing else moves Toke in those buckets) is warned of no later than a fresh start at the bucket
before its first rise would warn of it, or than the leak's 10th bucket when that is later, whatever lies before that
bucket in the window (quiet readings that are flat, wander or dip, or an earlier rise, steeper than the leak or not,
straight before it or not), as long as no later reading falls below that bucket's and it is one of the newest 32
judged then. At the default settings that is the 15th bucket of a leak of about 37 tokens a minute and the 28th of one
of about 18. Only with fewer than 10 buckets per verdict can a fresh start warn before the leak's 10th bucket: with 4
in the tests' rig, at the 7th. There a leak after 10 quiet buckets, or 120, is warned of at its 10th bucket, and after
fewer than 10 the whole window can warn of it sooner (after 4, at the 7th; after 9, at the 9th).

A noisier leak (other token churn, or readings that wander) is warned of once its own window clears the bound or the
whole window says so at the bar, whichever comes first. The bound stays at 99.9% so that these windows add no false
warning; the price is that such a leak can be warned of later than a fresh start of PoolWatch would warn of it, as late
as the whole window alone. Measured at the default settings with the tests' noise model (`TokenTests`, twelve draws: 45
quiet buckets whose readings wander, then the leak of about 37 a minute), the leak bucket of the first warning is:

| The leak | Warned of | The whole window alone | A fresh start as the leak begins |
|---|---|---|---|
| readings off by up to 25 tokens either way | 14th to 38th | 23rd to 38th | 14th to 16th |
| the same, after an earlier rise of 6 x 120 straight before it | 16th to 52nd | 33rd to 56th | 14th to 16th |
| the same, after a dip of 100 in the quiet readings that comes back at the last quiet bucket | 16th to 44th | 32nd to 44th | 14th to 16th |
| readings off by up to 50 | 33rd to 83rd | 33rd to 89th | 14th to 74th |
| the same, after the earlier rise | 45th to 148th | 57th to 148th | 14th to 74th |
| other churn of up to 40 tokens either way in each bucket | 13th to 42nd | 27th to 42nd | 10th to 19th |

These numbers hold for these models only: other noise, or another quiet spell before the leak, gives other numbers.
What holds for every leak is that it is warned of no later than the whole window alone would warn of it. An earlier
rise in the window can hold the whole window back until it leaves it (with readings off by up to 25, to the 53rd to
56th leak bucket in 8 of the 12 draws), and a dip that comes back just before the leak holds it back too (to the 32nd
to 44th, against the 23rd to 38th without the dip). The test pins these numbers, and that at every bucket the verdict
says Toke tracks the starts whenever the whole window alone does. "Not a per-start leak" is said only from the whole
window. The finding gives as the program behind it, and its text names, the owner whose starts rise and fall with Toke
the most (a rank correlation of 0.3 at least) of the five with the most starts: never merely the one with the most
starts. The five are ranked together with any of them PoolWatch could not tell: a parent whose ancestry it could not
read, named only by its process ID, and one whose ancestry holds no process it names an owner by (only shared
runtimes, shells and the like, as a cmd.exe that Explorer started), named by its image and process ID. When the owner
whose starts move with Toke the most is one of those, the finding gives no program, and not an owner whose starts move
with Toke less in its place; its text says the starts that move with Toke the most are made by a process PoolWatch
could not name. When no owner's starts move with Toke, the finding gives no program. The warning's text gives its
window's length on the clock that only goes forward (`Environment.TickCount64`), as the buckets are timed, so a wall
clock set back or forward does not change it.

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

AICC's version (for the timeline's version markers, `cache\versions.txt`) is read only from a running
`AI Code Conductor.exe` that is AICC's own (`VersionCollector.TrustedAiccVersion`): one only administrators can change,
or one signed by the same publisher as PoolWatch through the same certificate authority. Every running exe of that name
is a candidate, from any folder, so none can hide AICC's. The cost is bounded: the admin-only check reads a few access
rules; the signature check hashes the whole file, so it runs only when PoolWatch's own build is signed, and a candidate
found not to be AICC's is not checked again while each read meets that same file unchanged (`SignatureVerdicts`, keyed
by its final path, file ID, last write time and size). A candidate that passes is checked again at every read.

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

**How long an incident is held.** A closed incident stays in memory 6 hours (a release attaches to it, the same tag
growing within the hour re-opens it), but at most 20 closed ones do (`IncidentManager.MaxClosed`; the owner's PC held
16 after 4 hours of agentic load): past 20, the oldest closed leaves first. Its folder stays and the window lists it
from there; a tag that grows again within the hour after its incident left memory opens a new folder instead of
re-opening it, and a later release no longer attaches to it.

**How the folder is written** (`IncidentWriter`, `IncidentManager.Write`). It is written when the incident opens or
re-opens (`report.json` and `report.md`, what the window's details read), when it closes and when a trace is attached
(everything); while it is open, its `report.json` and `report.md` on a new top suspect, 25% more growth or every 5
minutes, the chart, `timeline.csv` and `processes.csv` with them at most every 30 minutes and only while the write
budget has room to spare, and the rows of `events.csv` only while no open incident's report is due; a closed one on a
new timeline entry, and every 30 minutes for alerts counted after it closed. All but the opening, re-opening, closing,
trace and end-of-run writes wait while the report folders' write budget has no room (section 3), and are written at a
later re-rank, the incident written longest ago first, so on a busy PC with many incidents open the report may be up
to about an hour behind the window. The window is not behind it: the list, the tray and the notices are live (the
growth is measured every 30 s, the cause comes from a ranking at most 10 minutes old), and an
open incident's details and Copy summary (the window's and the tray's), and Report an issue's summary of it, are built
from the incident in memory as its `report.json` would say them now (`IncidentManager.SummaryJson`, each field made by
the writer's own code, which `LiveDetailsTests` holds field by field to a `report.json` written at the same moment),
with when its folder was last written and why it can be behind; a closed incident's come from its `report.json`. The
summary is built again only when what it says can have changed (`LiveSummaries`: a ranking, at most every 30 s while
the incident changes, an alert, a re-open, a trace, its end or a write; its growth alone moves at nearly every sample
and is brought up to date by the next ranking), so it is never behind what its folder says. Export report writes
an open incident's folder whole first, every event of its first part included, but for a file that stays as written
until the close (one with its `Note` row, or a re-opened incident's with both ends; `IncidentManager.WriteNow`, a
write a reader needs at once), puts both ends of its `events.csv` in the zip from memory in place of the
folder's, with a
`report.md` that counts them (`IncidentManager.ExportFilesNow`: the file as the incident's close would write it now,
its first part following the window's start at once,
and the report as a write would make it beside that file, so the zip agrees with itself), and does not export it when
that write fails; a closed incident's export reads its files. `report.md`, `report.json`,
`timeline.csv`, `processes.csv` and `timeline.png` are each replaced in one step (temp file and move), and only when
what they say changed since this run last wrote them (the chart is drawn again only when what it shows changed), so a
closed incident's CSVs and chart are written once. `events.csv` holds both ends of the window, 500 events each (the
owner's choices, 2026-11-10: both ends, as at 9,400 events an hour the first 1,000 alone were only the first 6
minutes; then 500 at each end rather than 1,000, so a long incident's closing write is about half as big: section 3
says what it still costs). At the owner's rate each end is about 3 minutes of events. The incident takes each event
from the shared buffer as it comes (the buffer keeps 90 minutes): its first part, the earliest 500 by time from its
window's start, the onset less 2 minutes (the summarizer's rollups, over
half of the owner's events, come up to a minute and a half late at their first start: a late event earlier than the first part's
last takes that one's place, and that one moves on).
The onset is where the growth first passed 5% of its rise (`Detector.FindChangePoints`), so a steady growth's onset,
and the window's start with it, is measured later by about a twentieth of the time it has grown (about 4 minutes after
100 minutes, `IncidentPersistenceTests`): after an hour, past most of the 500 the incident first kept (about 3
minutes of events at the owner's rate; 416 of them on average after an hour in `BothEndsTests`, all of them after
100 minutes). So the first part follows the window's start (decided 2026-11-11): a person reads the report's window
and expects `events.csv` to show that window, and a first part left where the incident opened was mostly from before
the growth. When the start is measured somewhere else (later, or earlier: a second rule that fires with an earlier
onset, or the first samples after the opening), the incident takes every event of the buffer again from there
(`IncidentManager.Follow`): its first part becomes the window's first 500 as its start is measured then, and what it
leaves out is counted again, each event by its own time. A closed incident's first part follows at once (at its
close, and at a later write that measured it again: a trace attached), an open one's at most every 10 minutes (each
follow takes the whole buffer again, about 14,000 events at the owner's rate, and a long growth's onset moves every
few minutes), and at once when an export takes both ends. The first part is exactly the window's first events while
the buffer holds every event from the start: the onset is measured only within the raw history (70 minutes; once
the history no longer reaches it, it stays as last measured), so with the default settings the start is at most 72
minutes back when it last moves and 82 when an open incident's first part follows it, which the buffer's 90 minutes
hold, unless its 20,000 events are fewer than those 82 minutes of the PC's (over about 14,600 events an hour, more
than half as many again as the owner's) or the settings make the raw history the longer. When the buffer no longer
holds every event from the new start, the first part is the first events PoolWatch still holds from it, up to 500:
the incident's own, when its first part holds an event from the new start on that the buffer has let go (it keeps
those, and lets go of the ones from before the start: after a start measured later they are the window's first
events, often fewer than 500, and the file says how many, "events.csv keeps the first 200 events from ..."), else the
first 500 the buffer still holds. Where PoolWatch had let go of events from the new start on, the incident notes up
to when (`KeptEvents.LostTo`), which the file and the report then say. (Before the first part followed the start, it
stayed where the incident opened: at first a close left out its rows from before the window as measured then, so
that after 105 minutes at the owner's rate a long incident's file held none of the 500, and after an hour 84 on
average; then the rows were kept, and after an hour 416 of the 500 were from before the window the report gave.)
What the file says is counted from the rows written. The `Gap` row says what the file keeps
and from where ("events.csv keeps the first 500 events from 2026-11-10T06:01:40.000Z, where the incident's window
started when this file was written, and the latest 500 up to the window's end"): a file written whole starts where
the window did then, as the first part follows at once for such a write, and the row, which stays as written, says
of when. An open file's `Note` row says how many events it holds and from where in the same words, a fact that stays
true as the onset moves on, and its time is the first event after them. A file can hold rows from another start than
the window has now: an open incident's between two follows (up to 10 minutes) and once it has its `Note` row (it
stays as written until the close), a re-opened one's (as its last close wrote it), and a closed one's that waits for
a write that must be made. `report.md`'s line under
the window's events then says where the file's events are from and where the window starts now, in each of its forms
(`IncidentWriter.StartSaid`), and calls the file's rows "the window's first" only while they are; for an open file
with its `Note` row it also says how many more events have come since, every one the incident took in from the
file's start on that is not one of its rows, counted across follows (the window's end as it is measured lags behind
while the growth goes on, and stands still in the
incident's quiet minutes, so a count up to it would leave out events that had come). Each of these is judged on the
times of day the text gives, as it writes them (`report.md` to the second, `events.csv` to the millisecond), so a
start that is the same time as written is not said to have moved. An incident made for a trace of its own (kind
`Capture`: Capture now, or a lag trip's CPU trace with `CpuTraceEnabled` and `DeeperCapture`) has no growth whose
start is to be measured: its trace is its window (the owner's words, 2026-11-11: the window is the trace, start to
end), from the trace's own start to the trace's own end, without the 2 minutes before the start that every other
incident's window has, and without a minute after the end. It is made
with its start and its
window's end at that moment (the newest sample's time, once PoolWatch has taken a sample), and when its trace
finishes its start is set to the trace's own start and its window's end to the trace's own end, exactly, on both
clocks (`IncidentManager.ApplyCapture`, `IncidentManager.EventWindowStartMs`, `IncidentManager.EventWindowEndMs`; a
trace that did not record leaves both where the incident was made). That is their only move:
`IncidentManager.Measure` never moves the start, neither to
where the tag's rise began (before, a rising tag's start was measured up to a minute before a 60 s trace, which could
be before the incident was made) nor to the next sample's time (before, the window the report listed then started up
to a sample later than the start it printed), and the window's end is not measured either: it is not a minute after
the end of the tag's rise, nor a minute after the incident was made (before, it was: a 5-minute trace's `events.csv`
and report held the events of its first minute).
Until its trace is attached the incident has no span: its report's title, its Growth line, its plain words (when it
has a suspect) and its draft bug report say that its trace had not finished by the time of the newest sample at that
write, not what the tag did over no time (before: "grew 0 B in 0 s; no suspect stood out"), and those of an incident
whose trace recorded nothing PoolWatch could use say that (`IncidentWriter.NoTraceSpan`).
The vendor bundle's `incidents.md` lists such an incident by the same words (its trace had not finished by the
time of the newest sample as the bundle is made, or recorded nothing PoolWatch could use), and gives no growth
for it (`BundleBuilder.IncidentLine`; before: "100 MB -> 100 MB (+0 B)").
So `report.json`'s `onsetUtc`, the start of the window `report.md` lists (the trace's start itself, to the trace's
end, with no minute added) and the start the texts give are one: the tag's levels are read from the first sample at
or after it
(the last one before it, when the trace ended before the next sample), the end of its rise is looked for from there
on only, and what its trace's answer says and counts over the incident's span ("grew 20 MB in 1 min", an owner's
process starts during it) runs from it to the end of that rise, else to the trace's end (`Incident.SpanEndMs`).
When the trace is attached, the first part follows the start as a closed incident's does, at once, and the file is
written again from the trace's start, every row of it (with no event row when the trace's window holds no event: a
row from before the trace, written while it recorded, is not left in a closed incident's file); `report.md` gives
the window's end as the time the incident closed and as the trace's ("Recorded 07:05:20-07:10:20"). While the trace records, the
incident holds the events that come after it was made (at most 4,000, as an incident that can re-open does), so the
window the trace gives it finds them however often its folder was written meanwhile
(`IncidentManager.CanTakeLater`). An incident can also open with its window starting further back than the buffer
reaches (the shared buffer keeps 90 minutes and at most 20,000
events, `IncidentManager.MaxEvents`: a slow growth that trips the 60-minute rule on a PC with 2 to 2.5 times the
owner's event rate or more, 20,000 events in the 50 to 60 minutes back to its window's start, and any incident when
the event-keep setting is shorter than
its rule's window; `IncidentPersistenceTests`). Then too the first part is the first 500 PoolWatch still holds from
the start, the incident
notes the time of the latest event the buffer had let go by then (`KeptEvents.LostTo`; how many it let go is not
known), and the `Note` row, the `Gap` row and `report.md` say that instead of giving the window's start as where the
rows begin ("the first 500 events PoolWatch still held from 06:05:20 (by then it had already let go of events up to
06:13:19)" in `report.md`; the rows give the times as `events.csv` writes them, and a `Gap` row adds what the first
time is, as above). A file with no first
part (the buffer let its events go before the incident looked at them, which only an incident PoolWatch does not
hold in memory can find, and it writes none such: see below) keeps the latest part alone and says so: its `Gap` row,
and `report.md`, which
says it holds only its latest events and where its first part would have started.
Then its latest part, a rolling
tail in memory: the latest 500 up to the
window's end as it would be if the incident closed now (500 more before them while it is open, so an end measured up
to about 3 minutes earlier still leaves 500: how far a measured end moves is a matter of samples, so this stayed 500
when the ends went from 1,000 to 500), the earlier ones let go and counted (how many, the first and last of their
times), and those after that end (its quiet minutes, then the time since it closed, at most 4,000, the earliest of more
counted apart) while the window can still take them in: until an hour after it closes, when it can no longer re-open
(4,000 is about 25 minutes of the owner's events, so an end measured later that lands up to about 19 minutes before
now still finds its whole latest part; a matter of time too, so it also stayed as it was).
By time the three never interleave: a late event among those let go is counted with them, and when the window's end
reaches events counted apart, every event of the latest part before them is let go with them, so the latest part has
no hole and the `Gap` row's range holds no row of the file. While the incident is open, `events.csv` holds the first
part, appended to in whole rows sorted by time within each write, so the earliest kept event not written yet is
always there to write and the file has every event from its first row to its last, however long the budget held it
back; each write adds at most 100 (every one left at the end of a run), and once it has all of a full first part a
last `Note` row says how many it holds, from where and to when, as a fact about the file (a crash or a power loss
leaves it true, and so does an onset measured somewhere else afterwards): no
event row follows it, a late event that takes a place in the first part since waiting for the close, and the file
stays as written while the first part follows the window's start, until the close. A file that has no `Note` row yet
starts again from a first part that followed the start, at its next write of rows: it is written whole (temp file
and move) with every event of that first part up to the last one it held already, and up to 100 more, so it still
has every event from its first row to its last. It starts again in the same way when a late event takes a place in a
first part the file holds whole (the first part's last event then moves on to the latest part, and the file would
hold a row that is no longer the first part's): written again from the first part, it never holds more than the
first part's 500, nor a row after its `Note` row's time (before, the late event's row was appended: 502 event rows
under a `Note` row that said "the first 502 events ... to this row's time", at a time before the file's last rows).
Those are the two ways an open file loses a row: a write that brings no rows, or a first part
that holds no event to start the file with, leaves it as it is, with no `Note` row, and `report.md` says where its
rows are from and what it gets (`IncidentWriter.WriteEventsCsv`). A file that starts again is written whole once
more (its 500 rows at most, about 0.15 MB at the owner's mix) by the write that brings it rows, an append the
budget allows or a write that must be made; with ten incidents open for an hour at the owner's load the folders
write what they did before the first part followed (1.68 MB, `WriteBudgetTests`). When the incident closes, its
closing write (a
write that must be made, through
the budget like the rest of it) replaces the file whole in one step (temp file and move, as `timeline.csv` is) with
both ends of the window as it is measured then: the first part, a `Gap` row between the two parts (placed there, at
the time of the first one left out),
saying how many, the first and last of their times and what the file keeps, and the latest 500, all sorted by time,
each event once (an incident with under 1,000 events from its first part's start to its window's end has every one
and no `Gap` row). A file that holds its first part alone (an incident with no more events than those 500) is
written by that write too: every row left, and whole, in time order, when a row was added after one of a later time
while the incident was open (an alert's row comes with the write it fired at, the window's earlier events after it;
a late event after later ones) or would be now. So every write of a closed incident that must be made (its close, a
trace attached, the write for its window's late events below, the end of a run) leaves its file with every row it
is to hold, in time order, and no row waiting: before, such a write gave a file with its first part alone 100 rows
as a write that can wait does, so a minute's trace on the owner's PC (about 157 events) had 100 rows after its
trace was attached and `report.md` said the rest would follow when the budget had room, and a short incident's
late event stayed after later rows for good. It is written so once per close; a re-opened incident
that closes again writes it again (both ends of the whole window as it is measured then), a
trace attached after the close
that measures the window again (or sets it, for an incident made for the trace) does too, and the end of a run
(PoolWatch stopping, Windows shutting down) and an incident leaving memory (6 hours after closing, or as the oldest of
more than 20 closed) write it when its file does not hold both ends as they are (its closing write failed, or its
window took events in after it: below), so a later run keeps them; the incident's report is written with it then, so
the two say the same of the file (before, an incident leaving memory had only its `events.csv` written, and its
`report.md` went on giving the count its `Gap` row had before). An hour after it closes, when it
can no longer re-open, a closed incident whose file holds both ends as written lets go of its latest part (counted
among those left out): the file is final then, and its first part no longer follows the window's start (`report.md`
says where its rows are from, should the window be measured somewhere else afterwards). A closed
incident without a latest part (under 500 events from its first part's start) that leaves memory with late events of
its window whose rows no write had room for (it left before the write for late events below was due, as the oldest of
more than 20 closed) gets a last `Gap` row then, after its other rows, saying how many it leaves out and between
which times; with Detailed recording the day's
`data\<yyyyMMdd>\events.csv` has every event of the time it was on. While an incident's rows wait for the budget, its
`report.md` names that file only with Detailed recording (`IIncidentHost.DayRecords`): it "has every one" when
Detailed recording was on for the whole window, "has those of the time Detailed recording was on" after a switch in the
window, and no day file is named with Reports only; after a switch, the day's file holds only the time Detailed
recording was on. A closed incident goes on taking events. One timed at or before its window's end that comes after
its last write (the summarizer hands a rollup or a long-running start over up to a minute and a half after its time,
so those of a trace's last seconds can come after the trace is attached; and the window of an incident closed while
it still rose, at its maximum duration, ends a minute after that close) is taken as it comes
(`IncidentManager.AddEvents`), and the incident's report and the event's row are written together at the next sample,
as the budget allows: the row is appended to a file that holds its first part only, while a file that holds both
ends is replaced whole only by a write that must be made,
and until then the report lists the event and counts the file's rows as they are. (An appended row sits at the end
of the file, after rows of later times, until the next write that must be made. A write that can wait adds at most
100 rows; when more late events than that come at once, the rest follow 100 at a time, 30 seconds apart (as the
budget allows), each time with the report again, so its line counts the file's rows: before, they were appended
alone, and `report.md` went on saying "events.csv gets the rest when PoolWatch's write budget has room" of a file that had
every row.) That write comes two minutes after
the window's end (`IncidentManager.LateEventMargin`: a rollup is handed over up to a minute and a half late, so by
then every late event of the window can have come): when the file does not hold the window's events the report
lists, each once and in time order (`IncidentWriter.LacksEvents`), the folder is written once more, a write that
must be made, whatever the budget, for an incident of any kind: a file that holds both ends is replaced whole (about
0.3 MB at the owner's mix), another gets every row left and is written in time order, and the report is written
beside it, so the two agree. It is made once, and again
only when still more of the window's events come, no sooner than two minutes after the last such write. (Before, a
file with both ends got a late event only at the end of the run or as its incident left memory, up to 6 hours
later, and its report listed an event the file did not hold until then.) The end of a run and an incident leaving
memory still write a file that does not hold both ends as they are (above). When a write of a closed incident's
folder fails (another program holds one of its files open), the folder is tried again, as a write that
must be made: at the first sample a minute or more after the write failed, and a minute after each try that fails,
while PoolWatch holds the incident (`IncidentManager.FailedWriteRetry`), and once more at the end of a run. A write
that cannot write `timeline.csv` or `processes.csv` does not stop there: it writes the other of the two,
`events.csv` and the two reports, and counts as failed for that file (`IncidentWriter.ChartFailure`), so while
another program holds its `timeline.csv`, `report.md` still says that the incident closed. (Before, the write
stopped at the first file it could not write: `report.md` could go on saying "still open" of a closed incident
while the file was held.) A try writes the whole folder, unless all that is still to write is `timeline.csv` or
`processes.csv` (the write that failed wrote the reports, and no earlier failed write left more:
`Incident.ChartsAloneFailed`) and no change waits to have the folder written (`Incident.Dirty`, set by what has
a closed incident's folder written again: a new line on its timeline, a growth found still held, a suspect's
session found; a repeat counted on a line and a late event have their own writes, as without a failed write):
such a try writes the chart and those two
files alone, and `events.csv` and the two reports only when it made a file the report does not show yet (its
chart line, the files its draft bug report lists as attached), so the report stays true
(`IncidentManager.Retry`). (Before, every try wrote both reports again: they give PoolWatch's own cost as it is
at each write, so a report is hardly ever the same as the one before. While another program held a closed
incident's `timeline.csv` that came to a report a minute, about 5.4 MB an hour at a report of about 90 KB, more
than the whole 5 MB an hour of section 3; and as a write that must be made spends the report folders' budget
too, the open incidents' refreshes waited for as long as the file was held.) The try at the end of a run is of
the whole folder. A try
makes the chart, `timeline.csv` and `processes.csv` from the window's samples while the raw history (70 minutes)
still reaches back to the start of the incident's
window. Once it does not, the chart, `timeline.csv` and `processes.csv` would come out of fewer of the window's
samples, or of samples from after the incident, and each of the three is decided on its own
(`IncidentWriter.Leaves`): one that was written for the incident's close stays as that write made it, and one that
never was (another program held it, the folder could not be written, or the chart could not be drawn) is made
only when the try holds at least as many of the window's samples as the file holds already, and at least one. So
such a try replaces a file that stops before the close with one that reaches it, and no file comes out of fewer
of the window's samples than it holds. (The window of an incident that ran about 50 minutes or longer starts at the
history's oldest sample, and the history lets its oldest samples go 32 at a time, every 5 minutes 20 seconds at a
sample every 10 seconds: it stops reaching back to that window's start at most that long after the close, not 70
minutes after it. A rule that left such a file whenever the history no longer held the window's first sample
could leave a `timeline.csv` that stopped 20 minutes before the close for good, after another program had held
it for 30 seconds. The place of the window's first sample is kept from the closing write on, also when that
write is refused its folder (`FileState.WindowStart`), so the samples a file holds from before the window are
not counted against a try.) A try that leaves a file
that was never written for the close makes
its failure good all the same: no further try is made for that file (`IncidentWriter.ChartFilesLeft`; a later
write, for a note or an alert counted on the timeline, leaves it too). A write
of its report and rows that is made meanwhile (a late event's) does not stand for a try: the try is
still made. The log says once that the write failed and that PoolWatch tries again, however many tries fail
after it, and once what came of it: that the folder is written again, or, of a try that left such a file,
that the folder is written again but for that file, which stays as it is, with why
(`IncidentManager.FolderWrittenBut`; a chart is named so only when the folder has one, as a chart that could not
be drawn is no failed write and a folder without one is whole; a write that
first fails at the end of a run, or as its
incident leaves memory, is only said to have failed: nothing tries it again; an incident still open at the end of
a run is closed and written once there, whether or not that write is made). An incident that re-opens before its
folder is written gets its report at once, as every re-opening does, and the log says that its folder is written
again only once a write with the chart and the CSVs is made: a refresh with them, its close, or a try after that
close. (Before, a closed incident's write that failed was tried
again only at the incident's next change, or with rows of its `events.csv` that still waited, and a closed
incident may never change: its `report.md` could then stay behind its `events.csv`, saying rows were still to come
to a file that had them all, to the end of the run and after it.) An open incident's write that fails waits for
its next change, as before; when it is its `timeline.csv` or its `processes.csv` that cannot be written, its
report is written all the same, and the file is tried again at its next refresh with the chart. The events after
its window's end a closed incident takes when it is written, and at the latest before the buffer lets them go
(`IncidentManager.DropEvents`): every incident PoolWatch holds in memory has looked at every event before it goes,
and keeps or counts each by its own time against its own window. (Before, a closed incident took events only when it
was written. Those the buffer let go meanwhile it knew only as a number, and it counted all of them as left out when
the first of them was in its window: a 30 s trace's incident, attached 20 s before its window's end as it then was,
said "300 events left out" 100 minutes later, of which 10 were its window's, and a growth closed while it still
rose "335 events left out", of which 30 were (`IncidentPersistenceTests`).) And every incident PoolWatch writes is
one it holds: none leaves memory while a trace that may be attached to it records or is read
(`IncidentManager.Process`; they leave at the first sample after the trace). Before, a trace's result attached to an
incident that had left memory meanwhile (which takes 20 other incidents closing during the one trace) wrote
it once more without its having looked at the events the buffer let go, and it counted as many as went as left out,
those from after its window's end among them. So no event from after the window's end is counted as left out: each
is counted by its own time against the window's end, and when the end is measured again after events were counted,
they are counted again. The end can come out earlier than events the incident has already let go (it holds its
latest 500 up to the end, and 500 more while it is open; the ranking looks further back as an incident goes on, and
a level it then finds lower can make an earlier plateau the growth's end), or, once more than 4,000 events came
after it, later, among the earliest of those, which the incident had set aside, counted together. At the next write
that takes both ends (the close, a trace attached, an export) the incident then takes its latest part and its counts
again from the buffer (`IncidentManager.Refit`), as its first part follows the start: every event after the first
part that the buffer holds is kept, counted as left out, or left as after the end, by its own time against the end
as it is measured now, so the file has the latest 500 up to that end and the `Gap` row counts exactly the events
between its two parts. (Before, the count stayed as it was made: a window of 1,801 events whose end had been measured
20 minutes earlier got a `Gap` row of 7,320, up to 17 minutes after the window's end, and no latest part, while the
buffer held every one of its events.) What the incident had let go that the buffer has let go too stays counted as
it was made, up to the time of the latest event the buffer let go, the events it had set aside among them: with the
end measured after that time they are all the window's, left out between its two parts, and the latest part is the
events after the last of them up to the end (500, or as many as came between: the row says how many the file
keeps). (Before, nothing was taken again once the buffer had let go of any event set aside, also when every event it
had let go was before the new end: the file then had only the events after those set aside for a latest part, or
none, and its `Gap` row could count events from after the window's end as ones PoolWatch no longer held while it
held them all.) The end is measured only within the raw history (70 minutes), which the
buffer's 90 minutes hold. Only where the buffer no longer holds the events from after the new end (over 20,000
events since it, or settings that keep the events for less time than the history) does such a count hold events
from after the end,
and the `Gap` row and `report.md` then say that it runs past the window's end, and where the window ends ("the count
runs past the window's end, 06:23:50 when the file was written: PoolWatch no longer held those events, to count each
by its own time against that end" in `report.md`). An alert is one row per
growth rule and onset (a rule that keeps firing keeps its bigger alert under the first one's onset; a re-opening's has
its own), at the time the file first saw it, and one row per release. While open it can hold rows from after the
growth ended, up to the close; the closing write holds none from after the window's end. It is written whole otherwise
only when it is not as this run last wrote it, and when a file that holds its first part only starts again from a
first part that followed the window's start (above). Its rows are written in single appends while the
incident is open, and read by others as they are: a crash in the moment of an append can leave its last row cut short,
which a later write in the same run repairs (a file not as written is written whole again) and a later run leaves as
it is. An export reads it, and a vendor bundle a storm folder's `spawns.csv`, beside their writer (sharing writes) and
up to their last whole row (`Csv.ReadCompleteRows`: the last line break outside a quoted value), so a row being
appended in that moment is never in the zip as half a row (it is in the folder). At the end of a run everything left
is written.

**Spawn storms** (`Core.Spawn.SpawnTracker`, `StormReports`). A parent that starts more than 30 short-lived children
(each exited within 10 s) within 30 s, by default, opens a storm, which closes once its rate stayed under half that for a minute. A fork copy (a child
with its parent's own image and exact command line: how Git Bash, MSYS2 and Cygwin run a subshell, a pipeline stage or
a command substitution) is no launch and is not counted, whatever it lived; what a fork copy starts counts to the
process it was forked from (a copy of a copy followed). A start's parent is the registry's record of it, else the
process with the parent's PID that can have started it (`ProcessRegistry.ParentKeyOf`: the live one, else the newest
remembered one that had not exited before the child started, for a child seen after its parent exited), by the key it
has now: a key a snapshot re-keyed (an estimated create time made exact) is followed (`ProcessRegistry.CurrentKey`),
and an open storm moves with it (should it meet another open storm of that key, it ends there as a storm ends), so one
parent is one storm. The owner's 20261109-231314-SpawnStorm-bash, 48 fork copies of one bash, is no storm now. With
Reports only a storm is found, listed and told as below but gets no folder (`SpawnTracker.FoldersWanted`), and its
finding says so. With Detailed recording each storm
is written into its parent identity's report folder (`StormFolder`: the image, its role and its owner, as
`ProcessIdentities.Of` gives them, so one Claude Code session's bash storms share one): a storm that opens while another
of that identity is open, or within 60 minutes of the last one closing, goes into the same folder, as a growth incident
re-opens; after that a new one is made, at most 10 an hour (a storm past that is in its findings, the log and the
day's findings.jsonl, and says why it has no folder). The folder is written when it is made, then while its storms change at
most every 10 minutes (every 30 while only the numbers of an open one change), while the write budget has room to
spare (the open incidents' reports come first), and once more when it leaves memory an hour after its last storm
closed, at the end of a run, or when a storm of its identity opens in the very sample its hour ended. Its `report.md`
lists every storm in it (opened, closed, parent, starts, peak, fork copies, children) and its latest in full; its
`report.json`'s `data` is the latest storm's with `identity`, `storms`, `folderTotalStarts` and `episodes`;
`spawns.csv` gets the folder's first 1,000 starts, each appended once its process exited, and those still running at
its last write as they are then (no lifetime). tools/Leaker's agent-load session (a burst every 2.5 minutes) makes one
folder in an hour, where it made one a burst. A vendor bundle's `spawns.csv` is the most recently active storm folder's
(`BundleBuilder.LatestStormSpawns`: of the folders held, the one whose storms were active last; after a restart, of the
newest 500 storm folders, the one whose `spawns.csv` was written last), not the newest by name: a folder made in the
morning can be the one still in use in the evening.

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
start when missing or different (compared first), installed in the install folder, and added unredacted at the root of every
export zip and vendor bundle, since it describes PoolWatch and nothing about the PC. It is not at the repository root,
where coding agents would read it as contributor instructions.

## 11. UI

PoolWatch is meant to be simple: say what makes the pool grow, clear the cache, fix what it knows, and help trim
background work. The window shows that and little else; the analysis behind it is the same in both of its views.

**Icons and the tray.** The binoculars everywhere (`TrayIcons`, the `.ico` files in `Assets`): `poolwatch.ico` (16 to
256 px) on every window's title bar and the taskbar, and in the tray the binoculars with a dot in the state's colour,
calm on purpose (`UiData.State`): red only for a real risk (an open incident of the non-paged pool over its limit and
still rising, or an open growth whose cause a trace confirmed); amber while another incident is open or a tag keeps its
memory after the activity that grew it stopped, and, over green, while the token-leak guard says this sign-in is not
protected (`GuardTray.State`, from `TokenLeakGuard.NotProtectedNow`: the guard on, the workaround wanted or the guard's
last word asking for the click, no check running, and the live timeout reported and not 0, read at each refresh);
green otherwise, also while a tag grows for a while (watched, not alarming); grey binoculars while starting, before the
first reading. A test reads every window's source and fails on a tray-state icon in a title bar. The tray icon's
tooltip has the paged and non-paged totals, and, while the guard is not protected, the line "Token-leak guard: not
protected right now. Click to turn it on." (before an experiment's status, so it is the last part the 127-character
limit cuts). Its menu: that line at the top, only while the guard is not protected (it, and a left click on the icon,
turn the workaround on: `GuardTray.Clicked`, `TokenLeakGuard.TurnOnFromTray`), Open PoolWatch, Settings... (in
either view, the window the Advanced view's Settings button opens, one at a time: the Simple view's header has no
Settings button), Run setup again..., Active changes..., "PoolWatch x.y.z is available" (only while a newer release
is out and update notices are on; it opens that release's page, section 18), the tools that are not on the window
(Capture now, Since boot report, Open incident folder, Export vendor bundle, Copy latest incident summary, Mark, the
experiments), Remove my
clear task and Remove old clear-cache tasks (only while there is one to remove), Report an issue..., About PoolWatch
(version, overhead, data folder, what PoolWatch changes, credits and licences) and Exit. When a growth incident (or one
of the non-paged pool over its limit) opens, a notice says so in the status sentence's words, with its headline as its
title (Windows 11 shows a notice as a toast with PoolWatch's name and icon above it, so no notice's title is
"PoolWatch"): "Kernel pool: Open files (File) is growing" ("Non-paged pool over the limit" for the second, whose text
starts with that sentence; "Kernel pool growing" when a tag's words are too long for a title, which shows 63
characters), then "PoolWatch is keeping an eye on X." only at strong evidence or a trace, else "Click here to open
PoolWatch for details." (`PoolCauses.IncidentNotice`). A release or a capture gives no notice. Which of these notices
show is Settings' Notifications choice (`Settings.PoolNotices`, read at each notice and at each refresh of the tray
icon, so a change applies at once): every one, as the incident opens (the default, also for a `settings.json` without
it); only one the tray icon shows red, by the icon's own rule (`PoolCauses.NoticeWanted`), told as the incident opens
when it is red then (the non-paged pool over its limit, or a re-opened growth that still carries its trace) and
otherwise at the icon's 10-second refresh once it turns red (a growth whose cause a trace confirmed: the incident
manager raises its event before any trace, so a new growth is never red as it opens); or none. Each episode of an
incident gets at most one notice, under any choice, so a change of the choice repeats none (`IncidentNotices`); one
held back as the incident opens is a line in `poolwatch.log`. The token-leak guard's notices are not these, and always
show. A click on any notice opens the main window in the view last
used and brings it to the front, unless that notice offers a place of its own (Active changes for the service profile's,
the guard's offer for the token-leak watch's). Windows says only that a notice of the icon was clicked, never which, and
a close changes nothing: the latest notice's own place runs once, and every other click opens the window
(`BalloonClick`), so a click Windows passes on after the notice left the screen (from the notification centre) opens it
too, and a close that comes late, after the next notice showed, never takes that notice's place away. Whether Windows
passes on a click in the notification centre to a legacy notice of a running program, or starts the program's Start menu
entry for it instead, is to be seen on a real PC: PoolWatch opens its window either way. Each notice shown, each click
and the first close after each notice is one line in `poolwatch.log`. A start of PoolWatch while its tray app runs in
the same session (from the Start menu, or by Windows for a click on a notice in the notification centre, if it does so)
shows that window and brings it to the front instead of saying "already running" (`ShowSignal`, section 19); one
started with `--tray`, or with no tray app of its session listening, says "already running" as before. The icon comes
back after Explorer restarts: Explorer's "TaskbarCreated", which Windows stops on its way to an elevated program
(UIPI), is let through to a hidden window of the tray app's own, and the icon is added again then (`TaskbarWatch`). The
token-leak watch (section 16) also looks in from the tray every 30 minutes, so it runs while the window is hidden (with no facts about the PC yet, as after an upgrade, it reads the PC first); its notice,
when a check it started found the leak with the guard off, opens the window on the guard's offer (the Simple view's
card, or the switch in Settings in the Advanced view). The token-leak guard's own notices (`GuardNotices`: small
windows at the bottom right, not balloons) show only when the user's click is needed, and only for the start's check
and what the user clicked: the prompt that asks for the click Windows needs from the program in front
(`TokenLeakGuard.TurnOn`, OK or Cancel: for the start's check, Turn on in a notice, and a click on the tray icon that
Windows still refuses), and a notice that this sign-in is not protected, with Turn on and Not now, when the start's
check or Turn on in a notice needs the workaround and could not turn it on (`TokenLeakGuard.Report`, told so by each
caller; a click on the tray icon passes `show: false`, since the tray still says it). Report shows it only if, as it
shows it, no prompt of the guard's is under way (that prompt asks for the same click, and its caller shows the notice,
by the same rule, when its prompt ends without the workaround on), the guard is on and the live timeout is not 0; a
prompt, as it opens, takes back a notice still open. Later in a sign-in the guard opens no window by itself: the recheck
(every `TokenGuardRecheckMinutes`, on the first sample after it) tries once, silently, to turn the workaround on again
when the guard wants it and the live timeout is not 0, and never asks for the click. Refused (PoolWatch is not the
program in front), it says so in `poolwatch.log` and, once until the workaround is on again, in the findings, leaves
on screen what is there (a notice of the start's check is still true), and tries again at the next recheck; the tray
app's icon shows the sign-in not protected meanwhile. What it says of a set-back names the program that was in front
as PoolWatch saw it, as the program in front, which Windows lets change this setting, not as proof that it did
(`TokenLeakGuard.SetBackWords`): the tray app passes Windows' setting-change message (WM_SETTINGCHANGE, through
`SystemEvents.UserPreferenceChanged`, the window SystemEvents keeps for the app) to `TokenLeakGuard.SettingChanged`,
which reads the live timeout and, when it went from 0 (or from a set-back already seen) to another value, the
program in front (`TokenGuardNative.ProgramInFront`: the foreground window's process, its file name only); the event
does not say which setting changed, so a value unchanged since the guard's last look changes nothing. When Windows
did not tell PoolWatch of the change, the recheck names the program in front at its own look, and says that this can
be minutes after the change. The workaround turned
on at the start's check, again after something set the timeout back or after the click, a build that needs no
workaround, and the guard turned off show nothing: the guard writes them to `poolwatch.log` and its findings (with
Detailed recording, `findings.jsonl` too), and takes back what no longer needs the click (`IGuardUi.Withdraw`): the not-protected notice, and
the prompt when it finds the workaround on (the live value 0) or is turned off (also when Turn off fails part-way).
That prompt then ends as a cancel, and its caller tries once more: finding the workaround on or the guard off, it
shows nothing. The recheck takes both back too when it finds the workaround on, set by other means, also while a check
runs (it sets nothing then: the logon check turns the workaround on itself, and a leak test needs it off; that look
still counts as that period's recheck). Every show and take-back goes through one lock and decides from what it reads
there (the live value, the guard off, a prompt under way), and `TurnOn` reads the live value once more inside it before
each prompt: what was shown before a take-back is on screen when the take-back comes, and the guard shows nothing when
it reads the workaround on or the guard off. No set is made under that lock (a set Windows accepts broadcasts
WM_SETTINGCHANGE to the top-level windows), and `SettingChanged` takes no lock that a set holds while Windows is told
of the change.
`TokenGuardNoticeTests` holds each kind, the tray's colour, line and click with them (`TrayApp`'s wiring held by its
lines). Until setup was completed once, every way into the main window opens setup instead (`FirstRunGate`).

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
  X."), a tag that grew while the total held, a saturated PC ("Your PC is saturated; busiest: X."), or, while the
  pool's state (`UiData.State`) is amber or red and nothing else says why, where to look; it takes that state's colour,
  red only for a real risk. The header follows the pool's state alone: the token-leak guard's amber is said by the tray
  icon's tooltip and menu (`GuardTray`). In the
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
  changed at once; ticked only when that task starts PoolWatch, with a line under it when a task is there but does
  not, and put back when the change is not made) and update notices (`CheckForUpdates`); automatic cache clearing
  (`AutoClearEnabled`, `AutoClearEveryHours`, `AutoClearPoolGb`), named as its three rules (On a schedule, At a pool
  size, Whichever comes first, recommended), with the line under it saying first that a clear takes a few seconds and
  video or the mouse may stutter while it runs, then the 30-minute spacing; under it, **Notifications**
  (`PoolNotices`): which kernel pool notices the tray shows, as three choices (Every growth, the default; Only when it
  needs attention, the ones the tray icon shows red; Off), each with a tooltip, and the line under them that these are
  the notices about the kernel pool and that the token-leak guard's, which need your click, always appear (in that
  column because the form must fit a 1024 x 600 screen); deeper captures (`DeeperCapture`, with a
  line saying when it has no
  effect); the token-leak guard (`TokenGuardEnabled`: a line under the box with its trade-off, that it lasts until
  sign-out, that PoolWatch turns it on again each time it starts and that unticking it ends it; the rest in its tooltip,
  one sentence a line; unticking it and saving puts back the setting the guard changed in this sign-in, also when an
  earlier run of PoolWatch changed it, when Windows allows it (the message after Save says when it did not),
  `TokenLeakGuard.TurnOff` and `TokenGuardSetting`); and,
  when the Codex app is installed with the launcher and the shortcut is not on the desktop, an offer to create the
  Codex shortcut again (`ShortcutOffer`). Its values are checked before saving (`SimpleSettings`, unit-tested: ranges,
  and an automatic clear needs a schedule or a pool size), and the form's fields are bound in `SimpleSettingsFields`,
  unit-tested too. **Advanced...** opens the settings in the property grid, except the three marked
  `[Browsable(false)]`: `ShowRam` (the graph's Show RAM switch), `ServiceProfiles` (saved from the Services tab) and
  `SettingsVersion` (the file's format, which every save writes). Every setting the grid shows has a short description
  (`[Description]`, in plain words: what it does and its default, its unit where it has one, and the limits `Normalize`
  holds it to: a number's range, a text's length, a list's size, a local drive path), which the box under the grid
  shows for the selected one. A choice of a few (`PoolNotices`, which the grid offers as a list) names each value and
  its default as "EveryGrowth (default)". A text that is empty by default says "Empty (the default)" and what empty
  means; the
  watched apps give how many there are by default and two of them; a growth alert's own two values give the defaults
  of an alert added in the list's editor (that editor, WinForms' own, shows no description box). The box shows two
  lines, so each description in the grid is at most two lines at the dialog's own size; tests measure that, and hold
  each stated default and limit to the code. Starting at sign-in is not a setting:
  this form's sign-in box sets the scheduled task itself, through `LogonTask.Set`
  (a `startAtLogon` an earlier build saved in `settings.json`, never read, is skipped when read and left out at the next
  save); the form and the grid edit the same settings and share one Save. Save puts the
  automatic clear, the token-leak guard, the notifications and update notices into effect at once (section 14), only
  those the form changed; every other setting applies at PoolWatch's next start, and the form says which. Each opening
  starts from `settings.json`, so a change saved for the next start (deeper capture, an Advanced value) shows again and
  is never written back over, with the four at-once settings as they run (with `--config`, from the running settings,
  and Save then keeps nothing for the next start: section 14). Both forms are created each time they open.
- **Pool causes** is one list merging what were four tabs: open incidents and those of the last 7 days; tags the
  detector marks growing, retained or released that no open incident covers; live processes whose handles rose 5,000 or
  whose charged pool rose 32 MB in the hour, or that were orphaned while PoolWatch watched and still do 500 I/O
  operations a second; and, from the last 24 hours, spawn storms, child accumulation, git roots, stray `.git` folders,
  token-leak and filter-driver findings (warning or alert) and every cache clear's result. What is happening now comes
  first. The columns: What is growing (the tag in words, with its driver), Grew, Cleared, Net, Per hour, Likely cause,
  Confidence (in words), Impact and When. A row of a tag growing or held now, and of its open incident, takes Grew,
  Cleared, Net and Per hour from the growth stats over the picked window when they count that tag
  (`PoolCauses.WithWindow`; the legend row says which window, "Grew, Cleared, Net change and Per hour of tags now: last
  6 hours"); every other row keeps its own figures (a process's rise in pool charge, else in handles, else the I/O
  operations a second of one left running after its parent exited; an earlier incident's growth; what a clear or a
  release freed), and only Net is ever signed. **Impact** is the level of the tag's growth per hour with
  PoolWatch's clears added back over that window (`ImpactRules.FromGrowth`: section 16 has the scale), drawn with its
  word whole at any scaling; a held tag is None, and the cell is empty until the rate is measured. The selected row's
  details are a title, a line saying what PoolWatch already does about it ("Handled: the standby cache is emptied every
  hour, or at 8 GB.", "Handled: the token-leak guard is on, so Windows does not leak these."), the incident report's
  summary, a tag's top three suspects with their evidence and the recent notable events (a tag's begin with the
  window's figures as a sentence), a process's counts, chain and command line, or a finding's text, with Export report
  (the selected row's report folder, else the newest incident's, redacted by default: section 10), Open report folder
  (once the row's report folder exists) and Copy summary (when the row has a summary or evidence). In the details an
  incident's summary gives its kind and each suspect's confidence in the app's words, its window's times in local
  time to the second, and no Markdown marks; reports, exports and Copy summary
  keep their own text, but name a confidence in the same words as the column (Copy summary copies report.json, so the
  tag's code span, the kind's name such as `NonPagedTotal` and the times in UTC). A known issue that matches
  (section 17) adds its note under the details of its tags and of their incidents. A click on a column header sorts by
  it (numbers by value, the confidence by strength, When by time, names ignoring case; a cell with no number last either
  way; ties keep the list's own order), a second click reverses it, and Windows' own sort arrow shows on that header
  (`ListSort`, the one rule of every sortable list); a selected row stays selected. A line above the list says what the
  colours mean: pink for an open incident or an alert finding, yellow for a tag growing or held now, a process piling
  up, or a warning finding. A tag row's cause comes from its ranking (the correlator over the last hour, tens of
  milliseconds), built on a pool thread, one at a time (`TagRankings`, `RankPlan`): the selected row's tag at once,
  every listed tag once a few seconds apart, then each refreshed after 5 minutes (the selected one after 30 s), never
  two within 30 s. Until a tag has a ranking its cause cell is blank and its details say "Ranking the suspects…". The
  details box stays a native text box, so its text can be selected and copied. Where the window is too short for the
  details, they keep the height they need and scroll up and down (`DetailsPane`): the title, the words (at least two of
  their lines) and the green line stay one under the other, and the actions one column; another row's details open at
  their top, and so does each of the two texts without a row ("Nothing stands out right now", "Select a row to see its
  details"). The details are a tab stop of their own, before the words and the actions: Tab or Shift+Tab into them
  shows their top (`DetailsPane.Select`; the focus given back when the window is active again keeps their place), with
  the focus the title is framed as the focus while Windows shows focus cues (after the first Tab or Alt), and the
  arrow keys, Page Up, Page Down, Home and End scroll them. The list and its legend keep 58% of the tab (or what the
  owner dragged the splitter to); where that leaves the list no whole row, the splitter moves down exactly as far as
  its header and one row need, if the details then keep their title's first line, and otherwise stays (`CausesSplit`).
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
  at first. Cost now is the latest sampling window (`ProcessLoad.CostNow`); for an app busy in the background listed as
  a row of its own, its 30-minute average (`Diagnostics.HeavyBackground`, `TaskCost.Average`), which its details say (a
  busy app whose process a startup app or task runs is that entry's row, `TaskRows.Merge`, with the latest window and
  "right now"); a program that runs
  through a shared launcher (cmd, PowerShell) cannot be told apart,
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
between samples: a change across a sleep or other gap (more than the sampling interval plus 2 minutes) is neither
counted nor timed, and the next sample starts again from where the series is then; a clear left open across a sleep
still keeps its drop, as the detector says. The gaps, the minutes, how long they are kept, a clear's place among the
samples and each window are timed on the clock that only goes forward (section 4), so a system clock set back or
forward neither counts a change twice nor drops one, and each clear is counted once. The rate is (Grew - Fell) per
hour measured, said once 30 minutes are measured ("known in 22 min" before), so the rate times the hours measured,
less Cleared, is the net change. Its caption says whether the window holds a clear of PoolWatch's ("Without the clears
it would grow", or "would fall") or none ("It grows", "It falls"); before the rate is known, and for a rate under a
byte an hour ("nothing"), it says no direction ("Growth per hour"). Durations are to the nearest minute, the "so far"
line rounded down. It holds typically 1 to 3 MB, at most about 5 MB (256 tags changing every minute for 25 hours), and
is never written to disk; `cache\profile.json` reports its size. Limits: a clear whose purge call fails only after its
own 2-minute measurement is still counted as cleared (the status sentence's hour leaves it out), and a tag's counts
start when it first holds 4 MB or when PoolWatch starts.

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
tested) is a window of its own with eight steps on a rail: Welcome (the mission, the four pillars, and the level
choice, Simple or Advanced, which sets the app's view; both run the same steps), Your PC ("How your PC is impacted":
each known cause with its impact level from `ImpactRules.FromFacts`, section 16, or Not known where PoolWatch could not
tell, and "Fix in step N", "Trim in the app" or "Still watching"; listed by level, the highest first, with Not known and
a check still running just above None, `ImpactRows.ByLevel`), Safety (three promises, in green), Standby cache (On a
schedule, At a pool size, Whichever comes first (recommended, preselected on every PC at a first run), Decide later; a
small chart of the kernel pool; the stutter line; and, with a RAMMap clear task of the user's own, the note that
choosing a rule replaces it), Fixes (start when you sign in, Defender performance mode with its instructions, the Codex
shortcut as a Yes/No, and the token-leak guard when the check found the leak, its "Why?" open with what you will notice
and how to undo it, with the pages it cites), Services (for later, in the app: setup changes no services), Records
(what PoolWatch keeps on disk: Reports only, recommended and preselected, or Detailed recording, each with what it
writes, who needs it and what it costs; under them when reports and records are cleaned up, 1 to 90 days, the same
setting for both; Next writes both, `LiveSettings.Apply`) and Ready (what setup set, the Records line among it, the
update-notice box, where PoolWatch lives). "How PoolWatch works" (`HowItWorksForm`) opens from
every step's footer. The window is laid out from its fonts at any scaling, cut to the working area with the page
scrolling, opens on the screen of the window it opens over (else the one with the pointer), and is built once: a change
updates its rows in place, so a press never rebuilds the page. Switches, choice cards and the Yes/No report their
checked state to screen readers. Setup reads the PC as it opens ("Looking at your PC..."), then, on a Windows build that
needs it, runs the token-leak check (usually about 20 seconds) once; the CI UI test never runs it. Steps 4 and 5 wait
for the read before Next ("Next is ready in a moment."), at most a minute in all (`EnvironmentProbe.HungAfter`): after
that setup goes on with the facts PoolWatch had and shows the read when it ends. Nothing is changed until Next on the
step that shows it: the Standby cache step writes the chosen rule (`LiveSettings.Apply`) and, exactly when the step
shows the note, replaces the user's task first (from the same scan; the rule is written right after); the Fixes step's
Next registers the sign-in task, creates the shortcut and turns the guard on through the same code as the cards. A Fixes
row that appears while the step shows (the read or the leak check ended) holds Next for a second (`NewRowWait`), so a
Next pressed as it arrives never applies what the user had no time to see. A change that fails stays on the step with
why, and Next again goes on without it. Each change is on the timeline as made in setup and listed under Active changes.
Until setup was completed once (`setup.json`), it opens on its own at every start, the sign-in start included, and the
main window stays hidden. Then a close the user asks for (the close box, Esc, Alt+F4, the taskbar's Close) asks first
(`ISetupHost.ConfirmCloseFirstRun`, No the default): a yes closes setup and PoolWatch keeps running in the tray, with
any change already made with Next; every way into the main window opens setup again (`FirstRunGate`), and so does the
next start. PoolWatch exiting, Windows shutting down or Task Manager close it without asking (`SetupFlow.MayClose`);
the Ready step's Open PoolWatch completes it, applies the level choice, starts the update check and shows the main
window. Opened again from the tray (**Run setup again...**), closing it (the close box, Esc, Alt+F4) cancels it at
once, without asking, and finishing it with Simple picked switches an open window to the Simple view at once.
Once setup was completed but no record mode was ever chosen (`setup.json` without `recordsChosenUtc`: every
1.0.0-beta.2 install, whose setup had no Records step), the Records step opens alone at each start (`FirstRunGate`,
`SetupFlow.RecordsOnly`): the same page and words, the rail listing only Records, no Back, and Done, which applies the
choice and records it; closed without Done it is cancelled at once (Reports only stays in force), and it opens again
at the next start. Passing the Records step of the whole setup records the choice too, and so does a Settings Save
that changes the record mode or the cleanup's days (`AtOnceSettings.ChoosesRecords`, `SettingsEditor.Commit`); a
Save that changes neither chooses nothing. Settings is reachable in either view: the Simple view's header has no
Settings button (by its approved design), so the tray menu has **Settings...**, right under Open PoolWatch, which
opens the same window as the Advanced view's button (`SettingsWindow`: one at a time, and one asked for while it is
open comes to the front), over the main window when that shows, else on its own with a taskbar button. A Save while
setup is open (the Records step alone opens at each start until Done) that puts another record mode or cleanup into
effect moves the Records step's choices to it (`SetupSession.FollowRecords`, on the setup window's thread, from
`LiveSettings.Applied`), so its Next or Done never puts an older choice back over it; a change of anything else
leaves the step's own picks as they are; a change put into effect before the setup window has a handle is read as it
gets one. The CI UI run clicks the tray menu's Settings... with the main window hidden
in the Simple view and checks that Settings opens on its own, with a taskbar button, centred on its screen (read from
where the window is, against its screen's working area: `TraySettingsCheck`); when no Settings window shows within
10 s it closes what the click opened, and a failure of the click is told to it in place of its box, so the run fails
that check instead of waiting on a window nobody closes.

The lag chart, the since-start chart and the raw numbers block (commit, memory lists, lag, CPU and page faults, the
token-leak guard's state) are not on the window. With Detailed recording the data is still recorded in the data
folder (`totals.csv`, `lag.csv`, `load.csv`, `findings.jsonl`); the Since boot report and the exports have it from
memory in either mode.

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
  its grid (`4-settings.png`, `5-settings-advanced.png`), each of setup's eight steps once it has read the PC
  (`6-setup-1-welcome.png` to `6-setup-8-ready.png`), How PoolWatch works (`6-setup-how-it-works.png`) and the
  Records step alone, as it opens after an upgrade (`6-setup-records-alone.png`), Active
  changes (`7-active-changes.png`), Report an issue (`8-report-issue.png`) and About (`9-about.png`). It also takes the
  window with an update notice for a sample release (`10-update-notice.png`), which it checks but those pages do not
  show (the test never asks GitHub). Nothing it presses changes anything, setup runs no token-leak check there, and it
  never applies a service profile at start.
- The installer test compiles the setup, installs it silently, makes a real change with the installed exe, starts the
  tray app, uninstalls, and checks that PoolWatch exited through its exit signal, the change was put back and the
  files, shortcut, sign-in task and Uninstall entry are gone; a second pass keeps the changes. Upgrades while a
  process started through the folder's short spelling or a junction to it runs the exe, and an uninstall while one
  runs it, must finish at the next restart without restarting the runner (none of them is told `/NORESTART`): the
  setup with exit 3010 and Windows' rename list moving the new exe into place, the uninstaller with every other file
  gone and the exe and the folder on that list to delete; the test then does that list's work itself, as Windows would
  at the restart, and checks the result. So must an upgrade while a second process holds one of PoolWatch's DLLs open
  without letting it be renamed or deleted (the setup names that process), the same setup run again before that
  restart (Inno Setup's own check for an unfinished install must find none of its files in Windows' list, and its
  moves come after the first run's), and a setup run before the restart that an uninstall left work for (after that
  restart's work the install is whole). An upgrade while a second process holds the uninstaller's record must finish
  now and write its uninstaller in a folder of its own, and an upgrade that fails after every file was written (its
  Uninstall entry cannot be written, by a deny rule for that step) must leave the version that was there as it was,
  its uninstaller included. So must the same failed upgrade while a second process holds one of the new files open
  without letting it be renamed or deleted, once the test has done the restart's work, with no launcher and no
  PoolWatch itself in the install folder until then; an upgrade that fails after Inno Setup wrote the new Uninstall
  entry (a second process opens the uninstaller's record while the setup runs) must write the old entry back, every
  value as it was; and an upgrade during which a second process opens an old file the setup has just set aside must
  put every old file back, finish at the next restart and name that process.
- On GitHub's Windows 11 Arm runners: the unit tests, and the x64 build, laid out as the setup installs it, running the
  self-test, the CSV checks and the end-to-end and diagnostics scenarios under emulation.
- A 45-minute soak checks the overhead budget under a busy-PC load, headless and with the window open (walking the
  Simple view and the Advanced view's three tabs, one a minute: `MainForm.StartSoakCycle`), judged as steady state from
  15 minutes in until 10 seconds before PoolWatch's own end of run (the start-up peak, and the samples after that
  window, are reported on their own lines, not judged; the end-of-run screenshots or exit reports last a few seconds,
  less than the 30 s between samples, so the last sample is usually before them, and the line says how long before).
  Flat memory is judged through the heap PoolWatch's full
  collections leave (the floors in its profile.json, from the same window; a slope of private memory over every sample
  would depend on where the window cuts the sawtooth between collections): their trend, under 5 MB an hour, when at
  least three floors span 15 minutes, and, in every run, private memory's steady maximum, which must stay under 100 MB.
  With the window open it also checks About's own figures, which PoolWatch's meter takes over the same steady state
  (read as the end-of-run walk begins; the peak working set under 100 MB), and that the About drawn first in that walk,
  before any other window, judged them and found them within the budget: a peak working set under 100 MB, CPU under
  0.5% of one core and nothing over budget. Drawn last, after the 8 windows (16 screenshots) the walk made in about 4 s,
  it read 127 to 131 MB where the steady peak was 86 to 96 MB, and the CPU and the writes of the walk itself. The UI test
  checks that the About it draws first, about 80 s in, judges nothing. The soak and an overhead check run on
  demand and before every release. In `overhead.json`, which the overhead check (`ci/overhead.ps1`) reads,
  `steadyStateSeconds` keeps its older name: it counts from the warm-up (the sample interval three times, at least
  30 s), as do the CPU and disk-write figures beside it, not from steady state; the `judged...` figures, which About
  holds against the budget, count from steady state (`judgedFromSeconds`).
- The agentic-load check (`ci/agentic-soak.ps1`, its own job beside the soak: on demand and on a release tag, where the
  release needs it) holds the whole app to the hard budget on a PC doing agentic coding, the load that broke it in
  1.0.0-beta.2: PoolWatch headless for 45 minutes (the soak's shortened windows, `ci/soak-agentic-config.json`) while
  tools/Leaker agent-load runs a dozen sessions of short Git Bash commands in rotating bursts (about 3,000 starts a
  minute asked; their pipeline stages are fork copies), about 600 idle processes and a Claude Code session's
  transcripts, and every 7 minutes a load that grows a pool tag (60,000 File handles held, then 30,000 files touched), so
  growth incidents open beside it (a 10-minute rule of 6 MB there). Judged from 15 minutes as the soak judges, from
  outside and from PoolWatch's own files, each check with its marker: the load ran (Git Bash there, at least 500 starts
  a minute seen, fork copies seen and not counted), CPU under 0.5% of one core, working set under 100 MB, disk writes
  under 5 MB an hour with the report folders' writes that could wait kept to what their 1 MB an hour allows
  (`profile.json`'s `writeBudget`: `canWaitMB` within `canWaitAllowanceMB`, the bound the budget itself states and its
  tests hold), at most 10 spawn-storm folders an hour, and at least one growth incident with its `report.json` and
  `report.md` in every incident folder. It writes `agentic-soak.md` with the stages and the writes by file.
- Text pins and check markers. Some app-layer tests hold a CI script, the setup script's [Code] or a source file to its
  text (`ScriptCode`), read without comments (a source file also without the code under an `#if`; a whole [Code]
  routine as written, with its comments): a definition only where it is the
  only one of its name in the code; a check of a CI script as a statement of its own in a named block,
  with its CHECK-RAN marker as the next statement (for a check in a loop, as the loop's whole body, and the list the
  loop goes over as strings and nothing else, in the loop's head or in a statement that is the only assignment of its
  variable in the script's code, as an assignment is written: `$name =`, `${name} =`, with a scope, an index or a
  compound operator, or in a multiple assignment; a variable set by Set-Variable or New-Variable is not read); and a
  statement of [Code] as Pascal tokens, its strings as written, with the conditions of the blocks around it, or a
  block of [Code] statement by statement; and every routine of [Code] whole, from its header to its own last `end;`
  (a routine Windows provides, to the semicolon after its `external` string), with each run of white space made one
  space (`UninstallerTests.EveryRoutineOfTheScriptIsHeldWhole` finds such a pin for each routine of [Code], holds its
  text to the routine, and fails when a routine has none). They fail when what they pin is edited, removed, commented
  out, put in a string, or moved into another block or under another condition, with three limits: an edit that only
  changes how long a run of white space is, or which white space it is, can leave them green; a [Code] statement given
  with a block in it shown as `begin ... end` does not hold what that block holds (the tests hold the statements of
  such a block with pins of their own); and a C# member is found wherever it is in its file's code, so one moved into
  another type leaves its pin green. A test that only finds a text in a routine or a file holds that it is there, not
  where (in [Code], the routine's whole pin holds where). The readers take [Code]'s comments and strings as Pascal
  Script does (a comment from `{` to the next `}`, from `(*` to the next `*)` that starts at its `*` or later, so `(*)`
  is a whole one, or from `//` to the line's end; a string from `'` to the next lone `'` or the line's end), split the
  script's lines as Inno Setup reads the file (a line ends at a CR LF, a CR or an LF), and take the white space at
  their ends as Inno Setup does (its compiler takes the characters up to #32 off a line's ends before it looks for a
  section's header, and its preprocessor skips #1 to #32 before a directive's `#`). No line of the script longer than
  two characters may end in a character up to #32 and a backslash: Inno Setup's preprocessor joins such a line to the
  next (the ISPPBuiltins.iss that Inno Setup installs beside its compiler, and that the preprocessor reads in before
  every script, makes the backslash its span symbol; a line of two characters or fewer it does not join), so it joins
  none of the script's lines. Outside its routines [Code] may hold only comments and plain declarations of constants
  and variables (a name and one number or string; names and a type; no name a reserved word of Pascal Script or of
  Delphi), and no text of it may start a compiler directive (`{$`, `(*$`): an event attribute before a routine
  (`<event('InitializeSetup')>`, which makes that routine run at that event too), a type or any other text there fails
  `UninstallerTests.OutsideItsRoutinesTheCodeHasOnlyPlainConstantsAndVariables`; those declarations are held only where
  a test reads them. [Code] is the script's last section, from its one header line, `[Code]`, to the end
  (`UninstallerTests.CodeSectionOf` fails on a second header and on a line that the preprocessor would join to the
  next, so no header is made of two lines, and `InstallerPagesTests.NoLineOfTheCodeBeginsWithABracketOrAHash` on a line
  after it that starts with `[` or `#`, in a comment too). Inno Setup's preprocessor passes [Code] on as it is
  (`UninstallerTests.ThePreprocessorPutsNothingIntoTheCode`): it joins no line, [Code] has no inline directive
  (`{#...}`), no directive line and no line that the preprocessor takes out as a comment of its own (one that starts
  with `//`), the script's directive lines are its nine as they are (no `#pragma`, no `#include`), and each of its
  inline directives gives AppVersion, NumericVersion or PublishDir. CI compiles the script itself, and its installer
  test runs it. No pin tells whether what it pins runs (code before it that ends the script or the routine leaves it
  green). So each check of a CI script that such a test pins prints `CHECK-RAN: <id>` in its own block as the statement
  right after it or right after the last check of the run of checks it is in, or as the statement right after the try
  statement (with a finally and no catch) whose body holds it; a marker anywhere else does not count. Each test that
  pins such a check holds it to its marker: by `ScriptCode.CheckThenMarker`, the first two of these (given one check,
  the marker right after it; given a run of checks, the marker right after its last; `OnlyCheckThenMarker` for a loop
  whose body is the check and its marker alone), or by `UninstallerTests.ChecksWithoutAMarker`, all three, which reads
  the script's blocks by their braces and holds each statement that starts with Assert-True and a space and that holds
  a text it is given, at the script's top level and in its blocks at any depth, but not inside brackets (`$( )`,
  `@( )`, `( )`): each test that holds checks by it (of the installer's test, and the UI test's hidden-window counts)
  gives it every text by which it pins an Assert-True check there. `ci/expected-checks.txt` lists each marker. In the
  workflow files, each step that runs a listed script as `./ci/<script>` saves its output, and the step after it fails
  the job, naming each listed check whose line is missing, or when the line that Complete-Assertions prints once no
  check failed is missing; the soak runs its pinned checks only on its ui leg, so on its headless leg a step of its own
  looks for that line alone. `CheckMarkerTests` holds the scripts, the list and the workflows to each other: a step
  with a line other than a comment line that names a listed script (its file name, in any case) is one of those checks
  of its output, or names it only in lines that run it so (such a line outside the steps and jobs it reads fails it);
  each such job to no condition (but the soak's and the agentic-load check's: they run on demand and on a release
  tag, and the release needs both), no job it needs to one,
  nothing in it to going on after a failure, and the soak's ui leg to its matrix; and Assert-True and
  Complete-Assertions, which every check goes through, to their text, with no other statement of the CI scripts naming
  their list of failures, `ErrorActionPreference` or the words of the line Complete-Assertions prints once no check
  failed, and each listed script naming Complete-Assertions only in its last statement. So in a run of such a job, a
  pinned check that did not run (for the soak, on its ui leg), or a run that ended before Complete-Assertions said that
  no check failed (on any leg), fails the job. For [Code] there are no markers, and no pin tells whether a statement
  runs; but as every routine is held whole, an `Exit` put before a pinned statement fails its routine's pin, a routine
  added to the script without a pin fails too (written into [Code] outside what Pascal Script reads as a comment or a
  string, put there by the preprocessor, or in a second [Code] section), and so does an event attribute or any other
  text between the routines but a comment or a plain declaration of a constant or a variable. A parameter of the
  script's other sections that calls a routine (`Check:`, `BeforeInstall:`, `{code:...}`) is held only where a test
  reads it, and the installer test checks what its silent runs do. Neither layer checks the data a check reads.

## 13. Change ledger and Active changes

**Active changes** is one list over several sources, because some changes already have a store of their own and a second
copy would be a second truth (`Engine/Changes`, `ChangeLedger`). The window (`ActiveChangesForm`) shows up to three
groups (`ChangeGroup`; an empty one is left out): the features PoolWatch turned on (the sign-in task, the automatic
clear, the token-leak guard, the Codex shortcut), each with Turn off (the shortcut, and a sign-in task that does not
start PoolWatch: Remove); what it turned off (services, scheduled tasks, startup apps, a clear task of the user's own),
each with Put back; and "Other changes PoolWatch made" (a service set to its Windows default, a scheduled task or
startup app that was off and that PoolWatch turned on), each with Put back too. A row that cannot be put back, or that
the uninstaller leaves, says so, and the footer then says that uninstalling puts the rest back ("except where marked"),
or says why it could not (a change Windows would not let it put back). Its data file keeps its older name,
`applied-changes.json`, so an installed copy's records stay readable.

| Kind | Source | Put back |
|---|---|---|
| Start at sign-in | Task Scheduler (the `\PoolWatch` task exists) | removes the task |
| Automatic clear, token-leak guard | the running settings | turns it off (section 14); turning the guard off also puts back the Windows setting it replaced in this sign-in, when Windows allows it (when Windows refuses, the next row lists the setting); one the guard found already changed stays until sign-out |
| The token-leak guard's Windows setting, while the guard is off | `cache\token-guard-setting.json`, the value the guard replaced, one per sign-in with the sign-in it belongs to (`TokenGuardSetting`) | puts the value back while the live setting is still 0 (listed only when Windows did not allow that as the guard was turned off) |
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
on. Windows does not stop a service while one that depends on it has not stopped, nor start one while a service it
needs is Disabled, so of the services one change turns off, one that depends on another is stopped first, and of those one put
back restores, the other comes back first (`ServicePlan.InDependencyOrder`, from the dependencies Windows lists for each
service; Put all back in Active changes takes that order from the services source, `IChangeSource.PutBackOrder`).
`ServiceControl.Change` sets the start type first and then asks for the stop, so a service already Disabled cannot be
started again in between (by a trigger, say); when the start type cannot be set, no stop is asked for and nothing
changed. A service that is starting, resuming, pausing or stopping is waited for to settle, up to 20 seconds, and a stop
Windows took is waited for up to 20 seconds more; two pure functions decide each step (`ServiceControl.Looked` for the
state, `ServiceControl.Answered` for Windows' answer to the stop, 1062 counting as stopped, and a first 1061, from a
service that has just begun to pause, say, looked at again once). A service whose stop Windows refused (one that depends
on it has not stopped, which PoolWatch never stops, since nobody chose it), that did not settle or stop in time, or whose
state Windows could not give keeps its new start type: its result says why and when it stops, never done (it names the
services that kept it from stopping only when Windows refused for them, error 1051: each one with its state, running,
paused, starting, stopping, resuming or pausing, as Windows lists them right then, `EnumDependentServicesW` with
`SERVICE_ACTIVE` into `NotDone.Dependents`, or as the services were read when that list cannot be read; it says "still
running" only of one that runs: one still starting, stopping, resuming, pausing or paused when the wait ended is "not
stopped yet", in that state, `NotDoneCause.NotSettled`, and one that was paused when Windows refused its stop is "still
paused", `NotDone.Paused`; one that does not take a stop, error 1052, `NotDoneCause.TakesNoStop`, is said to stop when
Windows restarts, never when it is turned off again), and its record keeps the process it ran on in
(`ServiceChange.NotStoppedPid`; 0 when that was not known, and then any process counts). While it runs on in that
process, the State column says "set to Disabled, still running" (while it is in one of those states, that state, "set to
Disabled, paused", or "not known to have stopped" when Windows could not give it) and the self-repair check does not
warn (`ServicePlan.KeptRunning`); once it is seen STOPPED (`ServiceView.NeitherRunningNorStopped` false), in another
process or at another start type, it is read as any other. A service set to Disabled outside PoolWatch (no record of
PoolWatch's) reads "off (changed outside PoolWatch)" or "off (Windows default)" only once it is seen STOPPED, else
"set to Disabled, still running (...)", "set to Disabled, paused (...)" (or the state it is in) or "set to Disabled, not known to
have stopped (...)". Every other service reads as its state and start type ("running", "Manual, stopped", "off" for a
Disabled one seen STOPPED, "Disabled, paused", "Automatic, state not known"; `ServiceView.Between` holds the state), so a
protected service or one set to its Windows default never reads "off" or "stopped" while it is paused, and a service
PoolWatch cannot open reads "could not read" in the State and Cost columns (`ServiceControl.Unread`: its start type and
state unknown, so no change acts on it).

The self-repair check runs every 5 minutes on `Environment.TickCount64` (`IServicesHost.NowMs`), never the wall clock,
so setting the clock back or forward neither holds a check back nor runs one early. It skips a service from the save of
its Turn off's record until that change is done (its mark saved, for one that did not stop), as it skips one being put
back. Every Turn off and Put back also marks its record as under way in its own process (`ServiceChange.UnderWay`: the
process number, its start time and the `GetTickCount64` the change began at) before it acts, and takes the mark off (or
removes the record) once it is done, so the check of another PoolWatch reads it too: the app's check skips a record
another PoolWatch marked (`--service-off`, `--service-restore`) while that process still runs (the same process: its
start time must match, so a later process given its number is not it) and for no longer than the longest a Turn off can
take, `ServicesController.ChangeAtMost`. That bound is built from the waits in the code: the two 20-second waits,
Windows' wait on a busy service for each of the up to two stop requests (`ControlService` blocks up to 30 seconds while
any service is busy in its control handler, or as long as `ServicesPipeTimeout` sets when it is set longer, read at
each check), and 5 seconds for the rest: 105 seconds with Windows' default wait. It bounds a process that hangs, or
whose save of the end of its change failed; otherwise the skip ends as soon as the change does. The bound is sized for
a Turn off. A Put back's start can take longer (Windows first starts the stopped services it depends on), so a Put
back's mark says it is one (`ChangeUnderWay.PutBack`), and a service as a Put back leaves it (at its original start
type, and STOPPED for an original of Disabled: `ServicePlan.LeftByItsPutBack`) is never reported as switched back on
outside PoolWatch while its record carries that mark, however long the Put back takes and also once that PoolWatch has
exited without removing the record (its save failed, say); the State column says "PoolWatch began to put back its
change of ...; Put back finishes it" then, and the next Put back finds it back and removes the record, or starts it as
the record says. A Turn off's mark left behind is read as any other record once that process has exited or the bound is
over. While the check skips a record, and when a record changed while it read the services, it looks again 15 seconds later (`LookAgainMs`), so a
switch-on right after a change is reported then, not a whole check later. It reads `service-changes.json` each time, before reading the
services and again after, so a Put back from the command line that marked or removed its record while the services were
read is never reported as switched back on. It warns once per state a service is switched back to for each record of
it: a Turn off that set the start type (here or with `--service-off`) saved a new record, so a later switch-on is
reported again, while one whose change failed took its record back, so nothing is repeated, also when a check ran while
it was under way (what was reported is forgotten only once the service is seen Disabled and STOPPED, not paused,
starting, stopping, resuming or pausing, or is no longer installed in a list that was read). A service whose start type
or state could not be read (`ServicePlan.NotRead`: one PoolWatch cannot open, `ServiceControl.Unread`, say) is not
read at all by the check: it reports nothing, takes no `NotStoppedPid` mark off and forgets nothing it reported, and it
looks again 15 seconds later the first time (a service that stays unreadable is read at the usual pace). The memory is
in the app's process only: a restart reports a state again, never hides one.

A services list that cannot be read (`ServiceControl.Enumerate` throws when the service control manager cannot be opened
or the listing fails, and `ServicesController` counts a list with no service as not read) is never "nothing installed"
(`ServicesNotReadException`): Turn off, the profile applied at start, Put back and Set to its Windows default change
nothing and say "PoolWatch could not read the services", every record is kept, and the self-repair check reports and
forgets nothing until the list reads again. (Windows lists no service only when the listing failed: a running Windows
always has services.) What Turn off records of whether a service was running comes from what its own stop saw
(`ServiceControl.StopAndWait`): a stop asked for, which happens only while it runs or is paused, means it was running,
whatever came after; one still starting, resuming or pausing when the wait to settle ended was running too; a service
seen stopped, or stopping on its own, was not. A Turn off to Manual asks for no stop: it records the state Windows gives
once the start type is set (`ServiceControl.Change`). The record saved before the change says it was running only when
the services list showed it running, paused, starting, resuming or pausing, and not known otherwise
(`ServicePlan.RunningBeforeTheChange`; `ServiceChange.OriginalRunningNotKnown`, always written, true or false): a
service listed as stopped may start before Turn off reaches it, and only the change can say it was not running. A
record without `originalRunningNotKnown` was written by v1.0.0-beta.1, which saved "running" only when the services
list read the service as RUNNING, so also "not running" for one it turned off while paused, starting or in a state
Windows could not give: its "running" is read as running, its "not running" as not known. A change that could not tell either leaves the saved value as it is (a list that saw
it running stays "running"; otherwise it stays not known), and so does an end of change whose save failed, so a record
never says "was not running" of a service the change found running. Active changes then says "not known if it was
running", never the words of one that was not. For an original of Disabled (a "Set to its Windows default" record, or a
Turn off of a service Disabled but running; of beta.1 too) its row says nothing of whether the service ran, neither
"and running" nor "not known", since Put back sets that one to Disabled and stops it if it runs, whatever it did
before (`ChangeWords.ServiceDetail`). Put back starts a service again only when it was running, never one put back to
Disabled (`ServicePlan.StartAfterRestore`); for one not known, it sets the start type back and does not start it, and,
when its original is not Disabled and it is not running then, says plainly that PoolWatch does not know whether it was
running (`ServicePlan.RunningNotKnown`, `ServicePlan.RunningNotKnownText`), in the uninstaller's summary too: a
beta.1 "Set to its Windows default" record that says "not running" is put back whole. A record is already back, so Put back only removes it
(`ServicePlan.AlreadyRestored`), only when the service is at its original start type and, for an original of Disabled,
seen STOPPED (else Put back stops it), or for one that was running, or not known, running. Put back of a service whose
start type cannot be read now (one PoolWatch cannot open) changes nothing, keeps the record and says that it could not
read it (`ServicePlan.GateRestore`), never that the service was changed since. "Saved about N cores over the
hour after" is timed on the same clock that only goes forward (`ServiceSavings`), and so are the costs it compares:
the Cost column's load per process between two samples and the minute points a change's before-cost averages
(`Diagnostics.StepLoad`, `ProcessCostHistory`, both on each sample's `Sample.TickMs`), so setting the system clock back
neither freezes them nor gives a rate over a wrong time. Putting back only a service whose
needed one is still Disabled (Windows error 1068) names the needed one and says to put it back too
(`ServicePlan.NotStartedText`). Turn off, like the run at start, leaves a service that is already as it would leave it
(`ServicePlan.IsOff`: at the start type it sets, and seen STOPPED when that is Disabled) as it is and records nothing
for it, so Active changes lists only what changed. A Disabled service that runs, is paused, is starting, stopping,
resuming or pausing, or whose state Windows could not give is not off, so turning it off again tries to stop it again
(`ServiceControl.Looked`: a stop for one that runs or is paused, a wait for one that is starting, stopping, resuming or
pausing, and no stop while Windows still cannot give its state). Turn off's question lists only the services it
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
while `service-changes.json` or the services cannot be read it changes nothing (each service is left as it is, with
why, never "not installed"); otherwise each service of the profile that is installed, on the catalogue, passes Turn
off's gate (`ServicePlan.BuildApply`: never a protected service, the Insider rules included) and is not already as
Turn off leaves it (`ServicePlan.IsOff`) has its original recorded first and goes
through the live gate, and every other one is left as it is, with why in the log. Each change is a log line and a
finding, with "profile NAME, at start" as its source, and a row in Active changes. The tray's notice says what the run
turned off and what it could not, with the log file's path for why (when it would pass the 250 characters a notice
shows: fewer names, then no "Click here", then "The profile applied at start" for the profile's name when that is
shorter; the log's path and "says why." stay whole, and no word is cut; only a log folder so long, one given with
`--out`, that the notice is still too long makes the words before the log's path give way from the first one on, marked
"...", and a log path too long even for that ends the notice where a word ends), and a click on it opens Active changes (`BalloonClick`: the latest notice's own action, run once, so a click
never runs an older notice's; any other click opens the main window, section 11). The self-repair warning for a service turned back on names the profile only when it lists
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
then as Turn off leaves it (`ServicePlan.IsOff`).

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

A saved setting takes effect at PoolWatch's next start, except four: the automatic clear and the token-leak guard
apply at once when setup, a card, Settings' Save or Active changes changes them, and update notices when setup or
Settings does, or the next-clear line, Active changes and the update notice would be wrong until a restart; and the
notifications (`PoolNotices`) when Settings does, since the tray reads the running value at each notice and at each
refresh of its icon.
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
Save then puts only the four at-once settings into effect and says that nothing was kept for the next start.

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
  ("Decide later"), the Simple view offers the hourly clear on a PC with a ReFS volume or a Dev Drive, or whose drives
  the scan could not all read, or before the scan has read them (one of them may be such a drive:
  `ClearAdvice.MayHaveReFs`), on an NTFS-only PC once a clear showed that it helps, or once file-system records that a
  clear frees grew in the last day (`ClearAdvice.ForSimpleView`).

## 16. Environment detection and suggestions

**Detection** (`Engine/Setup/EnvironmentProbe`) is one scan of explicit reads, never a crawl, on a worker thread and one
at a time: the Windows name, display version and build, the Insider channel, the processor (`IsWow64Process2`, so an
x64 process on Arm says Arm), memory, the volumes with their file system and Dev Drive flag and each Dev Drive's trusted
flag, AI tools (the desktop apps' Store packages, Claude Code, AI Code Conductor, and a few fixed paths for VS Code,
Cursor and the Codex and Claude CLIs), a RAMMap clear task of the user's own, the sign-in task, the optional services
running, and Microsoft Defender's real-time protection and Dev Drive performance mode through WMI (never its exclusion
list). It runs at the first start, from the tray menu, when the main window opens with facts more than a day old or
from another Windows build (one registry read decides), and from the tray's token-leak watch while there are no facts
at all (after an upgrade, an older version's cache is not read); once there are facts, never on a timer or per sample.
A read that fails counts as not known. The result is cached in `cache\environment.json` (no user names, no paths but
drive roots) and read back as untrusted.

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
Low; the Codex desktop app installed, Low; services PoolWatch knows running, Low; the token leak found, High. None is
only for what was read and found clean (real-time protection off, or performance mode on and every Dev Drive trusted) or
a check that ran and found nothing; it also covers a build without the leak and the guard on, since the leak cannot
build up there. A failed read the level depends on gives no level where the scan records it: Defender's settings, a Dev
Drive's trust, and the volumes, Store apps or running services; so does a token-leak check that could not run (something
already working around the leak stops it too) or gave no clear answer. Setup shows Not known for them
(`ImpactRow.NotKnown`: the word in the grey of "Still watching", the meter's bars empty, "Still watching" under it, no
fix offered), never None. A volume the scan could not read whole is not read too (`EnvironmentFacts.VolumesNotAllRead`):
a fixed one, or one whose drive type Windows reports as unknown (`GetDriveTypeW` 0 or 1), whose file system Windows
could not read (named in `UnreadVolumes`), and a ReFS one whose Dev Drive flag could not be read (`IsDevDrive` null).
One of an unknown drive type whose file system was read counts by it, as a fixed one does (another file system than
ReFS is never a Dev Drive).
With one, the Dev Drive row has no level unless a ReFS drive was found, and the Defender row none unless real-time
protection is off or a Dev Drive found is checked before its files open; the row names those drives. Not known is not
a level of the scale, so it is never compared, counted or drawn as one; step 2 lists it between Low and None. What
PoolWatch measures for a cause wins (`Combine`), over Not known too.

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
check), then a day plus up to an hour after each check, on the clock that only goes forward (it goes on while the PC
sleeps), so a PC that sleeps still checks daily. While the setting is off nothing is sent and the check stays due.

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

**The launcher** (`src/PoolWatch.Launcher`) is how PoolWatch starts as administrator. .NET reads environment variables
that load code (a profiler, a start-up hook, another GC or JIT) before a program's own code runs, and a program running
as the user can set them for the user's account, so the elevated process must never get the user's environment. The
layout (`LaunchGate`): the install folder's `PoolWatch.exe` is the launcher, and PoolWatch itself is the program of the
same name in its `app` folder, with the files it loads from its own folder (`app\amd64\`). Both zips also carry the
launcher as `PoolWatch-selfcontained.exe`, the name the self-contained zip's program had before 1.0, and the setup puts
it there where a file of that name is. The launcher thus has the names and the place the program had before 1.0, so the
Start menu entry, Explorer, **Run as administrator** on it, an administrator console and a logon task or a shortcut made
by 1.0.0-beta.1 (with the setup or either zip) all start the launcher, also where one zip was unpacked over a copy of
the other. A file under either name counts as the launcher only when it has the same product version as
`app\PoolWatch.exe` (`LaunchGate.Judge`: CI holds the two equal in every build), so an older program left under one of
those names is never handed a start, registered as the logon task or taken for a fine task. Its manifest asks for
administrator rights; the program's does not. It is published with NativeAOT (no .NET runtime to load, and its build
links no start-up hook support, no loadable GC and no diagnostic server; the runtime compiled in reads a few `DOTNET_`
settings of its own from its environment, its GC's among them, none of which loads code) and linked with
`/DEPENDENTLOADFLAG:0x800`, so Windows loads the DLLs it imports from System32 only (CI checks the flag in the built
exe). Before it starts anything it finds its own path as the kernel names its open file (`GetFinalPathNameByHandleW`:
long names, junctions and `subst` drives resolved) and checks (`Program.WhyNotStart`) that it has administrator rights
itself (a compatibility setting or UAC off for a standard account can start it without them, and PoolWatch would then
refuse and point back at the shortcut), that its folder is inside Program Files as the machine's registry names it, that
`app\PoolWatch.exe` is there, and that no one but an administrator can change the launcher, the program or a folder
either is in (a link on the way followed where it leads), by the same access rules PoolWatch applies to itself
(`Engine/AdminFolderRules.cs`, compiled into both; the launcher counts its own account as an administrator's only when
every program of it runs as administrator, `ElevatedAccounts.OfThisProcess`: its token holds Administrators unsplit and
is a sign-in in person, or, for a batch, service or network sign-in, whose token can be unsplit even where UAC splits
the account's sign-in in person, UAC's settings say UAC is off or that it is this PC's built-in Administrator without
Admin Approval Mode), so a copy of the signed launcher above some other `PoolWatch.exe` starts nothing. It starts
`app\PoolWatch.exe` with `CreateProcessW` and an explicit environment block (`EnvironmentBlock`, passed with
`CREATE_UNICODE_ENVIRONMENT`): an allowlist whose values come from the kernel's Windows folder, the machine's registry
(Program Files, ProgramData, the processor facts), the profile folder from the machine's profile list (the
`ProfileImagePath` of the account's SID), the account's AppData folders from its own User Shell Folders settings
(`AppData`, `Local AppData`; where Windows puts them when a setting is not there), each read as written with only
`%SystemDrive%` and `%SystemRoot%` (from the Windows folder) and `%USERPROFILE%` (that profile folder) filled in, and a
value that names any other variable left out (`WindowsPaths`; the shell's known-folder calls would fill a setting's
variables in from the calling process's own environment, so the launcher makes none), and its names, Path set to
Windows' own folders, a marker that it was started there, and nothing else: no variable of the launcher's own
environment is passed on, and `EnvironmentBlock.Add`, which every variable goes through, refuses any name starting with
`DOTNET_`, `COMPlus_`, `CORECLR_` or `COR_` whatever the allowlist names. The arguments are quoted back (`CommandLine`)
so that PoolWatch reads exactly the arguments the launcher got; the caller's standard handles, window state and the
shortcut it was started from are passed on (`Native.ForChild`), so a window of a PoolWatch started from a shortcut
groups with it and a pin of it is that shortcut; a PoolWatch started any other way (the logon task, Explorer, a console)
gets the launcher's AppUserModelID with `STARTF_PREVENTPINNING` instead, so none of its windows can be pinned, as such a
pin would name `app\PoolWatch.exe` itself (the setup's Start menu entry carries that same AppUserModelID, so PoolWatch's
windows and notices are one app with that entry however PoolWatch was started; the setup also registers that
AppUserModelID in Windows' own list, as Microsoft documents it for an unpackaged app's notifications:
`HKLM\Software\Classes\AppUserModelId\PoolWatch.Monitor` with `DisplayName` and an `IconUri` naming `poolwatch.png`,
the emblem as a PNG installed beside the launcher; the owner's PC had no such entry while the "..." menu on a notice did
nothing, and whether the entry is what that menu needs is to be seen on a real PC; the uninstaller removes it);
PoolWatch is started in its own app folder, so nothing loaded by a bare name before its own code runs comes from the folder the caller was in, and that
folder is passed on in the block instead (`POOLWATCH_STARTED_IN`, the launcher's own current folder, never a variable of
its environment); it waits and ends with PoolWatch's exit code. In PoolWatch, `LaunchGate.Check` runs first in
`Program.Main`: not elevated, it hands the start to the launcher through the shell (the administrator prompt is then the
launcher's); elevated without the marker (an administrator starting `app\PoolWatch.exe` from their own console, or with
**Run as administrator** on that file), it starts again through the launcher and does no work of its own (in a console
mode it waits for that PoolWatch and passes on its output and exit code; otherwise it ends at once); with the marker but
a start-up variable all the same, it refuses. Then the files named on the command line are made full paths against the
folder the launcher was started in (`LaunchGate.StartedIn`; its own current folder in a start without the launcher), and
its own folder becomes its current one (`CommandLineOptions.ResolvePaths`, `Program.UseOwnFolder`). The CI and test
switches (`InstallGuard.Applies`) run as they are once elevated, without the launcher, as does the token-leak test's
helper, which inherits the clean environment of the PoolWatch that starts it. `StartupHookSupport` is off in the build
too. What a program of the account will see, PoolWatch reads from the account's own environment as data
(`UserEnvironment`, built by Windows' `CreateEnvironmentBlock` without inheriting PoolWatch's): a task's or startup
app's command line is filled in from it, as Task Scheduler and Windows fill it in when that program starts, and the
account's TEMP folder is redacted with PoolWatch's own. The logon task starts the launcher (`LogonTask.TaskXml`); one
made by an earlier version names the same path and needs no change. The check at every start (`LogonTaskCheck.Run`,
`LogonTaskPolicy.Judge`) judges a task untrusted when an action starts something outside Program Files, names its
program through a `%name%` (Task Scheduler fills it in from the account's environment, which the account can change), or
is not a program, and outdated when an action names a program with PoolWatch's name that is not a launcher
(`LaunchGate.Judge`: a file with a launcher's name that has `app\PoolWatch.exe` below it, of the same product version),
such as a file that is not there (the task starts nothing, and is said so), `app\PoolWatch.exe` itself, an older
PoolWatch elsewhere, or an older program left under a launcher's name. A task that passes those is still untrusted
unless it is exactly the task PoolWatch makes: one action; when the running PoolWatch is the installed, checked one,
that action's program this PoolWatch's own launcher (either of its names in its install folder); `--tray` and no start
folder; this account (`ExistingTask.UserId`, matched by name or SID: `LogonTaskPolicy.IsAccount`), signed in
interactively (`ExistingTask.LogonType`, `TASK_LOGON_INTERACTIVE_TOKEN`), with highest privileges; an account, sign-in
kind or run level that cannot be read is not PoolWatch's. A task that is there but cannot be read (any Task Scheduler
error but "no such task", `LogonTask.IsNoSuchTask`) counts as untrusted. Each is said in the log and as a setup finding,
and the tray app asks whether to replace the task with PoolWatch's own for this PoolWatch's launcher, or remove it;
nothing is changed without that answer, and a headless run only says so. A re-point registers the task and its sign-in
trigger on or off as Task Scheduler had them (`ExistingTask.Enabled`, read from `IRegisteredTask.Enabled`;
`ExistingTask.SignInTrigger`, from the `ITrigger.Type` and `ITrigger.Enabled` of its triggers: on when a sign-in trigger
is on, off when it has sign-in triggers and each is off, none when it has no sign-in trigger, which a re-point registers
off; each null when it cannot be read, and then registered on). The question says which stays off and which is turned on
(`LogonTaskCheck.RePointKeeps`), and so does the outcome (`RePointKept`). Settings' box, the setup guide and the
suggestions (`EnvironmentFacts.SignIn`) and Active changes take the task for one that starts PoolWatch only when it is
on, a sign-in trigger of it is on, the check finds it fine apart from its run level (`Judge` with `judgeRunLevel:
false`), and it runs with highest privileges where the account needs them (`LogonTaskPolicy.AtSignIn`, the Core
`SignInState`). The task signs in in person, whose token UAC splits for an administrator: a task without highest
privileges then gets the account's token without administrator rights, which cannot start the `requireAdministrator`
launcher. So they are needed unless every program of the account runs as administrator (`LogonTask.HighestNeeded`, by
`ElevatedAccounts.OfThisProcess`, as the launcher judges its own account): PoolWatch's own token is a sign-in in person
that UAC did not split, or, for a PoolWatch run from a batch or service sign-in, whose token can be unsplit even where
UAC splits the account's sign-in in person, UAC's settings say UAC is off or that this is the built-in Administrator
without Admin Approval Mode. Each other state is said by which it is: the task turned off, its sign-in trigger turned
off, no sign-in trigger (`SignInStates.KeptOff`: the guide and the suggestions leave these as they are and point to
Settings), without highest privileges, not read, or asked about at start (for these, the guide's switch and the
suggestion's Turn on say they replace the task). The box says under it why a task that is there does not start
PoolWatch, and goes back to how it was when its change is not made. In Active changes such a task's button is Remove.
Removing the task in Active changes or at uninstall needs no reading, so a task that cannot be read is removed then all
the same (`LogonTaskSource`).

**The setup** is made with Inno Setup 6.7.3 (downloaded in CI from a pinned address and checked against its SHA-256)
from the self-contained build, staged as it is installed: the launcher as `PoolWatch.exe`, PoolWatch itself (.NET
included) as `app\PoolWatch.exe` with its DLLs, the Codex shortcut's launcher script, `AGENTS.md`, `LICENSE`,
`THIRD-PARTY-NOTICES.md` and the runtime's own licence files. It installs to `%ProgramFiles%\PoolWatch` only, with the
permissions of Program Files, which `InstallGuard` expects: there is no folder page, and a setup started with `/DIR=`
elsewhere stops before copying anything. One x64 build; on Windows on Arm it installs too, and runs under Windows' x64
emulation. Every PoolWatch the setup and the uninstaller start, they start through the launcher, and the layout is
decided in one place each: `LauncherFile` (the install folder's `PoolWatch.exe`, or `PoolWatch-selfcontained.exe`
where only that one is there, by name, for the list of processes), `LauncherPath` (the one of the two that is the
launcher of the program below it, by `LaunchGate.Judge`'s rule: `app\PoolWatch.exe` is there and has the same product
version, which the script reads from each file's version resource itself, `ProductVersionOf`; a test holds that reader
to .NET's `FileVersionInfo` for real files), `SecondLauncherPath`, `ProgramPath` and `HasLauncher`. An upgrade first
runs `--stop-running` through that launcher (an install made before the launcher, with PoolWatch itself as its
`PoolWatch.exe` and no `app` folder, has none, and neither has a folder where an older program was left under the
launcher's name: the setup then sets the exit signal of a PoolWatch made before the launcher, `Global\PoolWatch-Exit`,
itself, and again each quarter of a second while it waits, as that version's own stop did, and starts no PoolWatch,
which started from the setup would get the setup's environment), which asks PoolWatch to exit through the exit signal and, from this
version on, then ends the token-leak test helpers still running from that exe (`Engine/LeftoverHelpers.cs`: only a
process whose exe is that very file, by file identity, and whose first argument is `--token-leak-test`, ended through
a handle opened while another keeps its process ID from being reused). Then the setup waits, up to 60 s, until no file
of PoolWatch's program in that folder is in use any more, for as long as a process in Windows' list runs the install
folder's PoolWatch or its launcher, and 3 s when none does (a virus scanner or the search indexer has a file open only
for a moment); a launcher counts as something else of PoolWatch running (it runs for as long as the PoolWatch it started) and
is never taken for a helper, under either of its names. For each process the list names `PoolWatch.exe` or
`PoolWatch-selfcontained.exe` (or one of their short 8.3 names), the
setup reads the exe's path from the kernel
(`QueryFullProcessImageNameW`, not the path WMI reads from the process's own memory). The kernel gives the real folder
for a process started through a junction or a `subst` drive that points into the install folder, but keeps a short
(8.3) or mixed spelling as the process was started, so the two paths are compared in their short form: the folder's
long, short and mixed spellings all count. A process with PoolWatch's name whose exe the setup could not read (it could
not be opened, or Windows gave no path) may run it; the setup names it as one Windows gave no exe for. When every
process that runs the exe is a token-leak test helper, and none could not be read, the setup ends them itself
(`EndHelper` in the script: the process is opened first, which keeps its ID from going to another process, then read
again from the list by that ID, and ended through that handle only if it is still a helper running that exe). So a
helper an earlier PoolWatch left, waiting for a click on its notice on any desktop, does not hold a silent upgrade
either. Whether a file can be replaced now is asked of the file itself (`CanReplace`: opened for reading and writing
with no sharing, which Windows refuses while a process runs it or has it loaded, or another program has it open), so
a process the list could not read, or a list that could not be read, decides nothing. The setup replaces the files now,
as Inno Setup's `[Files]` does, only when Windows has no work in that folder for its next restart (its rename list,
`PendingFileRenameOperations`, which Windows works through in order at the restart and which would undo files put in
place now) and no file of PoolWatch's program there is in use. The files decide this, not the list of processes: a
process that runs one of PoolWatch's programs has its file in use, and a helper the wait has just ended can still be
listed for a moment. A file another program has open only for a moment (a virus scanner, the search indexer) does not
decide by one look: the setup looks again for `BriefHold` seconds first (`WhenToReplace` while any file is in use, and
`FreeSoon` for each file it sets aside). Before `[Files]` writes any file (`ssInstall`), the setup renames every old
file aside, each once it is free, listing each folder whole before it renames any of its files, and looks at each again
under its new name (`SetAllAside`), so a file that comes into use part way through can only send the setup to the
restart before anything new is in place: every file is put back then. `[Files]` then writes each new file to a name
nothing holds, and each entry notes the file it is about to write (`Shipping`). The old files are deleted once every
new one is in place (`DropAside`: one that something opened meanwhile goes at the next restart; one whose place the new
version left empty is put back only when this version does not have that file, and stays set aside when it does, as
when Inno Setup was told to skip it), and all of them are put back if the setup does not get that far
(`DeinitializeSetup`). Inno Setup's rollback after a cancel or a failed step deletes every file the setup wrote whose
place was empty before, which every set-aside file's place was, and the Start menu entry too, which is put back from a
copy made at `ssInstall`; so are the uninstaller's program and messages, which Inno Setup replaces before the steps that
can still fail (`SaveUninstallerBefore`), and the Apps and Features entry, which Inno Setup deletes and writes anew after
the files and its rollback then deletes, or, when it fails part way through writing it, leaves part written
(`SaveEntryBefore`, `PutEntryBack`, which writes the old one back whenever it is not as it was, and deletes what was
written of one where there was none before). Every file this
version has that is still there after the rollback is a new one it could not delete (another program opened it, or
runs it), whether or not an old file was in its place, and is taken back first: renamed out of the way and deleted
(`TakeBack`). Windows renames or deletes no file that another program has open without `FILE_SHARE_DELETE`
(`MoveFileExW`, `DeleteFileW`), and deletes no program's file while a process runs it or has it loaded, so one that
cannot be renamed, or renamed but not deleted, is given to Windows to delete at the next restart, and then every old
file goes back only at that restart, after it. An old file another program opened under the name it was set aside under
goes back now all the same: renamed back, or copied back, or, when that program lets no one read it, given its name
back as a second name of the same file (`CreateHardLinkW`: Windows checks the share modes of a file's open handles only
for an open that reads, writes, runs or deletes it, and a test holds that the link is made while another handle shares
the file with no one), and the name it was set aside under is deleted at the restart. Only one that cannot go back
even so goes back at the restart, with every one after it (`PutAllBack`). Either way the files that start PoolWatch,
the launcher's two names, go back last, so until that restart the install folder never holds an old file beside a new
one: it holds only the new files that could not be taken back (when those are this version's launcher and its program,
those two start this version, without its other files, until the restart), or old files and no launcher (or the
launcher under only one of its names), and PoolWatch itself started any other way hands its start to the launcher
(`LaunchGate`; only its switches for CI and development run it without one). A folder only new files were in goes now,
or at the restart once it is empty (`DropNewFolders`). Until then this version's uninstaller removes nothing
(`RunCleanup`), unless Windows refused some of that work (below); for an install made before the launcher, whose
uninstaller would not wait, the setup holds that uninstaller until then when it can, or else says not to uninstall
before the restart and what that uninstaller would do (`HoldOldUninstaller`: see Uninstall). The setup says what
starts PoolWatch until then, read from the install folder (`UntilRestart`), and names the files in use, each with what
has it open (`HeldFiles`), in a box when someone is at it (the first ten, `MaxShown`, and how many more) and in its log
(every one). When Windows will not take an old file to put back at the restart, or a new one to delete then
(`MoveFileExW` fails when it cannot write its list in the registry), that file stays as it is (`Refused`): an old one
under the name it was set aside under, a new one after the restart too, under the name `TakeBack` renamed it to, which
starts nothing, or, when another program kept it from being renamed, under its own name (`LeftNew`). In that last
case the launcher's two names are not given to Windows to move back at all and stay set aside, so they start nothing
from the old files that go back beside it, and when that new file is at one of the launcher's names
(`LauncherLeftNew`) no old file is given to Windows, so the new launcher starts nothing from old files either
(`PutAllBack`). The setup says what happens to any file of either kind, that PoolWatch may not start after the
restart until the setup has run again, and to run it again before uninstalling; it does not then say this version's
uninstaller removes nothing (of 1.0.0-beta.1's, held until the restart, it still does), as a new launcher and its
program left in place can start this version's cleanup. Until the restart PoolWatch
never runs from old and new files mixed, and the setup loses none of the files that were there. Otherwise the setup
finishes at the next restart: `[Files]` puts the new files in a folder of this run's own,
`at-restart\<time>` in the install folder, and at the end of the install the setup gives Windows each of them to move
over its old self at the next restart, then those folders to delete (Inno Setup's `RestartReplace`, `MoveFileEx` with
`MOVEFILE_DELAY_UNTIL_REBOOT`), so the whole version goes in place together, before any program can hold a file again.
That work comes after any earlier setup's or uninstall's in Windows' list. Windows deletes a folder at the restart only
while it is empty, so a folder the moves go into that an earlier uninstall gave Windows to delete keeps a file of the
setup's own (`at-restart.keep`, `KeepFolder`) until after the moves, and a Start menu entry such an uninstall gave
Windows to delete is copied and moved back after that delete (`KeepLink`). Inno Setup's own check for an unfinished
earlier install (`PreviousInstallCompleted`) runs before the script's `PrepareToInstall`, and asks for a restart, which
a silent setup not told `/NORESTART` carries out, when a `[Files]` destination is in Windows' list; so the script reads
that list at the first expansion of a destination (`ProgramDir`, `LookForRestartWork`), and a setup run while earlier
work is waiting puts every destination in a folder of its own, which is in no list. All this leaves Inno Setup's own
restart state alone, as does `RestartIfNeededByRun=no`, so the setup never restarts the PC or asks for a restart, also
when it is silent and not told `/NORESTART`. Its last page says the update finishes when Windows restarts and why, each
reason, what starts PoolWatch until then, read from the install folder so that it holds after an earlier setup or
uninstall too (`UntilRestart`: the version that was there, that version but for a launcher name that holds no file now
and goes back then, or but for files this run set aside, nothing, a new launcher and program another program keeps in
place, or, after an uninstall, nothing installed), and
lists every file in use, however many, in a list that scrolls, each with the programs that have it open,
their process IDs and accounts, as Restart Manager names them (`HoldersOf`, `FinishFiles`; a test holds its reading of
Restart Manager's list to what Windows writes). The setup always writes a log (`SetupLogging`, in the TEMP folder of
the account that ran it), which names them all too. "Start PoolWatch now" is not offered, and a silent setup ends with
exit code 3010 (Windows' `ERROR_SUCCESS_REBOOT_REQUIRED`); if Windows could not be given a file, the setup deletes the
new files, and undoes the move of one another program keeps it from deleting (renamed, so that Windows finds nothing
to move, or, when it cannot be renamed either, with what it replaces at the restart kept now, as a copy or under a
second name of the same file, and moved back after it: `Unstage`). What it replaces then is read from Windows' list
(`ReadRestartWork`, `FilledAtRestart`): the file there now, or the one an earlier entry moves there first (an old file
an earlier setup that did not finish left to go back then), so a place that earlier work fills is never given to
Windows to delete, and an old file there is kept. So the restart puts none in place but for one whose move could not
be undone (what it replaces could be neither copied nor given a second name, or Windows would not take the move back,
`MoveFileExW` failing when it cannot write its list in the registry: `Unkept`, which the last page names, saying the
restart puts it among the old files). The file such a move would replace, when no copy of it could be made, is renamed
aside now (`.<time>.out.old`) so that the move does not replace it, unless another program keeps that from being done
too; one in place now is counted (`TakenOut`) and named on the last page, and one at a launcher's name is left to
`KeepLauncherOut`. No file that starts PoolWatch is then left at the launcher's names after the restart
(`KeepLauncherOut`): each file Windows' list would move there at the restart, and then the file at each name now, is
renamed aside at once (a rename needs nothing from that list), and a name whose file, or a file that would move
there, another program keeps from being renamed is given to Windows to rename aside at the restart, after every move
to it (`SetAsideAtRestart`, asked before the file at that name is renamed aside, so that Windows is asked while that
file is still there); nothing is deleted. Only a name Windows will not take that for can hold a file that starts
PoolWatch after the restart: it is named, and the page then says PoolWatch may start from the mix after the restart;
otherwise that it does not start after the restart until the setup has run again. A later setup that replaces the
files deletes what was set aside: in the install folder with the other old files (`DropAside`), and in an earlier
setup's own folder with that folder (`WhenToReplace`). It says that Apps and Features shows the new version's number
until the setup has run again, and ends with 1603. Nothing in `PrepareToInstall` stops the setup but `/DIR`. Inno Setup writes its
uninstaller (`unins000.exe`, `.dat`, `.msg`) itself, in place, with no way to leave it for a restart, so when one of the
install folder's is in use, or the setup finishes at the next restart, it writes the uninstaller in a folder of this
run's own, `uninstall\<time>` (`UninstallFilesDir={code:UninstallDir}`), which Apps and Features then starts; the
uninstallers that are no longer the one in use go once the files are in place (`SweepUninstallers`: every other one in
the install folder itself, whatever number Inno Setup gave it, and every earlier folder of its own; one still in use
stays, and goes with a later setup or the uninstall). One of the uninstaller's files that another program opens after
the setup's look can make Inno Setup's own step fail; the setup then rolls back and puts everything back as above, the
Apps and Features entry included. What `--stop-running` answered is only logged and never taken for "stopped": the files decide, so an
install whose PoolWatch no longer runs can still be repaired. Started by hand, the wait is on a page of its own
whose bar moves a step four times a second, and each step lets the wizard draw. Once the files are in place (its
`[Code]` at `ssPostInstall`) it deletes what the layout before the launcher left (`OldLayoutFile`: the two native DLLs
in `amd64\` beside the old `PoolWatch.exe`, then that folder; a name the launcher had in builds made before this
layout), or, when the setup
finishes at the next restart, gives Windows them to delete after the moves (and not at all when Windows could not be
given the moves); one that a setup which could not put it back left set aside to go back at that restart is deleted now
under that name, so its move finds nothing (`DropOldLayoutAside`). A logon task made by an earlier version names the install folder's `PoolWatch.exe`, the launcher now,
and needs no step of the setup's. "Start PoolWatch now" starts the launcher with the setup's administrator rights, and
PoolWatch's own setup appears at that first start. The Start menu entry starts the launcher. In a release, the setup,
its uninstaller, the launcher, PoolWatch itself and the Codex shortcut's script are signed with the same certificate.
CI's installer test upgrades 1.0.0-beta.1's own setup, pinned by its SHA-256, while it runs.

**Uninstall.** The uninstaller's work runs at Inno Setup's `usAppMutexCheck` step: after its "Are you sure?" and before
any file goes, where an abort cancels cleanly. By default it puts back everything PoolWatch changed that it can (the
list's `uninstallLeaves` says beforehand which it cannot, and why). Both its PoolWatch steps start the launcher of the
install folder's program (`LauncherPath`), which passes on PoolWatch's exit code, and only when there is one (an update
from the layout before the launcher that finishes at the next restart, or a setup that did not finish and left old
files to go back at that restart, leaves the uninstaller no launcher to start until then, and so does one of the
launcher's names that goes back only at that restart, its old file set aside under a name Windows has to move back
then (`NameGoesBack`): it says to restart Windows first and removes nothing; otherwise, until the restart that puts an
update in place, the version that was there is in place, and its own launcher and program run the cleanup, or, when
Windows refused some of the restart work of a setup that did not finish, a new launcher and program another program
keeps in place can, which is why that setup then says to run it again before uninstalling). The uninstaller of
1.0.0-beta.1, made before the launcher, would not wait: its cleanup starts the install folder's `PoolWatch.exe` and it
goes on without that when it does not run. So a setup that did not finish over such an install and left old files to
go back at the restart sets that uninstaller's record (`unins000.dat`, still that version's, as Inno Setup saves its
record last) aside until the restart, when Windows puts it back; until then that uninstaller says its record does not
exist and removes nothing. When another program keeps the record from being set aside, the setup sets that
uninstaller's messages file (`unins000.msg`) aside instead: 1.0.0-beta.1's released setup made a signed uninstaller,
which reads its messages from that file before it looks for its record (Inno Setup 6.7.3: `RunUninstaller`,
`LoadSetupMessages`; Inno Setup writes that file for a signed uninstaller, or when Setup is started with
`/DETACHEDMSG`, and otherwise binds the messages to the program), so without it that uninstaller stops and removes
nothing too. When another program keeps the messages file from being set aside too, the setup sets that uninstaller's
program, `unins000.exe`, aside instead (`HoldOldUninstaller`), so that the Apps and Features entry starts nothing until
the restart, and says to answer No if Windows offers to remove PoolWatch from its list of apps before then. Only when
another program keeps all three from being renamed, or Windows will not take them for the restart, can the setup not
hold that uninstaller: it can run before the restart (once that program lets them go, or at once when it was Windows
that refused), removing the Apps and Features entry and its own files and keeping PoolWatch's changes and the logon
task, and the files Windows puts back at the restart then have no entry. The setup then says, in its box and its log,
not to uninstall before the restart, and what that uninstaller would do, whether or not Windows refused other work for
the restart (SECURITY.md, Known limitations). It first asks PoolWatch for its list
(`--changes-dump` into the data folder as `uninstall-list.json`, read only when PoolWatch wrote it, exit
0, and deleted once read; otherwise it touches nothing in the data folder). PoolWatch may still run then, so that step
uses the data folder as it stands and moves none: only a PoolWatch that holds the one-instance mutex moves a data
folder, makes a folder of its own or changes its record. Started by hand, it shows what it will put
back, what it cannot (with why) and, when the token-leak guard found its Windows setting already changed and did not
change it itself, that the setting stays until sign-out, and asks: Put them back (recommended, the
default) or Keep my changes; a question that cannot be shown means putting back. A silent uninstall and `/DELETEDATA`
put back without asking; `/KEEPCHANGES` keeps the changes. Names in its boxes are shortened to fit; the log keeps them
whole. Then it runs `--uninstall-cleanup [--keep-changes]`,
hidden. That stops PoolWatch through the exit signal and holds its one-instance mutex (so nothing starts meanwhile);
only then does it check the data folder, as a start of the tray app does, moves included. It holds the mutex while it
ends the token-leak test helpers still running from its exe (as the setup's stop does), removes the sign-in task and
the shortcut and, unless asked to keep the changes, runs the ledger's Put all back (section 13). It prints and
writes `uninstall-summary.txt`, each change "Put back", "Partly put back" (with what was not done), "Could not put back"
(with why) or "Kept". A change put back with more to say (`UndoResult.Caveat`: a service whose start type is back but
that was not started again, and why: Windows did not start it, or PoolWatch does not know whether it was running; or
one put back to Disabled that is still running) is a "Partly put back:" line, never a "Put back:" one. Exit 0 means
done, 8 that something was not put back (it stays listed in the data folder), and 12 either of those on a folder of
PoolWatch's own, which it uses because it could not use the usual data folder (the uninstaller then says so, with "put
back what it could" or, after Keep my changes, "your changes were kept", and reads and deletes nothing in the usual
folder; that folder of its own and its record in `HKLM\SOFTWARE\PoolWatch` stay, so a later install finds both; a later
PoolWatch moves that folder back to the usual name when it can, or deletes that record once that folder is gone, after
you deleted it, say, or is no longer admin-only); only these three let the uninstaller go on. Any
other exit is a failure (TECHNICAL.md's "Exit codes" table has them all): 1, an error stopped the cleanup, after which
some changes may have been put back and the uninstaller cannot tell which; 2, PoolWatch did not take the cleanup's
command line, so it put nothing back; 5, someone who is not an administrator could change PoolWatch's program, its
launcher or a folder they are in, so it did not run the cleanup and put nothing back; 6, its data folder could not be
made safe, so it did nothing; 7, PoolWatch is still running, so it put nothing back; 9, PoolWatch could not open its
private names, so it could not ask; 10, the launcher could not start PoolWatch, and 11, PoolWatch refused the start, so
it put nothing back; -1, PoolWatch could not be started; -2, the install folder has no launcher of its program's
version; or any code the uninstaller does not know, after which, as after 1, some changes may have been put back and the
uninstaller cannot tell which. No file is removed after a failure: started by hand, the uninstaller says which, with
what to do for that reason, and offers Retry (which runs the cleanup again) or Cancel; silent, the uninstall ends with
nothing removed. After 0, 8 or 12 it waits, as the setup does (ending a leftover helper the same way), until no file of
PoolWatch's program there is in use, for as long as a process with PoolWatch's name runs from it: a silent uninstall up
to 60 s, one started by hand 3 s, as it has no page that keeps drawing during a wait. If part of PoolWatch still runs
then, or may (a file of it is still in use and its processes could not be listed), it runs the cleanup once more with
`--append-summary`, which adds to `uninstall-summary.txt` instead of replacing it (a run after a
Retry adds too, once a run of this uninstall wrote the file; a summary a run could not write there is kept beside it, in
`uninstall-summary-kept.txt`, that run ends with exit code 1, and the next run writes it in first, so a Retry that
replaces the file keeps what the failed run put back), to stop a PoolWatch started since the first run (whose
service profile can apply at its start) and undo what that one changed (with `--keep-changes`, only by removing the
logon task and the desktop shortcut, as in the first run); then it waits 3 s. What still has a file in use after that
does not stop it. Once Inno Setup has removed what its log names, the uninstaller deletes what is left of the install
folder (only `%ProgramFiles%\PoolWatch`, never through a link; files a setup that finished at a restart put there are
not in that log), and gives Windows what is still in use, each folder once it is empty, the folder itself and a Start
menu entry still there, to delete at the next restart (`RestartReplace` again; no restart is asked for). A file an
earlier setup set aside for that restart and still in use would be moved into the folder then: the place it goes is made
now, so that Windows deletes it after the move. Started by hand, it says what Windows deletes at the restart: the first
ten files, each with the programs that have it open, their process IDs and accounts, and how many more, as its notice
has to fit on the screen; its log names each. It writes one only when it is started with `/LOG`, as Apps and Features
starts it (`UninstallLogging`), in the TEMP folder of the account that ran it, and a notice that names only the first
ten then says where it is; started by hand without `/LOG` it writes none, and the notice mentions no log. Then it says
what it put back, also after a `/DELETEDATA` uninstall: first what it put back only in part ("PoolWatch put back these
changes only in part:"), each with what was not done (each line cut to 360 characters); then the rest ("PoolWatch put
back each of these changes:", each cut to 180); each line once although two runs may list it; then the sign-out caveat.
Each list names its first 10 and counts the rest under its own heading, so a change put back only in part is never shown
or counted as plainly put back. The notice when something could not be put back lists the changes put back only in part
too, and says that `uninstall-summary.txt` says what was not put back and why. When Inno Setup's own removal of what its
log names also left a file, its own last words, at the very end, say again that what was still in use goes at the
restart. A silent uninstall ends with exit code 0 either way: Inno Setup's uninstaller gives its script no way to set
another (it has no counterpart of the setup's `GetCustomSetupExitCode`); its log, when it writes one, says what Windows
deletes at the restart. The uninstaller cannot start at all while another program holds its record open (`unins000.dat`,
which Inno Setup opens for itself alone before any of the script runs, and before it asks for administrator rights) or
its program without letting it be read (Windows opens a program's file to start it). Both are in the install folder,
which every account may read: Apps and Features starts the uninstaller with the standard rights of the administrator who
uninstalls, in which the Administrators group grants nothing, so those rights must be able to read both, and an account
without administrator rights has the same groups (SECURITY.md, Known limitations, says why files only administrators can
read would not help). A setup run then writes a new uninstaller in a folder of its own, and that one uninstalls.
Deleting the data folder is offered only when every change was put back (default No; silent: only with `/DELETEDATA`),
so a later PoolWatch can still put back what is recorded there. When that question counts some of a list rather than
naming them (`SummaryNamesMore`: more than 10 put back, or put back only in part), it says that `uninstall-summary.txt`,
in the folder it asks about, names them all (adding "with what was not done" only when a change was put back only in
part), and that No keeps it. A `/DELETEDATA` uninstall deletes that folder, the summary with it, as it was asked to, so,
started by hand, it names every change put back only in part itself: its notice names the first 10 of each list, counts
the rest and, when it counts some put back only in part, says the others are named next; then `ShowRestPutBackInPart`
shows those, 10 to a box ("PoolWatch put back these changes only in part (11 to 20 of 23):"), each with what was not
done. The changes put back plainly past the first 10 are counted only. Its notice says the folder is to be deleted, as
it comes before the delete. After the delete, `SayDataLeft` looks whether anything of the folder is left (a file another
program still has open, or the folder itself, which a PoolWatch process still running holds open) and, if so, logs each
file left with what has it open and says it in a box when someone is there (the first 10, and how many more), with what
to do: delete it once no program has it in use. Nothing is read through a link. What is left is not given to Windows to
delete at the next restart, as the install folder's rest is: a PoolWatch installed again before then keeps its data in
the same folder, under the same names.

**The exit signal** (`Engine/ExitSignal.cs`) is an auto-reset named event with an admin-only DACL (Administrators and
SYSTEM), created by the tray app and by a headless run. Listening is one registered wait on the thread pool's shared
wait thread; nothing runs until it is set. Setting it is the normal clean exit: open incidents are closed and written,
and the token-leak guard's failsafe runs. `--stop-running` and the cleanup set it every 250 ms and wait up to 60 s for
the one-instance mutex (which an exiting PoolWatch leaves abandoned; the cleanup takes it either way). An event that
already exists, which PoolWatch did not create, is not used.

**The show signal** (`Engine/ShowSignal.cs`) is the same kind of event, with the same admin-only DACL, one for each
Windows session (`PoolWatch\Show-<session>`, `AdminNamespace.ShowEventName`), created by the tray app (not in the CI UI
test) and listened on with a wait that runs each time it is set. A tray app started while the one-instance mutex is held
lets the running one take the foreground (`AllowSetForegroundWindow`) and sets its own session's event, and the running
one shows its main window and brings it to the front; setting it does nothing else. With no event of its session to set (a headless run holds the mutex, or the tray app runs in another
session) it says "already running" in a box as before, and so does one started with `--tray`, which opens no window.

**PoolWatch's private names** (`Engine/AdminNamespace.cs`). The event and the one-instance mutex are named
`PoolWatch\Exit` and `PoolWatch\Monitor` (and the show signal `PoolWatch\Show-<session>`) in a private object
namespace, not in the global or the session namespace. The
namespace's boundary holds the Administrators SID and the high integrity level, so Windows lets only an elevated
administrator or SYSTEM create it (a creator whose
token lacks a boundary SID, or whose integrity level is below the boundary's, is refused); its DACL lets only
Administrators and SYSTEM open it or create anything in it. Its owner is the Administrators group, never the account
that created it (an owner may read and change the DACL whatever it says, and Windows' "object creator" owner policy
would make an administrator's own account the owner), and it carries the high mandatory label, so no process below high
integrity can read, change or use it; only an elevated administrator or SYSTEM may set either. Each process opens it
once (create, or open when another
PoolWatch created it; one that went between the two calls is created again, five tries) and keeps it until it exits;
PoolWatch never destroys it. A tray app or headless run that cannot open it, or cannot take the mutex, does not start:
it says why in a box (the tray app), on the console, and in its log when it can open that (the unelevated run the
namespace refuses usually cannot open the admin-only data folder), and exits 9, never "already running" (3 is only
for a mutex another PoolWatch holds). The setup and the uninstaller do not open the namespace from their script (Inno
Setup's code could call the same Windows functions; the design does not need them): they go by Windows' list of
processes, which a program without administrator rights cannot make say that PoolWatch has stopped while it runs, and
by the cleanup's exit code.

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
- **PoolWatch's program started as administrator by an administrator's own hand, past the launcher** (`app\PoolWatch.exe`
  from their own elevated console, or **Run as administrator** on that file; nothing PoolWatch makes starts it so) has
  read that program's environment before `Main` runs, as every .NET program does: the runtime reads
  `CORECLR_ENABLE_PROFILING` and the profiler (`CORECLR_PROFILER`, `CORECLR_PROFILER_PATH`) from the environment at
  start-up, with no runtimeconfig.json setting to turn them off, and a garbage collector to load (`DOTNET_GCName`,
  `DOTNET_GCPath`) from the environment before its runtimeconfig. It starts again through the launcher and does no work
  itself, and start-up hooks are off, but a profiler or a garbage collector such a variable names was loaded into it
  (SECURITY.md, Known limitations).
- **A service profile applied at start asks nothing at start.** Its consent is the box ticked when it was saved. The
  uninstaller does not change profiles, so a data folder kept through an uninstall applies the profile again under a
  later PoolWatch. `--service-restore` never takes a service out of it (section 13).
- **The growth and clear stats** (section 11) count a clear whose purge fails only after its 2-minute measurement as
  cleared.
- **Known issues only explain.** A pack gives a reason to a suggestion the rules already make; an issue with no
  built-in action (the NTFS records one) shows only as a note in the Advanced view.
- **Windows on Arm and Windows 10 22H2 are not tested on real hardware.** On Arm the x64 build runs under Windows'
  emulation, which only CI's Arm jobs (section 12) are set up to check; Windows 10 22H2 has no check at all.
