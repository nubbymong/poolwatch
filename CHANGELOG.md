# Changelog

What changed in each PoolWatch release, newest first, in plain words.

## Unreleased

## 1.0.0 - 2026-10-30

The first public release. PoolWatch keeps a Windows 11 PC fast and stable through long days of agentic coding: it
watches the kernel pool, empties the standby cache before the pool slows you down, fixes known issues and, if you
want, trims background work. Every change it makes is listed, with a way to put it back.

- **Setup that fits itself to your PC.** At first start, Set up PoolWatch opens on its own, in seven short steps
  (about 3 minutes), before the main window. It reads the PC and shows, for each known cause of a growing kernel pool,
  how much it affects this PC (none, low, medium or high) and which step fixes it. You choose when PoolWatch empties
  the standby cache (on a schedule, at a pool size, whichever comes first, or decide later) and the fixes that fit:
  starting at sign-in, the token-leak guard if this Windows build has the leak, a Codex shortcut without its shader
  cache when the Codex app is installed, and where to find Defender's performance mode for a Dev Drive. The token-leak
  check says plainly what it found. On the steps that change things, Next pauses for a second, so you can read what it
  will do. Setup changes no services. **How PoolWatch works**, from every step, explains the kernel pool and why
  emptying the cache is safe. Open setup again from the tray menu: **Run setup again...**.
- **Watches the kernel pool.** The kernel pool is the memory Windows and its drivers keep for themselves. PoolWatch
  sits in the tray, checks it every 10 seconds with little overhead, and when it grows works out which program,
  service or driver is behind it, and says how sure it is.
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
  you click.
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
  is on, other programs can bring their windows to the front and take your keystrokes, even while you type. When the
  leak was not found, PoolWatch keeps watching for it, checks again after a Windows update, and offers the guard if it
  appears. Turn the guard off in Active changes; the setting itself lasts until you sign out.
- **Trims background work, if you want.** The Services tab shows Windows' own services with the source PoolWatch
  follows for each (Microsoft's virtual-desktop optimisation tool, the CIS security checklist for company PCs, or no
  official source), and the services of other apps apart; what is not running and the protected services stay hidden
  until you ask. **Decide with your AI agent** saves a prompt for Claude Code, Codex or any other agent, never with
  your name, paths or files in it (as best it can: it shows you every word first), and **Import answer** shows every
  change your agent suggests before anything is turned off. The new **Tasks and startup apps** tab ranks startup apps,
  scheduled tasks and apps busy in the background by what each costs now, with a panel that says what each one runs and
  when, and plain actions. Save the services you keep off as a named profile, next to the built-in **Telemetry only**
  and **Agentic coding PC**; a profile can be applied each time PoolWatch starts, a few minutes later, so a service a
  Windows update turned back on goes off again ([more in the user guide](docs/USER-GUIDE.md#services)).
- **Active changes: a way to put each change back.** Every lasting change PoolWatch makes to your PC is in one window:
  the features you turned on, each with Turn off, what PoolWatch turned off, each with Put back, any other change it
  made (a service set to its Windows default, a task or startup app turned on), also with Put back, and **Put all
  back** for the lot. The window says when one cannot fully come back.
- **Uninstalling puts your changes back.** The uninstaller lists what PoolWatch changed and, by default, puts back
  everything it can (you can keep your changes instead); it lists anything it cannot put back, with why. It says what
  stays until you sign out and what it put back, and, once everything is put back, asks whether to delete PoolWatch's
  reports, logs and settings. A silent uninstall and `/DELETEDATA` put back without asking.
- **Evidence you can share.** When something keeps growing, PoolWatch writes a report with a timeline, the likely
  causes and the evidence for each, ready for a bug report to the vendor. By default, Export report takes out your
  user and machine names, your home folder and values that look like passwords or keys. It does its best, so read the
  export before you share it.
- **Report an issue.** From the tray menu: PoolWatch writes what it saw with your user and machine names taken out,
  shows it to you, copies it, and opens GitHub's issue form in your browser. Nothing is public until you paste it and
  submit.
- **Tells you when a new version is out.** Once a day it checks GitHub for a new release. Information only: nothing is
  downloaded or installed. It sends only PoolWatch's version, nothing about your PC (GitHub sees your IP address, as it
  does for any web page). Turn it off on setup's last step or in Settings.
- **An installer.** A signed setup program installs PoolWatch in Program Files with .NET included. It says what
  PoolWatch does and what happens next: a welcome page; before anything is installed, the three steps (install, start
  in the tray, set up in about 3 minutes) and "You stay in control"; and, once it is installed, where PoolWatch lives
  and that its setup guide opens first. Two zips are there too, if you would rather install by hand.
- **A guide for your AI assistant.** An `AGENTS.md` in PoolWatch's data folder and in every export explains its files,
  so an AI assistant can help you read them.
- **The binoculars everywhere.** PoolWatch's icon on every window, the taskbar and the tray, where a coloured dot
  shows the status.
