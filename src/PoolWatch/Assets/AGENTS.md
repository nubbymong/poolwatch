# PoolWatch: a guide for AI assistants

This file is for an AI assistant (Claude, ChatGPT, Copilot or another) that someone has asked for help with
PoolWatch's data. It is written in plain words, so the person can read it too.

## 1. What this folder is

PoolWatch is a free, open-source Windows app (https://github.com/nubbymong/poolwatch, MIT licence). It watches the
Windows kernel's memory pools (the paged and non-paged pool; the app calls them "the kernel pool"), finds which program
or driver makes them grow, and can clear the standby cache. It sends nothing about the PC anywhere: the person shares
only what they choose. Its one connection of its own is an optional check for a new version, about once a day: an HTTPS
request to api.github.com for PoolWatch's latest release, which carries PoolWatch's version and nothing about the PC.
GitHub sees the IP address it comes from, as for any web page. Nothing is downloaded or installed, and the person can
turn the check off in Settings or in Set up PoolWatch.

This folder is one of these:

- PoolWatch's data folder, `C:\ProgramData\PoolWatch`. Only administrators can change it; the signed-in user can read it.
- An export of one incident (`<incident id>-redacted.zip`, from **Export report**) or a vendor bundle
  (`vendor-bundle-<date>-redacted.zip`, from **Export vendor bundle**). Both are redacted unless the person chose not to.
- The install folder, `C:\Program Files\PoolWatch`: the program itself, no data.

Rules for you, the assistant:

1. **Read only.** Do not change, move or delete anything here.
2. **Do not change the PC.** Do not run PoolWatch options that change it (`--clear-cache`, `--enable-auto-clear`,
   `--remove-legacy-clear-task`, `--service-off`, `--service-restore`, `--uninstall-cleanup`), or any other command that
   does, unless the person asks for that exact change. Options that only read (`--services-dump`, `--tasks-dump`,
   `--changes-dump`) are fine; they still ask for administrator rights, and write the file you name.
3. **Never post or send anything yourself.** Draft it; the person reads it and posts it.
4. **Redact first** (section 6). The files in the data folder name the person, their PC and their projects.

## 2. File map

Times inside CSV and JSON files are UTC (`2026-10-28T09:12:00.000Z`); `poolwatch.log` and the dates and times in
folder names and incident ids are local time, so match an id to CSV rows through the time zone. The clock times
`report.md` gives are the PC's local time too, and so are those inside these texts of `report.json`: `headline`,
`plainAnswer`, `traceAnswer`, `retention`'s `note`, the `timeline` texts and the suspects' `evidence` lines. They are
in the time zone `report.md`'s stamps show ("opened 2026-10-28 10:12:00 +01:00"), so on that PC a `timeUtc` of
`2026-10-28T09:12:00Z` stands beside a `note` that says "at 10:12:00". Sizes are in bytes
unless a name says otherwise. A pool tag is four characters and **case-sensitive**: `Reff` and `RefF` are different
tags. Tag owners and meanings are in each incident report, or in Microsoft's `pooltag.txt`.

What the data folder holds depends on what the person chose PoolWatch keeps on disk (setup's Records step, or
**Records** in Settings; `recordMode` in `settings.json`): with **Reports only**, the default, a report folder each time
the pool grows and no day's records (no `data\<yyyyMMdd>\` folder, no `findings.jsonl`, no `SpawnStorm` folder); with
**Detailed recording**, those as well. Either way the rest below is kept (the log, the settings, `boots\`, `cache\`,
the other report folders). A folder written before a change of mode stays until it is cleaned up. In the data folder:

| Path | What it holds |
|---|---|
| `AGENTS.md` | this guide |
| `settings.json` | PoolWatch's settings, and the service profiles: the services each keeps off, and the one applied when PoolWatch starts |
| `poolwatch.log` | what PoolWatch did, one line each (moved to `poolwatch.log.1` at 2 MB by the tray app, a headless run or the uninstall cleanup); a run during which another program kept refusing its appends goes on in `poolwatch-<date>-<time>-<process id>-<n>.log`, whose first line says why; it cannot be opened while that run writes it, except in the moment it rolls over at 2 MB, or tries to (its own `.1`, made then, can), and the newest five are kept, and any of another PoolWatch still running |
| `since-boot.md`, `since-boot.json`, `boots.md` | written when PoolWatch exits: what grew since boot, and the last boots side by side |
| `data\<yyyyMMdd>\` | with Detailed recording only: one folder per day, the CSV files below and `findings.jsonl`. Deleted once nothing in it was written for 7 days (by default; the same cleanup as the incidents and exports) of time the PC was awake, so a PC that sleeps keeps more calendar days |
| `incidents\<id>\` | one folder per incident (section 3). `<id>` is `<yyyyMMdd-HHmmss>-<tag>-<tag in hex>` |
| `incidents\<yyyyMMdd-HHmmss>-<kind>-<subject>\` | a report on one alert finding (such as `Experiment`, `Crash`, `SpawnStorm`): its `report.json` is that finding (time, kind, title, details, data), not an incident. A `SpawnStorm` folder holds every storm of one program (its image, role and owner, such as a Claude Code session's bash) that came within an hour of the one before: `data.episodes` lists them, `data` is otherwise the latest storm's, and `spawns.csv` has the folder's first 1,000 starts. Fork copies (a child with its parent's own image and command line, as Git Bash runs a pipeline) are not counted as starts. At most 10 such folders are made an hour; a storm past that is only in `findings.jsonl`. `SpawnStorm` folders are made with Detailed recording only: with Reports only a storm is in the window and the log, not on disk |
| `cache\clears.json` | the last 100 finished cache clears: when, how and why, the pool (and its paged part) and the standby list before and after, and any error; for a measured clear, the tags it freed (up to 12, each at least 1 MB, with the bytes before and after). Every tag that dropped is in the `CacheClear` findings |
| `cache\token-guard.json` | the last token-leak test: the Windows build, the result and when it ran |
| `cache\token-guard-setting.json` | the Windows setting the token-leak guard replaced (the live foreground lock timeout from before it set 0), one value per sign-in with the sign-in it belongs to, so turning the guard off or uninstalling in that sign-in puts it back when Windows allows it (one the guard found already changed is not recorded, and stays until you sign out) |
| `cache\tagscan.json` | which driver each pool tag belongs to, from PoolWatch's scan of the drivers |
| `cache\environment.json` | the last look at the PC: Windows version and build, memory, drives and their file systems, the AI tools found and what is set up, and which of the drives, installed apps and running services it could not read. No user names, and no paths except drive roots |
| `cache\profile.json` | PoolWatch's own cost, written every hour: its processor time, memory and heap, what it holds in memory, what it wrote to each file, and `writeBudget`: what the report folders' disk budget let them write (`canWaitMB` beside `canWaitAllowanceMB`, what it allows), how many times it held a write back for a later sample (`heldBack`, deferrals rather than distinct writes), and the open incidents whose report (`incidentsBehindNow`, `writesFailedNow`) or chart and CSV files (`chartsBehindNow`) are behind now |
| `cache\update-check.json` | the last check for a new version: when it ran, its result, and the newest release seen |
| `service-changes.json` | every background service PoolWatch changed, with its original start type (Put back needs it, on the Services tab and in Active changes) |
| `applied-changes.json` | the other changes PoolWatch made that can be put back (Active changes needs it) |
| `removed-tasks\<name>.xml` | only when a clear task of the owner's that PoolWatch removed could not be put back by itself: its definition, to import in Task Scheduler |
| `setup.json` | when setup was completed, when the owner chose what PoolWatch keeps on disk (`recordsChosenUtc`; absent after an upgrade from 1.0.0-beta.2 until that choice is made), the suggestions the owner hid (each shows again after 30 days), and the feature cards of the Simple view the owner hid (for good) |
| `ui-state.json` | which view the main window opens in: Simple or Advanced |
| `uninstall-summary.txt` | written by the uninstaller: each change it put back (or put back only in part, and what was not done), and any it could not put back (and why) or kept |
| `uninstall-list.json` | only while the uninstaller runs: the list of changes it asked PoolWatch for, deleted again once read |
| `boots\` | one folder per boot: `baseline.json` (the first full tag snapshot) and `summary.json` (the peaks) |
| `reports\` | the Since boot reports |
| `exports\` | export zips, vendor bundles, and Report an issue's saved text (`issue-<yyyyMMdd-HHmmss>.txt`) |
| `requests\` | notes waiting for the running PoolWatch to put on its timeline (`--mark`) |

Not in the data folder, but wherever the person saved or picked them, when they used **Decide with your AI agent** on
the Services tab: `poolwatch-services.md`, the prompt PoolWatch made for their AI agent (the services, scheduled tasks
and startup apps on the PC, with its own instructions), and `poolwatch-services-answer.json`, the agent's answer,
which PoolWatch reads only when the person imports it, and then shows every change before making any.

The daily CSV files (Detailed recording) have a header row. `totals.csv` has a row every 10 seconds; the others as
noted. When Detailed recording is turned on while PoolWatch runs, the day's files start again at the next sample with
the whole picture, as at a start; when it is turned off they stop at once. A day's rows of
one file can go on in a numbered file next to it (`totals-2.csv`, then `totals-3.csv`, and so on): when an update
changed its columns, or when another program refused its open or write. Read them together and order the rows by
their time: when PoolWatch starts again the same day, it can write to the first file again.
Every CSV file PoolWatch writes, here and in incident folders and exports, protects spreadsheets the same way: a text
value that starts with `=`, `+`, `-`, `@` (or their full-width forms), a tab or a line break is written with one leading
apostrophe (`'=...`), so a spreadsheet shows it rather than running it as a formula. Drop that one apostrophe to get
the value. A spreadsheet that splits lines at semicolons can start a cell after a semicolon, and a row after a line
break, inside a value whose quotes are not at the start of a cell as it splits the line, so a part of a text value
after a semicolon or a line break that starts the same way, or with a quote, gets an apostrophe too (`a;'=b`), and that
one stays in the value. Numbers never have it (a pid of -1 is written `-1`).

To stay inside PoolWatch's disk budget on a busy PC, several files hold what changed rather than a full copy each
time: each day's files (and each start of PoolWatch) begin with the whole picture, and after that rows come as the
table says. When you read one of them as a series, carry a value forward only as its file allows:

- `tags.csv`: a tag's last row holds, to within 1 MB, until its next row, which comes at least every 10 minutes while
  it is over 1 MB. A last row under 1 MB means it fell under 1 MB then and stayed under it.
- `services.csv`: a service's last row holds until a newer one; pid 0 means it stopped or left the list.
- `handles.csv`: a process's last row of a type holds until a newer one, which comes when the count changes, or once
  as the count leaves the selection (the 20 processes with the most handles, or 500 of the type) with its count then
  (0: it holds none of that type). A scan records at most 2,000 process counts, the largest first: a scan with more
  has a pid -2 row whose count says how many were left out; those are not recorded (each had its last row as it fell
  out) until a scan without a pid -2 row records them again. A count ends at the process's exit (in `events.csv`). The
  pid -1 totals come every scan: do not carry them.
- `processes.csv`: carry only what does not change: pid, create_time, image and parent_pid. The counters are as they
  were at the row's time: a process with no newer row was not among the 50 busiest by I/O and its handles moved by
  less than 100 in each minute, but they and its memory may have moved since. Never carry `d_read_ops`, `d_write_ops`
  or `d_other_ops` forward: each is the change over the minute before its row. A last row does not mean the process
  still runs: its exit, and when it became an orphan, are in `events.csv`.
- `totals.csv`, `system-handles.csv`, `lag.csv` and `load.csv` are a reading at each row: nothing to carry.
- `events.csv` holds events, not states: an `Exit` or `Orphaned` row's empty `command_line` is the one its `Start`
  row gave (below).

| File | Columns |
|---|---|
| `totals.csv` | time, committed_paged_bytes, committed_nonpaged_bytes (the pool as Task Manager shows it), tagsum_paged_bytes, tagsum_nonpaged_bytes (the sum over the tags, which runs lower: compare like with like), tag_count, process_count, commit_bytes, commit_limit_bytes, available_bytes, compression_ws_bytes, system_handles |
| `tags.csv` | time, tag, tag_hex, paged_used, nonpaged_used, paged_allocs, paged_frees, nonpaged_allocs, nonpaged_frees. Checked once a minute, tags over 1 MB: a tag has a row when its paged or non-paged use moved 1 MB since its last row, at least every 10 minutes, and once more as it falls under 1 MB |
| `processes.csv` | time, pid, create_time, image, parent_pid, handles, threads, quota_paged, quota_nonpaged, private_bytes, read_ops, write_ops, other_ops, read_bytes, write_bytes, d_read_ops, d_write_ops, d_other_ops (the change over the minute before the row), orphan (1: its parent is gone). Checked once a minute: every process at the first check of the day, then each process once when it has run a minute (a shorter one is in `events.csv`), and again whenever it is among the 50 busiest by I/O (any at all) or its handles moved by 100 |
| `handles.csv` | time, pid, image, type, count: handles by object type every 5 minutes. Pid -1 is the total of each type, every scan; a process's count of a type (the 20 with the most handles, those of a type it holds 500 of) when it changed since its last row, and once more, with its count then, as it leaves that selection while it runs; pid -2: how many counts a scan left out past its 2,000 |
| `system-handles.csv` | time, type, count: the System process's handles by type, every scan |
| `events.csv` | time, kind, pid, create_time, parent_pid, image, exit_code, user, command_line, detail: process starts and exits (of processes that ran 30 seconds or more), every finding, and the short-lived ones as `Rollup` rows, one per program, parent and kind of child per 5 minutes (written up to about 6 minutes after their first start; the time range and counts are in `detail`). An `Exit` or `Orphaned` row leaves `command_line` empty when the process's `Start` row earlier in the same day's file has the same one: match them by pid and create_time (it has no `user`: that is on the `Start` row) |
| `services.csv` | time, pid, service, display_name: which process hosts which service. The whole list (those running) at the first check of the day, then a row for each service whose host changed; pid 0 means it stopped |
| `volumes.csv` | time, root, file_system, label, dev_drive, drive_type: at start and every hour |
| `lag.csv` | time, timer_ms, message_ms, file_ms, dwm_missed, committed_paged_bytes, committed_nonpaged_bytes: how late small probes ran (how slow the PC felt) |
| `load.csv` | time, cpu_fraction, run_queue, cores, page_faults_per_sec, demand_zero_per_sec, transition_per_sec, pages_input_per_sec, probe_tripping |

`findings.jsonl` (Detailed recording) holds one JSON object per line: time, kind, severity (Info, Warning or Alert), title, details, pid,
image, data and reportDir. The kinds that matter most: `CacheClear` (a clear and what it freed, by tag), `TokenSpawn`
and `TokenGuard` (the token leak), `SpawnStorm` and `ChildAccumulation` (a process starting hundreds of others),
`Saturation` (every core busy), `ServiceChange`, `Crash`, `VersionChange`, `Lag`, `GitRoot` and `StrayGit` (a `.git`
folder where it makes tools crawl), `FilterEvent` and `FilterInstances` (file-system filter drivers), `Experiment`,
`Mark` (a note the person added) and `Setup` (PoolWatch's own data folder and tasks).

An incident export holds the incident folder's files (section 3) and this guide. When it is redacted, the `.etl` trace
is left out (`capture.etl.omitted.txt` says so).

A vendor bundle holds `README.md` (what each file is), `system.json` (Windows build, PoolWatch version, memory, app
versions), `samples.csv` (one row a minute: commit, paged, non-paged, handles, processes), `boots.md`,
`boot-baseline.json`, `tags\*.csv` (tag snapshots: after reboot, `clean-boot.csv`, now, during growth, before and
after a watched app ran), `incidents.md`, `experiment\` (when one ran), `filters.json`, `git-and-spawns.md`,
`spawns.csv`, `crashes.md`, `findings.jsonl` and this guide.

## 3. Reading `report.json`

Each incident folder has `report.md` (for people), `report.json` (the same, for you) and `events.csv`; from the
first write of its chart files on (an incident opens before that), `timeline.csv`, `processes.csv` and
`timeline.png` (no chart is drawn of fewer than two samples); and, when a trace was taken, `capture.etl`. The draft
bug report at the end of `report.md` lists which of them the folder held when it was written. (A folder whose name has a
finding's kind instead of a tag is a finding's report, described in the file map.) An incident's `events.csv` holds
both ends of the window its report gives, 500 events each (on a busy PC about 3 minutes of events at each end). Its
first part is the first 500 events by time from the window's start: 2 minutes before `onsetUtc`. PoolWatch measures
the onset again as the growth goes on (later as a long, steady growth's rise grows, sometimes earlier), and the first
part follows the window's start: it is taken again from where the window starts then, a closed incident's at once
and an open one's at most every 10 minutes, so the file shows the window the report gives. (Left where the incident
opened, the first part would be mostly from before the growth: after an hour on a busy PC, 416 of its 500 rows on
average.) For a `Capture` incident the start is not measured:
its trace is its window. Its start is the moment the incident was made until its trace finishes, and from then on
exactly the trace's own start, a little later (a trace that could not record leaves it where it was). PoolWatch never
moves it otherwise, so that start is `onsetUtc`, the window `report.md` lists starts exactly there, with no 2 minutes
before it, and `startBytes` is the tag's level at the first sample from it on (the last one before it, when the trace
ended before the next sample). Its window ends exactly where its trace
ended (`closedUtc`, the trace's own `endUtc` under `capture`; the moment the incident was made until the trace
finishes, and for a trace that could not record), with no minute added and whatever its tag did: its `events.csv`
and the events `report.md` lists are the trace's own, from its start to its end, however long the trace was.
On a very busy PC PoolWatch may no longer hold every event from the window's start (by default it holds the events
of the last 90 minutes, and never more than 20,000): the first
part is then the first events it still holds from there, up to 500. After a start measured later those can be the
first events the incident had kept itself from the new start on: they are the window's first, often fewer than 500, and
the file says how many. Where PoolWatch had let go of events from the start on, the `Note` row, the `Gap` row and
`report.md` say so, with
the time of the latest event it had let go by then ("the first 500 events PoolWatch still held from ... (by then it
had already let go of events up to ...)"): the events it had let go are not
in the file, and are not counted anywhere. While the incident is open the file holds that first
part, every event from its first row to its last, in the order PoolWatch added them (sorted by time within each
addition), and on a busy PC fewer, as PoolWatch writes within a disk budget; once it has all of it, its last row
(`kind` `Note`) says how many events it holds and from when, and its time is when they stop: no event row of the
file is later. When the first part
follows the window's start, a file without its `Note` row starts again from the new start, and one with it stays as
written until the incident closes; `report.md` then says where the file's rows are from and where the window starts
now. (A file without its `Note` row is also written again when a late event takes a place among rows it holds: it
never holds more than its first part's 500.) Once the incident has
closed it holds the window's first part as it is measured at the close, then its latest 500 events up to the window's
end, all sorted by time, each event
once; when events between the two were left out, a row of `kind` `Gap` sits between the two parts, at the time of the
first one left out, and its `detail` says how many (every event between the two parts), the time of the first and the
last of them, and what the file keeps: how many events from when ("the first 500 events from ..., where the
incident's window started when this file was written"), and how many up to the window's end. A file with no event
row before its `Gap` row holds only its latest events, and the row says so. An incident with under 1,000 events
from its first row to its window's end has every one and no
such row; with no more than 500 it has no latest part, and its close leaves its file with every
event of the window, in time order. An event of a closed incident's window that comes after its file was last written
(a short command's
`Rollup` is handed over up to a minute and a half after its first start, so those of a trace's last seconds can) is
listed in `report.md` at the next sample or soon after; a file that holds the first part only gets its row then too,
at its end, after rows of later times.
Two minutes after the window's end, when every late event of the window can have come, PoolWatch writes the folder
once more if `events.csv` does not hold the window's events as `report.md` lists them, each once and in time order
(a file that holds both ends
is replaced whole then, its `report.md` with it; one that holds the first part only is written in time order), and
again only when still more of them come later; until then that
`report.md` counts the file's rows as they are. The `Gap` row counts each event by its own time, so no event from
after the window's end is counted as left out: when the window's end is measured again after events were counted,
PoolWatch takes the latest part and the count again from the events it holds (those it no longer holds stay counted
as they were). Only on a PC so busy that it no
longer holds the events from after the new end (over 20,000 events since it) does such a count hold events from
after the end; the
row and `report.md` then say so, with where the window ends ("the count runs past the window's end, ... when this
file was written: PoolWatch no longer held those events, to count each by its own time against that end").
`report.md` says the same in words in the line under its list of the window's events, which are those of the
window as last measured; for an open incident whose file has its `Note` row, the line also says how many more events
have come since: every one PoolWatch took in from the file's start on that is not one of its rows, those after the
window's end as last measured among them. A last `Gap` row, after the file's other rows, also says how
many late events of a short incident's window PoolWatch's disk budget never had room for before the incident left
PoolWatch's memory.
When `reopened` in `report.json` is above 0 and the incident is open again, the file holds both ends as its last
close wrote them: the new episode's events come at its next close, which writes both ends of the whole window as it
is measured then. `Alert`, `Release`, `Note` and `Gap` rows are not events: count the
others. An `Alert` row marks when each growth rule first fired (the report has its latest numbers).
With Detailed recording the day's `data\<yyyyMMdd>\events.csv` has every event of the time it was on; with Reports
only there is no such file, and after a switch from one to the other during the day it holds only the time Detailed
recording was on. While an incident's rows wait for the disk budget, its `report.md` names that file only with Detailed
recording: it "has every one" when Detailed recording was on for the whole window, "has those of the time Detailed
recording was on" after a switch in the window, and no day file is named with Reports only. A folder an older
PoolWatch wrote can differ: 1.0.0-beta.2
and earlier kept up to the window's newest 5,000 events with no `Note` or `Gap` row, so such an `events.csv` may not
have every event of the window. On a busy
PC an open incident's `report.json` can be up to about an hour behind (PoolWatch's window shows it live); an export
of an open incident (Export report) writes its folder whole first, so the zip is current, and its `events.csv` in the
zip has both ends of the window, from memory, with a `report.md` that counts them. The fields of `report.json` that
matter:

- `id`, `kind` (`Growth`, `NonPagedTotal` for the whole non-paged pool over its limit, `Release` for a sharp drop,
  `Capture` for a trace taken on its own: one the person started with Capture now, or, where the person turned that
  on, a CPU trace of a moment the PC lagged; `traceFor` says which, `Manual` or `LagTrip`: it is empty for the other
  kinds, and a folder an older PoolWatch wrote has none), `tag` and `tagHex`, `owner` and `description` (the driver
  and what the tag holds);
- `openedUtc`, `onsetUtc` (when the growth began; a `Capture` incident's start, as above), `endUtc` (when it stopped;
  empty while still climbing, and when there was no growth to end),
  `growthBytes`, `startBytes`, `peakBytes`, `currentBytes`, and `clearedBytes` (what PoolWatch's own cache clears freed
  of the tag since the onset: the growth and the peak count it back, as a clear's drop is not the growth ending;
  `currentBytes` is the tag's real level) with `clearedAtUtc` (when, if one clear freed it; empty for none or
  several), and `currentAtUtc`, the sample `currentBytes` is of: the newest one while the incident is open; for a
  closed incident the one of its close (or of a later check that found the growth still held), however much later
  the report was written, so it is not the tag's level now (`report.md` gives it as "244 MB at 07:05:53", and says
  "now" only of an open incident; a folder an older PoolWatch wrote has no `currentAtUtc`, and its `report.md` says
  "now" of a closed incident's level too, which is the level as it was last measured, at its close or when the
  growth was found still held);
- `headline` (one line) and `plainAnswer` (the answer in plain words, with the suspects grouped by who they work for:
  an app, a service, a Claude Code session). Of a closed incident they say "was flat over 1 min", "No clear cause
  was found" and "Non-paged pool rose over 5.0 GB", where an open one's say "is flat over ...", "No clear cause yet"
  and "Non-paged pool over 5.0 GB and rising", and a drop's `plainAnswer` says the drop ("fell 140 MB in 1 min").
  The figure in a `NonPagedTotal` headline is the limit the pool was over when the alert fired: not the pool's size
  (the alert's `rule` under `alerts` gives it) and not that alert's `bytes`, which is how much the fastest-growing
  tag rose in 10 minutes. A `Capture` incident written before its trace has finished has no span yet: its
  `headline` says so, with the time of the newest sample when it was written ("its trace had not finished by
  07:05:20"), as `report.md`'s title and Growth line do, and its `plainAnswer` is empty or says the same; its
  `growthBytes` of 0 is then no finding of a trace. One whose trace recorded nothing PoolWatch could use never
  gets a span: its `headline` says that instead ("its trace recorded nothing PoolWatch could use"), and `capture`
  has the trace's `error` or `notes`.
  A folder an older PoolWatch wrote gives a growth in both cases ("grew ... in ..."), which is of no trace, says
  "is flat", "No clear cause yet" and "and
  rising" of a closed incident too, gives the alert's `bytes` as the figure in a `NonPagedTotal` headline, and in a
  drop's `plainAnswer` says "is flat" or "grew", which is of the time just
  before the drop, not of the drop;
- `suspects[]`: each process with `rank`, `pid`, `image`, `displayName`, `commandLine`, `ancestry` (its parent
  chain), `services`, `confidence` and `evidence` (each line one reason it ranks there). They are as PoolWatch last
  ranked them: for a closed incident at its close (or as its trace was attached, after it), however much later the
  report was written. So `report.md` says "still running" only of an open incident's suspect; of a closed
  incident's suspect that PoolWatch knew to be running it says "still running when it closed", which is not whether
  it runs now (a folder an older PoolWatch wrote says "still running" of a closed incident's too);
- `traceOwners` and `traceAnswer`: what a confirmation trace attributed, when one ran (`capture` holds its details).
  `traceAnswer` also says where the tag's level was against its growth: of an open incident at the newest sample
  ("and is still held", "has been freed since", "it is now empty"), of a closed one at the sample `currentAtUtc`
  gives, with its time ("and was still held at 07:05:53", "had been freed by 07:05:53", "it was empty at 07:05:53";
  a folder an older PoolWatch wrote says "is still held" of a closed incident too, which is of its close or of when
  the growth was found still held);
- `retention` (when a closed incident's growth was found still held after its likeliest cause went quiet or exited:
  `timeUtc`, `activityStoppedUtc`, `retainedBytes`, and `note`, which says it with that time, "still held 12 MB of
  its 12 MB rise at 07:20:53", as `report.md`'s "Retained" line does; a folder an older PoolWatch wrote says "still
  holds", which is of `timeUtc` too, not of now);
- `release` (for a drop: how much and when; `fromBytes` and `toBytes` are the tag's real levels, `dropped` is what fell
  on its own, and `clearedBytes` what PoolWatch's own cache clears freed of it meanwhile, with `clearedAtUtc` when one
  clear did), `window` (the time span the suspects were judged over), `timeline`, and `notice` (the file is not
  redacted, as written).

Confidence, strongest first: `ConfirmedByTrace` (a trace saw the process allocate this tag), `StronglyCorrelated` (a
direct charge, such as its own handles of the matching type, or several signals agree), `Correlated` (one signal
agrees), `Weak` (it was only active at the time). **Correlation is not causation**: say "likely" until a trace or a
reproduction confirms it.

## 4. Known patterns and how to recognise them

- **File-system metadata held by the standby cache.** Tags `Reff`, `RefF`, `Refx` and the `MS..` tags (`MSPi`,
  `MSeb`, `MSb+`, `MSde`, `MSpa`) of ReFS (every Dev Drive is ReFS), `NtFC`, `NtfF` and `Ntff` of NTFS, `FMfn` (the
  filter manager's file-name cache), `MmSt` and `CcVl` grow while programs touch many files and stay while the files
  stay in the standby cache. They drop sharply after a cache clear: compare the `CacheClear` findings ("By tag: Reff
  722 MB -> 72 MB") and `cache\clears.json`. Common drivers: a crawl of a whole drive, many git worktrees, and an app
  that opens files in a loop, such as a desktop app's GPU shader cache (for the Codex desktop app, whose program is
  `ChatGPT.exe`, starting it with `--disable-gpu-shader-disk-cache` stops that, as traced by Faarfarin in openai/codex
  issue 31212; PoolWatch can make a shortcut that does).
- **The token leak.** `Toke` can rise with every process start on some Windows 11 24H2 and 25H2 builds (26100 and
  26200); other builds are untested, and only a test tells. PoolWatch's result for this build is in
  `cache\token-guard.json`. Look for `TokenSpawn` findings; the token-leak guard works around it.
- **A handle leak.** `File` (or another object tag such as `Even` or `Sect`) rises together with one process's handle
  count (`processes.csv` handles, `handles.csv` by type). This is direct evidence: the process holds the objects.
- **A spawn storm.** `SpawnStorm` and `ChildAccumulation` findings: one process starting hundreds of short-lived ones
  (a script in a loop, a file watcher, an AI agent's shell).
- **A saturated PC.** Every core busy (`load.csv` cpu_fraction near 1 with a long run_queue) and `Saturation` findings
  naming the busiest process and its services. The pool may be fine; the PC is slow for another reason.

## 5. Confirming a root cause

- **Reproduce it.** PoolWatch's experiment (tray menu) records a baseline with the app closed, then the app running,
  then closed again, and compares every tag (`experiment\report.md` in a vendor bundle).
- **Clear the cache before and after.** If the growth drops after a clear, it is cached file-system metadata (it
  comes back as files are touched again). If it stays, something is holding it.
- **Take a trace.** Capture now records a confirmation trace; a suspect "confirmed by a trace" was seen allocating.
- **Compare with a clean boot.** `tags\clean-boot.csv` and `boots\*\baseline.json` show what is normal on this PC.
- **Rule out the other suspects**: check that their activity did not follow the growth.
- **Enough** is a trace that attributes the growth, or a reproduction that starts and stops it with one program,
  together with a clear that shows how much is only cache. Correlation alone is a lead, not proof.

## 6. Redaction

Before anything leaves the PC, remove: the user and machine names, paths inside the user's profile and project
folders, SIDs (`S-1-5-21-...`), GUIDs, anything secret on a command line (passwords, keys, sign-in values), e-mail
addresses, IP addresses, and Claude Code or AI Code Conductor session, project and account names.

- Use **Export report** or **Export vendor bundle** with redaction on (the default). It replaces names and paths with
  placeholders such as `<USER>`, `<MACHINE>`, `<HOME>` and `<SECRET>`.
- Never share `capture.etl` or a raw incident folder: they cannot be redacted reliably.
- Redaction is best-effort. Read everything before it is posted, and point out to the person anything that still
  looks personal.

## 7. Writing a vendor bug report

Draft it for the person to read and post. Keep to facts, not blame.

1. **Environment**: Windows edition and build, the app's version, memory, file systems (ReFS or Dev Drive, NTFS).
2. **Symptom with numbers**: which tag grew, by how much, over how long ("`Reff` grew 6.1 GB in 3 h").
3. **Tag and driver**: the tag, its owner driver and what it holds.
4. **Steps** that reproduce it, if known.
5. **Evidence and its confidence**: the suspects and why, the trace or the experiment if there was one.
6. **What was ruled out.**
7. **Attachments**: a redacted export or vendor bundle, read first.

Where: the vendor's own tracker for an app; Feedback Hub for a Windows component (`refs.sys`, `fltmgr.sys`, `ntfs.sys`,
the kernel); PoolWatch's own GitHub for a problem with PoolWatch.

## 8. What not to suggest

- Turning Microsoft Defender off. For a Dev Drive, Defender's performance mode keeps protection on and costs far less.
- Running a RAMMap clear task as well as PoolWatch's automatic cache clear: two clears fight over the same cache.
  PoolWatch pauses its own while such a task exists, and can replace it.
- Deleting `service-changes.json` or `applied-changes.json`: Active changes and the Services tab need them to put
  things back.
