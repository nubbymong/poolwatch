# Security

PoolWatch always runs as administrator, so a weakness in it can matter more than in an ordinary app. Thank you for
reporting one privately.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's private vulnerability reporting:
[Report a vulnerability](https://github.com/nubbymong/poolwatch/security/advisories/new) (Security tab → "Report a
vulnerability"). Only the maintainer sees the report; a fix is developed privately and published with an advisory.
PoolWatch's Report an issue window links to the same page.

Please include the PoolWatch version, your Windows build, what an attacker needs (a standard user? malware running as
you? local admin?), and the steps. Leave out personal data: do not attach an unredacted incident folder (see
"Privacy" in the README).

PoolWatch is maintained by one person in their spare time. Expect an acknowledgement within a week; fixes go into the
next release. Only the latest release is supported.

## Security model

The attacker that matters is a standard (unelevated) user, or malware running as you, trying to make the elevated
PoolWatch run their code, change the wrong thing, or leak data.

- **Starts as administrator only through its launcher, with an environment the launcher builds.** .NET reads some
  environment variables before a program's own code runs, and some of them make it load code: a profiler, a start-up
  hook, another garbage collector or compiler. A program running as you can set them for your account without
  administrator rights. So the install folder's `PoolWatch.exe` is PoolWatch's launcher, and PoolWatch itself is
  `app\PoolWatch.exe` below it, which does not ask for administrator rights. The launcher does: it starts PoolWatch with
  an environment it builds from Windows' own values: the Windows folder from the kernel; Program Files, ProgramData and
  the processor facts (such as `PROCESSOR_ARCHITECTURE`) from the machine's registry, which only an administrator can
  change; your profile folder from the machine's profile list, your AppData folders from your account's own settings
  for them (the settings every program you run takes them from), each read as written with only Windows' own values
  filled in (a setting that names any other variable leaves its folder out), and your account names, from Windows' own
  calls. Path holds Windows' own folders only. PoolWatch is started in its own `app` folder, and the folder the
  launcher was started in goes to it in that environment as well, for files named on its command line by a relative
  path. No variable of the environment the launcher was started with is passed on, none of .NET's
  start-up variables among it. The launcher is compiled to native code, so it loads no .NET runtime for those variables
  to act on; the .NET runtime compiled into it reads a few `DOTNET_` settings of its own from that environment (its
  garbage collector's among them), and none of them loads code (it is built without start-up hooks, a loadable garbage
  collector or EventPipe, and has no profiler or compiler to load). Because the launcher has the name and the place PoolWatch itself
  had before 1.0, everything that starts the install folder's `PoolWatch.exe` starts the launcher: the Start menu entry,
  the logon task (one made by 1.0.0-beta.1 included), Explorer, **Run as administrator** on it, an administrator
  console, the setup's **Start PoolWatch now**, its stop before an upgrade and the uninstaller's steps. Both zips also
  carry the launcher under the name the self-contained zip's program had before 1.0, `PoolWatch-selfcontained.exe`,
  and the setup puts it there where a file of that name is, so a logon task or a shortcut that a copy of either zip made
  before 1.0 starts the launcher too once a newer zip or the setup is put over that copy. A file under either name
  counts as the launcher only when it has the same version as `app\PoolWatch.exe`: PoolWatch never hands a start to,
  registers a logon task for, or calls fine a task that starts, an older program left under one of those names, and
  the setup and the uninstaller never start one (they read both files' product versions themselves). Where there is no
  such launcher (a PoolWatch installed before the launcher, or an older program left under its name), the setup stops
  PoolWatch by setting the exit signal of a PoolWatch installed before the launcher itself, again each quarter of a
  second while it waits, and starts no PoolWatch, and the uninstaller starts none and removes nothing. `app\PoolWatch.exe` opened by itself without administrator rights hands its start to the launcher, so the
  administrator prompt is the launcher's; started as administrator any other way, it starts again through the
  launcher and does no work of its own: in a console mode it waits for that PoolWatch and passes on its output and exit
  code, otherwise it ends at once. A PoolWatch the launcher started still refuses to run if its environment holds one of
  those variables. Its build turns .NET's start-up hooks off as well. The token-leak test's helper, which PoolWatch
  starts as `app\PoolWatch.exe --token-leak-test`, does not go through the launcher: it inherits the environment of the
  PoolWatch that starts it, the one the launcher built. What `app\PoolWatch.exe` started as administrator directly still
  gets is under Known limitations.
- **Runs only from an admin-only place.** An elevated PoolWatch starts only from under `%ProgramFiles%` or
  `%ProgramFiles(x86)%`, and only where no one but an administrator can change it: otherwise someone could
  replace the exe, its launcher or a DLL it loads. Read only, it judges the access rules (the DACL and the owner)
  of every folder from its own up to Program Files, the rules its folder gives the files made in it, the rules of
  `app\PoolWatch.exe` and of its launcher (`PoolWatch.exe`, and `PoolWatch-selfcontained.exe` when that is there too)
  themselves, the own rules of every other file and folder in `app` and below it (PoolWatch's DLLs, in `app\amd64\`,
  among them), and every file below `app` with the folders it is in. When a folder on the way is a link (a junction),
  the folder it really leads to must be under Program Files too, and every folder from there up to Program Files
  is judged the same way. Rules that cannot be read count as changeable, and so does a program that is not there,
  a program or anything else in `app` that is itself a link, a folder below `app` whose files cannot be listed,
  and a folder that holds far more than PoolWatch's own files (over 5,000, or folders more than eight levels
  deep). Anywhere else (Desktop, Downloads, OneDrive) it refuses outright. The launcher checks before it starts
  anything: it finds its own folder as the kernel names its file (a short 8.3 spelling, a junction or a `subst`
  drive resolved), it starts PoolWatch only from a folder inside Program Files (as the machine's registry names it),
  only once it has administrator rights itself, and only when the same access rules, compiled into it from the same
  source, find that no one but an administrator can change the launcher, `app\PoolWatch.exe` or a folder either is in
  (a link on the way followed, as above). Of the accounts the UAC settings below make count as administrators, the
  launcher counts only the one it runs as, and only when every program of that account runs as administrator: its token
  holds Administrators and UAC did not split it, and either it is a sign-in in person (its token holds the INTERACTIVE
  group, which UAC would have split for an administrator unless UAC is off or the account is the built-in Administrator
  without Admin Approval Mode), or, for a batch, service or network sign-in, whose token can hold Administrators unsplit
  even for an account whose sign-in in person UAC splits (so that token alone cannot tell), UAC's settings say UAC is
  off or that the account is this PC's built-in Administrator with its Admin Approval Mode off. So where those settings
  make other accounts count too, it can refuse a folder PoolWatch itself would accept (it fails closed). Windows loads
  the DLLs the launcher imports from System32 only (its build sets `/DEPENDENTLOADFLAG:0x800`, which CI checks). So a
  copy of the signed launcher put above some other program named `PoolWatch.exe`, in a folder you can write to or in one
  inside Program Files that someone who is not an administrator can change, starts nothing, and a DLL put next to such a
  copy is not loaded. The setup installs it to `%ProgramFiles%\PoolWatch` only. Three switches skip PoolWatch's own
  check, whoever passes them: `--allow-any-location` (CI and development), `--ui-smoke` (CI's UI test) and the
  token-leak test's hidden helper (`--token-leak-test`, which a PoolWatch that passed the check starts by its own path).
  None is for a copy you use, and each still needs an administrator start: your Yes on the prompt. Approve a PoolWatch
  administrator prompt only when you started PoolWatch yourself: **Show more details** on the prompt shows the program's
  location, which should be `C:\Program Files\PoolWatch\PoolWatch.exe`.
- **Who counts as someone who is not an administrator** in that check, in putting back a clear task, and in reading
  the record of a folder PoolWatch uses in place of its data folder (both below): every account and group except
  SYSTEM, Administrators, TrustedInstaller and "Local account and member of Administrators group", and except the
  accounts and groups whose every program runs with administrator rights by the PC's User Account Control settings.
  With UAC off (EnableLUA 0), these members of the Administrators group: each of its direct members (an account or
  a group), the account PoolWatch runs as when its token holds Administrators, and any other local, domain or
  Microsoft Entra ID account or group that Windows' Authz API (`AuthzInitializeContextFromSid`), asked about its
  SID, finds to be a member through a group. That API is documented for user and computer accounts, and asks a
  domain controller about a domain account; a SID it gives no answer for (no domain controller answers, say) is not
  counted, so PoolWatch refuses rather than trusts it. With Admin Approval Mode off for the built-in Administrator
  (FilterAdministratorToken 0, Windows' default), that account (the RID 500 account of this PC's account domain, so
  only when that domain can be read; otherwise no account counts this way): while the group lists it as a direct
  member, and when PoolWatch runs as it with Administrators in its token. On client Windows such an account owns
  what it makes, setup's folder included, so without this exception PoolWatch could never start there. These are
  decided from the PC's settings and its Administrators group, not from the token of the moment, with two
  exceptions, each through PoolWatch's own token: with UAC off, the account PoolWatch runs as counts when its token
  holds Administrators; and with Admin Approval Mode off for the built-in Administrator, that account counts when
  PoolWatch runs as it with Administrators in its token. So two kinds of account count when PoolWatch runs as them,
  and not when another account runs PoolWatch: with UAC off, an administrator that the group's members, as read, do
  not list (they could not be read, or it is a member only through a group) and that the Authz API does not find to
  be a member (it gives no answer, say); with UAC on and Admin Approval Mode off for the built-in Administrator,
  that account when the group's members cannot be read or do not list it. With UAC on, Windows is asked about no
  account's groups. Everything else counts: any other user's own SID (the signed-in account's, another user's, and
  an administrator's whose programs without administrator rights hold it); every other group (Users, Everyone, a
  local or domain group, the service accounts, app containers); and the groups UAC treats as elevated, whose
  members without UAC hold them in every program. A program that such an administrator deliberately starts with
  fewer rights (a SAFER "Basic User" level, say) still holds that account's SID: PoolWatch treats that as the
  administrator's own choice, as Windows does.

  A folder or file counts as changeable when a rule lets one of them add, write, append, delete, change the
  permissions or take ownership, when its owner is one of them, when it has no access rules at all, or when it holds a
  kind of rule PoolWatch does not know. A deny rule gives nothing, and neither does a rule for CREATOR OWNER or OWNER
  RIGHTS (they stand for the owner, who is judged as such). A rule for CREATOR GROUP gives nothing on the folder or
  file itself, but where it reaches the files made in PoolWatch's own folder it counts like any other group's: it
  gives them to the maker's primary group, which is not an administrators' group.
- **Native DLLs** that PoolWatch's program loads ship below it, in `app\amd64\` (never unpacked to `%TEMP%`), and are
  judged with every other file below `app` as above; system DLLs are loaded from System32 only.
- **Its data folder** `%ProgramData%\PoolWatch` is admin-only: Administrators and SYSTEM full control, read for your
  account, inheritance off. It is opened without following links on every start; a link, a file, or a folder someone
  else owns or can change (a rule of a kind PoolWatch cannot read counts as one that lets someone else change it)
  there is renamed aside (`PoolWatch.untrusted-<time>`, the link itself, never its target). So is a folder whose rules
  shut Administrators out when PoolWatch, reading its owner and rules with the backup privilege, finds a foreign owner
  or writer (the note names them), or when not even an open with that privilege can read its owner (a folder whose
  delete is pending, say). What was moved aside is kept for you to inspect and delete. Then the folder of its own that
  PoolWatch kept its data in comes back to the usual name when there is one (below); otherwise a fresh folder is
  created with its permissions set at creation. A folder there that may hold PoolWatch's data and that PoolWatch cannot
  read now is never moved aside: when the look at it fails, at three tries a quarter of a second apart (it cannot be
  opened for a reason other than being denied; or its open is denied and the backup privilege cannot be turned on to
  read it; or its permissions cannot be read through the open handle; or the look itself fails: nothing is known
  about it then), or when its rules shut Administrators out while the backup privilege reads it as an administrator's
  alone, PoolWatch stops with a message that names it and uses no other data folder meanwhile, so the data in it is
  neither set aside nor split; it starts as usual once that folder can be read again. On a PC where administrators do
  not hold the backup privilege (a policy took it from them, say), that includes a folder another account made there
  that shuts Administrators out: PoolWatch cannot tell whose it is, so it stops rather than set it aside. A folder only
  administrators can change whose permissions differ from PoolWatch's gets PoolWatch's once; when its owner cannot be
  set (its rules do not let administrators change it, say), PoolWatch applies its rules alone. When not even those can
  be applied, or they still differ after PoolWatch applied them, it is used as it is, unless its rules would let an
  account other than Administrators, SYSTEM and TrustedInstaller change the files PoolWatch makes in it (CREATOR OWNER
  and OWNER RIGHTS stand for their owner, an administrator), or administrators may not make files in it: then
  PoolWatch stops with a message that names it, moves that folder nowhere and uses no other data folder. When it
  cannot be made safe at all (a process holds that entry open, or makes it again after every move), PoolWatch does
  not use it and does not exit: it keeps its data in a folder of its own next to it,
  `PoolWatch.safe-<time>-<random>`, created with its permissions set at creation and checked like the usual one, and
  says so in its log and on the timeline. It records that folder's name in `HKLM\SOFTWARE\PoolWatch` (value
  `DataFolder`; PoolWatch makes that key owned by Administrators, with rules for administrators and SYSTEM only), and
  reads the record only while that key's owner and rules are an administrator's alone; a later start judges that one
  folder and no other, so folders someone else makes with names like it are never even looked at. When the name
  cannot be recorded, the note says so, and that what PoolWatch keeps in that folder does not come back to the usual
  folder by itself. Settings and records in the usual folder are not used meanwhile. While that folder is admin-only,
  it holds PoolWatch's data: a later start that finds the usual name free, or the usual folder safe to use where it
  stands (an administrator made it so, say), moves that folder back to the usual name and deletes the record; a usual
  folder that is there is first renamed aside (`PoolWatch.untrusted-<time>`) and kept, with what was in it. When a
  move fails, that start uses the folder of its own in place, and a later start tries again; when that folder cannot
  be used as it is (on the same terms as the usual folder), the start stops and makes no new one in its place. When
  that folder is gone or no longer admin-only (another account owns it or may change it, it is a link or a file, or
  its rules shut Administrators out and who owns it cannot be read: an open with the backup privilege was denied too),
  PoolWatch does not use it again: a start that uses the usual folder deletes the record (when that fails, a later
  start tries again), and a start that cannot makes another folder of its own and records that one in its place. A
  start that uses the usual folder also deletes a record that names no folder of PoolWatch's own. While that folder is
  there and PoolWatch cannot read it now, PoolWatch keeps the record and starts on no other data folder: when its
  rules shut Administrators out while the backup privilege reads it as an administrator's alone, and when the look at
  it fails at three tries a quarter of a second apart (for the same reasons as for the usual folder above: nothing is
  known about it then). It stops with a message that names that folder and why, so its data is never split between
  two folders, and starts as usual once that folder can be read again or it is deleted.
  Otherwise the start's notes name each recorded folder of its own that it does not use, and why; anything PoolWatch
  kept in one that is still there stays there. A note about a folder moved aside or no longer used also says when
  files named like PoolWatch's lists of changes (`applied-changes.json`, `service-changes.json`) stay in it:
  PoolWatch does not read them there, so neither Undo nor the uninstaller puts back what only they list. PoolWatch
  does not merge them into the list it uses, as it cannot tell who changed them while it did not use that folder. A
  start that stops says in its message what it did before (a folder moved aside, say).

  Only a PoolWatch start given the proof that its thread holds PoolWatch's one-instance lock moves a data folder,
  makes a folder of its own, or writes or deletes the record. Only the steps that take that lock make the proof, and it
  holds only on the thread that took the lock, until that step ends: the tray app and a headless run, which run only
  once they have taken the lock, and the uninstaller's cleanup once it has stopped PoolWatch and taken it. Any other
  PoolWatch process (the token-leak check's helper, `--mark`, `--changes-dump` and the other command-line steps) uses
  the folder in use as it stands: the recorded folder of its own while it holds the data, otherwise the usual folder;
  either only when it is safe to use as it stands, and, when its rules are not exactly PoolWatch's, only while they
  give no account but the files' own owner, Administrators, SYSTEM and TrustedInstaller a right to change the files
  made in it and administrators may make files in it. The only folder it makes is the usual one, with its permissions
  set at creation, when nothing is there and PoolWatch's one-instance lock does not exist (no PoolWatch is running; a
  lock it is not allowed to open counts as existing). Otherwise it stops with a message and writes nothing there. So
  no PoolWatch process moves the folder a running tray app or headless run uses, or makes it again while one runs.
  Once a process has checked the data folder, it writes only in that very folder. It holds that folder open from then
  on, sharing no delete, so while it runs Windows lets no one rename or delete it, and a write that passed its check
  lands in it. Each write is checked just before it too: none is allowed while no folder is there (renamed or deleted
  meanwhile, or a link or a file in its place), while the folder there is another one (made or moved there later, by
  anyone: PoolWatch tells them apart by their file ID), or while which folder is there cannot be read. While another
  program has the folder open in a way that keeps that hold out, each write tries the hold again before that check, and
  until it is held the check just before each write is all that holds it. So no write makes it again with the
  permissions `%ProgramData%` hands down, and a process that is still running after the tray app has exited (the
  token-leak check's helper, say) writes nothing in a folder that a later start moved there or made.
- **Every write and delete is checked against links.** In the data folder (or an explicit headless `--out` folder)
  every path goes through one check (`PathGuard.CheckReal`): never through a link, never next to the exe, never on a
  network drive or a ReFS volume. The few writes and deletes elsewhere check for links themselves: the desktop
  shortcut and a prompt saved from an AI-agent window are opened without following links and their final path checked
  (`NoFollow`), and an older version's helper files in Program Files are checked folder by folder
  (`PathGuard.FirstLinkBelow`).
- **What it reads from places you can write to** is bounded, and links are skipped rather than followed: Startup-folder
  shortcuts (its own parser, not the shell), Claude Code transcripts (size and file budgets, links skipped),
  `pooltag.txt` (read in one bounded pass of at most 8 MB; a path that is itself a link is skipped), symbol files
  only from admin-only local folders, and an AI agent's answer file (below). Path settings must be plain local drive
  paths. The AI Code Conductor version on the timeline (`cache\versions.txt`) is read only from a running exe that is
  its own: one installed for everyone under Program Files that only administrators can change, or one signed by the
  same publisher as PoolWatch through the same certificate authority, held open while it is checked and read. Any
  other program called `AI Code Conductor.exe` is ignored (its signature is checked once for as long as each read
  meets that file unchanged, and not at all by an unsigned build of PoolWatch), and only a well-formed version is kept.
- **The AI agent's files** (Services tab, **Decide with your AI agent** and **Import answer**). The prompt
  (`poolwatch-services.md`) is written only where you pick in a Save dialog: into a new file, or over the file you
  agreed to replace only when that is a plain file with one name, never through a link, and never when a folder on the
  way is a link to another place. The answer (`poolwatch-services-answer.json`) is read only from the file you pick,
  at most 64 KB, never from a link in its own place or through a folder on the way that is a link; hard links are not
  refused on read. The answer is untrusted input: it must be exactly PoolWatch's format (version 1: what to turn off,
  what to keep, and a reason per name, as valid UTF-8 text), or the whole answer is refused, with why; reading it never
  throws. A name in it only ever picks an item from the list PoolWatch reads from the live PC again at that moment;
  nothing in it is run or opened, and its words are shown only as plain text. The prompt holds names that other
  programs choose (and any account that can register a scheduled task); they are written only as names in its tables,
  and the prompt's own instructions name only the products in PoolWatch's catalogue. Its redaction is best-effort, as
  an export's is: it reads each word whole, or not at all.
- **Its log goes on in a new file when its appends stay refused.** Your account can read `poolwatch.log`, so a program
  running as you can open it. PoolWatch opens it for each line with append-only access, so Windows writes the line at
  the true end of the file and two PoolWatch processes never write over each other's lines; only the PoolWatch that
  holds the one-instance mutex rolls it over at 2 MB, and only while it holds it. An append that another program refuses
  — it has the log open sharing reading only, or it locks the end of the file where the next line goes — or whose open
  would have to wait for another program (PoolWatch gives such an open up at once rather than wait) is tried again three
  times over about a tenth of a second, by which time a program that reads or copies the log in one go has let go. When
  it is still refused, that line and every later one in that run go to a new file next to the log,
  `poolwatch-<date>-<time>-<process id>-<n>.log`, whose first line says why. PoolWatch keeps that file open, sharing
  nothing, while it writes there, so no program can open it and refuse the append the same way; it lets go only to roll
  the file over at 2 MB, and a program that opens it in that moment, or makes the next one first and keeps it open, in a
  folder it may write, refuses its appends while it has it open: the run then moves on to another new file, with no line
  lost (the file is kept again when the program lets go within the retries; a file deleted in that moment is made again,
  so no later line is lost). A program that may write that folder can still, as for the log itself, put text of its own
  first in the next file, or make that name something PoolWatch cannot write, and then that run's later lines are lost;
  only administrators and SYSTEM may write the data folder (a run given `--out` logs in that folder instead). A new file
  can be read once that run has stopped (its `.1` once it has rolled over). The newest five such files are kept, each
  with its `.1`; one of another PoolWatch that is still running is not deleted (nor, while a process other than the
  pruning one has its process id, one of a PoolWatch that has stopped). While a program keeps the log open sharing
  writing but not deleting, or keeps the log's or a new file's `.1` open in a way that refuses the rename, that file is
  not renamed but can still be appended to: it is not rolled over and grows until the program lets go (one that also
  refuses the appends moves the run on, as above). The log can still be read and copied while PoolWatch runs.
- **Records it acts on are checked again first.** The change records (`applied-changes.json`, `service-changes.json`,
  and the token-leak guard's `cache\token-guard-setting.json`, the value it replaced, one per sign-in with the sign-in it belongs to) are
  admin-only, but each one is read as untrusted, size-bounded and validated, and checked against the live system right
  before PoolWatch puts a change back or the uninstaller acts on it (the guard's value only in the same sign-in, and only
  while the live setting is still 0). The same holds for its cached files
  (`cache\environment.json`,
  `cache\update-check.json`): a file with anything out of place is ignored. The service profiles in `settings.json`
  are read as untrusted too, since one can be applied at start without a click: at most 50 profiles of at most 200
  services each, a profile not in the form PoolWatch writes is left out, an entry for a protected service or one
  PoolWatch does not recognise is ignored, and a choice of profile or delay that cannot be read applies nothing. Each
  is said in the log.
- **Programs and pages it opens.** Programs are started by their full path, never through a shell association or the
  current folder. A web page is opened only when you click, only for an `https://` address PoolWatch knows (its
  repository, the credits, a release page, the private advisory page, and three reference pages setup cites, each a
  fixed address matched whole) or a new-issue address it built itself, and only through `explorer.exe`, so your
  browser runs as you, never as administrator.
- **The logon task** (optional "Start PoolWatch when you sign in") is only created for a copy installed under
  `%ProgramFiles%`, and starts its launcher (the install folder's `PoolWatch.exe --tray`). A task made by an earlier
  version names that same path, so it starts the launcher too, unchanged. At every start PoolWatch checks an existing
  `\PoolWatch` task, which a program of yours without administrator rights can also make: if any of its actions starts
  something outside Program Files, names its program through an environment variable (which Task Scheduler fills in
  from your account's environment, which a program you run can change), names a program with PoolWatch's name that is
  not a launcher (a file that is not there, so the task starts nothing; PoolWatch's program `app\PoolWatch.exe` itself;
  an older PoolWatch elsewhere that has no launcher; or a file under a launcher's name that is not of the same version
  as the PoolWatch below it), or is not a program it can check; when it is otherwise not exactly the task PoolWatch
  makes (one action, this installed PoolWatch's launcher by its literal path, `--tray` and no start folder, run as you,
  signed in interactively, with highest privileges); or when the task is there but cannot be read, it says what is
  wrong in its log and as a setup finding, and the tray app offers to replace it with PoolWatch's own task for this
  PoolWatch's launcher, or remove it. Replacing it keeps the task, and its sign-in trigger, on or off as they were
  (either whose state could not be read is turned on, and a task with no sign-in trigger gets PoolWatch's, turned off;
  the question says so). Nothing is changed without your answer, and a headless run only says so. Settings, the setup
  guide and Active changes show the task as starting PoolWatch only when it is on, a sign-in trigger of it is on, this
  check finds it fine apart from its run level, and it runs with highest privileges where the account needs them (when
  UAC splits the account's token, a task without them gets the token without administrator rights, and the launcher
  starts only as administrator; they are needed unless every program of the account runs as administrator, judged as the
  launcher judges its own account, so a PoolWatch run from a batch or service sign-in goes by UAC's settings, not by its
  own token, which can be unsplit even where UAC splits the account's sign-in in person); otherwise they say which of
  these it is, or, when PoolWatch could not read all of the task's settings, that it could not (Settings then offers to
  set the task up again, Active changes to remove it). Only Task Scheduler's "no such task" counts as no task. The
  PoolWatch the task starts is then judged by the full check above.
- **The exit signal and the one-instance name.** The setup and the uninstaller ask PoolWatch to exit through a named
  event, and one PoolWatch at a time runs through a named mutex; a tray app whose token-leak guard shows its notices
  also holds a mutex of its own, named for its Windows session, which a token-leak test helper left running in the same
  session looks for before it asks for a click (the setting the guard looks after is each session's own, so a tray app
  in another session does not count). All three are in PoolWatch's own private namespace, whose
  boundary holds the Administrators group and the high integrity level, so only an elevated administrator or SYSTEM can
  create it, and whose permissions let only Administrators and SYSTEM open it: a program without administrator rights
  can neither make PoolWatch exit nor create or open any of them. The namespace is owned by the Administrators group,
  never by the account that made it, and carries the high integrity label, so no unelevated program gets in through an
  owner's rights either. An event that already exists there is not used. If
  PoolWatch cannot open its namespace or take its mutex, it does not start, and says why — in a message for the tray
  app, on the console for a run started from one, and in its log when it can open that (a run without administrator
  rights, the case the namespace refuses, usually cannot) — never "already running".
- **One network request, optional: the update check.** About once a day, while "Tell me when a new version is out" is
  on (it is by default), the tray app sends one HTTPS GET for its own repository's latest release, to
  `https://api.github.com/repositories/1435312349/releases/latest`: the repository by its number, which stays with it
  through a rename or a transfer and is never given to another one. The first check waits until setup has been
  completed once. The request carries no sign-in, no cookies and nothing about the PC: only the User-Agent
  `PoolWatch/<version>` and an `Accept` header asking for GitHub's JSON (`application/vnd.github+json`). GitHub sees
  the IP address it comes from, and it goes through the PC's proxy when one is set. Redirects are not followed,
  the request gives up after 15 seconds, and an answer over 1 MiB is refused (unread when its length says so). Only a
  stable release is offered (a pre-release or a draft never is), and only when its page is that release's own page under
  `https://github.com/nubbymong/poolwatch/releases/tag/`. The check never downloads or runs anything: the notice only
  opens that page in your browser when you click. There is no telemetry, crash upload or symbol server.
- **Known-issue packs** (`knowledge/*.json`) are built into PoolWatch and change only with a release; PoolWatch does not
  download them. Each is still read as untrusted (at most 32 KB, strict JSON, every field checked, links only to
  github.com, learn.microsoft.com and support.microsoft.com). A pack holds text, pool tags, program names, numbers and
  links: nothing in it is run, and it cannot make PoolWatch suggest or change anything.

## What PoolWatch changes on your PC

PoolWatch watches and reads. This is every elevated or system-changing action it can take; everything else only reads,
or writes its own data folder. The changes that last are listed under **Active changes**, each of which can be turned
off or put back (the few that cannot fully come back say so there). Uninstalling puts them all back by default, except
those Active changes marks "Uninstalling does not put this back" (a change PoolWatch cannot put back, and a clear task
of yours that needs your yes first): the uninstaller lists those, with why, and leaves them as they are.

| What | When | How it is confirmed |
|---|---|---|
| Starts its own real-time kernel ETW session (process starts and exits) and restarts it if it is stopped | Always, while running | Running PoolWatch |
| Records a WPR trace (`wpr.exe`, instance "PoolWatch": kernel pool events, optionally Kernel-File and CPU sampling), at most 5 minutes; may end its own stuck `wpr.exe`, never someone else's recording | **Capture now** (tray menu), or on a pool alert when automatic captures are on (off by default; at most one an hour, never while the PC is saturated or in use); a headless run with `--capture-now` | The menu item, the setting, or the command line |
| Empties the standby list (`NtSetSystemInformation`, after enabling SeProfileSingleProcessPrivilege in its own token); no file or process is touched | **Clear now**, the automatic clear (off until you turn it on in setup or Settings), the "lag before / after a clear" experiment; a headless run with `--clear-cache` | The button, the setting, starting the experiment, or the command line |
| Runs a full clear right after an automatic light clear (one that empties only the low-priority part of the standby list) that freed little | Only with the automatic clear on, in "auto" mode, once, within the 30-minute spacing, and only when no full clear was measured yet or the last one freed real pool | The automatic-clear setting |
| Deletes **your own clear task**: a task in the Task Scheduler root that runs Sysinternals RAMMap with an `-E` option (RAMMap itself is never touched; PoolWatch does not ship it). Every definition is saved and read back before the first task is deleted, so it can be put back | Turning the automatic clear on in Settings; **Next** on setup's Standby cache step with a rule chosen, exactly when the step shows the note that a rule replaces your task (decided from the same scan; the chosen rule is written right after the task is removed); **Remove my clear task** (tray menu); or `--enable-auto-clear` | A Yes in a dialog that shows the task, or your Next with setup's note on screen (`--yes` / `--no` on the command line) |
| Puts back a clear task of yours that PoolWatch removed, from its saved definition, with the account it ran as; PoolWatch's automatic clear is turned off first, so the two never both run | **Put back** in Active changes; the uninstaller's put-back | Re-checked first: only RAMMap memory-clear programs given by their full path with no environment variable, the Task Scheduler root (a task path with `/` is refused), no task of that name now, at most 64 KB. Every part PoolWatch checks (the programs, their options, the start folder and the account) must be plain text, with no comment, CDATA, processing instruction or element inside, and no program, option or start folder may hold a line break or another control character (a tab may separate the options). Never one that ran with a saved password or carries its own access rules (its definition is saved in `removed-tasks\` for you to import instead). One whose RAMMap a standard user could change (the folders' or the file's permissions, judged as for PoolWatch itself in the security model), whose permissions cannot be read, that is not there now or that is itself a link, only after a warning you accept in the app, which names every such program; the uninstaller never puts that one back (saved for import) |
| Removes what an older PoolWatch's Clear cache setup installed: the two `\PoolWatch\Clear cache (...)` tasks (only if they run the old helper), the empty `\PoolWatch\` task folder, and three helper files in `%ProgramFiles%\PoolWatch` | Automatically, once, at the first elevated start that finds them (a line in the findings log, `findings.jsonl`); **Remove old clear-cache tasks** (tray menu) or `--remove-clear-cache` as the fallback | Automatic (it never touches anything else) |
| Changes Windows services: sets one to Disabled and stops it, or to Manual without stopping it; puts back the saved start type and starts it again if it was running (never one put back to Disabled: that one is stopped if it runs); when PoolWatch does not know whether it was, as for a record of v1.0.0-beta.1 that says it was not, it is not started and, unless it is put back to Disabled or is running by then, the result says so, in the uninstaller's summary too; sets the Windows default start type | **Services** tab (**Turn off N services**; **Put back...**: put back the selected service, set it to its Windows default, or put all back), **Apply** in the AI agent's review (below), **Put back** and **Put all back** in Active changes, the uninstaller, `--service-off`, `--service-restore` | A dialog listing the services (and a second one for services of other apps PoolWatch does not recognise); the original is saved first, and a service whose original cannot be read or saved is not changed. It never stops or changes a service you did not choose: when a service you did not choose depends on one you turn off and has not stopped (it runs, or is paused, starting or stopping), that one is set to Disabled but not stopped (it keeps running, or stays paused if it was paused), and the result names each such service with its state. When the services cannot be read, nothing is changed or put back, and the result says so. A service already Disabled is never set to Manual, and Turn off leaves a service that is off already as it is and records nothing for it (unless PoolWatch changed it before and it was changed again since, so that Put back refuses it: Turn off then records it again, so Put back works). Security, Windows Update, input and audio, Hyper-V / WSL and core services are never offered. Putting back (on the Services tab or in Active changes, one or all) or setting to the Windows default a service that the profile applied at start lists, when that leaves it on, says that the profile would turn it off again and, once you confirm, takes out of that profile the services the question named (one turned off after the question was asked stays in it, and the result says so); a put-back that leaves the service off (that of a Windows-default change, or an original of Disabled) never changes the profile. `--service-restore` and the uninstaller never change profiles (see [Known limitations](#known-limitations)) |
| Turns off the services of your service profile chosen to apply at start that are on (sets each to Disabled and stops it, or to Manual without stopping it, as Turn off does; one that a service the profile does not list depends on, while that service has not stopped, is set to Disabled but not stopped, and the log says so) | Each time the tray app starts, after the 1 to 15 minutes you chose; only from PoolWatch's own `settings.json` (never with `--config`), only when PoolWatch runs as administrator, and never in CI's UI test | No click at start: you opted in with **Apply this profile when PoolWatch starts** when you saved the profile. Each service goes through Turn off's gate and the live gate, and its original is recorded before it is changed, so it can be put back (nothing is changed while the record cannot be read). Never a protected service or one PoolWatch does not recognise. One already as Turn off leaves it (Disabled and stopped, or Manual for a service PoolWatch turns off to Manual) is left alone; any other service of the profile found at Manual, for example after a Windows update, is set to Disabled and stopped if it runs (unless a service the profile does not list depends on it and has not stopped, as above), with no click. It reads the profile only when its turn comes, after any other service change, and a change made to the profile since PoolWatch started never makes it turn off more: a service added or another profile chosen waits for the next start. A put-back takes a service out of the profile in its own turn, so this run never turns it off again. A notice says what it turned off (a click on it opens Active changes), and Active changes lists each, with Put back |
| Enables or disables a non-Microsoft scheduled task, or a startup app (the `StartupApproved` switch in HKCU / HKLM that Task Manager uses) | On the **Tasks and startup apps** tab: **Don't start at sign-in** / **Start at sign-in again** for a startup app, **Turn this task off** / **Turn this task back on** for a task; **Apply** in the AI agent's review (below); **Put back** in Active changes; the uninstaller | Your click on a selected row; recorded before the switch is made, so it can be put back (nothing is switched when the record cannot be saved, and a switch that fails takes its record back), one switch at a time. Microsoft's own startup apps and tasks are refused |
| Ends a process | **End it now** on the Tasks and startup apps tab: an app busy in the background, or the running program of a startup app or a task, offered only when every process of it was matched by its own full path | One question naming it, with its processes (PID and start time), its program and, for a busy app, its command line, and saying it cannot be undone; each process is ended through a handle checked against its start time. Never Windows' own, security or service-host processes: if any process of it is one, nothing of it is ended |
| Turns off the services, startup apps and scheduled tasks an AI agent's answer names | **Apply** in the review that **Import answer** (Services tab) shows | The review lists every change first, and every refusal with its reason (protected services, anything not on this PC now, Microsoft's own startup apps and tasks). Only items the answer matched on the live PC are changed, each through the same path as the Services and Tasks tabs, recorded first; services of other apps PoolWatch does not recognise are asked about again by name. Each can be put back from Active changes, and the uninstaller puts them back |
| Writes the AI agent's prompt, `poolwatch-services.md`, outside the data folder | **Save the prompt...** in **Decide with your AI agent** (Services tab) | The Save dialog: only where you pick, never through a link (security model). **Copy to clipboard** writes no file |
| Sets the live `ForegroundLockTimeout` to 0, at most until you sign out (never the registry; turning the guard off, in PoolWatch or by uninstalling, sets back the value the guard replaced in this sign-in, which it keeps with the sign-in in the admin-only data folder, when Windows allows it; when Windows does not, it can stay until you sign out), so any app may take the focus: the token-leak guard, for the leak documented on Windows 11 24H2 and 25H2 builds | When the guard is on (off until you turn it on): at once when you turn it on in setup's Fixes step (with Next), a card in the Simple view or Settings, at each start of the tray app (or of a headless run with `--token-guard`) unless two leak tests in a row on this build were clean, and again, by itself, if something resets it later in the sign-in, at the guard's next check when Windows allows it then (it opens no window for that: while it cannot, the tray app's icon shows the sign-in not protected) | Your switch and Next in setup, or your click on the card or in Settings; when Windows refuses that from the background, a notice asks for one click. Later in a sign-in, your click on the tray icon, or on the line at the top of its menu, while the icon shows the sign-in not protected. Setup and the card offer it only on evidence of the leak on this PC |
| Runs the token-leak test: a hidden PoolWatch helper starts 600 `cmd.exe /d /c rem` children and measures (usually about 20 seconds); no setting is changed. The helper runs in a job object of its own, gets only Windows' own folders as its environment (nothing from your account's environment), and writes its result into a new file that PoolWatch makes for that run in the data folder's `cache` folder and holds open, sharing it only for writing, so no other program can open it to read, rename or delete it; PoolWatch reads it through its own handle, and Windows deletes it once the last handle on it closes (a program that opens it only to read its attributes can keep its name listed, unreadable, until it closes that handle). The result is saved in the data folder's `cache\token-guard.json`, also when a headless run writes elsewhere with `--out` | Setup on Windows 11 24H2 and later when no result is saved for this build; the token-leak watch, by itself, with the guard off, once setup was completed and Windows was updated since the last result (or none was saved): in the window, and every 30 minutes from the tray; and the guard at start (the tray app, or `--token-guard`) when it needs a test | Opening setup, the watch (check only: when it finds the leak, a notice says so and a click opens the guard's card or its switch in Settings; nothing is turned on), or the guard's setting |
| Applies the automatic clear, the token-leak guard and update notices at once (to the running PoolWatch and `settings.json`) | Setup (Next on its Standby cache and Fixes steps; update notices on its last step), a card, Settings' Save, **Turn off** in Active changes (update notices: setup and Settings only) | Your click |
| Creates (replacing any task of that name) or deletes the `\PoolWatch` logon task (highest privileges; it starts the launcher, the install folder's `PoolWatch.exe --tray`); replaces an existing one that is not exactly PoolWatch's own, or that cannot be read, with its own for this PoolWatch's launcher, or deletes it | **Start when you sign in** (setup's Fixes step, with Next), **Start PoolWatch when I sign in** (Settings), **Turn off** in Active changes (**Remove** for a task that does not start PoolWatch), the uninstaller (which removes it also when it cannot be read); the check at start, which asks | Your click, or your answer to the check's question (a headless run only says so) |
| Creates the desktop shortcut "ChatGPT (no shader cache)", the Codex shortcut: built in the data folder, checked with PoolWatch's own parser, written only as a new file and never through a link (the Desktop and each folder above it up to your profile must be real folders, except OneDrive's); deleted by **Remove** in Active changes and by the uninstaller only if it is still exactly what PoolWatch wrote | Setup's Fixes step (**Yes**, then Next) when the Codex app is installed, or **Create shortcut** in Settings when it is not on the desktop | Your Yes and Next, or your click. The launcher script it runs, `chatgpt-no-shader-cache.ps1`, runs as you, not as administrator, through `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File`: PowerShell does not check the script's signature then (it is signed in releases), so what protects it is the admin-only install folder. It starts the app through Windows' app activation and changes nothing on the PC |
| Opens a web page in your browser: the GitHub issue form (Report an issue), the links in About, the pages setup cites, a release page (update notice), the private advisory page | Your click | Only the addresses described in the security model, through `explorer.exe`. Report an issue's address holds only the title, version and Windows; the rest goes through your clipboard, and nothing is public until you submit the form |
| Asks GitHub once a day whether a newer release is out | While the tray app runs, after setup was first completed, and while "Tell me when a new version is out" is on (on by default) | The setting, on setup's last step and in Settings (see the security model) |
| Looks at the PC for setup (reads only): Windows version values and the Insider channel in the registry, your Store package list, a few fixed paths for AI tools, Task Scheduler, each Dev Drive's trusted flag, and Microsoft Defender's real-time protection and Dev Drive performance mode through WMI (never Defender's exclusion list). The result is kept in `cache\environment.json` (no user names, no paths except drive roots) | The first start, **Run setup again...**, when the window opens with facts more than a day old or from another Windows build, from the tray's token-leak watch while there are none at all (after an upgrade, an older version's cache is not read), and again after a change setup or a card makes (a read that has run for a minute is given up on; the facts from before stay) | Automatic (reads only). Defender advice is instructions only: PoolWatch never changes Defender settings |
| Writes `AGENTS.md` (a guide to its files for AI assistants) into the data folder, and `setup.json` (when setup was completed, hidden suggestions and feature cards) and `ui-state.json` (the view) | When missing or different; when they change | Automatic (its own files in its own folder) |
| Creates and locks `%ProgramData%\PoolWatch`; renames an unsafe existing one aside; applies its rules alone to one only administrators can change whose owner it cannot set, and uses one whose rules cannot be applied at all, or still differ once applied, as it is, unless its rules would let someone else change what PoolWatch makes in it or administrators may not make files in it (then it stops); reads a folder Administrators own as its owner may, whatever its rules say, and judges and secures it as above; never moves aside a folder it cannot read now (the look at it fails, which includes an open that is denied while the backup privilege cannot be turned on, or its rules shut Administrators out while the backup privilege reads it as an administrator's alone, as for one SYSTEM or TrustedInstaller owns): it stops instead; when it cannot be made safe, keeps its data in a folder of its own next to it (`PoolWatch.safe-<time>-<random>`), records that folder's name in `HKLM\SOFTWARE\PoolWatch` (value `DataFolder`); moves that folder back and deletes the record once the usual name is free or the usual folder is safe to use where it stands (renaming that usual folder aside first, and keeping it); deletes a record whose folder is gone or no longer admin-only, or that names no folder of its own; while that folder cannot be read (or the look at it fails), keeps the record and stops rather than use another data folder; names in its notes each recorded folder of its own it does not use, and files named like its lists of changes left in a folder it no longer uses | Every start of the tray app, of a headless run, and of the uninstaller's cleanup once PoolWatch has stopped. Every other PoolWatch process (the token-leak check's helper, a command-line step) moves and records nothing: it uses the folder in use as it stands (one whose rules are not exactly PoolWatch's only while they let no one else change what is made in it and administrators may make files in it), makes the usual folder (with its permissions set at creation) only when nothing is there and no PoolWatch is running, and otherwise stops. Every PoolWatch process writes only in the very data folder it checked | Automatic; noted in `poolwatch.log` and the findings log; a start that stops says why, and what it did before, in a message |
| The setup: installs to `%ProgramFiles%\PoolWatch` only (there is no folder page, and a setup started with `/DIR=` for another folder stops before copying anything). An upgrade first asks the running PoolWatch to exit through the exit signal (never a kill: through its launcher, a file under one of the launcher's names with `app\PoolWatch.exe`'s product version; without one, by setting the exit signal of a PoolWatch installed before the launcher, without starting anything), then waits until none of its files is in use, for as long as a process runs the install folder's launcher or PoolWatch itself (`app\PoolWatch.exe`), and a few seconds when none does; a token-leak test helper left running from it is ended, by PoolWatch's own stop step from this version on and by the setup itself when nothing else of PoolWatch runs. It closes no other program, another account's or a service included: Inno Setup's own close of the programs that have a file of the setup open is off, so a file another program keeps in use sends the update to the next restart. It replaces no file while that file is in use, which it asks of each file itself (Windows refuses to open a file for writing, with no sharing, while a process runs it or has it loaded, or another program has it open); a file in use decides only once it has stayed in use a few seconds more (a virus scanner opens a file only for a moment). To replace them now it first renames every old file aside, each once it is free, and looks at each again under its new name; it deletes them once every new file is in place, and puts every one back when it does not get that far (a cancel, or a later step that fails), with the Start menu entry, the uninstaller's program it replaced and the Apps and Features entry. An old file another program has open so that it cannot be renamed back goes back as a copy, or, when that program lets no one read it either, under a second name of the same file (a hard link). A file this version has that is still there after Inno Setup's rollback is a new one the rollback could not delete, whether or not an old file was in its place; when the setup cannot take it back either (another program has it open without letting it be renamed, or runs it), Windows deletes it at the next restart and every old file goes back only then, the launcher's files last, and so does an old file that cannot be put back now even so, with every one after it: until then no old file is beside a new one, and the launcher starts nothing from old and new files mixed (when Windows will not take such a new file to delete at the restart either, it stays after it; when it stays under its own name, the launcher's files stay set aside too, and every old file does when it is at one of the launcher's names: see Known limitations). Until that restart this version's uninstaller removes nothing, unless Windows refused some of that work (the setup then says to run it again before uninstalling), nor does 1.0.0-beta.1's while the setup has its record (`unins000.dat`), or else its messages file (`unins000.msg`), or else its program (`unins000.exe`; the setup then says to answer No if Windows offers to remove PoolWatch from its list of apps), set aside until then; when the setup can set none of them aside (another program keeps all three from being renamed, or Windows will not take them for the restart), that uninstaller can run before the restart, and the setup says not to uninstall before it and what that uninstaller would do (see Known limitations). The setup then says, in a box when someone is at it and in its log, what starts PoolWatch until the restart, read from the install folder, and names the files in use, each with what has it open (the box the first ten, and how many more; the log every one); a folder only new files were in goes, now or at the restart. When a file is in use, one came into use while it set them aside, or an earlier setup or uninstall of PoolWatch is waiting for the next restart, it puts the new files in a folder of their own in the install folder and gives Windows each of them to move into place at the next restart (Inno Setup's restart-time rename, `MoveFileEx` with `MOVEFILE_DELAY_UNTIL_REBOOT`), after the earlier work and keeping each folder that work would delete until its moves are done; it decides that before Inno Setup's own check for an unfinished earlier install looks at Windows' list, so that check, which would ask for a restart, finds none of its files there. It names each file in use with the program, process and account that have it open (Restart Manager), on its last page (in a list that scrolls) and in its log, which it always writes, and ends with exit code 3010 (1603 when Windows could not be given the files: it then takes them back, but for one another program holds whose move it could not undo, which the restart puts among the old files: it then leaves no file that starts PoolWatch at the launcher's names after the restart, unless Windows refuses that too, and the last page names each such file; see Known limitations). Its last page says what starts PoolWatch until the restart, read from the install folder, so it holds after an earlier setup or uninstall too. It never restarts the PC or asks for a restart. Its check before copying stops it only for `/DIR`: when another program has one of the uninstaller's own files open then, which Inno Setup writes in place itself, it writes its uninstaller in a folder of its own, which Apps and Features then starts (one opened later, while the setup runs, can make the setup fail, which then puts everything back as above). Once the new files are in place it deletes what the layout before the launcher left (the native DLLs beside the old `PoolWatch.exe`), or gives Windows them to delete after the moves at the next restart; where a copy of a zip left a `PoolWatch-selfcontained.exe`, it puts the launcher there too. "Start PoolWatch now" starts the launcher with the setup's administrator rights; it is not offered when the setup finishes at the next restart | Running the setup | Windows' User Account Control prompt |
| The uninstaller, after "Are you sure?" and before any file is removed: asks PoolWatch for its list of changes (`--changes-dump` into the admin-only data folder, as `uninstall-list.json`, deleted once read), and reads that list only when PoolWatch made it; otherwise it touches nothing in the data folder. Started by hand, it shows what it will put back and what it cannot (with why), and, when the token-leak guard found its Windows setting already changed and did not change it itself, that the setting stays until sign-out, and asks: putting back is the default. It starts PoolWatch only through the launcher of the install folder's program (same product version), and with none there, or while one of the launcher's names goes back only at the next restart (a setup that did not finish), removes nothing. It stops PoolWatch through the exit signal (never a kill) and ends a token-leak test helper still running from its exe; removes the logon task and the desktop shortcut; puts back every recorded change it can unless you keep them (one it cannot stays as it is); removes no file after a cleanup that failed (it may have put back only part, and the uninstaller cannot tell which); once the cleanup did what it was asked, runs it once more if part of PoolWatch still runs, or may (a file of it is still in use and its processes could not be listed) (to stop a PoolWatch started meanwhile and undo what it changed: when you keep the changes, only by removing its logon task and desktop shortcut), then removes every file of the install folder it can and gives Windows those still in use, and the folder, to delete at the next restart (only PoolWatch's own folder in Program Files, never through a link; it never restarts the PC or asks for a restart), and, started by hand, names the first ten with the program, process and account that have each open, and how many more (its log names each: it writes one when started with `/LOG`, as Apps and Features starts it, and none without); a silent uninstall then still ends with exit code 0, as Inno Setup's uninstaller lets its script give no other; writes `uninstall-summary.txt` (a second cleanup adds to it; a summary that cannot be written there is kept beside it, in `uninstall-summary-kept.txt`, the cleanup ends with exit code 1 so nothing is removed, and the next cleanup writes it in first); says what it put back (first what it put back only in part, with what was not done; past ten of either list it counts the rest, and then its question whether to delete the data folder says that `uninstall-summary.txt` there names them all; a `/DELETEDATA` uninstall started by hand, which deletes that file with the folder, names every change it put back only in part, ten to a box), or what it could not and why; deletes the data folder only when you ask and only when every change was put back (when PoolWatch had to keep its data in a folder of its own because it could not use the usual one, it puts back from there unless you keep the changes, says which it did, and deletes neither folder; the record of that folder in `HKLM\SOFTWARE\PoolWatch` stays too, so a later install finds the folder and its list; a later PoolWatch moves that folder back to the usual name when it can, or deletes the record once that folder is gone or no longer admin-only, and if you do not install PoolWatch again, you can delete that key yourself); after deleting it, says what of it is left, still in use by another program (a PoolWatch still running keeps the folder itself open) or not deleted for another reason, in its log and, started by hand, in a box, to delete by hand: never read through a link, and not left to the next restart, as a PoolWatch installed again before then keeps its data in that folder. Names in its boxes are shortened; the log keeps them whole | Uninstalling. `/KEEPCHANGES` keeps the changes. A silent uninstall and `/DELETEDATA` put back without asking (a `/DELETEDATA` uninstall started by hand then says what it put back); `/DELETEDATA` also deletes the data folder | Started by hand, its question (putting back is the default; a question that cannot be shown means putting back), then the data question (default No) |

Setup's **Next** confirms what its Standby cache and Fixes steps apply, so setup guards it against a press meant for
something else:

- Next is held, greyed, for one second whenever Next or Back brings up either step, whenever the scan's results arrive
  while either step shows (on time or late), after a change on either step did not work, and after a switch or a
  Yes/No appears on the Fixes step (while a change runs, from when that change ends).
- Next on the Fixes step applies only the switches and Yes/Nos that were on screen when it was pressed.
- Enter acts once per press. The setup window ignores the repeats of a held Enter (Shift+Enter too), so holding Enter
  moves setup on by one step at most.
- While Next is greyed (a change running, the scan, or the one-second wait), setup refuses Next however it is pressed
  (a click, Enter or a held key), and the keyboard focus moves from Next to the page area, so Enter does not land on
  another button.

## How changes are checked

- A change to an elevated action, to code that reads what a standard user can write, `src/PoolWatch/Native/`, the
  update check, the redaction, the installer or the release workflow (the list in AGENTS.md) gets a separate security
  review before it merges: a reviewer that did not write the change (another person or an AI review agent) tries to
  break it, and the pull request records what was tried.
- Every pull request runs the unit tests, end-to-end scenarios on Windows runners, the installer test and a
  dependency review. GitHub code scanning (CodeQL), secret scanning with push protection, Dependabot and OpenSSF
  Scorecard watch the repository.
- The workflows' actions are pinned to exact commits, and NuGet packages are restored in locked mode from
  `packages.lock.json`.
- A release is built and signed by CI from a `v*` tag; every file is checked for a valid signature from the expected
  certificate and an RFC 3161 timestamp before anything is published, together with `SHA256SUMS.txt`.

## Known limitations

- **Another program can delay an update or an uninstall until the next restart of Windows.** Windows lets no program
  replace or delete a file while another program runs it or holds it open without sharing it, and every user may read
  and run the programs in Program Files (Windows needs that to show the administrator prompt). So any account can keep
  PoolWatch's files in place for as long as its program runs. The setup and the uninstaller end a token-leak test
  helper left running, and do not wait on anything else for long: the setup puts the new version beside the old one and
  Windows moves it into place at its next restart, before any program can hold a file again (a setup run again before
  that restart does the same, after the first); the uninstaller removes everything else at once (after its cleanup has
  put back PoolWatch's changes, unless you kept them) and Windows deletes the rest at that restart. Until then what is
  in the install folder stays as it is, and the setup's last page says what starts PoolWatch until then, read from that
  folder: the version that was there, unless an earlier setup or uninstall left work for that restart (see the next
  entry). The setup names each file in use and what has it open, on its last page and in its log, which it
  always writes; the uninstaller names the first ten in its notice, and each in its log when it writes one (Apps and
  Features starts it with `/LOG`; started without `/LOG` it writes none). Neither restarts the PC or asks for a
  restart, so no program can make a silent update restart it; a silent setup ends with exit code 3010 instead, and a
  silent uninstall that leaves files for that restart still ends with 0: Inno Setup's uninstaller returns 0 once its
  own removal has finished and lets its script set no other code (Inno Setup 6.7.3, `Setup.Uninstall.pas`:
  `UninstallExitCode` starts at 1, line 44, is set to 0 as that removal deletes the uninstaller's data files, lines 187
  and 271, and becomes the exit code at line 908; no uninstall event function or support function sets it). That code
  is the one of the uninstaller's first copy, which ends then; its second copy (`_unins.tmp`, in a folder of its own in
  TEMP, lines 409 and 410) waits for that (lines 260 to 263) and only then removes what is left and logs what it gives
  Windows for the restart (`usPostUninstall`, line 767). A script that needs to know starts the uninstaller with
  `/LOG=<file>` and waits for that second copy to end too. The log then says whether the uninstall finishes at the next
  restart: it does not when Windows could not be given some of what is left, which then stays, to delete by hand; and it
  may not when the uninstaller could not make ready the place of a file an earlier update may have Windows move into the
  install folder at that restart, which would then stay after it. In the log, Inno Setup puts a time stamp before each
  message (`Setup.LoggingFunc.pas`, line 146): each file or folder left for that restart is named in a message that
  starts "Windows deletes it at its next restart: ", each thing Windows could not be given in one that starts "Windows
  could not be given ", and each file that may stay after it in one that starts "May stay after the next restart: ".
- **A setup that is cancelled or fails while another program has one of its files open can leave PoolWatch unable to
  start until the next restart of Windows (or, when Windows will not take the work for that restart, until the setup
  runs again), and can leave 1.0.0-beta.1's uninstaller free to run before that restart.** Windows renames or deletes no
  file while another handle has it open without `FILE_SHARE_DELETE` (`DeleteFileW` and `MoveFileExW` then fail with a
  sharing violation; Microsoft's documentation of `DeleteFileW` and of `CreateFileW`'s `dwShareMode`), and deletes no
  program's file while a process runs it or has it loaded, and every account may read PoolWatch's files. When such a
  program has a new file the setup wrote, or runs it, the setup cannot take that file back before the restart, whether
  or not an old file was in its place. What PoolWatch does: Windows deletes that file at the restart, and the setup
  gives Windows every old file to move back into place after it, the launcher's files last; none goes back before then,
  so until the restart the install folder holds no old file beside a new one, and a folder only new files were in goes
  then too. An old file another program opened under the name it was set aside under is put back at once all the same:
  as a copy, or, when that program lets no one read it either, under a second name of the same file (a hard link,
  `CreateHardLinkW`: Windows checks the share modes of a file's open handles only for an open that reads, writes, runs
  or deletes it, and a test holds that the link is made while another handle shares the file with no one). Only if even
  that fails does it go back at the restart, with every one after it and the launcher's files. Until the restart the
  folder then has no launcher, so the logon task, the Start menu entry and Explorer start nothing, and PoolWatch's
  program started directly hands its start to the launcher, which is not there (only the switches for CI and
  development, `--allow-any-location` among them, run it without its launcher, for an administrator who deliberately
  starts it so); when only one of the launcher's two names goes back at the restart, what names that one starts nothing
  until then, and the other starts the version that was there, which is whole; and when that other program also keeps
  this version's launcher and its program from being taken back, those two start this version, without its other files,
  until Windows deletes the launcher at the restart. Until the restart PoolWatch never runs from a mix of old and new
  files, and at the restart, before any program can hold a file again, the version that was there is back whole. The
  setup says so, with what starts PoolWatch until then (read from the install folder) and the files in use, each with
  what has it open, in a box when someone is at it (the first ten, and how many more) and in its log (every one). When
  Windows will not take an old file to put back at the restart, or a new one to delete then (`MoveFileExW` with
  `MOVEFILE_DELAY_UNTIL_REBOOT` fails when it cannot write its list in the registry: Microsoft's documentation of
  `MoveFileExW`), that file stays as it is: an old one under the name it was set aside under, a new one after the
  restart too: under the name the setup renamed it to, which starts nothing, or, when another program kept it from being
  renamed, under its own name. When a new file stays under its own name, the setup leaves the launcher's files set aside
  after the restart as well, so they start nothing from the old files that go back beside it; and when that new file is
  at one of the launcher's names, it leaves every old file set aside, so that the new launcher starts nothing from old
  files either (it, and this version's program when that stays too, start only this version, without its other files,
  after the restart). The setup says which, that PoolWatch may not start after the restart until the setup has run
  again, and to run it again before uninstalling. Until the restart PoolWatch's own uninstaller removes nothing and says
  to restart Windows first, unless Windows refused some of that work (the setup then does not say it removes nothing,
  and says to run the setup again before uninstalling). 1.0.0-beta.1's would not wait for the restart, so the setup sets
  its record (`unins000.dat`) aside until then: that uninstaller then says its record does not exist and removes
  nothing. When another program keeps the record from being renamed, the setup sets that uninstaller's messages file
  (`unins000.msg`) aside instead: 1.0.0-beta.1's released setup made a signed uninstaller, which reads its messages from
  that file before it looks for its record and stops without them, removing nothing (Inno Setup 6.7.3,
  `RunUninstaller`). When another program keeps the messages file from being renamed too, the setup sets that
  uninstaller's program (`unins000.exe`) aside instead, so that the Apps and Features entry starts nothing until the
  restart, and says to answer No if Windows offers to remove PoolWatch from its list of apps before then. Only when
  another program keeps all three from being renamed (the rule above), or Windows will not take them for the restart,
  can the setup not keep that uninstaller from running, whose code, released with 1.0.0-beta.1, does not wait for the
  restart: it can run before the restart (once that program lets go of them; at once when it was Windows that would not
  take them), removes the Apps and Features entry and its own files, keeps PoolWatch's changes and its logon task, and
  the files Windows puts back at the restart then have no entry. What PoolWatch does then: the setup says, in its box
  and its log, not to uninstall PoolWatch before the restart, and what that uninstaller would do.
- **When Windows will not take a setup's work for the restart, the restart can put a new file among the old ones.** A
  setup that finishes at the next restart gives Windows each new file to move into place then (`MoveFileExW` with
  `MOVEFILE_DELAY_UNTIL_REBOOT`, which fails when it cannot write its list in the registry: Microsoft's documentation of
  `MoveFileExW`). When Windows refuses one, the setup takes back every new file: it deletes each, renames one another
  program holds so that its move finds nothing, or, when that program does not let it be renamed, keeps the file that
  move replaces at the restart (what is there now, or what earlier work in Windows' list, such as an earlier setup that
  did not finish, moves there first; a place that work fills is never given to Windows to delete) as a copy, or under a
  second name of the same file, and gives Windows that to move back after it. Only when that cannot be done either
  (the file it replaces can be neither copied nor given a second name, or Windows refuses that move too) does the move
  stay, and the restart puts that new file among the files of the version that was there. What PoolWatch does: the
  file that move would replace, when no copy of it could be made, is renamed aside now, so that the restart does not
  replace it (unless another program keeps that from being done too); the last page names each file of the version in
  place that it set aside so, and what starts PoolWatch until the restart, read from the install folder. And it leaves
  no file that starts PoolWatch at the launcher's names after that restart: each file Windows' list would move there
  at the restart (an old launcher an earlier setup that did not finish left to go back then, or one an earlier setup
  put in its own folder), and then the file at each name now, is renamed aside at once (a rename needs nothing from
  Windows' list), and a name whose file, or a file that would move there, another program keeps from being renamed
  (the rule in the entry above) is given to Windows to rename aside at the restart, after every move to it (asked
  before the file at that name is renamed aside, so that it is still there when Windows is asked). Only when Windows
  will not take that either can a file that starts PoolWatch stay there, and the last page then says PoolWatch may
  start from old and new files mixed after the restart until the setup has run again; otherwise it says PoolWatch does
  not start after the restart until then. The setup ends with exit code 1603, its last page names each such file and
  says to run the setup again after the restart, which puts the whole version in place, and its log says what
  happened.
- **Another program can keep the uninstaller from starting while it holds the uninstaller's files open.** Two of
  Windows' rules meet here. First (Microsoft's documentation of `CreateFileW`'s `dwShareMode`, and "Creating and
  Opening Files"), an open of a file fails while another handle to it is open if the new open asks for an access that the
  other handle's share mode does not allow, or if the new open's own share mode does not allow an access that the other
  handle already has. Second, to start a program Windows opens its file to read and run it. Inno Setup's uninstaller
  opens its record, `unins000.dat`, sharing it with no one, before any of PoolWatch's
  code runs and before it asks for administrator rights. So another program that has the record open in any way, or the
  uninstaller's program open without sharing it for reading, keeps the uninstaller from starting. Which accounts can do
  that is set by where the files are and how the uninstaller starts. Both files are in the install folder, which every
  account may read, and Apps and Features starts the uninstaller with the standard rights of the administrator who
  uninstalls, in which the Administrators group grants nothing (User Account Control's filtered token: Microsoft's "How
  User Account Control works"). Those rights must be able to read both files, and an account without administrator
  rights has the same groups. Files only administrators can read would need the uninstaller started with administrator
  rights from the first, through a program that asks for them; that program would then be the file Apps and Features
  starts with standard rights, which every account could read, and hold, in the same way. Windows then says the
  uninstaller could not be started, or the uninstaller says its record could not be opened, and nothing is changed.
  What PoolWatch does: such a file never stops the setup. Run it again, and it writes a new uninstaller in a folder of
  its own, which Apps and Features starts from then on (the held files go with a later setup or the uninstall once they
  are free; a restart frees them).
- **PoolWatch's program started as administrator by an administrator's own hand, past its launcher, gets the
  environment of whatever started it.** Only an administrator who deliberately starts the program inside the install
  folder, `app\PoolWatch.exe`, as administrator does this: from their own elevated console, or with **Run as
  administrator** on that file. Nothing PoolWatch makes starts it so, and the install folder's `PoolWatch.exe`, which
  everything else starts, is the launcher; a taskbar pin of PoolWatch's window can be made only of a window started
  from a shortcut, and is then that shortcut (the launcher gives a PoolWatch started any other way an AppUserModelID
  with "prevent pinning", `STARTF_PREVENTPINNING`). .NET reads its start-up variables from that environment before any of
  PoolWatch's own code runs, and a program cannot turn them off for itself: the runtime reads `CORECLR_ENABLE_PROFILING`
  and the profiler (`CORECLR_PROFILER`, `CORECLR_PROFILER_PATH`) from the environment as it starts and has no
  runtimeconfig.json setting for them (Microsoft's "Runtime configuration options for debugging and profiling"), and it
  takes a garbage collector to load (`DOTNET_GCName`, `DOTNET_GCPath`) from the environment before its runtimeconfig.
  That copy then starts PoolWatch again through the launcher and does no work of its own, and its build ignores
  start-up hooks, but a profiler or a garbage collector that such a variable names has already been loaded into it, as
  into every .NET program started that way. Start PoolWatch from its Start menu entry, or, from a console, with the
  install folder's `PoolWatch.exe` and the same options.
- **The admin-only check reads the own rules only of what PoolWatch or its launcher runs or loads as administrator**
  (the security model above): `app\PoolWatch.exe` and every file and folder in `app` (PoolWatch's DLLs, in
  `app\amd64\`, among them), and the launcher. The install folder's other files (the notices, the licences, the Codex
  shortcut's script and the uninstaller), none of which PoolWatch starts or loads, are judged by the rules the install
  folder gives the files made in it, not by their own.
- **A clear task's definition is checked as PoolWatch reads it.** Task Scheduler's own XML parser could still read a
  definition differently. PoolWatch's rules (one XML namespace, at most one of each part, plain text only in every
  checked part, no control character in a path or an option) narrow that to how single plain-text values are encoded.
  The definitions are kept in PoolWatch's admin-only data folder.
- **During the token-leak test at sign-in, the workaround is off.** PoolWatch tests before it turns the workaround on,
  instead of always turning it on. That is a deliberate choice: the workaround lets any program take the focus, and the
  test can only run with it off, because it measures the leak the workaround removes. What PoolWatch cannot change is
  Windows: it resets the setting at each sign-in, and only a program that may bring a window to the front can set it
  (SystemParametersInfo: "the calling thread must be able to change the foreground window"), so nothing running in the
  background can turn the workaround on in PoolWatch's place. What PoolWatch does about it: before the test it writes a
  marker. While one of the test's two helper runs is running, the helper watches PoolWatch (it holds PoolWatch's process
  open, so a later process given the same id is not taken for it), and if PoolWatch is killed or crashes, it asks for one
  click to turn the workaround on. No helper watches before the first run starts, between the two runs, in the moment a
  run starts, or after the second ends. If PoolWatch is killed in one of those moments, or you end the helper too, or
  answer **Not now**, that sign-in stays without the workaround until PoolWatch next starts. That start finds the marker
  and turns the workaround on without testing (or, when it is already on, for example after your click, leaves it on and
  still saves the build as not tested to the end). From then on, every sign-in on that Windows build does the same, until
  a test that finishes saves a result: to check now, turn the guard off (if Active changes then still lists its Windows
  setting, put that back too) and choose **Run setup again** in the tray menu; a new build is tested again by itself. A
  result that cannot be saved is treated the same way.
- **Later in a sign-in, the token-leak workaround can be off until Windows lets PoolWatch set it again or you click its
  tray icon.** The setting is the sign-in's, and Windows lets a program change it only when that program "must be able
  to change the foreground window" (SystemParametersInfo), as the program in front can; PoolWatch running in the
  background can set it again only when Windows lets it. What PoolWatch does: at each of its checks (about every 3
  minutes by default) it tries again by itself and opens no window; while it cannot, the dot of the tray app's icon is
  amber (a red dot stays red) and its tooltip and the top of its menu say so, and a click on the icon or that line turns
  the workaround on. Its log and findings name the program that was in front as PoolWatch saw the change, which Windows
  lets change this setting (other programs may be able to as well); this is not proof that it changed it. When Windows
  did not tell PoolWatch of the change, PoolWatch names the program in front when its check found the change, which can
  be minutes later, and says so.
- **The token-leak watch starts the test by itself.** After a Windows update, with the guard off, PoolWatch runs its
  check-only test without a click. It never turns the guard on by itself, and it leaves the setting as it was.
- **A service profile applied at start asks nothing at start.** Ticking its box when you saved the profile is the
  consent. The uninstaller does not change profiles, so a data folder kept through an uninstall applies the profile
  again under a later PoolWatch. `--service-restore` never takes a service out of the profile, so the profile turns it
  off again at PoolWatch's next start, unless the start type it put back is already as Apply leaves it (Disabled, or
  Manual for a service PoolWatch turns off to Manual).
- **Over-the-shoulder elevation.** PoolWatch runs as the account that approved the administrator prompt. When a standard
  user elevates with another administrator's password, the per-user facts (AI tools in the profile, Store packages) and
  the desktop that gets the Codex shortcut are that administrator's.
- **The AI agent's Save and Open dialogs run elevated.** They are Windows' own dialogs, shown by the elevated
  PoolWatch. A standard user can preselect the folder they open in (Windows' per-app list of recent folders), and the
  dialog's own menus use the user's file associations, elevated. PoolWatch writes and reads only the file picked, under
  the rules in the security model. A prompt cannot be saved, and an answer cannot be imported, on a mapped network
  drive or a `subst` drive letter.
- **The AI agent's review tells items apart by their text.** A name that only looks like another's (letters from
  another alphabet), or that imitates "name (place)", is not caught: the review shows a startup app's place and a
  task's folder on their own line. PoolWatch always acts on the exact item the answer's name matched on this PC, shows
  the change before making it, and the change can be put back.
- **Without the backup privilege, PoolWatch cannot tell whose a shut data folder is unless Administrators own it.**
  PoolWatch reads the folder at `%ProgramData%\PoolWatch` as its owner may: Windows grants an object's owner the right
  to read and to change its rules whatever they say, unless they hold an entry for OWNER RIGHTS (Microsoft's
  documentation of the OWNER RIGHTS SID), and PoolWatch's open asks for nothing more but the right to read its
  attributes, which `%ProgramData%`'s own rules give administrators (a folder's right to list what is in it grants
  that right on each entry in it, whatever the entry's rules say). So a folder owned by
  Administrators, as every folder PoolWatch and 1.0.0-beta.1 make is, is read, judged and given PoolWatch's rules again
  even when its rules shut administrators out. Another folder whose rules do not let administrators open it (one an
  administrator gave to SYSTEM or TrustedInstaller, or one another account made there) PoolWatch reads only with the
  "Back up files and directories" right. When a policy took that right from administrators, PoolWatch reads neither
  its owner nor its rules: Windows lets a program read them only when the folder's rules allow it, when the program
  runs as the folder's owner, or with that right. So PoolWatch cannot tell such a folder another account made there
  from one an administrator shut that may hold its data, and it never sets it aside: each start stops with a message
  that names the folder and moves that folder nowhere, until an administrator deletes it or lets administrators read
  it.
- **Symbol folders** are checked as folders (owner and permissions), not file by file: put only admin-owned PDBs in
  them.
- **Redaction is best-effort.** It replaces your user name, machine name, home and TEMP paths, domain, SID and values
  that look like secrets (and, in Report an issue, other paths, GUIDs, volume serials, e-mail, IP and MAC addresses),
  but it cannot know every secret format, and the `.etl` trace is left out because it cannot be redacted reliably. An
  export keeps Claude Code session ids and project names (Report an issue leaves sessions out), and the names you gave
  your service profiles. Read an export before you post it. The AI agent's prompt has a redaction of its own, also
  best-effort, and shows every word before you save or copy it.
- **The incident folders are not redacted.** They are for you (readable by your account and administrators) and say
  so at the top.
