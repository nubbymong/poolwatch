# PoolWatch: a guide for AI assistants

This file is for an AI assistant (Claude, ChatGPT, Copilot or another) that someone has asked for help with
PoolWatch's data. It is written in plain words, so the person can read it too.

## 1. What this folder is

PoolWatch is a free, open-source Windows app (https://github.com/nubbymong/poolwatch, MIT licence). It watches the
Windows kernel's memory pools (the paged and non-paged pool; the app calls them "the kernel pool"), finds which program
or driver makes them grow, and can clear the standby cache. It sends nothing about the PC anywhere: the person shares
only what they choose.

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
folder names and incident ids are local time, so match an id to CSV rows through the time zone. Sizes are in bytes
unless a name says otherwise. A pool tag is four characters and **case-sensitive**: `Reff` and `RefF` are different
tags. Tag owners and meanings are in each incident report, or in Microsoft's `pooltag.txt`.

In the data folder:

| Path | What it holds |
|---|---|
| `AGENTS.md` | this guide |
| `settings.json` | PoolWatch's settings |
| `poolwatch.log` | what PoolWatch did, one line each (moved to `poolwatch.log.1` at 2 MB) |
| `since-boot.md`, `since-boot.json`, `boots.md` | written when PoolWatch exits: what grew since boot, and the last boots side by side |
| `data\<yyyyMMdd>\` | one folder per day, kept 7 days by default: the CSV files below and `findings.jsonl` |
| `incidents\<id>\` | one folder per incident (section 3). `<id>` is `<yyyyMMdd-HHmmss>-<tag>-<tag in hex>` |
| `incidents\<yyyyMMdd-HHmmss>-<kind>-<subject>\` | a report on one alert finding (such as `Experiment`, `Crash`, `SpawnStorm`): its `report.json` is that finding (time, kind, title, details, data), not an incident |
| `cache\clears.json` | the last 100 finished cache clears: when, how and why, the pool (and its paged part) and the standby list before and after, and any error. Every tag that dropped is in the `CacheClear` findings |
| `cache\token-guard.json` | the last token-leak test: the Windows build, the result and when it ran |
| `cache\tagscan.json` | which driver each pool tag belongs to, from PoolWatch's scan of the drivers |
| `cache\profile.json` | PoolWatch's own cost, written every hour: its processor time, memory and heap, what it holds in memory, and what it wrote to each file |
| `service-changes.json` | every background service PoolWatch changed, with its original start type (Restore needs it) |
| `applied-changes.json` | the other changes PoolWatch made that can be put back (Active changes needs it) |
| `removed-tasks\<name>.xml` | only when a clear task of the owner's that PoolWatch removed could not be put back by itself: its definition, to import in Task Scheduler |
| `uninstall-summary.txt` | written by the uninstaller: each change it put back, and any it could not put back (and why) or kept |
| `uninstall-list.json` | only while the uninstaller runs: the list of changes it asked PoolWatch for, deleted again once read |
| `boots\` | one folder per boot: `baseline.json` (the first full tag snapshot) and `summary.json` (the peaks) |
| `reports\` | the Since boot reports |
| `exports\` | export zips, vendor bundles, and Report an issue's saved text (`issue-<yyyyMMdd-HHmmss>.txt`) |
| `requests\` | notes waiting for the running PoolWatch to put on its timeline (`--mark`) |

The daily CSV files have a header row. `totals.csv` has a row every 10 seconds; the others as noted.

| File | Columns |
|---|---|
| `totals.csv` | time, committed_paged_bytes, committed_nonpaged_bytes (the pool as Task Manager shows it), tagsum_paged_bytes, tagsum_nonpaged_bytes (the sum over the tags, which runs lower: compare like with like), tag_count, process_count, commit_bytes, commit_limit_bytes, available_bytes, compression_ws_bytes, system_handles |
| `tags.csv` | time, tag, tag_hex, paged_used, nonpaged_used, paged_allocs, paged_frees, nonpaged_allocs, nonpaged_frees. Once a minute, tags over 1 MB |
| `processes.csv` | time, pid, create_time, image, parent_pid, handles, threads, quota_paged, quota_nonpaged, private_bytes, read_ops, write_ops, other_ops, read_bytes, write_bytes, d_read_ops, d_write_ops, d_other_ops (the change since the previous row), orphan (1: its parent is gone). Once a minute, the 50 busiest processes and any whose handles moved by 100 |
| `handles.csv` | time, pid, image, type, count: handles by object type every 5 minutes. Pid -1 is the total of each type |
| `system-handles.csv` | time, type, count: the System process's handles by type |
| `events.csv` | time, kind, pid, create_time, parent_pid, image, exit_code, user, command_line, detail: process starts and exits, and every finding |
| `services.csv` | time, pid, service, display_name: which process hosts which service, written when that changes |
| `volumes.csv` | time, root, file_system, label, dev_drive, drive_type: at start and every hour |
| `lag.csv` | time, timer_ms, message_ms, file_ms, dwm_missed, committed_paged_bytes, committed_nonpaged_bytes: how late small probes ran (how slow the PC felt) |
| `load.csv` | time, cpu_fraction, run_queue, cores, page_faults_per_sec, demand_zero_per_sec, transition_per_sec, pages_input_per_sec, probe_tripping |

`findings.jsonl` holds one JSON object per line: time, kind, severity (Info, Warning or Alert), title, details, pid,
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

Each incident folder has `report.md` (for people), `report.json` (the same, for you), `timeline.csv`,
`processes.csv`, `events.csv`, `timeline.png` and, when a trace was taken, `capture.etl`. (A folder whose name has a
finding's kind instead of a tag is a finding's report, described in the file map.) The fields of `report.json` that
matter:

- `id`, `kind` (`Growth`, `NonPagedTotal` for the whole non-paged pool over its limit, `Release` for a sharp drop,
  `Capture` for a trace the person started), `tag` and `tagHex`, `owner` and `description` (the driver and what the
  tag holds);
- `openedUtc`, `onsetUtc` (when the growth began), `endUtc` (when it stopped; empty while still climbing),
  `growthBytes`, `startBytes`, `peakBytes`, `currentBytes`;
- `headline` (one line) and `plainAnswer` (the answer in plain words, with the suspects grouped by who they work for:
  an app, a service, a Claude Code session);
- `suspects[]`: each process with `rank`, `pid`, `image`, `displayName`, `commandLine`, `ancestry` (its parent
  chain), `services`, `confidence` and `evidence` (each line one reason it ranks there);
- `traceOwners` and `traceAnswer`: what a confirmation trace attributed, when one ran (`capture` holds its details);
- `release` (for a drop: how much and when), `window` (the time span the suspects were judged over), `timeline`, and
  `notice` (the file is not redacted, as written).

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
  that opens files in a loop, such as a desktop app's GPU shader cache (for the ChatGPT desktop app, starting it with
  `--disable-gpu-shader-disk-cache` stops that, as traced by Faarfarin in openai/codex issue 31212).
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
- Deleting `service-changes.json` or `applied-changes.json`: Active changes and Restore need them to put things back.
