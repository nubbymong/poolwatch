# Changelog

What changed in each PoolWatch release, newest first, in plain words.

## Unreleased

## 1.0.0 - 2026-11-13

The first public release. PoolWatch keeps a Windows 11 PC fast and stable through long days of agentic coding: it
watches the kernel pool, empties the standby cache before the pool slows you down, fixes known issues and, if you
want, trims background work. Every change it makes is listed, with a way to put it back.

- **What PoolWatch keeps on disk is your choice.** PoolWatch watches the kernel pool live and tells you when it grows
  either way. **Reports only**, the default, writes a report each time the pool grows and keeps no record of the day
  (beside the reports, only its log, its settings, a summary of each boot and the Since boot report);
  **Detailed recording** also keeps a record of the whole day (the pool and its tags, programs and their handles,
  services, every program started) and report folders for its spawn storms, up to about 60 MB a day on a PC doing heavy
  agentic coding. Choose in setup's new Records step, or under **Records** in Settings (**Settings...** in the tray
  menu, in either view), where a change applies at once; both also set when reports, records and exports are cleaned
  up (7 days by default). People upgrading from 1.0.0-beta.2, which always recorded, get Reports only and are asked
  once to choose: the Records step opens alone at the first start, and again at each start until they press Done or
  save a change of the record mode or the cleanup in Settings. What beta.2 recorded is cleaned up by the usual rule.
- **Setup that fits itself to your PC.** At first start, Set up PoolWatch opens on its own, in eight short steps
  (about 3 minutes), before the main window. It reads the PC and lists each known cause of a growing kernel pool with
  how much it affects this PC, the highest first (high, medium, low, not known where it could not check, then none), and
  which step fixes it. You choose when PoolWatch empties the standby cache (on a schedule, at a pool size, whichever
  comes first, or decide later) and the fixes that fit: starting at sign-in (a sign-in task you turned off shows as
  off, and stays off unless you switch it on), the token-leak guard if this Windows build has the leak, a Codex shortcut
  without its shader cache when the Codex app is installed, and where to find Defender's performance mode for a Dev
  Drive. The token-leak check says plainly what it found. On the steps that change things, Next pauses for a second,
  so you can read what it will do. Setup changes no services. Last, you choose what PoolWatch keeps on disk.
  **How PoolWatch works**, from every step, explains the
  kernel pool and why emptying the cache is safe. The first time, closing setup asks first, with No (keep it open) the
  default; if you close it, PoolWatch keeps running in the tray, what you set with Next stays, and setup opens again
  when you open PoolWatch and at its next start. Open setup again from the tray menu: **Run setup again...**; and
  Settings from there too, **Settings...**, in either view.
- **Watches the kernel pool.** The kernel pool is the memory Windows and its drivers keep for themselves. PoolWatch
  sits in the tray, checks it every 10 seconds with little overhead, and when it grows works out which program,
  service or driver is behind it, and says how sure it is.
- **Notices you can click, and choose.** When the kernel pool grows, a notice by the clock says what grows; a click
  on it opens PoolWatch's window. Under **Notifications** in Settings, choose a notice for every growth (the
  default), only when it needs attention (when the tray icon turns red, so a growth's notice comes once a trace
  confirms its cause), or none; the token-leak guard's notices, which need your click, always appear. Starting
  PoolWatch while it runs brings its window to the front, and its tray icon comes back after Windows' taskbar restarts.
- **A Simple view that shows, not tells.** The kernel pool now, an arrow for which way it went in the last 10
  minutes, when the next cache clear runs (with a ring that fills as it comes) and what the last one freed; four tiles
  for the last 6 hours (growth per hour with the clears added back, what the clears freed, the net change, and the
  memory in use); the graph, with a mark at every cache clear; and **Get more from PoolWatch**, up to three cards for
  what PoolWatch found on this PC and for parts of the app you may not have found. It stays calm, and turns red only
  for a real risk.
- **An Advanced view with the details.** A graph of the last hour, 6 hours or 24 hours (the time before PoolWatch
  started shows as a labelled band), with the growth and clears beside it, and **Show RAM**, on at first, for RAM in
  use, the cache and free memory behind it. **Pool causes** lists what grew, what the clears freed of it, its impact,
  the likely cause and how sure PoolWatch is, with what PoolWatch already does about it. Every list sorts by the column
  you click. The Pool causes page no longer shows an error box, or stops laying out, when Windows switches desktops (a
  lock, a UAC prompt) while the page is laid out, as when its window resizes.
- **Clears the cache automatically.** On PCs with plenty of memory, Windows' file cache can hold file records in the
  kernel pool and grow it by gigabytes, above all on ReFS drives such as a Dev Drive. PoolWatch can empty the standby
  cache on a schedule, when the pool reaches a size, or whichever comes first, and shows what each clear freed. Your
  files and programs are not touched. A clear takes a few seconds, and video or the mouse may stutter while it runs:
  PoolWatch says so where you turn clearing on.
- **Tells its own cache clears apart.** What PoolWatch's own cache clear frees is reported as the clear's result, not
  as the pool or one kind of kernel memory (a pool tag) falling, everywhere it reports growth. Growth after a clear
  counts from the level the clear left, growth before it still counts, and each alert keeps its own time span and
  size. A pool tag's growth in the 2 minutes until a clear is measured counts as the clear's, so a leak just over an
  alert's size that the clears keep trimming can go unreported: at the default 10-second sampling, up to about 4% over
  the hourly growth alert with hourly clears, and about 8% with clears every 30 minutes (or in an hour where a light
  clear is followed by a full one); more with a longer sampling interval. For the same reason a program's own release
  in those 2 minutes is credited to the clear, and a release across a clear is raised only on what the tag fell after
  the clear was measured.
- **Recognises known issues.** A built-in list of known problems: the Windows 11 token leak, ReFS file records kept
  by the file cache, NTFS file records piling up while some AI desktop apps run, and the Codex desktop app's GPU shader
  cache. When what PoolWatch measures matches one, it says what it is, what helps and who found it. For the shader
  cache, PoolWatch can make a desktop shortcut that starts the Codex app without it; it never changes the app itself.
  [Know another one?](https://github.com/nubbymong/poolwatch/issues/new?template=known_issue.yml)
- **Token-leak guard, and a watch for the leak.** Some Windows 11 builds leak a little of the kernel pool every time
  one program starts another, and coding assistants start thousands of programs an hour. PoolWatch tests your PC for
  the leak and, only if it has it, offers the workaround, which it applies again each time PoolWatch starts. While it
  is on, other programs can bring their windows to the front and take your keystrokes, even while you type. The guard
  shows a notice only when it needs your click. When it finds the setting off later in a sign-in, it turns it on again
  by itself when Windows lets it; until then the tray icon's dot is amber (a red one stays red), its tooltip and menu
  say this sign-in is not protected, and a click on the icon turns it on. When the leak was not found, PoolWatch keeps
  watching for it, checks again after a Windows update, and offers the guard if it appears. Turning the guard off in
  Active changes puts back the Windows setting it changed when Windows allows it (one it found already changed lasts
  until you sign out).
- **Trims background work, if you want.** The Services tab shows Windows' own services with the source PoolWatch
  follows for each (Microsoft's virtual-desktop optimisation tool, the CIS security checklist for company PCs, or no
  official source), and the services of other apps apart; what is not running and the protected services stay hidden
  until you ask. **Decide with your AI agent** saves a prompt for Claude Code, Codex or any other agent, never with
  your name, paths or files in it (as best it can: it shows you every word first), and **Import answer** shows every
  change your agent suggests before anything is turned off. The new **Tasks and startup apps** tab ranks startup apps,
  scheduled tasks and apps busy in the background by what each costs now, with a panel that says what each one runs and
  when, and plain actions. Save the services you keep off as a named profile, next to the built-in **Telemetry only**
  and **Agentic coding PC**; a profile can be applied each time PoolWatch starts, a few minutes later, so a service a
  Windows update turned back on goes off again ([more in the user guide](docs/USER-GUIDE.md#services)). Put back
  starts a service again only if PoolWatch saw it running when it turned it off, and says when it does not know. If
  PoolWatch cannot read the list of services, the tab says so, and Turn off, Put back and a profile change nothing
  until it can.
- **Active changes: a way to put each change back.** Every lasting change PoolWatch makes to your PC is in one window:
  the features you turned on, each with Turn off, what PoolWatch turned off, each with Put back, any other change it
  made (a service set to its Windows default, a task or startup app turned on), also with Put back, and **Put all
  back** for the lot. The window says when one cannot fully come back.
- **Uninstalling puts your changes back.** The uninstaller lists what PoolWatch changed and, by default, puts back
  everything it can (you can keep your changes instead); it lists anything it cannot put back, with why. It says what
  stays until you sign out and what it put back, and, once everything is put back, asks whether to delete PoolWatch's
  reports, logs and settings. If PoolWatch's cleanup fails, the uninstaller removes no file and says why (started by
  hand, it offers Retry). What is still in use in the install folder is deleted the next time Windows restarts, unless
  the uninstaller says it could not arrange that; the uninstaller never restarts Windows itself. A silent uninstall and
  `/DELETEDATA` put back without asking.
- **Evidence you can share.** When something keeps growing, PoolWatch writes a report with a timeline, the likely
  causes and the evidence for each, ready for a bug report to the vendor. How sure PoolWatch is about a cause reads the
  same in the window, Copy summary, the reports and the exports: weak evidence, some evidence, strong evidence or
  confirmed by a trace. A trace you start with **Capture now** gets a report of its own: once the trace has finished,
  that report starts exactly where the trace started and measures the growth from there (in 1.0.0-beta.2 the report of
  a tag that was already rising started earlier, where PoolWatch measured the rise to begin, which could be before you
  asked for the trace), and its events are the trace's own, from exactly where the trace started to exactly where it
  ended, however long the trace was (in 1.0.0-beta.2 they began 2 minutes before the report's start and stopped a
  minute after you asked for the trace, or a minute after the tag stopped rising, so a 5-minute trace's report held
  the events of its first minute). Until the trace has finished, that report gives no growth: its title and its
  Growth line say "its trace had not finished by 07:05:20" (the time of PoolWatch's latest measurement when the
  report was written), and when a trace recorded nothing PoolWatch could use, they say that. A vendor bundle's
  `incidents.md` lists such a trace in the same words, not as a growth (the time it gives for a trace that had not
  finished is that of PoolWatch's latest measurement when the bundle was made). The report of a sharp
  drop lists the events of the drop
  (in 1.0.0-beta.2 its event
  list was empty), and a report written again after its incident closed says what was so at its close, with the time:
  the system's figures, the tag's allocation rates, which pool the tag was in, and the level the tag had reached. A
  closed incident's report gives that level with the time it was measured ("244 MB at 07:05:53") and says "now" only
  while the incident is open (in 1.0.0-beta.2 a report written again hours after the close still said "now" of the
  level at the close). The same goes for what its trace showed, for its suspects and for a growth found still held
  after its cause went quiet: a closed incident's report says the growth "was still held at 07:05:53" where an open
  one's says "is still held", a suspect that had not exited is "still running when it closed", and its "Retained"
  line gives the time it is of ("still held 12 MB of its 12 MB rise at 07:20:53") (in 1.0.0-beta.2 a closed
  incident's report said "is still held", "still running" and "still holds", whenever it was written). The line
  with the allocation rates says what they are over: the growth, the
  drop in a sharp drop's report, or the trace in the report of a trace whose tag did not grow (in 1.0.0-beta.2 it
  called that span the growth in a drop's and a trace's report too). A sharp drop's report says the drop in plain
  words ("fell 140 MB in 1 min"; in 1.0.0-beta.2 it said the tag "is flat" or "grew"). In a closed incident's
  report a tag that did not move "was flat", and one that only fell "fell" by how much (in 1.0.0-beta.2 both were
  "is flat", whenever the report was written), and where it names no cause it says "No clear cause was found"
  (an open incident's report says "No clear cause yet", as 1.0.0-beta.2 said of a closed one too). A likely cause's
  line says what its handles and its activity did, not what they do now: "its File handles rose by 24,000" and
  "rose and fell with the tag: strongly", in a report and in the window's details of a growing tag (in 1.0.0-beta.2
  "holds 24,000 more File handles" and "rises and falls with the tag", which a report written after the program
  had exited still said). The title of a report for the whole non-paged pool names the limit the pool went over
  (5.0 GB unless you set another): "Non-paged pool over 5.0 GB and rising" while the incident is open, and
  "Non-paged pool rose over 5.0 GB" once it has closed (in 1.0.0-beta.2 the figure was how much the fastest-growing
  tag had risen in 10 minutes, so the title could read "over 100 MB and rising" of a pool at 5 GB, and it said "and
  rising" after the incident had closed too). When a closed incident's
  report cannot be written because another program holds one of its files, then for as long as PoolWatch still
  holds the incident it keeps trying, about once a minute, and tries once more as it stops; its log says so once,
  and once more when the files are written again. While it is `timeline.csv` or `processes.csv` that is held, the
  rest of the report is written all the same, so `report.md` says that the incident closed, and when. The chart,
  `timeline.csv` and `processes.csv` are made from the measurements PoolWatch holds of the incident's time; it
  keeps the last 70 minutes of measurements, and lets the oldest go in batches. Once it no longer holds the
  incident's first ones (for an incident that ran about 50 minutes or longer, that is within minutes of its
  close), a try writes a file that could not be written before only when it has at least as many of the
  incident's measurements as the file holds already, so a file that stops before the incident closed is replaced
  by one that runs to the close; when a try would have fewer, it leaves the file as it is, the log names the
  file, and no further try is made for it (in 1.0.0-beta.2 the report stayed as it was until something about the
  incident changed, which for a closed incident could be never). The draft bug report at the end of `report.md`
  lists as attached evidence only the files the folder held when that report was written (in 1.0.0-beta.2 it
  always listed `timeline.csv`, `processes.csv` and `events.csv`). When the folder holds no `events.csv` (deleted by
  hand, say), the line under the window's events says so instead of counting events in it. In `report.md`, names,
  command lines and
  paths are shown as plain text: web and e-mail
  addresses, `@` names, issue numbers and commit hashes in them go in code spans, so GitHub makes no link or mention of
  them. By default, Export report takes out your user and machine names, your home folder and values that look like
  passwords or keys. It does its best, so read the export before you share it.
- **Spawn storms, counted right, and light on the disk.** A program that starts more than 30 short commands in 30
  seconds shows as a spawn storm. The copies Git Bash makes of itself to run a pipeline are not counted (what they
  start is), and one program is one storm however PoolWatch first saw it. One program's storms within an hour share one
  report folder, and at most 10 new ones are made an hour. PoolWatch writes its incident and storm reports within a
  disk budget: a report is written when an incident opens and closes, and brought up to date in between as the budget
  allows (with many incidents open at once, up to about an hour behind). The window is never behind: the list's
  figures, an open incident's details, its Copy summary and Report an issue's summary of it come from what PoolWatch
  knows now, and Export report writes an open incident's folder whole first. About and `cache\profile.json` say what the
  budget wrote, how much of it could wait against what the budget allows, how often it held a write back, and which
  open incidents' reports or charts are behind. An
  incident's `events.csv` holds both ends of the window its report shows: the first 500 events from the window's
  start and, once it closes, the latest 500 up to the window's end (on a busy PC about 3 minutes of events at each
  end), with a row between them saying how many events were left out, between which times, and what the file keeps.
  PoolWatch measures a growth's start again as it grows, later for a long, steady one and sometimes earlier, and the
  first 500 follow the window's start: they are taken again from where the window starts then (when the incident
  closes, and at most every 10 minutes while it is open), so the file shows the window the report shows. Left where
  the incident opened, they would be mostly from before the growth after an hour on a busy PC (416 of the 500, on
  average, in a test of that load). On a PC so busy that PoolWatch no longer holds every event from the window's start
  (by default it holds the events of the last 90 minutes, and never more than 20,000), the file keeps the first events
  it still holds from there, up to 500: where those are the window's first events (the report had kept them itself,
  from a start now measured later) there can be fewer than 500 and the file says how many, and where PoolWatch had let
  go of events from the start on, the file and `report.md` say so, with the time up to which it had let events go. While
  the incident is open the file holds those first events, every event from its first row to its last, never more
  than 500; when the window's start moves while the file is still being filled, the file starts again from the new
  start, and once its last row says to when it holds its first events it
  stays as written until the incident closes, with `report.md` saying where its rows are from and where the window
  starts now. Export report of an open incident
  has both ends, with a `report.md` that counts them, and an export never takes half a row. When an incident closes,
  its `events.csv` gets every event the close has to give it, in time order, each event once (a short incident's file
  too, which holds the first events alone), and the row that says how many were left out counts only events of the
  window: when the window's end is measured again, the latest 500 and that count are taken again from the events
  PoolWatch holds (on a PC so busy that it no longer holds them, the row and `report.md` say that the count runs past
  the window's end, and where the window ends). An event of a closed
  incident's window that comes late (a short command's summary can come a minute and a half after it ran) is listed in
  `report.md` at the next sample the budget has room for (its row is added at the end of a file that holds the first
  events alone), and two minutes after the window's end PoolWatch writes the
  folder once more, whatever the budget, when `events.csv` does not hold the window's events as `report.md` lists them,
  each once and in time order.
  The incident list always shows your incidents, however many storm reports came after them.
- **Lighter on the CPU and memory under agentic load.** On a PC starting hundreds of processes a minute with ten
  incidents open, 1.0.0-beta.2 spent 2.4% of one core and its memory rose for hours (170 MB of managed memory after
  7 hours). An open incident's growth is still measured every 30 seconds, but its suspects are ranked again only before
  its report is rewritten, every 2 minutes while the disk budget has room, and at least every 10 minutes, so the cause
  the list shows is at most 10 minutes old; each report holds the ranking of the moment it was written. What PoolWatch
  holds is bounded: an idle process's samples take no room and a busy one's half, Git Bash's copies share their
  shell's command line, only processes a ranking can name are kept for an incident's window, a process that ended over
  90 minutes ago keeps only its name and details (the latest 10,000 such), the one-minute history keeps each process's
  I/O for 2 hours and then each hour's 10 busiest by name (what the Since boot report names), an incident keeps the
  first 500 events of its window and its latest 500, a closed one its first 500 alone
  once it can no longer
  re-open (its `events.csv` says how many it left out between them, and when; with Detailed recording the day's `events.csv` has every one),
  and at most
  20 closed incidents stay in memory (the rest are listed from their folders; a tag growing again an hour after its
  incident left memory gets a new one). Replaying a day of that load through PoolWatch's per-sample analysis (not its
  process query, event trace, window or files) takes 0.14% to 0.17% of one core, from run to run, where it took 2.6%
  before this change, and
  holds about 40 MB of managed memory from the sixth hour to the day's end (36 to 42 MB, by the hour and by how the
  test is run), where it held 101 MB after 4 hours and
  rose. PoolWatch as a whole is still over its own budget on such a PC (About and every report say how much). An
  incident open longer than 70 minutes keeps where its growth began, so its growth no longer shrinks in its report,
  and PoolWatch's own cache clears still count in it after they leave the 70 minutes.
- **The day's data files, light on the disk.** With Detailed recording, on a PC doing heavy agentic coding the day's
  CSV files wrote about 4.7 MB an hour, nearly the whole 5 MB budget; in a test of that load they now write about 2.1 MB.
  With the reports of long incidents closing about 3 an hour beside them (about 2.2 MB an hour, a little over their
  2 MB share, as each close writes both ends of its `events.csv`) and `findings.jsonl`, PoolWatch writes about 4.6 to
  5.0 MB an hour on such a PC with Detailed recording, before its log and the files under `cache\`: at its 5 MB
  budget, not inside it. With Reports
  only, the default, the day's files and `findings.jsonl` are not written. Each day's files
  start with the whole picture and
  then hold what changed: a pool tag when it moved 1 MB (and every 10 minutes), a process once it has run a minute and
  while it is busy, a service when its host changes, handle counts when they change (and once more as a count leaves
  the ones recorded), an exit without the command line its start already gave, and the short commands summed over 5
  minutes. The guide for AI assistants in the data folder says, file by file, how to read them. A handle scan writes
  only its own files, on a thread of its own, so PoolWatch's sampling never waits for it. The vendor bundle takes the
  spawn trace of the storm folder in use most recently.
- **A week of data, whatever the clock says.** PoolWatch keeps its data in `C:\ProgramData\PoolWatch`, which only
  administrators can change. A day's folder, an incident or an export is deleted once nothing in it was written for 7
  days (by default; 1 to 90 in the Records step and in Settings), counted on the PC's own timer while the PC is on and awake, so a change of the PC's clock never
  deletes anything early. While PoolWatch runs, the growth, the clears and the alerts are timed on the PC's own
  timer too, so a change of its clock then moves none of them. Each check of what to delete is one line in the log.
- **Settings, each explained.** **Advanced...** in Settings lists the settings in a grid and says what the one you
  select does, its default and, where it has them, its unit and limits. The sign-in box is ticked only while PoolWatch
  really starts when you sign in; when its task is there but does not start PoolWatch, a line under it says why.
- **Report an issue.** From the tray menu: PoolWatch writes what it saw with your user and machine names taken out,
  shows it to you, copies it, and opens GitHub's issue form in your browser. Nothing is public until you paste it and
  submit.
- **Tells you when a new version is out.** Once a day it checks GitHub for a new stable release (it does not offer
  betas). Information only: nothing is downloaded or installed. It sends only PoolWatch's version, nothing about your
  PC (GitHub sees your IP address, as it does for any web page). Turn it off on setup's last step or in Settings.
- **An installer.** A signed setup program installs PoolWatch in Program Files with .NET included. It says what
  PoolWatch does and what happens next: a welcome page; before anything is installed, the three steps (install, start
  in the tray, set up in about 3 minutes) and "You stay in control"; and, once it is installed, where PoolWatch lives
  and that its setup guide opens first. PoolWatch starts as administrator only through its launcher, the install
  folder's `PoolWatch.exe`, which starts PoolWatch itself, `app\PoolWatch.exe`, with an environment it builds from
  Windows' own values. To pin PoolWatch to the taskbar, pin its Start menu entry. An update first asks PoolWatch to
  close and waits for it, and closes no other program: if another program still has one of PoolWatch's files in use,
  the update finishes the next time Windows restarts, and the setup says so and names each file in use and the program
  that has it open. The setup never restarts Windows itself; a silent setup then ends with exit code 3010. Two zips are
  there too, if you would rather install by hand.
- **A guide for your AI assistant.** An `AGENTS.md` in PoolWatch's data folder and in every export explains its files,
  so an AI assistant can help you read them.
- **The binoculars everywhere.** PoolWatch's icon on every window, the taskbar and the tray, where a coloured dot
  shows the status.
