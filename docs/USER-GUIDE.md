# PoolWatch user guide

Everything about installing and using PoolWatch that the front page ([README](../README.md)) keeps short. The
screenshots come from PoolWatch's own tests on a GitHub test machine, a minute or two after PoolWatch starts, so no
cache clear has run in them yet and the growth rate is not known yet; the figures in the text are examples.

## At first start: Set up PoolWatch

At first start, **Set up PoolWatch** opens on its own, in a window of its own: seven short steps, about 3 minutes. The
main window waits until you finish. The first time it has no close button and cannot be skipped: it is how PoolWatch
fits itself to your PC. If PoolWatch stops before you finish, setup opens again at its next start. Later, **Run setup
again...** in the tray menu opens it at any time, and then you can cancel it. **How PoolWatch works**, at the bottom of
every step, explains the kernel pool, why it grows with AI agents, and why emptying the standby cache is safe.

| | |
|---|---|
| ![Setup, step 1: Welcome](screenshots/6-setup-1-welcome.png) | ![Setup, step 2: How your PC is impacted](screenshots/6-setup-2-your-pc.png) |
| ![Setup, step 3: You stay in control](screenshots/6-setup-3-safety.png) | ![Setup, step 4: the standby cache](screenshots/6-setup-4-standby-cache.png) |
| ![Setup, step 5: Fixes for this PC](screenshots/6-setup-5-fixes.png) | ![Setup, step 6: Services, for later in the app](screenshots/6-setup-6-services.png) |
| ![Setup, step 7: PoolWatch is watching](screenshots/6-setup-7-ready.png) | ![How PoolWatch works](screenshots/6-setup-how-it-works.png) |

1. **Welcome.** What PoolWatch does, and a choice: **Simple** or **Advanced**. It picks the view the app opens in;
   you can switch it later in the app. Both run the same seven steps.
2. **How your PC is impacted.** PoolWatch reads your PC (the Windows version, memory, drives, Microsoft Defender, AI
   tools and background services) and shows, for each known cause of a growing kernel pool, how much it affects this
   PC: **None**, **Low impact**, **Medium impact** or **High impact**, and where setup fixes it ("Fix in step 4"). The
   causes: Dev Drive records piling up (a Dev Drive or another ReFS drive: high), Defender scanning your Dev Drives
   (medium when performance mode does not cover a Dev Drive; low on a PC with no Dev Drive), the Codex shader cache
   (low, when the Codex app is installed), background services (low, when services you may not need are running) and
   the Windows token leak. On Windows 11 24H2 and later PoolWatch checks this build for the leak by itself (usually
   about 20 seconds; nothing is changed): found, it is high; not found, the row says "Still watching", because a
   Windows update can bring it back.
3. **You stay in control.** Three promises: nothing changes without your OK; anything PoolWatch changes can be put
   back, one at a time or all at once; uninstalling puts everything back. The few changes that cannot fully come back
   say so in [Active changes](#active-changes), and the uninstaller lists them too.
4. **When should PoolWatch empty the standby cache?** **On a schedule** (every 1 hour to begin with), **At a pool
   size** (when the kernel pool reaches 8 GB to begin with), **Whichever comes first** (recommended) or **Decide
   later**, which leaves automatic clearing off: PoolWatch watches the pool and suggests turning it on when it helps.
   The step says that video or the mouse may stutter for a few seconds while the cache is emptied. If you already clear
   the cache with a RAMMap task of your own, the step says so: choosing a rule replaces that task, so the two never both
   run, and your task is saved so you can put it back.
5. **Fixes for this PC.** **Start when you sign in** (on to begin with); **Defender performance mode** for the Dev
   Drives it does not cover, with **Show me how** (a Windows setting: PoolWatch shows you where it is and never changes
   Defender); **Create a Codex shortcut without its shader cache?** (Yes or No; only when the Codex app is installed;
   PoolWatch does not change the Codex app, the shortcut only starts it without that cache); and the **Token-leak
   guard** when the check found the leak, with its **Why?** open: what you will notice and how to undo it (see the
   [README](../README.md#3-it-recognises-known-issues)). While the check still runs, the step says so; if it finds the
   leak after you have moved on, the app offers the guard. When the guard is on already (when you run setup again, for
   example), its row says so and that you turn it off in Active changes, and its **Why?** still says what you will
   notice and how to undo it.
6. **Trim background services, when you're ready.** For later, in the app: setup changes no services. Windows services
   PoolWatch knows, and the services of other apps, which you decide about with your AI agent (see
   [Services](#services)).
7. **PoolWatch is watching.** What setup set (the standby cache, the Codex shortcut, the token leak, Active changes),
   **Tell me when a new version is out** (ticked to begin with; see [Privacy](../README.md#privacy)) and where PoolWatch
   lives: next to the clock. **Open PoolWatch** finishes setup.

Steps 4 and 5 wait until PoolWatch has read the PC ("Looking at your PC... Next is ready in a moment."), at most a
minute; after that, setup goes on with what it knows. Nothing changes until you press **Next** on step 4 (the cache
rule) or step 5 (the fixes you switched on); each change is then listed under [Active changes](#active-changes). On
the Standby cache and Fixes steps, Next is greyed for a second when the step appears, when new results arrive on it,
and after a change that did not work, so you can read what Next will do or why the change failed. Holding Enter does
not repeat Next: press it again to go on.

## Using PoolWatch

### Simple view

![The Simple view: the kernel pool now, the trend, the next cache clear, four tiles, the graph and the cards](screenshots/1-simple-view.png)

The window opens in the view you chose in setup, and later in the view you used last. The Simple view has:

- **The header**: the kernel pool now ("Kernel pool now 8.4 GB", with its paged and non-paged parts), which way it
  went in the last 10 minutes (an arrow and the amount, or "Steady"), when the next cache clear runs with a ring that
  fills as it comes closer ("Next clear in 37 min or at 8 GB"), and what the last one freed ("Last at 08:59: freed
  914 MB"). Point at the trend for a sentence about it, and at the next clear for the whole line about clearing. A
  drop that follows PoolWatch's own cache clear is that clear's result, never the pool shrinking. A note shows only
  when there is something to watch: a likely cause PoolWatch is sure of ("PoolWatch is keeping an eye on ..."), a
  saturated PC, or where to look while the tray icon is amber or red. It turns red only for a real risk.
- **Clear now**, which empties the standby cache at once. It takes a few seconds, and video or the mouse may stutter
  while it runs.
- **Four tiles**, over the graph's 6 hours: **Growth per hour** (how fast the kernel pool grew with PoolWatch's own
  clears added back: "without the clears"), **Cleared** (what the clears freed, and how many), **Net change** ("up 307
  MB") and **Memory in use** now ("41 GB", "of 127 GB, 71 GB cache"). See [Growth and clears](#growth-and-clears).
- **A graph** of the kernel pool over the last 6 hours: the paged and non-paged pool, as Task Manager counts them.
  Each cache clear has a mark: point at it to see what that clear freed. The lines break where PoolWatch was not
  measuring, for example while the PC was asleep.
- **Get more from PoolWatch**: up to three cards. First what PoolWatch found on this PC that one click puts right: the
  token-leak guard once the leak shows, the automatic clear while it is off and clearing is shown to help on this PC,
  and a report about an incident PoolWatch captured. Then cards for parts of the app you may not have found yet
  (Pool causes, trimming background services, and **Mark** for a slow moment). **Hide** keeps a suggestion away for 30
  days, and a feature card for good.
- **The footer**: **Active changes** with how many changes are in effect, the **Advanced view** switch and, while a
  newer version is out, a line that says so.

### Advanced view

Switch on **Advanced view** for the details. The header stays, and the window adds:

- **The graph** with a **1 h / 6 h / 24 h** choice (6 hours to begin with). The graph follows it: its title ("Kernel
  pool, last 24 hours"), its time axis and its clear marks. Before PoolWatch has run that long, the time before it
  started is a labelled band. A clear from before today says so ("Cache clear yesterday at 23:00 (automatic): freed
  1.2 GB"). PoolWatch keeps the window you picked until it exits.
- **Show RAM**, a switch on the graph: RAM in use, the cache (standby) and free, as pale columns behind the pool's
  lines, on a right axis in percent of the PC's RAM. Point at a column for the gigabytes. It is on at first, and
  PoolWatch remembers what you chose.
- **Growth and clears**, a panel beside the graph, for the window picked: the rate, Grew, Cleared, Fell on its own,
  Net change and how long PoolWatch measured.
- **Settings**, in the header on the Pool causes tab.
- Three tabs:
  - **Pool causes**: one list of what grew or was released, what is happening now first. Each row says what grew (the
    pool tag in words, with its driver), how much it grew, what PoolWatch's own cache clears freed of it, its net
    change, how fast it grows, the likely cause, how sure PoolWatch is, its **Impact** and when. Select a row for its
    evidence, with a line saying what PoolWatch already does about it ("Handled: the standby cache is emptied every
    hour, or at 8 GB."); **Export report**, **Open report folder** and **Copy summary** work on it. A known issue that
    matches shows as a note here. What PoolWatch's own cache clear frees is that clear's result, not a release: it
    shows in the clear's own row.
  - **Services**: see [Services](#services).
  - **Tasks and startup apps**: see [Tasks and startup apps](#tasks-and-startup-apps).

| | |
|---|---|
| ![Advanced view: the graph with the PC's RAM behind it and the Growth and clears panel beside it, and Pool causes with a row selected](screenshots/2-advanced-view.png) | ![Settings](screenshots/4-settings.png) |

**Impact** is the same scale setup uses, from the growth per hour with PoolWatch's own clears added back, over the
window picked: **None** under 10 MB an hour, **Low** under 5 GB a day, **Medium** under 10 GB a day, and **High** at
10 GB a day or more (1 GB is 1,073,741,824 bytes, as PoolWatch shows sizes). The cell is empty until PoolWatch has
measured 30 minutes.

**Settings** is a short form in plain words: starting at sign-in; update notices; automatic cache clearing, by name
(**On a schedule**, **At a pool size** or **Whichever comes first**, recommended) with how often and at what pool size;
deeper automatic captures (traces PoolWatch records by itself also record file activity); and the token-leak guard.
Under automatic clearing it says that clearing takes a few seconds, and video or the mouse may stutter while it runs.
When the Codex app is installed, PoolWatch was installed with its setup and its Codex shortcut is not on your desktop,
Settings offers **Create shortcut**. **Advanced...** opens every setting in a grid. Starting at sign-in changes as
soon as you tick it; **Save** puts automatic clearing, the token-leak guard and update notices into effect at once, and
the other settings when PoolWatch next starts.

![Settings, every setting](screenshots/5-settings-advanced.png)

### Growth and clears

The Simple view's tiles and the Advanced view's panel say what the kernel pool did over a window, with PoolWatch's own
cache clears counted apart. The tiles cover the graph's 6 hours; the panel, the window you pick, with Grew, Fell on its
own and how long PoolWatch measured:

- **Grew** or **Fell on its own**: the pool's change over the window with what PoolWatch's clears freed added back, so
  growth before a clear still counts. A rise shows as Grew, a fall as Fell on its own.
- **Cleared (N clears)**: what PoolWatch's own cache clears freed.
- **Net change**: how much the pool rose or fell while PoolWatch was measuring: Grew, less Cleared, less Fell on its
  own. A change while the PC was asleep is not counted (the [technical page](TECHNICAL.md#how-it-decides) gives the
  one exception).
- **The rate** ("Without the clears it would grow 1.26 GB/h" in the panel; "Growth per hour ... without the clears"
  on the tile): that change with the clears added back, for each hour PoolWatch measured. When the pool fell even with
  the clears added back, the panel says "Without the clears it would fall"; with no clear in the window it says "It
  grows" or "It falls". It shows once PoolWatch has measured 30 minutes; until then it says when it will be known
  ("known in 22 min"). The rate times the hours measured, less Cleared, is the net change: 1.26 GB/h over 18 hours is
  22.7 GB, and less the 21.3 GB cleared that is up 1.4 GB.
- **Measured over** (in the panel): time PoolWatch was not measuring, such as while the PC was asleep, does not count
  ("Measured over 18 h of the 24: the rest was not sampled, for example while the PC was asleep.").

PoolWatch keeps these figures, and the graph, in memory, so they start again each time PoolWatch starts. Until it has
run for the whole window, the tiles say since when ("since 08:05"), and once the rate shows, the panel says when
PoolWatch started ("PoolWatch started at 12:20").

In Pool causes, **Grew**, **Cleared**, **Net** and **Per hour** follow the window picked on the graph for the tags
growing or held now and their open incidents; the line above the list says which window. Other rows show their own
figures: what a process's pool charge rose by, what an earlier incident grew, and what a clear or a release freed. A
selected tag's details begin with the same figures in a sentence ("Last 6 hours: it grew 4.9 GB, PoolWatch's own
cache clears freed 4.8 GB of it, and it is 103 MB higher.").

Click a column header in any list to sort by it; click it again to reverse the order. The header shows an arrow for
the order.

### Services

![The Services tab](screenshots/3-services.png)

The Services tab lists the background services you may not need. On this tab and the next, the graph steps aside, so
its room goes to the list.

- **Two groups.** **Windows services**, which PoolWatch knows, each with where the advice to turn it off comes from:
  "Microsoft's virtual-desktop optimisation tool turns this off", "The CIS security checklist for company PCs turns
  this off", both, or "No official source". These sources were written for virtual desktops and company PCs, so the
  group says to check what you lose first. **From other apps**: no official source, so decide about them with your AI
  agent (below).
- **The columns**: Service, You lose, Cost (what it costs the processor now: None, Low, Medium or High), State and
  Source. Rest the pointer on a row for its note and the whole source line. **Find a service** filters the list.
- **Show not running** and **Show protected** are off at first: the list shows what runs now, and leaves out the
  services PoolWatch never changes (security, Windows Update, input and audio, Hyper-V and WSL, and Windows' core). A
  service you ticked always shows. A service PoolWatch turned off is not running, so it waits with the others that are
  off: put it back from Active changes or **Put all back**, or tick **Show not running** to select it.
- **Ticking.** Tick a service, or a group's header to tick its shown services. To tick several at once, select them
  (Shift or Ctrl and click, or Ctrl+A for all) and press Space, or right-click and choose **Tick selected** or
  **Untick selected**.
- **Turn off N services**, on the right, lists the services it changes and asks before it changes anything: those it
  stops and sets to not start, and apart from them those it sets to Manual without stopping them. PoolWatch saves how
  each one was set first, so you can put it back. Services of other apps that PoolWatch does not recognise get a
  second question.
- **Put back...** has three items: **Put back the selected service** (how it was before PoolWatch changed it), **Set
  the selected service to its Windows default** (for a service that was turned off outside PoolWatch; PoolWatch saves
  your setting first) and **Put all back**.

Turning a service off sets it to Disabled and stops it. For some, mostly updaters and helpers, PoolWatch sets Manual
instead and does not stop it: Windows then starts it only when something asks for it. A service that is already
Disabled stays Disabled. So a service counts as off when it is as turning it off leaves it: Disabled and stopped, or
set to Manual if it is one PoolWatch turns off to Manual. A Disabled service that is running, paused, or still
starting or stopping is not off yet, so turning it off asks Windows to stop it. Any other service set to Manual counts as on, and turning it
off sets it to Disabled. Turn off leaves a ticked service that is off already as it is and records nothing for it: its
question lists only the services it changes and says how many ticked ones are off already, and when all of them are,
it says there is nothing to turn off. One exception: a service PoolWatch turned off that was then changed again outside
it, so that Put back refuses it ("it was changed since"), is turned off and recorded again, so Put back works again.

Windows does not stop a service while a service that depends on it is running. When you turn off both, PoolWatch
stops the one that depends on the other first, and when you put them back, the other comes back first. PoolWatch never
stops or changes a service you did not choose: if a service you did not tick is running and depends on one you turn
off, the one you turn off is set to Disabled but not stopped (it keeps running, or stays paused if it was paused), and
Turn off says so. It stops when Windows restarts, or when you turn it off again after the other one has stopped. A
service that is starting, resuming, pausing or stopping is waited for, up to 20 seconds, and so is its stop. One that
has not stopped by then, or whose stop Windows refuses, is still set to Disabled, and Turn off says it is not stopped
and what it was doing: still running, still paused, or still starting, stopping, resuming or pausing (or that Windows
could not say). It stops when Windows restarts, if not before, and until it has stopped it does not count as off, so
turning it off again asks for its stop again.

#### Decide with your AI agent

What is safe to turn off depends on how you use this PC, so for the services of other apps there is no official list.
**Decide with your AI agent** opens a window with three steps:

1. **Save the prompt**: a file, `poolwatch-services.md`, with what runs on this PC and what to ask you. **Save the
   prompt...** saves it where you pick, nowhere else; **Copy to clipboard** copies it.
2. **Give it to your agent**: Claude Code, Codex or any other: "Read poolwatch-services.md and follow it." Your agent
   asks how you use your apps, then saves its answer, `poolwatch-services-answer.json`.
3. **Import its answer**: **Import answer** on the Services tab.

The window shows every word of the prompt before you save or copy it. What your agent will see: the names and
publishers of the services, scheduled tasks and startup apps on this PC, what each does, and your Windows version.
Never in it: your name, user or PC name, folder paths, or anything from your files (accounts read `<ACCOUNT>`, and a
name longer than 4,096 characters reads `<TOO LONG>`). Like an export's, this redaction is best-effort, so read it.
The prompt is at most 96 KB: on a PC with more than fits, a list stops there and says how many more there are.
Nothing is sent until you give the file to your agent.

**Import answer** reads the answer and shows a review before anything changes: what **Apply** would turn off, what
PoolWatch will not turn off and why (protected services, anything that is not on this PC, Microsoft's own startup apps
and tasks), and what stays as it is. It lists every change and every refusal with its reason; what stays as it is is
listed up to 50 and counted. An item whose name another shares says where it lives (a startup app's place, a task's
folder). Use Tab to reach the list, then the arrow keys, Page Up, Page Down, Home and End. Nothing changes until
**Apply**; services of other apps that PoolWatch does not recognise are asked about again, by name (up to 15, and how
many more). A message then says what was turned off and what was not, and why: each line names up to 15 items and
counts the rest, and it gives up to ten reasons, with the items of any other reasons counted on one more line. If a
service is gone since the review was read, the others are still turned off, and the message says which was not. Every
change is listed under Active changes and can be put back; Put all back and uninstalling put them back too. PoolWatch
refuses an answer in any other form, and never runs or opens anything from it.

#### Profiles

**Profiles** save a choice of services to keep off. Pick one from **Profile** to tick its services:

- **Telemetry only** and **Agentic coding PC** are built in and read-only. Each ticks its services that are not off
  yet. **Agentic coding PC** ticks only services with an official source.
- One of your own ticks all of its services on this PC, also those that are off already (Turn off leaves those as
  they are).
- **Custom (tick by hand)** keeps the ticks you made yourself; they come back when you pick it again.
- Your profiles are kept for the PC, not for your Windows account: another administrator who runs PoolWatch on this PC
  sees the same profiles, and the same one applies at start.
- **Save as profile...** saves the ticked services under a name of your own (or replaces one of yours); you can keep up
  to 50. A profile holds only services PoolWatch recognises; the dialog says how many ticked services it leaves out.
  **Rename...** and **Delete** work on your own profiles. Deleting a profile changes no service. To change which
  services one of yours keeps off, pick it, change the ticks and save it under the same name: the dialog fills in its
  name and whether it applies at start.

Picking a profile only ticks its services: press **Turn off** to turn them off. The line under **Profile** says what
the picked profile keeps off, how many of its services are on now, and whether it applies at start ("Build box: keeps
9 services off; 2 of them are on now. Applied when PoolWatch starts, after 3 minutes."). The tab opens on the profile
that applies at start, with its services ticked; nothing changes until you press **Turn off**.

**Apply a profile when PoolWatch starts.** In the save dialog, tick **Apply this profile when PoolWatch starts, after N
minutes** (1 to 15; 3 to begin with). One profile at a time can do this; **Profile** shows it as "(at start)". Each time
PoolWatch starts, after that many minutes, it turns off any of the profile's services that are on, without asking. That
includes one you turned on yourself outside PoolWatch, in the Windows Services app for example: to keep it on, pick the
profile, untick the service and save the profile again under the same name. A service that is already off is left alone:
Disabled and stopped, or set to Manual if PoolWatch turns it off to Manual (mostly updaters and helpers). Any other
service of the profile found at Manual, for example after a Windows update put it back to its default, is set to
Disabled and stopped if it runs, with no click: that is what the profile is for. (As with Turn off, one that a running
service the profile does not list depends on is set to Disabled but not stopped: it stops when Windows restarts, and
the notice says it was set to Disabled but is not known to have stopped.) A Disabled service stays Disabled, and
a protected service is never touched. When it turned something off, a notice by the clock says so ("Build box turned off
2 services that were on: MapsBroker, SysMain. Click here to see them in Active changes."), and each change is listed
under Active changes: a click on the notice opens it. When it could not turn one off, the notice says so too, and
where the reason is ("Build box could not turn off 1 service: C:\ProgramData\PoolWatch\poolwatch.log says why."). When
you save such a profile while some of its services are on, PoolWatch offers to turn those off now, with Turn off's own
question. A change you make to the profile while PoolWatch waits to apply it never makes that run turn off more: a
service you add, or another profile you choose, waits for the next start, and a service you take out, or a profile you
delete or no longer apply at start, is left alone. If PoolWatch is still waiting to apply that profile from this start,
the question also says that the services it listed then go off when that wait ends, whatever you answer.

To stop a profile applying at start, pick it in **Profile**, choose **Save as profile...**, untick **Apply this
profile when PoolWatch starts** and press **Save**, or delete the profile. Save keeps the services that are ticked
then, so check the ticks first. Either way, the services it turned off stay off until you put them back.

**Putting back a service that profile lists.** If you put back (in Active changes, or with **Put back...** on the
Services tab) a service that the profile applied at start lists, and the service is on afterwards, PoolWatch first says
that the profile would turn it off again at the next start. If you go ahead, it also takes the service out of the
profile, so the profile no longer turns it off. It takes out only the services its question named: one that was turned
off after you were asked, by the profile at start for example, stays in the profile, and the result says so. **Put all
back** says this once, for all such services, at the end of its question. Putting back that leaves the service off
changes no profile and says nothing about it: for example the row for "Set ... back to its Windows default", whose Put
back sets Disabled again. A service that was put back on is also unticked on the Services tab, so the next **Turn off**
does not turn it off again.

### Tasks and startup apps

![The Tasks and startup apps tab, with the top row selected and its detail panel below](screenshots/3b-tasks-and-startup-apps.png)

This tab lists startup apps, scheduled tasks and apps busy in the background, ranked by what each costs now.

- **All**, **Startup apps**, **Scheduled tasks** or **Busy in the background** picks what the list shows; **Find**
  filters it. **Show not running** and **Show Microsoft** are off at first: the list shows what is running, and leaves
  out Microsoft's own startup apps, which PoolWatch leaves alone.
- **The columns**: Name, Kind, Cost now and Starts. **Cost now** is the latest sampling window, with the same meter as
  the Services tab (None, Low, Medium, High); for a busy app it is its 30-minute average. A row reads "measuring" until
  PoolWatch has its first sample. An empty list says why.
- **The detail panel**, under the list, says what the selected row runs and when, and what it costs (a program that
  runs through a shared launcher, such as PowerShell, cannot be measured on its own, and the panel says so). The full
  command scrolls in its box and can be selected and copied. **Open file location** shows the program in File
  Explorer.
- **Don't start at sign-in** (a startup app) or **Turn this task off** (a scheduled task) is the same switch as Task
  Manager's Startup apps or Disable in Task Scheduler: nothing is deleted, and you can put it back here ("Start at
  sign-in again", "Turn this task back on") or in Active changes.
- **End it now**, in red, closes the running program until it next starts. It asks first, naming it, and cannot be
  undone: unsaved work in it is lost. It is offered only when every process of the program was matched by its own full
  path, and never for Windows' own, security or service-host processes.
- The switch and End it now wait while the list is read again. **Refresh** reads it again now.

### The tray icon

The binoculars by the clock, with a dot in the state's colour:

- **Green**: all is well, also while something grows for a while (PoolWatch keeps an eye on it).
- **Amber**: an incident is open (a growth PoolWatch is writing a report on), or something keeps memory after the
  program that grew it has stopped.
- **Red**: something needs attention: the non-paged pool past PoolWatch's alert level and still rising, or a growth
  whose cause a trace confirmed.
- **Grey binoculars**: starting, no reading yet.

When PoolWatch opens an incident for a growth, a notice by the clock says so ("Kernel pool: Open files (File) is
growing."). It names a likely cause only when the evidence is strong; otherwise it says "Open PoolWatch for details."
A pool that falls gives no notice. When PoolWatch's own check finds the token leak while the guard is off, a notice
says so; a click on it opens the window on the guard.

Point at the icon for the paged and non-paged pool; double-click it to open the window. Right-click it for **Open
PoolWatch**, **Run setup again...**, **Active changes...**, a line when a newer version is out, and the tools for
technical users: **Capture now** (a short trace that shows which program allocates the memory), **Since boot
report**, **Open incident folder**, **Export vendor bundle...**, **Copy latest incident summary**, **Mark...** and the
experiments; then **Report an issue...** and **About PoolWatch**. **Exit**, at the bottom, stops PoolWatch until you
start it again (or until you next sign in, if it starts at sign-in). Until setup was finished once, Open PoolWatch and
a double-click open setup instead.

## Active changes

![Active changes](screenshots/7-active-changes.png)

Every change PoolWatch made to your PC that is still in effect is in one window. Open it from the window's footer or
the tray menu. It has up to three groups (a group with nothing in it is left out):

- **Features you turned on**: starting at sign-in ("Start PoolWatch when you sign in"), the automatic cache clear
  ("Empty the standby cache", with its rule), the token-leak guard and the Codex shortcut ("Codex shortcut on the
  desktop"). Each has **Turn off**; the shortcut has **Remove**, which deletes it only if it is still as PoolWatch
  made it.
- **Turned off by PoolWatch**: background services (by the name Windows shows), scheduled tasks, startup apps
  ("<name> at sign-in"), and a RAMMap clear task of yours that PoolWatch replaced. Each has **Put back**. A service one
  of your own profiles turned off says which ("profile Build box", and "at start" when it applied at start). A clear
  task PoolWatch cannot put back by itself, for example one that ran with your saved password, is saved in
  `C:\ProgramData\PoolWatch\removed-tasks` for you to import in Task Scheduler. One whose RAMMap you could change
  without administrator rights, or whose RAMMap is no longer there, is put back only after a warning you accept.
- **Other changes PoolWatch made**: a service you set to its Windows default with **Put back...** on the Services tab
  ("service · set to its Windows default"), and a scheduled task or startup app that was off until you turned it on in
  PoolWatch ("scheduled task · was off", "startup app · was off"). Each has **Put back**, which sets it back to how it
  was before.

**Put all back**, at the bottom, asks once and then does them all. Some rows carry a note. The token-leak guard's row
says that its Windows setting stays until you sign out, even after Turn off. A row PoolWatch cannot put back says so,
and a row the uninstaller would leave says "Uninstalling does not put this back", with why. A service that the profile
applied at start lists, and that putting back turns on again, carries a warning: putting it back also takes it out of
that profile, and **Put all back** says so once for all of them (see [Profiles](#profiles)). When anything is listed,
the footer says "Uninstalling puts all of these back.", or, when a row says the uninstaller leaves it, "Uninstalling
puts all of these back, except where marked." Not listed: cache clears that already ran (the cache refills by itself)
and programs you ended (start them again).

## Report an issue

![Report an issue](screenshots/8-report-issue.png)

From the tray menu, from About, or from a card after PoolWatch recorded an incident:

1. Write what happened.
2. Read the preview: it is exactly the text that goes into the issue, with your user name, PC name, paths, e-mail
   and network addresses, and anything that looks like a password or key replaced.
3. Press **Open in browser**. PoolWatch copies the text and opens GitHub's issue form with only the title, PoolWatch's
   version and Windows filled in. Paste the text into "What happened", tick the **Redaction** box (the preview already
   took your names out), and press **Submit** when you are ready (you need a free GitHub account). Nothing is public
   until you do.

What goes in: PoolWatch's version and how it was installed; the Windows version, build and processor type; the amount
of memory and number of Dev Drives; the kernel pool now and over the last hour; the automatic clear and its last
result; the token-leak guard; PoolWatch's own CPU and memory use; the 8 largest pool tags; and, for an incident, its
summary and likely causes. Never: command lines, other programs' paths, window titles, the names of AI assistant
sessions (the issue may say a program ran under Claude Code or Codex), project or account names, the services list or
your environment variables.

**Copy all** and **Save as file** keep the text without opening anything. A security problem goes to the private
link in the same window, never to a public issue (see [SECURITY.md](../SECURITY.md)).

Asking an AI assistant to look at PoolWatch's data? Point it at `C:\ProgramData\PoolWatch\AGENTS.md` first: it explains
every file, and asks the assistant to change nothing and post nothing.

## The zips (install by hand)

Each release also has two zips, if you would rather install by hand:

| File | Needs | The program inside |
|---|---|---|
| `PoolWatch-v1.0.0-win-x64-selfcontained.zip` | nothing else (.NET is inside) | `PoolWatch-selfcontained.exe` |
| `PoolWatch-v1.0.0-win-x64.zip` | the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) | `PoolWatch.exe` |

PoolWatch runs only from inside Program Files, from a folder that only administrators can change, so unpack the
**whole** zip into `C:\Program Files\PoolWatch` (keep the `amd64` folder next to the program). Unpack it straight
there: a folder unpacked somewhere else, such as Downloads, and then moved keeps the permissions it had, and PoolWatch
refuses to run from it. Starting at sign-in works there too; the Codex shortcut (see
[Known issues](../README.md#3-it-recognises-known-issues)) needs the setup.

## Uninstall

Open **Settings** > **Apps** > **Installed apps**, click **...** next to **PoolWatch**, and choose **Uninstall** (or
right-click PoolWatch in the Start menu and choose **Uninstall**). The uninstaller:

1. lists the changes PoolWatch made that are still in effect, and any it cannot put back (with why), and asks: **Put
   them back (recommended)**, the default, or **Keep my changes**. Putting them back turns back on the services,
   scheduled tasks and startup apps PoolWatch turned off, puts back a clear task it replaced and its other changes, and
   turns off its own features; what it cannot put back stays as it is. It says that the token-leak guard's Windows
   setting stays until you sign out;
2. always removes the sign-in task, and the Codex shortcut unless you changed it;
3. writes what it put back, and anything it could not, to `C:\ProgramData\PoolWatch\uninstall-summary.txt`;
4. if everything was put back, says what it put back and asks whether to also delete PoolWatch's reports, logs and
   settings (`C:\ProgramData\PoolWatch`); the default is No. If something could not be put back, it says what and why.
   If you kept the changes, or something could not be put back, the folder is kept without asking, so a later
   PoolWatch can still put back what is recorded there.

Your service profiles are part of the settings in that folder, and the uninstaller does not change them. If you keep
the folder and install PoolWatch again, a profile you chose to apply at start applies again.

From a command line: `"C:\Program Files\PoolWatch\unins000.exe" /VERYSILENT` puts back everything it can without
asking and keeps the data folder; it shows nothing. Add `/KEEPCHANGES` to keep the changes (the sign-in task and the
shortcut still go), or `/DELETEDATA` to also delete the data folder, once every change was put back. `/DELETEDATA`
never asks either: started by hand, it puts back what it can and then says what it put back, or what it could not.

Installed from a zip: use **Put all back** in Active changes, exit PoolWatch (tray menu > **Exit**), then delete
`C:\Program Files\PoolWatch` and `C:\ProgramData\PoolWatch`.

## About PoolWatch

The About window (tray menu, **About PoolWatch**) lists the credits, with links; the README's
[Credits](../README.md#credits) has the same list.

![About PoolWatch, with the credits and licences](screenshots/9-about.png)
