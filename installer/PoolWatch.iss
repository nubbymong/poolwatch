; The PoolWatch installer (Inno Setup 6.7). CI builds it from the self-contained build, staged as it is installed
; (installer\build.ps1):
;   ISCC.exe /DAppVersion=<version> /DPublishDir=<staged folder> installer\PoolWatch.iss
; A release adds /DSign and /Spwsign=<sign command>, so the setup and its uninstaller are signed as they are made.
;
; The install folder's PoolWatch.exe is PoolWatch's launcher, which asks for administrator rights and starts PoolWatch
; itself, app\PoolWatch.exe, with a clean environment. Everything here that starts PoolWatch starts the launcher (the
; Start menu entry, "Start PoolWatch now", the setup's stop and the uninstaller's steps), and so does a logon task or a
; shortcut made before 1.0, which named the install folder's PoolWatch.exe, or PoolWatch-selfcontained.exe where a copy of
; the self-contained zip was unpacked: the setup puts the launcher under that name too where a file of that name is there.
; A file with one of the launcher's names counts as the launcher only when PoolWatch itself is below it with the same
; product version (LauncherPath), as PoolWatch's own LaunchGate.Judge decides. Otherwise (an install made before the
; launcher, which has PoolWatch itself there, or an older program left under the launcher's name) the setup stops it by
; setting that version's exit signal itself and starts nothing, and the uninstaller starts nothing. Once the new files
; are in place, what the layout before the launcher left elsewhere goes (RemoveOldLayout).
;
; By default the uninstaller puts back each change PoolWatch made, or says why it could not: it stops PoolWatch through
; its exit signal and has it put back each change in its list before any file is removed (PoolWatch
; --uninstall-cleanup). Started by hand, it first lists what it will put back (PoolWatch --changes-dump) and asks, with
; putting the changes back as the default and keeping them as the other choice. A silent uninstall (/SILENT,
; /VERYSILENT) and /DELETEDATA do the same without asking (/DELETEDATA started by hand then says what it put back).
; /KEEPCHANGES keeps the changes (the logon task and the desktop shortcut still go); /DELETEDATA also deletes the data
; folder, once every change was put back.
;
; When another program still has one of PoolWatch's files in use, the setup and the uninstaller finish at the next
; restart of Windows instead of stopping. The setup puts the new files in a folder of their own beside the old ones and
; gives Windows each of them to move into place at that restart (FinishAtRestart, SwapAtRestart), and leaves what is in
; the install folder as it is until then (what its last page says starts PoolWatch until then is read from that folder
; and Windows' list, so that it also holds after an earlier setup or uninstall left work for that restart: UntilRestart);
; the uninstaller removes everything else and gives Windows what is left to delete then (RemoveRest). Both use MoveFileEx's
; restart-time rename (RestartReplace), which leaves Inno Setup's own restart state alone. A setup run again before that
; restart finishes at the restart too, after the earlier work: it decides that before Inno Setup's own check for an
; unfinished earlier install looks at Windows' list (ProgramDir), so that check, which would ask for a restart, finds
; none of this run's files there. No run of the setup or the uninstaller restarts the PC or asks for a restart. A setup
; that replaces the files now first renames every old file aside, and puts each back if the setup does not finish
; (SetAllAside, DeinitializeSetup), with the Start menu entry, the uninstaller's program it replaced and the Apps and
; Features entry. An old file another program has open under its new name without letting it be renamed goes back as a
; copy, or, when it may not be read either, under a second name of the same file (CreateHardLinkW, which another
; program's sharing does not stop: PutBack). Every file this version has that is still there after Inno Setup's
; rollback is a new one the rollback could not delete, whether or not an old file was in its place; one the setup
; cannot take back now either (another program has it open without letting it be renamed, or runs it, so that it
; cannot be deleted: TakeBack) is deleted by Windows at the next restart, and every old file then goes back only at
; that restart; so does an old file that cannot be put back now even so, with every one after it. The files that start
; PoolWatch, its launcher's two names, go back last (PutAllBack). So until that restart the install folder holds no old
; file beside a new one, nor the old launcher beside only part of the old files: nothing starts PoolWatch from old and
; new files mixed (a new launcher and program that another program keeps from being taken back start only this version,
; without its other files, until then), and a setup that is cancelled or fails loses none of the files it had (an old
; file Windows will not take to put back at the restart stays under the name it was set aside under, and the setup says
; to run it again: Refused). When Windows will not take such a new file to delete at the restart either, it stays after
; the restart too: under the name TakeBack renamed it to, which starts nothing, or, when another program kept it from
; being renamed, under its own name (LeftNew); then the old launcher's names stay set aside too, so that they start
; nothing from the old files that go back beside it, and when that new file is at one of the launcher's names, every
; old file stays set aside, so that the new launcher starts nothing from them either. Until that restart this
; version's uninstaller starts no cleanup and removes nothing (RunCleanup), unless Windows refused some of that work
; (a new launcher and its program left in place can then start a cleanup: the setup does not say this version's
; uninstaller removes nothing then, of 1.0.0-beta.1's held until the restart it still does, and it says to run the
; setup again before uninstalling); the uninstaller of a version made before
; the launcher would not wait for that restart, so the setup sets its record, or else its messages file, or else its
; program, aside until then too when it can (HoldOldUninstaller), and until then that uninstaller removes nothing
; either; when another program keeps all three from being set aside, or Windows will not take them for the restart,
; the setup says not to uninstall before the restart (DeinitializeSetup). When a setup that finishes at the restart
; cannot undo the move of a new file another program holds (Unstage, which reads Windows' list to know what that move
; would replace, and keeps that file now as a copy, under a second name, or else renamed aside, as far as it can), no
; file that starts PoolWatch is left at the launcher's names after that restart (KeepLauncherOut: the file at each
; name, and each file Windows' list would move there then, is set aside now when it can be, and when another program
; holds one of them, Windows is given that name to set aside at the restart too), so that the old files and that new
; one do not start together; only when Windows will not take that either can one stay (LauncherKept), which the last
; page then says.

#ifndef AppVersion
  #error Pass /DAppVersion=<version> (the Version in Directory.Build.props)
#endif
#ifndef PublishDir
  #error Pass /DPublishDir=<the staged self-contained build>
#endif
; The setup's file version takes numbers only: AppVersion without a pre-release suffix (1.1.0-beta.1 gives 1.1.0).
#define NumericVersion Pos("-", AppVersion) > 0 ? Copy(AppVersion, 1, Pos("-", AppVersion) - 1) : AppVersion

[Setup]
; Never change AppId: Windows and Inno Setup find an installed PoolWatch by it, to upgrade or uninstall it.
AppId={{0FAA15F3-94EF-4192-B6DF-5CECFACD2E1C}
AppName=PoolWatch
AppVersion={#AppVersion}
AppPublisher=Nicholas Moger
AppPublisherURL=https://github.com/nubbymong/poolwatch
AppSupportURL=https://github.com/nubbymong/poolwatch/issues
; The setup exe's own version info (its Details tab). The copyright is LICENSE's line; build.ps1 checks the file version
; (numbers and text) and the copyright after the build.
VersionInfoVersion={#NumericVersion}
VersionInfoCopyright=Copyright (c) 2026 Nicholas Moger
UninstallDisplayName=PoolWatch
UninstallDisplayIcon={app}\PoolWatch.exe
; Program Files only: PoolWatch runs as administrator and refuses to run from a folder you can change without that.
DefaultDirName={autopf}\PoolWatch
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; One x64 build: x64 Windows, and Windows 11 on Arm, which runs it through its x64 emulation (not tested there yet).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
WizardStyle=modern
; The pages: Welcome, Ready and Finished, in PoolWatch's words (the Messages and CustomMessages sections below, laid out
; by the Pascal code further down, before the uninstaller's). The Ready page shows three steps and the "You stay in
; control" panel in place of Inno Setup's list of settings, which would hold nothing here.
DisableWelcomePage=no
DisableReadyMemo=yes
; PoolWatch's own artwork (docs/brand), in the sizes of the wizard's image areas: Setup picks the closest for the display
; scale. The title-bar icon is the program's icon: the binoculars at every size.
SetupIconFile=..\src\PoolWatch\Assets\poolwatch.ico
WizardImageFile=poolwatch-sidebar-202x386.png,poolwatch-sidebar-269x515.png,poolwatch-sidebar-328x628.png
WizardSmallImageFile=poolwatch-small-58x58.png,poolwatch-small-77x77.png,poolwatch-small-97x97.png,poolwatch-small-124x124.png
OutputBaseFilename=PoolWatch-v{#AppVersion}-setup-win-x64
Compression=lzma2/max
SolidCompression=yes
; The setup closes no program. Inno Setup's own close of the programs that have a file of the setup open (through
; Restart Manager, without asking in a silent setup) is off, and its filter matches no file the setup has, so Inno Setup
; registers none and closes none even when /CLOSEAPPLICATIONS on its command line turns that close on. PrepareToInstall
; asks a running PoolWatch to exit, then waits, up to RunningWait seconds, until none of its files is in use, for as long
; as a process with PoolWatch's name runs from the install folder or the list of processes cannot be read, and up to
; BriefHold seconds when no such process runs (WaitForApp). When a file is still in use after that and BriefHold seconds
; more (WhenToReplace), or stays in use while the old files are set aside (SetAllAside), the whole update goes in place
; at the next restart.
CloseApplications=no
CloseApplicationsFilter=*.closes-no-program
RestartApplications=no
; No restart is ever asked for, so a silent setup never restarts the PC: Inno Setup would otherwise ask for one when
; Windows' list of files to rename at the next restart changed while a [Run] entry ran, which another program can do.
RestartIfNeededByRun=no
; The setup always writes a log, as /LOG makes it, in the TEMP folder of the account that runs it: it names each file in
; use with what has it open. The uninstaller writes one only when it is started with /LOG (or /LOG=<file>): the Apps
; and Features entry starts it so (UninstallLogging), and its notice, which names only the first MaxShown, points to
; that log only when there is one (RestartText).
SetupLogging=yes
UninstallLogging=yes
; Where Inno Setup writes the uninstaller (unins000.exe, .dat, .msg): the install folder, or a folder of this run's own
; when one of the uninstaller's files there is in use or the setup finishes at the next restart (UninstallDir).
UninstallFilesDir={code:UninstallDir}
#ifdef Sign
SignTool=pwsign
SignedUninstaller=yes
#endif

[Messages]
WelcomeLabel1=Welcome to PoolWatch
WelcomeLabel2=PoolWatch watches the kernel pool, clears the standby cache before it slows you down, fixes known Windows issues and, if you want, trims background work.
ClickNext=Click Next to continue.
WizardReady=Ready to install
ReadyLabel1=PoolWatch is ready to install on this PC.
ReadyLabel2a=Click Install to continue.
ReadyLabel2b=Click Install to continue.
FinishedHeadingLabel=PoolWatch is installed
FinishedLabel=PoolWatch lives in the tray, next to the clock. Its setup guide opens first and takes about 3 minutes.
FinishedLabelNoIcons=PoolWatch lives in the tray, next to the clock. Its setup guide opens first and takes about 3 minutes.
; Inno Setup's own last words after an uninstall started by hand, when its own removal of what the setup installed left
; something (a file it could not delete). RemoveRest, before it, gave Windows what was still in use to delete at the next
; restart, and said so in a notice of its own, which also covers a file only RemoveRest found in use.
UninstalledMost=PoolWatch was removed from your computer.%n%nWhat was still in use is deleted when Windows next restarts, unless the uninstaller said it could not arrange that.

[CustomMessages]
; The Welcome page's second paragraph, and the Ready page's steps and panel (%1: the folder Setup installs into).
WelcomeSetup=Setup installs PoolWatch {#AppVersion} and starts it. A short setup guide then asks how you want it to look after this PC.
ReadyInstallTitle=Install
ReadyInstallText=Into %1.
ReadyTrayTitle=Start in the tray
ReadyTrayText=Next to the clock, as administrator, so it can read the kernel pool.
ReadySetupTitle=Set up in about 3 minutes
ReadySetupText=Choose what PoolWatch looks after on this PC.
ReadySafeTitle=You stay in control
ReadySafeText=Nothing changes without your OK. Anything PoolWatch changes can be put back, and uninstalling puts each change back or says why it could not.

[Files]
; The Ready page's icons, unpacked for the wizard and never installed. First in this section: with solid compression,
; unpacking one file means unpacking every file above it first.
Source: "ready\*.png"; Flags: dontcopy
; PoolWatch's own files go into the install folder, or, when the setup finishes at the next restart, into a folder of
; their own there, from which Windows moves each into place at that restart (ProgramDir, FinishAtRestart). Before they
; go into the install folder, every old file there has been renamed aside (SetAllAside), so [Files] writes each new file
; to a name nothing holds; the old ones are deleted once the setup has put every new file in place (DropAside), and put
; back if it does not get that far (DeinitializeSetup). Each entry notes the file it is about to write (Shipping), so
; that DropAside puts back only an old file this version does not have.
; Named one by one, so a build without one of them fails here instead of shipping without it. First the launcher, the
; install folder's PoolWatch.exe, and the launcher again under its second name where a file of that name is there already
; (a copy of a zip was unpacked there: SelfContainedNameThere), so a logon task or a shortcut that names it keeps starting
; PoolWatch through its launcher; then PoolWatch itself below it.
Source: "{#PublishDir}\PoolWatch.exe"; DestDir: "{code:ProgramDir}"; Flags: ignoreversion; BeforeInstall: Shipping
Source: "{#PublishDir}\PoolWatch.exe"; DestDir: "{code:ProgramDir}"; DestName: "PoolWatch-selfcontained.exe"; Flags: ignoreversion; Check: SelfContainedNameThere; BeforeInstall: Shipping
Source: "{#PublishDir}\app\PoolWatch.exe"; DestDir: "{code:ProgramDir}\app"; Flags: ignoreversion; BeforeInstall: Shipping
Source: "{#PublishDir}\chatgpt-no-shader-cache.ps1"; DestDir: "{code:ProgramDir}"; Flags: ignoreversion; BeforeInstall: Shipping
Source: "{#PublishDir}\AGENTS.md"; DestDir: "{code:ProgramDir}"; Flags: ignoreversion; BeforeInstall: Shipping
Source: "{#PublishDir}\LICENSE"; DestDir: "{code:ProgramDir}"; Flags: ignoreversion; BeforeInstall: Shipping
Source: "{#PublishDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{code:ProgramDir}"; Flags: ignoreversion; BeforeInstall: Shipping
; The emblem as a PNG of its own (build.ps1 stages it), for the AppUserModelID's entry under [Registry]: Windows reads the
; icon of an app's notices from an image file, which Microsoft documents as a PNG for that entry.
Source: "{#PublishDir}\poolwatch.png"; DestDir: "{code:ProgramDir}"; Flags: ignoreversion; BeforeInstall: Shipping
; The rest of the build: the native DLLs PoolWatch loads from its own folder (app\amd64\), and the .NET runtime's
; licences (licenses\).
Source: "{#PublishDir}\*"; DestDir: "{code:ProgramDir}"; Excludes: "\PoolWatch.exe,\PoolWatch-selfcontained.exe,\app\PoolWatch.exe,\chatgpt-no-shader-cache.ps1,\AGENTS.md,\LICENSE,\THIRD-PARTY-NOTICES.md,\poolwatch.png"; Flags: ignoreversion recursesubdirs createallsubdirs; BeforeInstall: Shipping

[Icons]
; The launcher, which asks for administrator rights and starts PoolWatch with a clean environment. The entry carries the
; AppUserModelID the launcher gives a PoolWatch it starts any other way (the logon task, Explorer; the launcher's
; Native.AppUserModelId), so PoolWatch's windows and notices are one app's however it was started, the app this entry is.
Name: "{autoprograms}\PoolWatch"; Filename: "{app}\PoolWatch.exe"; AppUserModelID: "PoolWatch.Monitor"

[Registry]
; That AppUserModelID's own entry, which Windows reads through HKEY_CLASSES_ROOT: the name and icon of PoolWatch's
; notices, as Microsoft documents it for an unpackaged app that shows notifications. The owner's PC had no such entry
; while the "..." menu on a notice did nothing; whether this entry is what that menu needs is to be seen on a real PC.
; The uninstaller removes it.
Root: HKLM; Subkey: "Software\Classes\AppUserModelId\PoolWatch.Monitor"; ValueType: string; ValueName: "DisplayName"; ValueData: "PoolWatch"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\AppUserModelId\PoolWatch.Monitor"; ValueType: string; ValueName: "IconUri"; ValueData: "{app}\poolwatch.png"; Flags: uninsdeletekey

[Run]
; The setup guide opens at this first start. The launcher requires administrator rights, so it starts with Setup's:
; started as the original user (the default for postinstall) Windows would refuse it with error 740. Not offered when
; the setup finishes at the next restart: the new version is not in place until then (StartsNow).
Filename: "{app}\PoolWatch.exe"; Description: "Start PoolWatch now"; Flags: postinstall nowait skipifsilent runascurrentuser; Check: StartsNow

[Code]
const
  { The exit codes --uninstall-cleanup can end with, through the launcher, which passes PoolWatch's on and gives its own
    when PoolWatch did not run: each has the name and the value it has in PoolWatch's one table of exit codes
    (ExitCodes, which docs/TECHNICAL.md lists), and the uninstaller handles each (CleanupDone, NotRemovedText). 1, an
    error stopped the cleanup, which may have put back part of the changes; 2, PoolWatch did not take its command line,
    and nothing was done; 5, someone who is not an administrator could change PoolWatch's program, its launcher or a
    folder they are in, and nothing was done; 6, its data folder could not be made safe to use, and nothing was done; 7,
    PoolWatch still runs, and nothing was done; 8, the cleanup could not put everything back (the uninstaller goes on
    after it, or after 0 or 12, only); 9, PoolWatch could not open its private names, so it could not ask a running
    PoolWatch to exit, and nothing was done; 10, PoolWatch did not run (its launcher could not start it); 11, PoolWatch
    refused the start; 12, the cleanup ran on the folder of PoolWatch's own it keeps its data in while it could not use
    the usual one. What --stop-running and --changes-dump end with is only logged. PoolWatch's one-instance mutex and
    its exit signal are in a private namespace that only an elevated administrator or SYSTEM can create or open. This
    script does not open it: it goes by the cleanup's exit code, by Windows' list of processes (RunningFromApp), and by
    the install folder's files themselves (HeldFiles), which no program can make look free while it has one in use. }
  ExitFailed = 1;
  ExitBadCommandLine = 2;
  ExitNotInAdminFolder = 5;
  ExitDataNotSafe = 6;
  ExitStillRunning = 7;
  ExitNotAllPutBack = 8;
  ExitNamesNotSafe = 9;
  ExitNotStarted = 10;
  ExitRefused = 11;
  { The cleanup ran (as 0 or 8 would say), but on the folder PoolWatch keeps its data in while it could not use
    %ProgramData%\PoolWatch: nothing is read or deleted in %ProgramData%\PoolWatch then, since PoolWatch did not use it
    this time (ExitCodes.OtherDataFolder). }
  ExitOtherDataFolder = 12;
  { The uninstaller's own code for a cleanup it did not start because the install folder has no launcher of its
    program's version (RunCleanup). }
  NotInPlace = -2;
  { The install folder's layout: its PoolWatch.exe is the launcher (LauncherPath), and PoolWatch itself is the program
    of the same name in its app folder (ProgramPath, LaunchGate.AppFolderName). An install made before the launcher has
    no app folder: its PoolWatch.exe is PoolWatch itself (HasLauncher). The launcher's second name, the one the
    self-contained zip's program had before 1.0 (LaunchGate.LauncherFileNames): both zips carry the launcher under it
    too, and [Files] puts it there where a file of that name is (SelfContainedNameThere). }
  PoolWatchName = 'PoolWatch.exe';
  SelfContainedName = 'PoolWatch-selfcontained.exe';
  AppFolder = 'app';
  { The exit signal of a PoolWatch installed before the launcher (an older version: a global name, not in the private
    namespace), and the right to set it (StopBeforeLauncher). }
  OldExitEvent = 'Global\PoolWatch-Exit';
  EVENT_MODIFY_STATE = $0002;
  { The setup's exit code when it finishes at the next restart (FinishAtRestart): Windows' own code for "done; a restart
    completes it" (ERROR_SUCCESS_REBOOT_REQUIRED). The setup never restarts the PC and asks Inno Setup for no restart. }
  ExitFinishesAtRestart = 3010;
  { The setup's exit code when Windows could not be given every file to move into place at the restart: Windows' code
    for an install that failed (ERROR_INSTALL_FAILURE). The setup then deletes the new files it had put in its own
    folder, so the restart has none of them to move, and says to run it again after the restart (SwapAtRestart). }
  ExitNotFinished = 1603;
  { The folder in the install folder where a setup that finishes at the next restart puts PoolWatch's files until then,
    each run in a folder of its own below it (StageDir). Nothing is ever replaced in it, so the setup does not look at
    it when it checks which files are in use. }
  StageFolder = 'at-restart';
  { A file a setup that finishes at the next restart leaves in a folder its files move into at that restart, when an
    earlier uninstall gave Windows that folder to delete then: Windows deletes a folder at the restart only while it is
    empty, so the folder is still there for the moves, which come after the delete; the file goes after the moves
    (KeepFolder). }
  KeepName = 'at-restart.keep';
  { The folder in the install folder that holds the uninstaller of a setup that could not write it in the install folder
    itself, each run in a folder of its own below it (UninstallDir). It holds no file of PoolWatch's program, so the
    setup does not look at it when it checks which files are in use, nor set it aside. }
  UninstallFolder = 'uninstall';
  { Where Windows keeps what MoveFileEx gives it to rename or delete at the next restart, under HKLM: the value
    PendingFileRenameOperations, and PendingFileRenameOperations2 when that one is full. }
  SessionManagerKey = 'SYSTEM\CurrentControlSet\Control\Session Manager';
  { PoolWatch's Apps and Features entry, under HKLM (in its 64-bit view, as the setup runs in 64-bit mode): where Inno
    Setup writes it, from AppId. }
  UninstallEntryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{0FAA15F3-94EF-4192-B6DF-5CECFACD2E1C}_is1';
  { The attributes of a found file (TFindRec) that this script reads: a folder, and a link (a reparse point), which it
    never follows. }
  AttrFolder = $10;
  AttrLink = $400;
  { How long, in seconds, the setup and a silent uninstall wait, after the installed PoolWatch was asked to exit, until
    no process runs the install folder's PoolWatch or its launcher, or none of its files is in use: PoolWatch's own wait
    after its exit signal. }
  RunningWait = 60;
  { How long an uninstall started by hand waits for the same, and how long any uninstall waits after a second cleanup
    (whose own wait has then already stopped PoolWatch): the uninstaller has no page that keeps drawing during a wait,
    as the setup has. }
  HandWait = 3;
  { How long, in seconds, any wait goes on while a file is in use but no process with PoolWatch's name runs from the
    install folder: a virus scanner or the search indexer opens a file only for a moment, and should not send an update
    to the next restart. }
  BriefHold = 3;
  { Restart Manager (RmGetList), which names the programs that have a file open: a session key's length with its #0
    (CCH_RM_SESSION_KEY + 1), how many programs one look reads at most, and one RM_PROCESS_INFO as characters (668
    bytes: RM_UNIQUE_PROCESS, strAppName[256], strServiceShortName[64], ApplicationType, AppStatus, TSSessionId,
    bRestartable). ERROR_MORE_DATA: more programs have it open than were read. }
  RmKeyChars = 33;
  RmMaxRead = 16;
  RmInfoChars = 334;
  ErrorMoreData = 234;
  { The first argument of a token-leak test helper (PoolWatch.exe --token-leak-test; LeftoverHelpers.HelperOption). }
  HelperOption = '--token-leak-test';
  { The rights a helper is opened with to end it: PROCESS_TERMINATE and PROCESS_QUERY_LIMITED_INFORMATION. }
  EndRights = $1001;
  { The rights a process is opened with to read its exe path: PROCESS_QUERY_LIMITED_INFORMATION. }
  ReadRights = $1000;
  { At most this many processes are named in a box (the log names each one). }
  MaxProcessesShown = 3;
  { What the uninstaller does with what PoolWatch changed (PutBackChoice). }
  ChoiceKeep = 0;
  ChoicePutBack = 1;
  ChoiceAsk = 2;
  { The list PoolWatch writes for the uninstaller, in its admin-only data folder, and the lines of each change in it
    that the uninstaller reads (ChangeLedger.ToJson writes one property per line; a test holds the shape). }
  ListFileName = 'uninstall-list.json';
  WhatKey = '    "what": "';
  LeavesKey = '    "uninstallLeaves": "';
  { What stays for a while after a change is put back (the token-leak guard's Windows setting, until you sign out). }
  CaveatKey = '    "putBackCaveat": "';
  { The lines of uninstall-summary.txt (Program.UninstallSummary writes it) that say what was put back, what was put
    back only in part (a service whose start type is back but that was not started again, and why), and what could not
    be; a test holds them to what PoolWatch writes. }
  PutBackPrefix = 'Put back: ';
  PartlyPutBackPrefix = 'Partly put back: ';
  NotPutBackPrefix = 'Could not put back: ';
  { The heading the notices after the cleanup list the changes put back only in part under, ahead of the rest. }
  PartlyPutBackHeading = 'PoolWatch put back these changes only in part:';
  { The heading of each box that names more of them, after a /DELETEDATA uninstall started by hand
    (ShowRestPutBackInPart): the first and the last it names, and how many there are. }
  PartlyPutBackMoreHeading = 'PoolWatch put back these changes only in part (%d to %d of %d):';
  { The same heading for a box that names one of them: which one, and how many there are. }
  PartlyPutBackOneMoreHeading = 'PoolWatch put back this change only in part (%d of %d):';
  { At most this many changes are named in a question or a notice, so each box fits on the screen. }
  MaxShown = 10;
  { A name shown in a box is cut to MaxNameShown characters, and a whole line to MaxLineShown: a startup app's name is
    chosen by whoever made the entry, and a box must still fit on the screen. The log keeps the whole text. }
  MaxNameShown = 120;
  MaxLineShown = 360;
  { A line of the summary in a notice after the cleanup ("Put back: ...", "Could not put back: ... why"): a name and
    some room for why, so ten of them still fit on the screen. A "Partly put back: ... what was not done" line is cut to
    MaxLineShown instead (as a change it cannot put back is in the question before): room for what was not done. }
  MaxSummaryShown = 180;
  { When PoolWatch could not list its changes, the uninstaller cannot tell whether the token-leak guard was on, so it
    says this in its place. The cleanup still puts back the setting the guard changed itself (or names it among what it
    could not put back); only one the guard found already changed stays (the list's own words for that are
    ChangeWords.TokenGuardSettingStays). }
  CaveatIfGuardWasOn = 'If the token-leak guard was on and found its Windows setting already changed, that setting stays until you sign out.';

var
  { Every change PoolWatch recorded was put back: only then is deleting the data folder offered (it holds the records a
    later PoolWatch needs to put them back). }
  ChangesPutBack: Boolean;
  { What PoolWatch listed before the uninstall (ReadChangeList): the changes uninstalling puts back, and the ones it
    cannot put back with why, each as lines to show (the first MaxShown) and as a count; and what stays for a while
    after the ones it puts back (each once, a line of its own). }
  ListRead: Boolean;
  PutBackList, LeftList, CaveatList: String;
  PutBackCount, LeftCount: Integer;
  { What the uninstaller's last wait for the install folder found (WaitForApp): whether to run the cleanup once more,
    and for its log. }
  AppCount: Integer;
  { The processes with PoolWatch's name that the last look at Windows' list found running the install folder's
    PoolWatch.exe, or whose exe it could not read (so they may), the first MaxProcessesShown of them, each with its
    process ID and its account, and said for the second kind (RunningFromApp): for the log. What the boxes name is what
    has each file in use (HoldersOf). }
  RunningNames: String;
  { The setup's page for the wait before files are replaced (made in InitializeWizard; the uninstaller has none). }
  WaitPage: TOutputProgressWizardPage;
  { The files of PoolWatch's program in the install folder that the last look found in use, so that they could not be
    replaced or removed then (HeldFiles): the first MaxShown of them as lines to show, each with what has it open when
    the look was logged, and how many; and every one of them as lines (HeldAll), for the setup's Finished page.
    HeldUninstaller: in the setup, one of the uninstaller's own files there (unins*) is in use, so the setup writes its
    uninstaller elsewhere (UninstallDir). }
  HeldList, HeldAll: String;
  HeldCount: Integer;
  HeldUninstaller: Boolean;
  { The accounts that run the processes the logged looks named (AccountOf), as '|<process ID>=<account>|' each, and the
    one WMI connection it reads them through (AccountWmi: 0 not made yet, 1 made, -1 could not be made). }
  Accounts: String;
  AccountService: Variant;
  AccountWmi: Integer;
  { The setup puts PoolWatch's files in place at the next restart of Windows (decided before anything is put in place:
    LookForRestartWork, WhenToReplace, SetAllAside), from StageDir, where [Files] puts them until then (ProgramDir);
    FinishReason says why, and FinishFiles names every file that was in use with what had it open, one per line, for
    the Finished page and the log. NotScheduled: how many of them Windows could not be given to move then
    (SwapAtRestart); Unkept: how many of the new files another program then kept from being taken back, so that
    Windows still moves them into place then (Unstage); TakenOut: how many files of the version in place Unstage set
    aside now, as neither a copy nor a second name of one could be kept, so that such a move does not replace it;
    LauncherKept: how many of the launcher's names may still hold a file that starts PoolWatch after that restart, as
    another program kept it from being set aside and Windows would not take it to set aside then (KeepLauncherOut).
    RestartWorkLooked: Windows' list has been read for this run (LookForRestartWork). }
  FinishAtRestart, RestartWorkLooked: Boolean;
  StageDir, FinishReason, FinishFiles: String;
  NotScheduled, Unkept, TakenOut, LauncherKept: Integer;
  { This setup run's own name (RunStamp). Each old file of the install folder that SetAllAside renamed aside before the
    new ones were written (AsideTargets: its place; AsideNames: its name now), kept until the setup has put every new
    file in place (DropAside), or put back when it did not (DeinitializeSetup). }
  Stamp: String;
  AsideTargets, AsideNames: TArrayOfString;
  { Every file [Files] was about to write, whether or not it was written (Shipping): the files this version has. }
  Shipped: TArrayOfString;
  { The Apps and Features entry as it was before the files (SaveEntryBefore): each value's name, and its text or its
    number (EntryIsNumber); EntrySaved when the entry was there, and EntryCount, how many values it had then (one that
    is neither text nor a number among them); EntryNew when there was no entry then. And the uninstaller's program and
    messages in the install folder that Inno Setup replaces (UninstallerBefore: their names, copied into Setup's
    temporary folder). For DeinitializeSetup, which puts them back when the setup does not finish. }
  EntryNames, EntryText: TArrayOfString;
  EntryNumber: array of Cardinal;
  EntryIsNumber: array of Boolean;
  EntrySaved, EntryNew: Boolean;
  EntryCount: Integer;
  UninstallerBefore: TArrayOfString;
  { The files a setup that finishes at the next restart left in folders its files move into then (KeepFolder), for
    Windows to delete after the moves. }
  Keeps: TArrayOfString;
  { Where this run writes its uninstaller (UninstallDir), once decided. }
  UninstallerDir: String;
  { Whether the install folder had a PoolWatch-selfcontained.exe when the setup first looked (SelfContainedNameThere),
    kept, since SetAllAside renames that file aside before [Files] asks. }
  SecondNameLooked, SecondNameWas: Boolean;
  { The Start menu entry as it was before the files were set aside, copied into Setup's temporary folder, so that a
    setup that does not finish can put it back after Inno Setup's rollback deleted it (DeinitializeSetup); '' when there
    was none. InstallBegun: the setup got to ssInstall, where it first changes one of PoolWatch's files or entries
    (before that it may only have asked PoolWatch to exit, ended a token-leak test helper, and deleted what an earlier
    setup left in its own folder for a restart that has happened since, WhenToReplace: nothing a step puts back; it
    closes no program, CloseApplications being off), and
    the install folder (the app constant) may not be known before it (Inno Setup sets it once its wizard has passed the
    folder page, which it does even with that page hidden). InstallDone: the setup got to its end (ssPostInstall). }
  LinkBefore: String;
  InstallBegun, InstallDone: Boolean;
  { OldAtRestart: an old file of the version that was there goes back only at the next restart, or stays set aside
    (PutAllBack); what starts PoolWatch until then is read from the install folder (UntilRestart). Refused: how many old
    files Windows would not take to put back at that restart, and new files it would not take to delete then
    (MoveBackAtRestart, PutBack, TakeBack): each stays as it is. LeftNew: each new file TakeBack left at its own name as
    Windows would not take it to delete then either, lower case, each followed by #0 (LeftNewHas). OldUninstaller: when
    the setup began, the install folder had no launcher of its program's version, so its uninstaller may be one made
    before the launcher, which would not wait for that restart (HoldOldUninstaller); UninstallerProgramHeld: that
    uninstaller's program, not its record or its messages file, was set aside until then. }
  OldAtRestart, OldUninstaller, UninstallerProgramHeld: Boolean;
  Refused: Integer;
  LeftNew: String;
  { What the uninstaller gave Windows to delete at the next restart because it was still in use (RemoveRest): the first
    MaxShown files as lines to show and how many files; how many things it gave, files and folders; how many things
    Windows could not be given; and how many files an earlier setup may have Windows move into the install folder at
    that restart, which would stay after it, as their place could not be made for Windows to delete after the move
    (KeepOut). }
  RestartList: String;
  RestartCount, RestartGiven, RestartFailed, RestartStays: Integer;
  { The setup asked a PoolWatch installed before the launcher to exit through its exit signal (StopBeforeLauncher): the
    wait sets it again (WaitForApp). }
  StoppingOld: Boolean;

function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/' + Name) = 0 then
      Result := True;
end;

{ S cut to at most Max characters, ending in '...' when it was cut. }
function Shorten(const S: String; const Max: Integer): String;
begin
  if Length(S) > Max then
    Result := Copy(S, 1, Max - 3) + '...'
  else
    Result := S;
end;

{ Adds a line to a list to show: only the first MaxShown are named, the rest are counted (MoreLine); each is cut to
  MaxLineShown characters. }
procedure AddShown(var List: String; var Count: Integer; const Line: String);
begin
  Count := Count + 1;
  if Count <= MaxShown then
    List := List + #13#10 + '- ' + Shorten(Line, MaxLineShown);
end;

function MoreLine(const Count: Integer): String;
begin
  if Count > MaxShown then
    Result := #13#10 + Format('And %d more.', [Count - MaxShown])
  else
    Result := '';
end;

function InitializeSetup: Boolean;
begin
  { Shown only to someone at the setup: a silent install must never wait for a click. }
  if IsArm64 and not WizardSilent then
    MsgBox('PoolWatch is built for x64 PCs. Windows on Arm runs it through its x64 emulation, where PoolWatch has not been tested yet.',
      mbInformation, MB_OK);
  Result := True;
end;

{ The Windows calls the setup and the uninstaller read a process's exe with (FromApp), and end a token-leak test helper
  with (EndHelper). QueryFullProcessImageNameW gives the exe's path from the kernel, so a junction or subst spelling the
  process was started through is resolved to the real file (unlike the path WMI reads from the process's own memory). }
function OpenProcess(DesiredAccess: DWORD; InheritHandle: BOOL; ProcessId: DWORD): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageNameW(Process: THandle; Flags: DWORD; ExeName: String; var Size: DWORD): BOOL;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function TerminateProcess(Process: THandle; Code: DWORD): BOOL;
  external 'TerminateProcess@kernel32.dll stdcall';
function CloseHandle(Opened: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
{ For StopBeforeLauncher: the exit signal of a PoolWatch installed before the launcher, set by the setup itself. }
function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
{ For ProductVersionOf: a program's version resource, as one block, into a buffer of the caller's (no pointer comes
  back, so the script reads the block itself). The DLLs below are loaded only when first called (delayload), so one that
  cannot be loaded raises there, where the caller catches it, and never keeps the setup or the uninstaller from
  starting. }
function GetFileVersionInfoSizeW(Filename: String; var Handle: DWORD): DWORD;
  external 'GetFileVersionInfoSizeW@version.dll stdcall delayload';
function GetFileVersionInfoW(Filename: String; Handle, Size: DWORD; Data: String): BOOL;
  external 'GetFileVersionInfoW@version.dll stdcall delayload';
{ For HoldersOf: Restart Manager, which names the programs that have a file open (the list comes back into a buffer of
  the caller's; no pointer comes back). The files are passed as an array of strings, which the script passes as the
  array of string pointers the call takes; the two lists left empty are passed as nil. }
function RmStartSession(var Session: DWORD; Flags: DWORD; Key: String): DWORD;
  external 'RmStartSession@rstrtmgr.dll stdcall delayload';
function RmRegisterResources(Session: DWORD; FileCount: DWORD; Files: TArrayOfString; ProcessCount: DWORD;
  Processes: TArrayOfString; ServiceCount: DWORD; Services: TArrayOfString): DWORD;
  external 'RmRegisterResources@rstrtmgr.dll stdcall delayload';
function RmGetList(Session: DWORD; var Needed, Count: DWORD; Infos: String; var Reasons: DWORD): DWORD;
  external 'RmGetList@rstrtmgr.dll stdcall delayload';
function RmEndSession(Session: DWORD): DWORD;
  external 'RmEndSession@rstrtmgr.dll stdcall delayload';
{ For PutBack and Unstage: gives the existing file ExistingFileName the second name FileName in the same folder (a hard
  link: one file, two names; its third argument, reserved, is passed as nil). Another program that has the file open
  without sharing it at all stops MoveFileEx (no FILE_SHARE_DELETE) and FileCopy (no FILE_SHARE_READ), but not this: a
  test makes such a name while a FileStream holds the file with FileShare.None, as Windows checks the share modes of a
  file's open handles only for an open that reads, writes, runs or deletes it. }
function CreateHardLinkW(FileName, ExistingFileName: String; Reserved: THandle): BOOL;
  external 'CreateHardLinkW@kernel32.dll stdcall';

{ The character of S at I, or #0 before its start or past its end, so that a test never reads outside it. }
function CharAt(const S: String; I: Integer): Char;
begin
  if (I >= 1) and (I <= Length(S)) then
    Result := S[I]
  else
    Result := #0;
end;

{ Whether a command line is a token-leak test helper's (PoolWatch.exe --token-leak-test): the program's name (from a
  quote to the next quote, or up to the first space or tab), then a space or a tab, then HelperOption as a whole word.
  Only a space and a tab separate (any other character, a control character or another kind of space included, is part
  of a word), and a #0 ends the line. PoolWatch's own reader (LeftoverHelpers.IsHelper) follows the same rules, and a
  test holds the two to the same answer for each line it lists. Neither is Windows' own split of a command line, which
  for one ends an unquoted program name at any control character. }
function IsHelperLine(const Line: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  I := 1;
  while (CharAt(Line, I) = ' ') or (CharAt(Line, I) = #9) do
    I := I + 1;
  if CharAt(Line, I) = '"' then
  begin
    I := I + 1;
    while (CharAt(Line, I) <> '"') and (CharAt(Line, I) <> #0) do
      I := I + 1;
    if CharAt(Line, I) = #0 then
      Exit;
    I := I + 1;
  end
  else
    while (CharAt(Line, I) <> ' ') and (CharAt(Line, I) <> #9) and (CharAt(Line, I) <> #0) do
      I := I + 1;
  if (CharAt(Line, I) <> ' ') and (CharAt(Line, I) <> #9) then
    Exit;
  while (CharAt(Line, I) = ' ') or (CharAt(Line, I) = #9) do
    I := I + 1;
  Result := (Copy(Line, I, Length(HelperOption)) = HelperOption) and
    ((CharAt(Line, I + Length(HelperOption)) = #0) or (CharAt(Line, I + Length(HelperOption)) = ' ') or
     (CharAt(Line, I + Length(HelperOption)) = #9));
end;

{ The install folder App's file with the launcher's second name, PoolWatch-selfcontained.exe (SelfContainedName). }
function SecondLauncherPath(const App: String): String;
begin
  Result := App + '\' + SelfContainedName;
end;

{ The install folder App's file with the launcher's usual name, PoolWatch.exe, or, when only the launcher's second name
  is there, PoolWatch-selfcontained.exe (SecondLauncherPath): the file a process runs when it runs the launcher, or, in
  an install made before the launcher, PoolWatch itself. By name only, for the list of processes: which program it is,
  and whether it may be started, LauncherPath decides. }
function LauncherFile(const App: String): String;
begin
  Result := App + '\' + PoolWatchName;
  if not FileExists(Result) and FileExists(SecondLauncherPath(App)) then
    Result := SecondLauncherPath(App);
end;

{ For [Files]: whether the install folder has a file with the launcher's second name (a copy of a zip was unpacked
  there, or an earlier setup put the launcher there too). The launcher then goes there as well, so a logon task or a
  shortcut that names that file keeps starting PoolWatch through its launcher. Decided at the first look and kept:
  SetAllAside renames that file aside before [Files] asks. }
function SelfContainedNameThere: Boolean;
begin
  if not SecondNameLooked then
  begin
    SecondNameLooked := True;
    SecondNameWas := FileExists(SecondLauncherPath(ExpandConstant('{app}')));
  end;
  Result := SecondNameWas;
end;

{ PoolWatch itself in the install folder App, below its launcher: app\PoolWatch.exe. }
function ProgramPath(const App: String): String;
begin
  Result := App + '\' + AppFolder + '\' + PoolWatchName;
end;

{ Whether the install folder App has the launcher's layout (PoolWatch itself in its app folder); False for an install
  made before the launcher, whose PoolWatch.exe is PoolWatch itself. }
function HasLauncher(const App: String): Boolean;
begin
  Result := FileExists(ProgramPath(App));
end;

{ The product version of the program at Path, as Windows' version reader gives it (what .NET's
  FileVersionInfo.ProductVersion gives for PoolWatch's programs): the ProductVersion string of its version resource; ''
  when it has none or the file cannot be read. GetFileVersionInfoW copies the whole resource into Data, where each
  string is a structure of its own (its length, its value's length and its type, then its key in UTF-16 with a #0,
  then its value, which starts on a 32-bit boundary of the block); this finds the first one keyed ProductVersion and,
  when its value is not empty, reads that value up to its #0. A test holds a copy of this reader to Windows' own answer
  for real programs. }
function ProductVersionOf(const Path: String): String;
var
  Handle, Size: DWORD;
  Data: String;
  P, V: Integer;
begin
  Result := '';
  try
    Handle := 0;
    Size := GetFileVersionInfoSizeW(Path, Handle);
    if Size = 0 then
      Exit;
    SetLength(Data, (Size + 1) div 2);
    if not GetFileVersionInfoW(Path, 0, Size, Data) then
      Exit;
    P := Pos('ProductVersion' + #0, Data);
    if (P < 4) or (Ord(CharAt(Data, P - 2)) = 0) then
      Exit;
    V := P + Length('ProductVersion') + 1;
    if (V mod 2) = 0 then
      V := V + 1;
    while CharAt(Data, V) <> #0 do
    begin
      Result := Result + CharAt(Data, V);
      V := V + 1;
    end;
  except
    Log('The version of ' + Path + ' could not be read: ' + GetExceptionMessage);
    Result := '';
  end;
end;

{ Whether the file Path, with one of the launcher's names, is the launcher of the install folder App, as PoolWatch's own
  LaunchGate.Judge decides: PoolWatch itself is below it (ProgramPath), and the two have the same product version, read
  from each file (ProductVersionOf: the build's version and commit, which CI holds equal for the launcher and the
  program of a build). An older PoolWatch left under the launcher's name (a zip unpacked over a copy while that file was
  in use) has another product version, and so has an older launcher. }
function IsLauncherOf(const Path, App: String): Boolean;
var
  Version: String;
begin
  Result := False;
  if not FileExists(Path) or not HasLauncher(App) then
    Exit;
  Version := ProductVersionOf(Path);
  Result := (Version <> '') and (Version = ProductVersionOf(ProgramPath(App)));
end;

{ The install folder App's launcher, to start: its PoolWatch.exe, or else its PoolWatch-selfcontained.exe, whichever is
  first the launcher of the program below it (IsLauncherOf), as LaunchGate.LauncherOf picks; '' when neither is (an
  install made before the launcher, or an older program left under the launcher's name). The setup and the uninstaller
  start PoolWatch only through this one. }
function LauncherPath(const App: String): String;
begin
  Result := '';
  if IsLauncherOf(App + '\' + PoolWatchName, App) then
    Result := App + '\' + PoolWatchName
  else if IsLauncherOf(SecondLauncherPath(App), App) then
    Result := SecondLauncherPath(App);
end;

{ Windows' list of the processes that may run the install folder's PoolWatch (Exe, PoolWatch itself; ShortExe, its
  short form) or its launcher (ShortLauncher, the short form of its PoolWatch.exe; ShortSecond, the short form of the
  launcher's second name, PoolWatch-selfcontained.exe; each '' when it is not there or there is no launcher): those
  named PoolWatch.exe (PoolWatch itself and the launcher have that name) or PoolWatch-selfcontained.exe, or one of
  their short (8.3) names, with their handle, process ID and command line; with Only, the one with that process ID.
  The exe path is not asked for: WMI reads it from the process's own memory, so a junction or subst spelling it was
  started through would show through; FromApp reads the kernel's path instead. Raises an exception when the list cannot
  be read. }
function PoolWatchProcesses(Service: Variant; const Exe, ShortExe, ShortLauncher, ShortSecond: String;
  Only: Integer): Variant;
var
  Query: String;
begin
  Query := 'SELECT Handle, ProcessId, CommandLine FROM Win32_Process WHERE (Name = "' +
    ExtractFileName(Exe) + '" OR Name = "' + ExtractFileName(ShortExe) + '"';
  if ShortLauncher <> '' then
    Query := Query + ' OR Name = "' + ExtractFileName(ShortLauncher) + '"';
  if ShortSecond <> '' then
    Query := Query + ' OR Name = "' + SelfContainedName + '" OR Name = "' + ExtractFileName(ShortSecond) + '"';
  Query := Query + ')';
  if Only <> 0 then
    Query := Query + ' AND ProcessId = ' + IntToStr(Only);
  Result := Service.ExecQuery(Query);
end;

{ The full path of the exe that process Id runs, read from the kernel (QueryFullProcessImageNameW), so a junction or
  subst spelling the process was started through is resolved to the real file (unlike the path WMI reads from the
  process's own memory). '' when the process could not be opened (another account's, or already gone) or its path could
  not be read. The process is opened for a limited query only, and the handle is always closed. }
function ImagePathOf(Id: Integer): String;
var
  Handle: THandle;
  Buffer: String;
  Size: DWORD;
begin
  Result := '';
  Handle := OpenProcess(ReadRights, False, Id);
  if Handle = 0 then
    Exit;
  try
    SetLength(Buffer, 32768);
    Size := Length(Buffer);
    if QueryFullProcessImageNameW(Handle, 0, Buffer, Size) then
      Result := Copy(Buffer, 1, Size);
  finally
    CloseHandle(Handle);
  end;
end;

{ How the process Item stands to the install folder's PoolWatch (ShortExe, the short form of PoolWatch itself) and its
  launcher (ShortLauncher and ShortSecond, the short forms of its two names, each '' when it is not there or there is no
  launcher): 1, it runs PoolWatch itself; 2, it runs the launcher; 0, it runs another exe; -1, its exe could not be read
  (the process could not be opened, or Windows gave no path), so it may run either. The path comes from the kernel
  (ImagePathOf), which gives the real folder for a process started through a junction or a subst drive, but keeps a
  short (8.3) or mixed spelling as the process was started; the paths are compared in their short (8.3) form, so the
  folder's long spelling, its short one and a mix of the two all count. }
function FromApp(Item: Variant; const ShortExe, ShortLauncher, ShortSecond: String): Integer;
var
  Image, ShortImage: String;
begin
  Image := ImagePathOf(Item.ProcessId);
  if Image = '' then
    Result := -1
  else
  begin
    ShortImage := GetShortName(Image);
    if CompareText(ShortImage, ShortExe) = 0 then
      Result := 1
    else if ((ShortLauncher <> '') and (CompareText(ShortImage, ShortLauncher) = 0)) or
            ((ShortSecond <> '') and (CompareText(ShortImage, ShortSecond) = 0)) then
      Result := 2
    else
      Result := 0;
  end;
end;

{ The process Item's command line, or '' when Windows did not give it (which is never a helper's). }
function CommandLineOf(Item: Variant): String;
var
  Value: Variant;
begin
  Value := Item.CommandLine;
  if VarIsNull(Value) then
    Result := ''
  else
    Result := Value;
end;

{ The account that runs the process Item (domain\user), for the log and the boxes; said so when Windows does not say. }
function OwnerOf(Item: Variant): String;
var
  Owner: Variant;
  User, Domain: String;
begin
  Result := 'an account Windows did not name';
  try
    Owner := Item.ExecMethod_('GetOwner');
    if (Owner.ReturnValue = 0) and not VarIsNull(Owner.User) then
    begin
      User := Owner.User;
      if VarIsNull(Owner.Domain) then
        Result := User
      else
      begin
        Domain := Owner.Domain;
        Result := Domain + '\' + User;
      end;
    end;
  except
    Log('The account that runs a PoolWatch process could not be read: ' + GetExceptionMessage);
  end;
end;

{ Ends the token-leak test helper with process ID Id. It opens the process first: while that handle is open, Windows
  gives the ID to no other process. Then it reads Windows' list again for that ID, and ends the process (through that
  handle) only when it is still a helper (IsHelperLine) that runs the install folder's PoolWatch itself (FromApp, 1;
  never its launcher), as PoolWatch's own --stop-running and --uninstall-cleanup do (LeftoverHelpers). Each step is
  logged; it never raises. }
procedure EndHelper(Service: Variant; const Exe, ShortExe, ShortLauncher, ShortSecond: String; Id: Integer);
var
  Opened: THandle;
  Found, Item: Variant;
begin
  Opened := OpenProcess(EndRights, False, Id);
  if Opened = 0 then
  begin
    Log(Format('The token-leak test helper (process %d) could not be opened to end it (error %d).', [Id, DLLGetLastError]));
    Exit;
  end;
  try
    Found := PoolWatchProcesses(Service, Exe, ShortExe, ShortLauncher, ShortSecond, Id);
    if Found.Count <> 1 then
      Log(Format('The token-leak test helper (process %d) has ended by itself.', [Id]))
    else
    begin
      Item := Found.ItemIndex(0);
      if (FromApp(Item, ShortExe, ShortLauncher, ShortSecond) <> 1) or not IsHelperLine(CommandLineOf(Item)) then
        Log(Format('Process %d is no longer a token-leak test helper that runs from the install folder: it is left as it is.', [Id]))
      else if TerminateProcess(Opened, 1) then
        Log(Format('Ended the token-leak test helper (process %d) that still ran from the install folder.', [Id]))
      else
        Log(Format('The token-leak test helper (process %d) could not be ended (error %d).', [Id, DLLGetLastError]));
    end;
  except
    Log(Format('Whether process %d is still a token-leak test helper could not be checked, so it is left as it is: %s', [Id, GetExceptionMessage]));
  end;
  CloseHandle(Opened);
end;

{ How many processes run the install folder's PoolWatch (Exe, PoolWatch itself) or its launcher (Launcher and Second,
  its two names, each '' when it is not there or there is no launcher) now, from Windows' list of processes (FromApp);
  -1 when the list could not be read, or when it names a process with PoolWatch's name whose exe could not be read (it
  could not be opened, or Windows gave no path, so it may be PoolWatch and the count is not known). RunningNames gets
  the first of both kinds, each with its process ID and its account, and says which ones Windows gave no exe for; with
  Logged, each is logged with its command line too. Every mode of PoolWatch counts, and the launcher under either name,
  which runs for as long as the PoolWatch it started: while any process runs one of them, that exe can be neither
  removed nor replaced. With EndHelpers, when every process that runs them is a token-leak test helper (PoolWatch.exe
  --token-leak-test, IsHelperLine, running PoolWatch itself; a launcher never is) and none is of the second kind, they
  are ended (EndHelper). Such a helper holds no mutex and can run on after the
  PoolWatch that started it is gone, for example waiting for a click on its notice, on any desktop; nothing else of
  PoolWatch runs then that could still want it. A PoolWatch whose own --stop-running ends no helper, as an earlier one
  did not, is upgraded so too. }
function RunningFromApp(Service: Variant; const Exe, Launcher, Second: String; Logged, EndHelpers: Boolean): Integer;
var
  ShortExe, ShortLauncher, ShortSecond, Line, Shown, Who, Which: String;
  Found, Item: Variant;
  I, Id, Total, Where, Others, Unknown, Named: Integer;
begin
  ShortExe := GetShortName(Exe);
  ShortLauncher := '';
  if Launcher <> '' then
    ShortLauncher := GetShortName(Launcher);
  ShortSecond := '';
  if Second <> '' then
    ShortSecond := GetShortName(Second);
  Result := 0;
  Others := 0;
  Unknown := 0;
  Named := 0;
  RunningNames := '';
  try
    Found := PoolWatchProcesses(Service, Exe, ShortExe, ShortLauncher, ShortSecond, 0);
    Total := Found.Count;
    for I := 0 to Total - 1 do
    begin
      Item := Found.ItemIndex(I);
      Id := Item.ProcessId;
      Line := CommandLineOf(Item);
      if Line = '' then
        Shown := '(command line not readable)'
      else
        Shown := Line;
      Where := FromApp(Item, ShortExe, ShortLauncher, ShortSecond);
      if Where = 2 then
        Which := 'its launcher, '
      else
        Which := '';
      if Where <> 0 then
      begin
        Who := OwnerOf(Item);
        if Named < MaxProcessesShown then
        begin
          if RunningNames <> '' then
            RunningNames := RunningNames + '; ';
          if Where > 0 then
            RunningNames := RunningNames + Format('%sprocess %d, run by %s', [Which, Id, Who])
          else
            RunningNames := RunningNames + Format('process %d, run by %s, whose exe Windows did not give', [Id, Who]);
          Named := Named + 1;
        end;
      end;
      if Where > 0 then
      begin
        Result := Result + 1;
        if (Where = 2) or not IsHelperLine(Line) then
          Others := Others + 1;
        if Logged then
          Log(Format('PoolWatch still runs from the install folder: %sprocess %d, run by %s: %s', [Which, Id, Who, Shown]));
      end
      else if Where < 0 then
      begin
        Unknown := Unknown + 1;
        if Logged then
          Log(Format('Process %d has PoolWatch''s name but its exe could not be read, so it may run from the install folder (run by %s): %s', [Id, Who, Shown]));
      end;
    end;
    if Result + Unknown > MaxProcessesShown then
      RunningNames := RunningNames + Format('; and %d more', [Result + Unknown - MaxProcessesShown]);
    if Unknown > 0 then
      Result := -1
    else if EndHelpers and (Result > 0) and (Others = 0) then
      for I := 0 to Total - 1 do
      begin
        Item := Found.ItemIndex(I);
        if FromApp(Item, ShortExe, ShortLauncher, ShortSecond) = 1 then
        begin
          Id := Item.ProcessId;
          EndHelper(Service, Exe, ShortExe, ShortLauncher, ShortSecond, Id);
        end;
      end;
  except
    Log('The processes that run from the install folder could not be listed: ' + GetExceptionMessage);
    Result := -1;
  end;
end;

{ Whether the file Path can be replaced or removed now. It is opened for reading and writing with no sharing, which
  Windows refuses while a process runs the file or has it loaded (an exe or a DLL), and while any other program has it
  open; it is closed at once, unchanged. }
function CanReplace(const Path: String): Boolean;
var
  Probe: TFileStream;
begin
  try
    Probe := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
    Probe.Free;
    Result := True;
  except
    Result := False;
  end;
end;

{ Whether the file Path can be replaced or removed (CanReplace) now or within BriefHold seconds, looking again each
  quarter of a second: a virus scanner or the search indexer has a file open only for a moment, and should not send an
  update to the next restart. }
function FreeSoon(const Path: String): Boolean;
var
  Quarter: Integer;
begin
  Result := CanReplace(Path);
  Quarter := 0;
  while not Result and (Quarter < BriefHold * 4) do
  begin
    Sleep(250);
    Quarter := Quarter + 1;
    Result := CanReplace(Path);
  end;
end;

{ Renames the file Path to Aside once it is free (CanReplace), now or within BriefHold seconds, looking again each
  quarter of a second as FreeSoon does, and trying the rename again with each look: a program that opens the file only
  for a moment, also between a look and the rename, decides nothing. True when it was renamed. }
function SetAsideSoon(const Path, Aside: String): Boolean;
var
  Quarter: Integer;
begin
  Result := False;
  Quarter := 0;
  repeat
    if Quarter > 0 then
      Sleep(250);
    if CanReplace(Path) then
      Result := RenameFile(Path, Aside);
    Quarter := Quarter + 1;
  until Result or (Quarter > BriefHold * 4);
end;

{ One WMI connection to the local machine, made once for a whole wait and used for every look at the list, so a wait
  does not reconnect each second. Raises when it cannot connect. }
function ConnectWmi: Variant;
var
  Locator: Variant;
begin
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Result := Locator.ConnectServer('.', 'root\CIMV2');
end;

{ The account that runs process Id (OwnerOf), from Windows' list of processes; said so when it cannot be read. Read once
  per process for each logged look (Accounts, which HeldFiles and RemoveRest empty first), through one WMI connection
  made at the first call and kept (AccountWmi), so that a look at many files a process has in use asks Windows once. }
function AccountOf(Id: Integer): String;
var
  Found: Variant;
  Key: String;
  P: Integer;
begin
  Key := '|' + IntToStr(Id) + '=';
  P := Pos(Key, Accounts);
  if P > 0 then
  begin
    Result := Copy(Accounts, P + Length(Key), Length(Accounts));
    Result := Copy(Result, 1, Pos('|', Result) - 1);
    Exit;
  end;
  Result := 'an account Windows did not name';
  try
    if AccountWmi = 0 then
    begin
      AccountWmi := -1;
      AccountService := ConnectWmi;
      AccountWmi := 1;
    end;
    if AccountWmi = 1 then
    begin
      Found := AccountService.ExecQuery('SELECT Handle, ProcessId FROM Win32_Process WHERE ProcessId = ' + IntToStr(Id));
      if Found.Count = 1 then
        Result := OwnerOf(Found.ItemIndex(0));
    end;
  except
    Log(Format('The account that runs process %d could not be read: %s', [Id, GetExceptionMessage]));
  end;
  Accounts := Accounts + Key + Result + '|';
end;

{ What has the file Path open now, as Restart Manager names it (RmGetList, which Windows Installer and Inno Setup ask
  the same: each program, service or other process that has the file open or loaded): each one's name, its process ID
  and the account that runs it, as text to put after the file's name; a sentence of its own when Windows named none or
  could not be asked. The list comes back in Infos, one RM_PROCESS_INFO after another (RmInfoChars characters each),
  with the process ID in its first two characters and the program's name from its seventh, up to its #0. The name is
  the program's own choice, so each control character of it, and Unicode's line and paragraph separators, some of
  which start a new line in the log, a box or the setup's last page, is put as a space: each name stays on the line it
  is on. A test holds a copy of this reader to what Restart Manager gives for a file the test has open. Never raises. }
function HoldersOf(const Path: String): String;
var
  Session, Needed, Count, Reasons, Code: DWORD;
  Key, Infos, Name: String;
  Files, NoList: TArrayOfString;
  I, N, Base, C, Id, K: Integer;
begin
  Result := '';
  try
    Session := 0;
    SetLength(Key, RmKeyChars);
    if RmStartSession(Session, 0, Key) <> 0 then
    begin
      Result := ' (Windows could not say what has it open)';
      Exit;
    end;
    try
      SetArrayLength(Files, 1);
      Files[0] := Path;
      Count := 0;
      Code := RmRegisterResources(Session, 1, Files, 0, NoList, 0, NoList);
      if Code = 0 then
      begin
        Count := RmMaxRead;
        SetLength(Infos, Count * RmInfoChars);
        Needed := 0;
        Reasons := 0;
        Code := RmGetList(Session, Needed, Count, Infos, Reasons);
        if Code = ErrorMoreData then
        begin
          Count := Needed;
          SetLength(Infos, Count * RmInfoChars);
          Code := RmGetList(Session, Needed, Count, Infos, Reasons);
        end;
      end;
      if Code <> 0 then
        Result := ' (Windows could not say what has it open)'
      else if Count = 0 then
        Result := ' (Windows named no program that has it open)'
      else
      begin
        N := Count;
        for I := 0 to N - 1 do
        begin
          Base := I * RmInfoChars + 1;
          Id := Ord(CharAt(Infos, Base)) + Ord(CharAt(Infos, Base + 1)) * 65536;
          Name := '';
          C := Base + 6;
          while (C < Base + 6 + 256) and (CharAt(Infos, C) <> #0) do
          begin
            K := Ord(CharAt(Infos, C));
            if (K < 32) or ((K >= 127) and (K < 160)) or (K = 8232) or (K = 8233) then
              Name := Name + ' '
            else
              Name := Name + CharAt(Infos, C);
            C := C + 1;
          end;
          if Result <> '' then
            Result := Result + '; ';
          Result := Result + Format('%s, process %d, run by %s', [Name, Id, AccountOf(Id)]);
        end;
        Result := ' (in use by ' + Result + ')';
      end;
    finally
      RmEndSession(Session);
    end;
  except
    Log('What has ' + Path + ' open could not be read: ' + GetExceptionMessage);
    Result := ' (Windows could not say what has it open)';
  end;
end;

{ Adds the files of PoolWatch's program in Dir (Rel: Dir's path below the install folder) that cannot be replaced or
  removed now (CanReplace) to HeldList and HeldCount, and to HeldAll, and those in each folder below it; a link is never
  followed. With Logged, each is logged with what has it open (HoldersOf), and named so in both lists. Not looked at:
  the folders in the install folder itself where a setup that finishes at the next restart puts its files (StageFolder)
  and where a setup writes an uninstaller it could not write in the install folder (UninstallFolder), which hold nothing
  of the program; nor the uninstaller's own files in the install folder itself (unins*), which Inno Setup writes and
  deletes itself. In the setup, one of those in use sets HeldUninstaller instead, so that the setup writes its
  uninstaller elsewhere (UninstallDir). }
procedure FindHeld(const Dir, Rel: String; Logged: Boolean);
var
  Find: TFindRec;
  Name, Path, Holders: String;
  Skip, Own: Boolean;
begin
  if not FindFirst(Dir + '\*', Find) then
    Exit;
  try
    repeat
      Name := Find.Name;
      Path := Dir + '\' + Name;
      Own := (Rel = '') and (CompareText(Copy(Name, 1, 5), 'unins') = 0);
      Skip := (Name = '.') or (Name = '..') or
        ((Rel = '') and ((CompareText(Name, StageFolder) = 0) or (CompareText(Name, UninstallFolder) = 0)));
      if not Skip then
      begin
        if (Find.Attributes and AttrFolder) <> 0 then
        begin
          if (Find.Attributes and AttrLink) = 0 then
            FindHeld(Path, Rel + Name + '\', Logged);
        end
        else if Own then
        begin
          if not IsUninstaller and not CanReplace(Path) then
          begin
            HeldUninstaller := True;
            if Logged then
              Log('In use, so Setup writes its uninstaller in a folder of its own: ' + Path + HoldersOf(Path));
          end;
        end
        else if not CanReplace(Path) then
        begin
          Holders := '';
          if Logged then
            Holders := HoldersOf(Path);
          AddShown(HeldList, HeldCount, Rel + Name + Holders);
          HeldAll := HeldAll + #13#10 + Rel + Name + Holders;
          if Logged then
            Log('In use, so it cannot be replaced or removed now: ' + Path + Holders);
        end;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

{ How many files of PoolWatch's program in the install folder App are in use now, so that they cannot be replaced or
  removed (FindHeld), from a fresh look; HeldList names the first of them, HeldAll every one. A logged look reads each
  holder's account afresh (Accounts). }
function HeldFiles(const App: String; Logged: Boolean): Integer;
begin
  HeldList := '';
  HeldAll := '';
  HeldCount := 0;
  HeldUninstaller := False;
  if Logged then
    Accounts := '';
  FindHeld(App, '', Logged);
  Result := HeldCount;
end;

{ Whether Windows' value ValueName (under SessionManagerKey) names Folder (lower case, as \??\ and the full path), or a
  file or folder in it, among what MoveFileEx gave it to rename or delete at the next restart. }
function WorkFor(const ValueName, Folder: String): Boolean;
var
  Work: String;
begin
  Result := False;
  if RegQueryMultiStringValue(HKLM, SessionManagerKey, ValueName, Work) then
  begin
    Work := Lowercase(Work) + #0;
    Result := (Pos(Folder + '\', Work) > 0) or (Pos(Folder + #0, Work) > 0);
  end;
end;

{ Whether Windows has work in the install folder App for its next restart: a file to move into place (an earlier setup
  that finishes then) or to delete (an uninstall that removes the rest then). That work comes after anything the setup
  put in place now, and would undo it. }
function RestartWorkPending(const App: String): Boolean;
begin
  Result := WorkFor('PendingFileRenameOperations', Lowercase('\??\' + App)) or
    WorkFor('PendingFileRenameOperations2', Lowercase('\??\' + App));
end;

{ A path as Windows' list writes it (ReadRestartWork), without what may come before it: the ! of a move that may replace
  a file (MOVEFILE_REPLACE_EXISTING), the *1 or *2 that recent Windows builds put first, before that ! (a move's target
  reads *1!\??\...), and that other writers may put after it, and the \??\. }
function ListedPath(const Item: String): String;
begin
  Result := Item;
  if (CharAt(Result, 1) = '*') and ((CharAt(Result, 2) = '1') or (CharAt(Result, 2) = '2')) then
    Result := Copy(Result, 3, Length(Result));
  if CharAt(Result, 1) = '!' then
    Result := Copy(Result, 2, Length(Result));
  if (CharAt(Result, 1) = '*') and ((CharAt(Result, 2) = '1') or (CharAt(Result, 2) = '2')) then
    Result := Copy(Result, 3, Length(Result));
  if Copy(Result, 1, 4) = '\??\' then
    Result := Copy(Result, 5, Length(Result));
end;

{ Windows' list of what MoveFileEx gave it to rename or delete at the next restart, PendingFileRenameOperations then
  PendingFileRenameOperations2, as pairs in the order Windows works through them: the path of each file or folder
  (Froms) and where it moves to (Tos: '' for a delete), each in lower case and without what comes before the path
  (ListedPath). Each pair is two strings of a value, with #0 between them; Inno Setup drops the empty strings a value
  ends in, so a value that ends in a path with no string after it ends in a delete. Each value is read once, in one
  pass, and the arrays grow by half again at a time, so a long list of other programs' work costs time in proportion to
  its length. A test holds a copy of this reader to the list as Windows writes it. }
procedure ReadRestartWork(var Froms, Tos: TArrayOfString);
var
  Work, Item, ValueName: String;
  V, P, Start, N: Integer;
  IsTo: Boolean;
begin
  SetArrayLength(Froms, 0);
  SetArrayLength(Tos, 0);
  N := 0;
  for V := 1 to 2 do
  begin
    if V = 1 then
      ValueName := 'PendingFileRenameOperations'
    else
      ValueName := 'PendingFileRenameOperations2';
    if RegQueryMultiStringValue(HKLM, SessionManagerKey, ValueName, Work) then
    begin
      Work := Lowercase(Work);
      IsTo := False;
      Start := 1;
      for P := 1 to Length(Work) + 1 do
        if CharAt(Work, P) = #0 then
          if (P <= Length(Work)) or (Start <= Length(Work)) then
          begin
            Item := ListedPath(Copy(Work, Start, P - Start));
            Start := P + 1;
            if IsTo then
              Tos[N - 1] := Item
            else
            begin
              if N = GetArrayLength(Froms) then
              begin
                SetArrayLength(Froms, N + N div 2 + 16);
                SetArrayLength(Tos, N + N div 2 + 16);
              end;
              N := N + 1;
              Froms[N - 1] := Item;
              Tos[N - 1] := '';
            end;
            IsTo := not IsTo;
          end;
    end;
  end;
  SetArrayLength(Froms, N);
  SetArrayLength(Tos, N);
end;

{ Whether Windows' list has Path itself to delete or to move elsewhere at the next restart, a pair of it
  (ReadRestartWork) whose path is Path's: an earlier uninstall's delete of a folder, or of the Start menu entry. A move
  to Path is not one. }
function GoesAtRestart(const Path: String): Boolean;
var
  Froms, Tos: TArrayOfString;
  I: Integer;
begin
  Result := False;
  ReadRestartWork(Froms, Tos);
  for I := 0 to GetArrayLength(Froms) - 1 do
    if Froms[I] = Lowercase(Path) then
      Result := True;
end;

{ Whether Windows' list has Path itself to move at the next restart, not to delete: a pair of it (ReadRestartWork) whose
  path is Path's and whose target is not empty (a delete has none: MoveFileEx with no new name). A move to Path is not
  one. }
function MovesAtRestart(const Path: String): Boolean;
var
  Froms, Tos: TArrayOfString;
  I: Integer;
begin
  Result := False;
  ReadRestartWork(Froms, Tos);
  for I := 0 to GetArrayLength(Froms) - 1 do
    if (Froms[I] = Lowercase(Path)) and (Tos[I] <> '') then
      Result := True;
end;

{ Whether the install folder App's file Name, one of the launcher's names, goes back at the next restart: a setup that
  did not finish (or one that finishes at that restart) could not put the old one back now (PutAllBack), and Windows
  has it, set aside as Name.<run>.old, to move back then (MovesAtRestart; one set aside after an update that finished
  is only deleted then). }
function NameGoesBack(const App, Name: String): Boolean;
var
  Find: TFindRec;
begin
  Result := False;
  if FindFirst(App + '\' + Name + '.*.old', Find) then
  try
    repeat
      if ((Find.Attributes and AttrFolder) = 0) and MovesAtRestart(App + '\' + Find.Name) then
        Result := True;
    until Result or not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

{ Whether Windows' list has Path itself to delete at the next restart: a pair of it (ReadRestartWork) whose path is
  Path's and whose target is empty (MoveFileEx with no new name). A move to Path, or from it, is not a delete. }
function DeletedAtRestart(const Path: String): Boolean;
var
  Froms, Tos: TArrayOfString;
  I: Integer;
begin
  Result := False;
  ReadRestartWork(Froms, Tos);
  for I := 0 to GetArrayLength(Froms) - 1 do
    if (Froms[I] = Lowercase(Path)) and (Tos[I] = '') then
      Result := True;
end;

{ Whether TakeBack left the new file Path at its own name, as another program kept it from being taken back and Windows
  would not take it to delete at the next restart either (LeftNew): it stays after that restart. }
function LeftNewHas(const Path: String): Boolean;
begin
  Result := Pos(#0 + Lowercase(Path) + #0, #0 + LeftNew) > 0;
end;

{ Whether TakeBack left a new file at one of the install folder App's launcher names (LeftNewHas): that new launcher
  stays after the next restart, so no old file goes back beside it then (PutAllBack). }
function LauncherLeftNew(const App: String): Boolean;
begin
  Result := LeftNewHas(App + '\' + PoolWatchName) or LeftNewHas(SecondLauncherPath(App));
end;

{ What starts PoolWatch from the install folder App until the next restart, as the setup's words say it (the Finished
  page of a setup that finishes at that restart, and the box of one that did not finish), read from the folder and
  Windows' list once this run has done what it does now, so that it holds for work an earlier setup or uninstall left
  for that restart as well as for this run's own: an earlier uninstall gave Windows the folder itself to delete then
  (DeletedAtRestart), so PoolWatch is not installed until then; no file that starts PoolWatch is in place (no launcher
  of the program below it, LauncherPath, nor, in an install made before the launcher, its PoolWatch.exe, unless that is
  a new launcher another program keeps from being taken back, which has no program to start); the launcher in place is
  a new one another program keeps from being taken back (with the program below it, which LauncherPath found of the
  same version), which Windows deletes then (DeletedAtRestart), or would not take to delete then either, so that it
  stays after it (LeftNewHas); one of the launcher's names that holds no file now goes back only then (NameGoesBack),
  so what names it starts nothing until then (a name that holds a file now starts that file, whatever Windows' list
  moves there later); or the version that was there is in place, but for any file of it that this run set aside now so
  that the restart does not replace it (TakenOut, Unstage). The words follow "until then" or "Until that restart". }
function UntilRestart(const App: String): String;
var
  Starter: String;
begin
  if DeletedAtRestart(App) then
    Result := 'PoolWatch is not installed: an earlier uninstall removed it, and Windows deletes what is left ' +
      'of it at that restart'
  else
  begin
    Starter := LauncherPath(App);
    if (Starter = '') and not HasLauncher(App) and FileExists(LauncherFile(App)) and
       not DeletedAtRestart(LauncherFile(App)) and not LeftNewHas(LauncherFile(App)) then
      Starter := LauncherFile(App);
    if Starter = '' then
      Result := 'PoolWatch does not start: no file that starts it is in place'
    else if DeletedAtRestart(Starter) then
      Result := 'what starts PoolWatch starts a launcher and a program that another program keeps from ' +
        'being taken back, without the other files of their version: Windows deletes that launcher at that restart'
    else if LeftNewHas(Starter) then
      Result := 'what starts PoolWatch starts a launcher and a program that another program keeps from ' +
        'being taken back, without the other files of their version, and Windows would not take that launcher to ' +
        'delete at that restart, so it stays after it'
    else
    begin
      if (NameGoesBack(App, PoolWatchName) and not FileExists(App + '\' + PoolWatchName)) or
         (NameGoesBack(App, SelfContainedName) and not FileExists(SecondLauncherPath(App))) then
        Result := 'the version you had stays in place, but for one of the names of its launcher, which ' +
          'Windows puts back at that restart: what names that file starts nothing until then'
      else
        Result := 'the version you had stays in place';
      if TakenOut > 0 then
        Result := Result + '; Setup set some of its files aside so that the restart does not replace them (listed ' +
          'below), so it may not start until then';
    end;
  end;
end;

{ This setup run's own name, from the time it started: for its folder in StageFolder (StageDir), its uninstaller's
  folder when that is not the install folder (UninstallDir), and the files it sets aside (SetAsideIn). }
function RunStamp: String;
begin
  if Stamp = '' then
    Stamp := GetDateTimeString('yyyymmddhhnnsszzz', #0, #0);
  Result := Stamp;
end;

{ From now on the setup finishes at the next restart: StageDir, a folder of this setup run's own in the install folder's
  StageFolder, where [Files] puts PoolWatch's files until then (ProgramDir); Why says why, and is added to FinishReason.
  }
procedure FinishAtNextRestart(const Why: String);
begin
  FinishAtRestart := True;
  StageDir := ExpandConstant('{app}') + '\' + StageFolder + '\' + RunStamp;
  if FinishReason <> '' then
    FinishReason := FinishReason + #13#10#13#10;
  FinishReason := FinishReason + Why;
  Log('Setup puts PoolWatch''s files in place at the next restart of Windows, from ' + StageDir + '. ' + Why);
end;

{ Once per run: when Windows has work in the install folder App for its next restart (RestartWorkPending: an earlier
  setup's moves, or an earlier uninstall's deletes, which would come after anything put in place now and undo it), the
  setup finishes at that restart too, after that work. ProgramDir calls this first: Inno Setup's own check for an
  unfinished earlier install (PreviousInstallCompleted, which runs before PrepareToInstall) expands every [Files]
  destination, and would ask for a restart if one of them were in Windows' list; by then each destination is in this
  run's own folder, which is in no list. WhenToReplace calls it too, for a run where that check had nothing to look at.
  }
procedure LookForRestartWork(const App: String);
begin
  if RestartWorkLooked then
    Exit;
  RestartWorkLooked := True;
  if RestartWorkPending(App) then
    FinishAtNextRestart('An earlier setup or uninstall of PoolWatch finishes when Windows restarts, so this one does too, after it.');
end;

{ Where [Files] puts PoolWatch's files: the install folder, or StageDir when the setup finishes at the next restart.
  Windows' list is read before the first answer (LookForRestartWork). }
function ProgramDir(Param: String): String;
begin
  LookForRestartWork(ExpandConstant('{app}'));
  if FinishAtRestart then
    Result := StageDir
  else
    Result := ExpandConstant('{app}');
end;

{ For UninstallFilesDir: where Inno Setup writes this run's uninstaller (unins000.exe, .dat and .msg), decided when Inno
  Setup first asks, before it writes any file. The install folder itself when the setup replaces the files now and none
  of the uninstaller's files there is in use (HeldUninstaller): Inno Setup then adds to the uninstaller's record there
  and replaces its program in place. Otherwise a folder of this run's own, UninstallFolder\<RunStamp>, where no file is
  in use and Windows has no work: Inno Setup can neither write those files elsewhere nor leave them for a restart, so
  another program that has one of the old ones open would stop the setup there, and an earlier uninstall's delete of
  them at the next restart would delete the new ones. The uninstaller this one replaces goes once the setup has put its
  files in place (SweepUninstallers). }
function UninstallDir(Param: String): String;
begin
  if UninstallerDir = '' then
  begin
    UninstallerDir := ExpandConstant('{app}');
    if FinishAtRestart or HeldUninstaller then
      UninstallerDir := UninstallerDir + '\' + UninstallFolder + '\' + RunStamp;
    Log('Setup writes its uninstaller in ' + UninstallerDir + '.');
  end;
  Result := UninstallerDir;
end;

{ Whether "Start PoolWatch now" is offered: not when the setup finishes at the next restart, as the new version is not
  in place until then. }
function StartsNow: Boolean;
begin
  Result := not FinishAtRestart;
end;

{ Whether Path is one of the install folder's files that start PoolWatch: its PoolWatch.exe and its
  PoolWatch-selfcontained.exe, the launcher's two names (in an install made before the launcher, PoolWatch itself). The
  logon task, the Start menu entry and Explorer start one of them, and PoolWatch itself started any other way starts
  again through the launcher or does not start (LaunchGate). }
function StartsPoolWatch(const Path: String): Boolean;
begin
  Result := (CompareText(Path, ExpandConstant('{app}') + '\' + PoolWatchName) = 0) or
    (CompareText(Path, SecondLauncherPath(ExpandConstant('{app}'))) = 0);
end;

{ Puts the old file Aside back in its place Target, when that is empty. One that cannot be renamed back (another program
  opened it under its new name, without letting it be renamed) is copied back instead, or, when it cannot be read
  either, given its name back as a second name of the same file (CreateHardLinkW, which another program's sharing does
  not stop); either way Windows is given the name it was set aside under to delete at the next restart. Only when that
  fails too is Windows given it to move back at that restart; when Windows will not take that either, it stays set aside
  and is counted (Refused). Each step is logged. False when the file is not back in its place now, so that the caller
  leaves the files that start PoolWatch for that restart too (PutAllBack). }
function PutBack(const Target, Aside: String): Boolean;
var
  How: String;
begin
  Result := True;
  if FileExists(Target) or not FileExists(Aside) then
    Exit;
  if RenameFile(Aside, Target) then
  begin
    Log('Put back ' + Target + '.');
    Exit;
  end;
  if FileCopy(Aside, Target, True) then
    How := ' as a copy: '
  else if CreateHardLinkW(Target, Aside, 0) then
    How := ' under a second name of the same file: '
  else
    How := '';
  if How <> '' then
  begin
    Log('Put back ' + Target + How + Aside + ' is in use' + HoldersOf(Aside) + '.');
    try
      RestartReplace(Aside, '');
    except
      Log('Windows could not be given ' + Aside + ' to delete at its next restart: ' + GetExceptionMessage);
    end;
  end
  else
  begin
    Result := False;
    try
      RestartReplace(Aside, Target);
      Log('Could not put back ' + Target + ' now, as ' + Aside + ' is in use' + HoldersOf(Aside) + ': Windows moves it back at its next restart.');
    except
      Refused := Refused + 1;
      Log('Could not put back ' + Target + ' from ' + Aside + ', now or at the next restart, so it stays set aside: ' +
        GetExceptionMessage);
    end;
  end;
end;

{ Gives Windows the old file Aside to move back to its place Target at the next restart, over whatever is there then.
  One Windows will not take stays set aside, and is counted (Refused). Logged. }
procedure MoveBackAtRestart(const Target, Aside: String);
begin
  if not FileExists(Aside) then
    Exit;
  try
    RestartReplace(Aside, Target);
    Log('Windows puts ' + Target + ' back at its next restart, from ' + Aside + '.');
  except
    Refused := Refused + 1;
    Log('Windows could not be given ' + Aside + ' to put back at its next restart, so it stays set aside: ' +
      GetExceptionMessage);
  end;
end;

{ Puts back every old file SetAsideIn renamed aside (PutBack), the files that start PoolWatch last (StartsPoolWatch),
  and forgets them. With Later (a new file the setup wrote could not be taken back: TakeBack), or from the first file
  that could not be put back now, each one left is given to Windows to move back at the next restart instead
  (MoveBackAtRestart), the files that start PoolWatch last; one of those put back now (Back) comes only after every
  other old file is back. But when a new file stays after that restart too at its own name (TakeBack left it there, as
  another program kept it from being renamed and Windows would not take it to delete then: LeftNew), the files that
  start PoolWatch are not given to Windows at all: they stay set aside, under the names they were set aside under, so
  that they start nothing from the old files that go back beside that new one; and when that new file is at one of the
  launcher's names (KeepAll, LauncherLeftNew), no old file is given to Windows, so that the new launcher, which stays,
  starts nothing from old files either. A setup that replaces the files later deletes what stays set aside
  (DropAside). A new file TakeBack renamed but could not delete, which Windows would not take to delete either, stays
  under its new name, which starts nothing, and does not count here. When any file is left for that restart, or stays
  set aside, OldAtRestart is set (the uninstaller then starts no cleanup until that restart, unless Windows refused
  some of this work: RunCleanup; one made before the launcher is held when it can be: HoldOldUninstaller); when no
  file that starts PoolWatch is back now either, the old launcher is not back to start PoolWatch, whose old files are
  not all back (what does start it until then: UntilRestart). }
procedure PutAllBack(Later: Boolean);
var
  I, Pass, Back: Integer;
  Late, KeepAll: Boolean;
begin
  Late := Later and (GetArrayLength(AsideTargets) > 0);
  KeepAll := LauncherLeftNew(ExpandConstant('{app}'));
  Back := 0;
  for Pass := 1 to 2 do
    for I := 0 to GetArrayLength(AsideTargets) - 1 do
      if StartsPoolWatch(AsideTargets[I]) = (Pass = 2) then
      begin
        if Late and (LeftNew <> '') and ((Pass = 2) or KeepAll) then
          Log('A new file stays after the next restart, so ' + AsideTargets[I] + ' stays set aside as ' + AsideNames[I] +
            ' then too, so that nothing starts PoolWatch from old and new files mixed.')
        else if Late then
          MoveBackAtRestart(AsideTargets[I], AsideNames[I])
        else if not PutBack(AsideTargets[I], AsideNames[I]) then
          Late := True
        else if Pass = 2 then
          Back := Back + 1;
      end;
  if Late then
  begin
    OldAtRestart := True;
    if KeepAll then
      Log('A new file that starts PoolWatch could not be taken back, nor given to Windows to delete at its next ' +
        'restart: every old file stays set aside, so that it starts nothing from them.')
    else if LeftNew <> '' then
      Log('Not every new file could be taken back, nor given to Windows to delete at its next restart: the other old ' +
        'files go back then, where Windows took them, and the old launcher stays set aside.')
    else if Back = 0 then
      Log('Not every old file of PoolWatch''s could be put back now: Windows puts back the rest at its next restart, ' +
        'the launcher last, so the old launcher is not back to start PoolWatch until then.')
    else
      Log('One of the old launcher''s names could not be put back now: Windows puts it back at its next restart. Every ' +
        'other old file is back.');
  end;
  SetArrayLength(AsideTargets, 0);
  SetArrayLength(AsideNames, 0);
end;

{ Renames each file in Dir (Rel: its path below the install folder) aside, to Target.<RunStamp>.old, and those in each
  folder below it, and records each (AsideTargets, AsideNames). The folder is listed first and its files renamed after,
  so a name set aside is never listed and renamed again. A link to a file is renamed itself, and a link to a folder is
  left as it is: neither is followed. Not the folders and files FindHeld does not look at: nothing of PoolWatch's
  program is in StageFolder or UninstallFolder, and Inno Setup writes the uninstaller's own files itself. A file is
  renamed only when it is free, now or within BriefHold seconds (SetAsideSoon), as the file of a program that runs, or
  of a DLL one has loaded, could be renamed while in use; Blocked gets the first that stays in use or cannot be renamed
  all that time, and nothing after it is renamed. }
procedure SetAsideIn(const Dir, Rel: String; var Blocked: String);
var
  Find: TFindRec;
  Found: TArrayOfString;
  Name, Path, Aside: String;
  I, N: Integer;
begin
  if Blocked <> '' then
    Exit;
  if FindFirst(Dir + '\*', Find) then
  try
    repeat
      Name := Find.Name;
      if (Name <> '.') and (Name <> '..') and
         ((Rel <> '') or ((CompareText(Name, StageFolder) <> 0) and (CompareText(Name, UninstallFolder) <> 0) and
          (CompareText(Copy(Name, 1, 5), 'unins') <> 0))) and
         (((Find.Attributes and AttrFolder) = 0) or ((Find.Attributes and AttrLink) = 0)) then
      begin
        N := GetArrayLength(Found);
        SetArrayLength(Found, N + 1);
        if (Find.Attributes and AttrFolder) <> 0 then
          Found[N] := '\' + Name
        else
          Found[N] := Name;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
  I := 0;
  while (Blocked = '') and (I < GetArrayLength(Found)) do
  begin
    if Copy(Found[I], 1, 1) = '\' then
    begin
      Name := Copy(Found[I], 2, Length(Found[I]) - 1);
      SetAsideIn(Dir + '\' + Name, Rel + Name + '\', Blocked);
    end
    else
    begin
      Path := Dir + '\' + Found[I];
      Aside := Path + '.' + RunStamp + '.old';
      if SetAsideSoon(Path, Aside) then
      begin
        N := GetArrayLength(AsideTargets);
        SetArrayLength(AsideTargets, N + 1);
        SetArrayLength(AsideNames, N + 1);
        AsideTargets[N] := Path;
        AsideNames[N] := Aside;
      end
      else
        Blocked := Path;
    end;
    I := I + 1;
  end;
end;

{ At ssInstall, before [Files] writes any file, when the setup replaces the files now: every old file of PoolWatch's
  program in the install folder App is renamed aside first (SetAsideIn), so that a file that comes into use part way
  through cannot leave old and new files mixed, and [Files] writes each new file to a name nothing holds. Each file set
  aside is then looked at again under its new name, which a process started meanwhile that runs it or has it loaded
  holds (FreeSoon: a file open only for a moment gets BriefHold seconds). If one stays in use or could not be renamed,
  every one is put back (PutAllBack, as a copy or under a second name when it cannot be renamed back; one that cannot
  be put back even so goes back at that restart, before this version's files, with every one after it and the old
  launcher: OldAtRestart) and the setup finishes at the next restart instead, with nothing new in the
  install folder; a logged look then names every file in use with what has it open (FinishFiles). The answer for the
  launcher's second name (SelfContainedNameThere) is read before that file is renamed. }
procedure SetAllAside(const App: String);
var
  Blocked, Why: String;
  I: Integer;
begin
  if FinishAtRestart then
    Exit;
  SelfContainedNameThere;
  Blocked := '';
  Why := '';
  try
    SetAsideIn(App, '', Blocked);
    I := 0;
    while (Blocked = '') and (I < GetArrayLength(AsideNames)) do
    begin
      if not FreeSoon(AsideNames[I]) then
        Blocked := AsideNames[I];
      I := I + 1;
    end;
    if Blocked <> '' then
      Why := Blocked + HoldersOf(Blocked) + ' came into use while Setup was setting the old files aside';
  except
    Why := 'The old files could not all be set aside (' + GetExceptionMessage + ')';
  end;
  if Why = '' then
    Log(Format('Set aside the %d old files of PoolWatch''s program, which this setup replaces now.', [GetArrayLength(AsideNames)]))
  else
  begin
    PutAllBack(False);
    Why := Why + ', so none of them is replaced now.';
    if HeldFiles(App, True) > 0 then
    begin
      FinishFiles := HeldAll;
      Why := Why + ' Each file in use now is listed below with what has it open.';
    end;
    FinishAtNextRestart(Why);
  end;
end;

{ At ssInstall: a copy of the Start menu entry as it is, in Setup's temporary folder (LinkBefore), for
  DeinitializeSetup. }
procedure SaveLinkBefore;
var
  Link: String;
begin
  Link := ExpandConstant('{autoprograms}\PoolWatch.lnk');
  LinkBefore := '';
  if FileExists(Link) then
    if FileCopy(Link, ExpandConstant('{tmp}\PoolWatch.lnk'), False) then
      LinkBefore := ExpandConstant('{tmp}\PoolWatch.lnk')
    else
      Log('The Start menu entry could not be copied, so a setup that does not finish cannot put it back: ' + Link);
end;

{ One value of the Apps and Features entry, for SaveEntryBefore: its name, and its text or its number. }
procedure SaveEntryValue(const Name, Text: String; Number: Cardinal; IsNumber: Boolean);
var
  N: Integer;
begin
  N := GetArrayLength(EntryNames);
  SetArrayLength(EntryNames, N + 1);
  SetArrayLength(EntryText, N + 1);
  SetArrayLength(EntryNumber, N + 1);
  SetArrayLength(EntryIsNumber, N + 1);
  EntryNames[N] := Name;
  EntryText[N] := Text;
  EntryNumber[N] := Number;
  EntryIsNumber[N] := IsNumber;
end;

{ At ssInstall: the values of PoolWatch's Apps and Features entry as they are (UninstallEntryKey). Inno Setup deletes
  that entry and writes it anew once the files are in place, and the rollback of a setup that fails after that deletes
  the new one, or, when it failed part way through writing it, leaves what it wrote; DeinitializeSetup then writes these
  values back (PutEntryBack). Inno Setup writes only text and numbers there. EntrySaved: the entry was there. EntryNew:
  there was no entry, so that what a setup that does not finish leaves of the one it was writing is deleted. }
procedure SaveEntryBefore;
var
  Names: TArrayOfString;
  Text: String;
  Number: Cardinal;
  I: Integer;
begin
  EntryNew := not RegKeyExists(HKLM, UninstallEntryKey);
  EntrySaved := RegGetValueNames(HKLM, UninstallEntryKey, Names);
  if not EntrySaved then
    Exit;
  EntryCount := GetArrayLength(Names);
  for I := 0 to GetArrayLength(Names) - 1 do
    if RegQueryStringValue(HKLM, UninstallEntryKey, Names[I], Text) then
      SaveEntryValue(Names[I], Text, 0, False)
    else if RegQueryDWordValue(HKLM, UninstallEntryKey, Names[I], Number) then
      SaveEntryValue(Names[I], '', Number, True)
    else
      Log('A value of the Apps and Features entry is neither text nor a number, so a setup that does not finish cannot put it back: ' + Names[I]);
end;

{ Whether PoolWatch's Apps and Features entry is now as SaveEntryBefore found it: as many values, and each value it
  saved there with the same text or number. }
function EntryAsSaved: Boolean;
var
  Names: TArrayOfString;
  Text: String;
  Number: Cardinal;
  I: Integer;
begin
  Result := RegGetValueNames(HKLM, UninstallEntryKey, Names) and (GetArrayLength(Names) = EntryCount);
  for I := 0 to GetArrayLength(EntryNames) - 1 do
    if Result then
    begin
      if EntryIsNumber[I] then
        Result := RegQueryDWordValue(HKLM, UninstallEntryKey, EntryNames[I], Number) and (Number = EntryNumber[I])
      else
        Result := RegQueryStringValue(HKLM, UninstallEntryKey, EntryNames[I], Text) and (Text = EntryText[I]);
    end;
end;

{ After a setup that did not finish: PoolWatch's Apps and Features entry, written back as it was before the files
  (SaveEntryBefore), when it was there then and is not so now (EntryAsSaved): Inno Setup's rollback deleted the new
  one, or Inno Setup stopped part way through writing it, which leaves what it wrote. What is there is deleted first,
  so no value of the new one stays. When there was no entry then (EntryNew), what Inno Setup wrote of one is deleted,
  as its rollback deletes an entry only once every value is written. Logged. }
procedure PutEntryBack;
var
  I: Integer;
  Done: Boolean;
begin
  if EntryNew then
  begin
    if RegKeyExists(HKLM, UninstallEntryKey) then
      if RegDeleteKeyIncludingSubkeys(HKLM, UninstallEntryKey) then
        Log('Deleted what Setup wrote of an Apps and Features entry, where there was none before the setup.')
      else
        Log('Could not delete what Setup wrote of an Apps and Features entry, ' + UninstallEntryKey + '.');
    Exit;
  end;
  if not EntrySaved or EntryAsSaved then
    Exit;
  Done := True;
  if RegKeyExists(HKLM, UninstallEntryKey) then
    Done := RegDeleteKeyIncludingSubkeys(HKLM, UninstallEntryKey);
  for I := 0 to GetArrayLength(EntryNames) - 1 do
    if EntryIsNumber[I] then
      Done := RegWriteDWordValue(HKLM, UninstallEntryKey, EntryNames[I], EntryNumber[I]) and Done
    else
      Done := RegWriteStringValue(HKLM, UninstallEntryKey, EntryNames[I], EntryText[I]) and Done;
  if Done then
    Log('Put back the Apps and Features entry as it was before the setup.')
  else
    Log('Could not put back every value of the Apps and Features entry, ' + UninstallEntryKey + '.');
end;

{ At ssInstall, when this run writes its uninstaller in the install folder App itself (UninstallDir): Inno Setup
  replaces the uninstaller's program there (unins???.exe), and its messages (unins???.msg), before it writes the Apps
  and Features entry, a step that can still fail, and its rollback leaves what it replaced. So each of those files there
  is copied into Setup's temporary folder first (UninstallerBefore), and a setup that does not finish puts it back
  (PutUninstallerBack). }
procedure SaveUninstallerBefore(const App: String);
var
  Find: TFindRec;
  Ext, Saved: String;
  N: Integer;
begin
  SetArrayLength(UninstallerBefore, 0);
  if CompareText(UninstallDir(''), App) <> 0 then
    Exit;
  if FindFirst(App + '\unins*', Find) then
  try
    repeat
      Ext := ExtractFileExt(Find.Name);
      if ((Find.Attributes and AttrFolder) = 0) and ((CompareText(Ext, '.exe') = 0) or (CompareText(Ext, '.msg') = 0)) then
      begin
        Saved := ExpandConstant('{tmp}\uninstaller-before\') + Find.Name;
        if ForceDirectories(ExtractFileDir(Saved)) and FileCopy(App + '\' + Find.Name, Saved, False) then
        begin
          N := GetArrayLength(UninstallerBefore);
          SetArrayLength(UninstallerBefore, N + 1);
          UninstallerBefore[N] := Find.Name;
        end
        else
          Log('Could not copy ' + App + '\' + Find.Name + ', so a setup that does not finish cannot put it back.');
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

{ After a setup that did not finish: each of the uninstaller's files SaveUninstallerBefore copied goes back in the
  install folder as it was. One that cannot be copied over (another program has it open, say) is compared with the
  copy, as Inno Setup may not have replaced it yet when the setup stopped. Each is logged. }
procedure PutUninstallerBack;
var
  I: Integer;
  App, Path, Saved: String;
begin
  if GetArrayLength(UninstallerBefore) = 0 then
    Exit;
  App := ExpandConstant('{app}');
  for I := 0 to GetArrayLength(UninstallerBefore) - 1 do
  begin
    Path := App + '\' + UninstallerBefore[I];
    Saved := ExpandConstant('{tmp}\uninstaller-before\') + UninstallerBefore[I];
    if FileCopy(Saved, Path, False) then
      Log('Put back ' + Path + ' as it was before the setup.')
    else
      try
        if GetSHA256OfFile(Path) = GetSHA256OfFile(Saved) then
          Log(Path + ' could not be copied over' + HoldersOf(Path) + ', and is as it was before the setup.')
        else
          Log('Could not put back ' + Path + ' as it was before the setup' + HoldersOf(Path) + '.');
      except
        Log('Could not put back ' + Path + ' as it was before the setup, nor read it to see whether the setup had ' +
          'replaced it' + HoldersOf(Path) + ': ' + GetExceptionMessage);
      end;
  end;
end;

{ Deletes the file Path now, or, when another program has it in use, gives Windows it to delete at the next restart.
  Each step is logged. }
procedure DeleteNowOrAtRestart(const Path: String);
begin
  if DeleteFile(Path) then
    Log('Deleted ' + Path + '.')
  else
    try
      RestartReplace(Path, '');
      Log('In use, so Windows deletes it at its next restart: ' + Path + HoldersOf(Path));
    except
      Log('Windows could not be given ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
    end;
end;

{ BeforeInstall of each [Files] entry of PoolWatch's program: notes the file this version has at the place [Files] is
  about to write (CurrentFileName), whether or not it is then written (Inno Setup offers to skip a file it could not
  write), for DropAside. }
procedure Shipping;
var
  N: Integer;
begin
  N := GetArrayLength(Shipped);
  SetArrayLength(Shipped, N + 1);
  Shipped[N] := ExpandConstant(CurrentFileName);
end;

{ Whether this version has a file at Path: [Files] was about to write it there (Shipping). }
function Ships(const Path: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Shipped) - 1 do
    if CompareText(Shipped[I], Path) = 0 then
      Result := True;
end;

{ At ssPostInstall, when [Files] has put every new file in place: each old file set aside goes (DeleteNowOrAtRestart).
  One whose place the new version left empty is put back as it was (PutBack) only when this version does not have that
  file (Ships); one this version has but did not write (it was skipped) stays set aside, so no old file goes back beside
  the new ones. One an earlier setup left for its own use (a file it set aside, .old, or a KeepName) goes too. }
procedure DropAside;
var
  I: Integer;
begin
  for I := 0 to GetArrayLength(AsideTargets) - 1 do
    if FileExists(AsideTargets[I]) or (CompareText(ExtractFileExt(AsideTargets[I]), '.old') = 0) or
       (CompareText(ExtractFileName(AsideTargets[I]), KeepName) = 0) then
      DeleteNowOrAtRestart(AsideNames[I])
    else if Ships(AsideTargets[I]) then
      Log('Setup did not write ' + AsideTargets[I] + ', which this version has, so the old one stays set aside as ' +
        AsideNames[I] + ' and is not put back.')
    else
      PutBack(AsideTargets[I], AsideNames[I]);
  SetArrayLength(AsideTargets, 0);
  SetArrayLength(AsideNames, 0);
end;

{ After a setup that did not finish: takes back the new file Path, which [Files] wrote (Shipping) and Inno Setup's
  rollback could not delete (another program has it open, or runs it). It is renamed out of the way, to
  Path.<RunStamp>.new, and deleted. True when it is gone now, or was not there. One that cannot be renamed (another
  program has it open without letting it be renamed) stays until the next restart, and Windows is given it to delete
  then, before the old files go back (PutAllBack, which DeinitializeSetup calls after this); one renamed but not deleted
  (a program runs it or has it loaded, say) is given to Windows to delete then under its new name. Either way no old
  file goes back before then (PutAllBack). One Windows will not take to delete then stays after that restart too, and
  is counted (Refused): under its new name, which starts nothing, or, when it could not be renamed, under its own
  name, which is noted too (LeftNew), so that the old launcher, or every old file when that name is one of the
  launcher's, stays set aside then (PutAllBack). Each step is logged. }
function TakeBack(const Path: String): Boolean;
var
  Moved: String;
begin
  Result := True;
  if not FileExists(Path) then
    Exit;
  Moved := Path + '.' + RunStamp + '.new';
  if not RenameFile(Path, Moved) then
  begin
    Result := False;
    Log('Another program has ' + Path + ' open' + HoldersOf(Path) + ', so it stays until the next restart, and no old ' +
      'file goes back before then.');
    try
      RestartReplace(Path, '');
      Log('Windows deletes ' + Path + ' at its next restart, before it puts the old files back.');
    except
      Refused := Refused + 1;
      LeftNew := LeftNew + Lowercase(Path) + #0;
      Log('Windows could not be given ' + Path + ' to delete at its next restart, so it stays after it too: ' +
        GetExceptionMessage);
    end;
  end
  else if DeleteFile(Moved) then
    Log('Took back ' + Path + '.')
  else
  begin
    Result := False;
    Log('Could not delete ' + Moved + ' now' + HoldersOf(Moved) + ', so no old file goes back before the next ' +
      'restart.');
    try
      RestartReplace(Moved, '');
      Log('Windows deletes ' + Moved + ' at its next restart.');
    except
      Refused := Refused + 1;
      Log('Windows could not be given ' + Moved + ' to delete at its next restart, so it stays after it too: ' +
        GetExceptionMessage);
    end;
  end;
end;

{ For HoldOldUninstaller: renames the file Path aside, to Path.<RunStamp>.old, and gives Windows it to move back at the
  next restart. True when it is set aside until then, or is not there. One another program has open without letting it
  be renamed stays; one Windows could not be given goes back at once. Each step is logged. }
function HoldUntilRestart(const Path: String): Boolean;
var
  Aside: String;
begin
  Result := True;
  if not FileExists(Path) then
    Exit;
  Aside := Path + '.' + RunStamp + '.old';
  if not RenameFile(Path, Aside) then
  begin
    Result := False;
    Log('Another program has ' + Path + ' open' + HoldersOf(Path) + ', so it could not be set aside until the next restart.');
    Exit;
  end;
  try
    RestartReplace(Aside, Path);
    Log('Set aside ' + Path + ' until the next restart, when Windows puts it back.');
  except
    Result := False;
    Log('Windows could not be given ' + Aside + ' to put back at its next restart (' + GetExceptionMessage + '), so ' +
      'it goes back now.');
    if not RenameFile(Aside, Path) then
      Log('Could not put back ' + Path + ' from ' + Aside + '.');
  end;
end;

{ After a setup that did not finish and left an old file to go back only at the next restart, or set aside
  (OldAtRestart), when the install folder App had no launcher of its program's version as the setup began
  (OldUninstaller): its uninstaller may then be one made before the launcher (1.0.0-beta.1), whose own steps start the
  install folder's PoolWatch.exe to put back PoolWatch's changes and go on without that when it does not run. Before
  that restart it would remove the files it installed and the Apps and Features entry, put back none of PoolWatch's
  changes and leave the logon task, and the restart would then bring that version's files back with no entry. So each
  of the uninstaller's records there (unins???.dat, which this setup did not change) is set aside until that restart
  (HoldUntilRestart): until then that uninstaller stops at its start, as it finds no record (Inno Setup's "does not
  exist. Cannot uninstall"), and removes nothing. When a record cannot be set aside (another program has it open
  without letting it be renamed: while it is open, that uninstaller cannot start either, as Inno Setup opens its record
  with no sharing, but it can once that program lets go), its messages file (unins???.msg) is set aside in its place
  when there is one: an uninstaller whose messages are not bound to its program reads them from that file before it
  looks for its record, and stops when the file is not there ("Messages file ... is missing"), removing nothing (Inno
  Setup 6.7.3: RunUninstaller, LoadSetupMessages). Inno Setup writes that file for a signed uninstaller, as
  1.0.0-beta.1's released setup made it, or when Setup is started with /DETACHEDMSG (CreateUninstallMsgFile);
  otherwise the messages are bound to the program. When neither can be set aside, its program (unins???.exe) is set
  aside in their place (UninstallerProgramHeld), so that nothing can start that uninstaller until the restart, and the
  setup then says to answer No if Windows offers to remove PoolWatch from its list of apps before then. When none of
  them can be set aside (another program has each open without letting it be renamed, or Windows would not take them
  to put back at the restart), that uninstaller can start before the restart (once that program lets go of them, or
  at once when it was Windows that would not take them), and the setup says not to start it before then
  (DeinitializeSetup). True when each record, its messages file or its program is set aside until then, or there is
  no record. Each step is logged. }
function HoldOldUninstaller(const App: String): Boolean;
var
  Find: TFindRec;
  Found: TArrayOfString;
  Path, MsgFile, ExeFile: String;
  I, N: Integer;
  Kept, HasMsg: Boolean;
begin
  Result := True;
  if FindFirst(App + '\unins???.dat', Find) then
  try
    repeat
      if (Find.Attributes and AttrFolder) = 0 then
      begin
        N := GetArrayLength(Found);
        SetArrayLength(Found, N + 1);
        Found[N] := Find.Name;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
  for I := 0 to GetArrayLength(Found) - 1 do
  begin
    Path := App + '\' + Found[I];
    MsgFile := ChangeFileExt(Path, '.msg');
    ExeFile := ChangeFileExt(Path, '.exe');
    HasMsg := FileExists(MsgFile);
    Kept := HoldUntilRestart(Path);
    if Kept then
      Log('Until the next restart the uninstaller there finds no record, ' + Path + ', and removes nothing.')
    else if HasMsg then
    begin
      Kept := HoldUntilRestart(MsgFile);
      if Kept then
        Log('Until the next restart the uninstaller there finds no messages, ' + MsgFile + ', and removes nothing.');
    end
    else
      Log(Path + ' could not be set aside until the next restart, and that uninstaller has no messages file, ' +
        MsgFile + ', to set aside instead.');
    if not Kept then
    begin
      if HoldUntilRestart(ExeFile) then
      begin
        UninstallerProgramHeld := True;
        Log('Until the next restart the uninstaller there, ' + ExeFile + ', is not there to start, so it removes ' +
          'nothing.');
      end
      else
      begin
        Result := False;
        if HasMsg then
          Log('None of ' + Path + ', ' + MsgFile + ' and ' + ExeFile + ' could be set aside until the next restart, ' +
            'so that uninstaller can start before then.')
        else
          Log('Neither ' + Path + ' nor ' + ExeFile + ' could be set aside until the next restart, so that ' +
            'uninstaller can start before then.');
      end;
    end;
  end;
end;

{ After a setup that did not finish, when it replaced the files now: each folder of the install folder App that a file
  this version has (Shipped) is in, deepest first, is deleted when it is empty (Inno Setup's rollback leaves a folder
  it made while a new file it could not delete was in it, and TakeBack may have taken that file since). While a new
  file stays until the next restart (Later), one that is not empty is given to Windows to delete then, after the deletes
  and the moves back PutAllBack and TakeBack gave it first, which Windows does only while it is empty then: so a folder
  only new files were in goes, and one an old file goes back into stays. The install folder itself stays. Logged. }
procedure DropNewFolders(const App: String; Later: Boolean);
var
  Dirs: TArrayOfString;
  Dir: String;
  I, J, N, Deepest: Integer;
begin
  for I := 0 to GetArrayLength(Shipped) - 1 do
  begin
    Dir := ExtractFileDir(Shipped[I]);
    while (Length(Dir) > Length(App) + 1) and (CompareText(Copy(Dir, 1, Length(App) + 1), App + '\') = 0) do
    begin
      J := 0;
      while (J < GetArrayLength(Dirs)) and (CompareText(Dirs[J], Dir) <> 0) do
        J := J + 1;
      if J = GetArrayLength(Dirs) then
      begin
        N := GetArrayLength(Dirs);
        SetArrayLength(Dirs, N + 1);
        Dirs[N] := Dir;
      end;
      Dir := ExtractFileDir(Dir);
    end;
  end;
  for I := 0 to GetArrayLength(Dirs) - 1 do
  begin
    Deepest := 0;
    for J := 1 to GetArrayLength(Dirs) - 1 do
      if Length(Dirs[J]) > Length(Dirs[Deepest]) then
        Deepest := J;
    Dir := Dirs[Deepest];
    Dirs[Deepest] := '';
    if DirExists(Dir) then
    begin
      if RemoveDir(Dir) then
        Log('Deleted the folder ' + Dir + ', which only new files were in.')
      else if Later then
        try
          RestartReplace(Dir, '');
          Log('Windows deletes the folder ' + Dir + ' at its next restart, if it is empty then.');
        except
          Log('Windows could not be given the folder ' + Dir + ' to delete at its next restart: ' + GetExceptionMessage);
        end;
    end;
  end;
end;

{ When Setup ends without getting to its end (cancelled, or failed): Inno Setup's rollback has deleted the new files it
  wrote. When the setup replaced the files now, each file this version has (Shipped) that is still there is a new one
  the rollback could not delete, whether or not an old file was in its place, and is taken back first (TakeBack). Then
  each old file set aside goes back in its place (PutAllBack); when a new file could not be taken back now, every old
  file goes back only at the next restart instead, the launcher's names last, so that until then the install folder
  holds only new files that could not be taken back and no old file beside them; when Windows would not take such a
  new file to delete at the restart either and it stays at its own name (LeftNew), the launcher's names stay set aside
  after the restart too, and when it is at one of the launcher's names (LauncherLeftNew), every old file does. The
  folders only new files were in go (DropNewFolders). The uninstaller's program it replaced (PutUninstallerBack), the
  Apps and Features entry (PutEntryBack) and the Start menu entry, which the rollback deletes (LinkBefore), are put back
  as they were. When an old file goes back only at the restart, or stays set aside (OldAtRestart), an uninstaller made
  before the launcher is held until then when it can be (HoldOldUninstaller; this version's uninstaller starts no
  cleanup until then of itself, unless Windows refused some of this work: RunCleanup), and the log, and a box when
  someone is at the setup, say so: what starts PoolWatch until then, read from the folder (UntilRestart), what Windows
  would not take for that restart (Refused) and what the setup did about it, what the uninstaller does until then (or,
  when one made before the launcher could not be held, not to start it before the restart, and what it would do), and
  the first MaxShown files in use then, each with what has it open, and how many more (HeldFiles; the log names every
  one). Only once the setup got to ssInstall (InstallBegun), where it first changes one of PoolWatch's files or entries:
  before that it may only have asked PoolWatch to exit, ended a token-leak test helper and deleted what an earlier
  setup left in its own folder for a restart that has happened since (WhenToReplace), none of which a step puts back
  (it closes no program: CloseApplications is off), and the install folder may not be known yet, so expanding the app
  constant would stop Setup with an error (a cancel on the Welcome page, say). }
procedure DeinitializeSetup;
var
  I: Integer;
  Later, Held: Boolean;
  App, Link, Text: String;
begin
  if InstallDone or not InstallBegun then
    Exit;
  App := ExpandConstant('{app}');
  Later := False;
  if not FinishAtRestart then
    for I := 0 to GetArrayLength(Shipped) - 1 do
      if not TakeBack(Shipped[I]) then
        Later := True;
  PutAllBack(Later);
  if not FinishAtRestart then
    DropNewFolders(App, Later);
  PutUninstallerBack;
  PutEntryBack;
  Link := ExpandConstant('{autoprograms}\PoolWatch.lnk');
  if (LinkBefore <> '') and not FileExists(Link) then
    if FileCopy(LinkBefore, Link, True) then
      Log('Put back the Start menu entry, ' + Link + '.')
    else
      Log('Could not put back the Start menu entry, ' + Link + '.');
  if OldAtRestart then
  begin
    Held := True;
    if OldUninstaller then
      Held := HoldOldUninstaller(App);
    Text := 'Setup did not finish, and another program has some of PoolWatch''s files open, so Setup could not put ' +
      'every file of the version you had back now';
    if LauncherLeftNew(App) then
      Text := Text + ', and a new file that starts PoolWatch stays after the next restart too, as Windows would not ' +
        'take it to delete then: so that nothing starts PoolWatch from old and new files mixed, every file of the ' +
        'version you had stays under the name Setup set it aside under (Setup''s log names each), and PoolWatch may ' +
        'not start after that restart until the setup has run again'
    else
    begin
      Text := Text + ': Windows puts back the rest when it next restarts';
      if Refused > 0 then
        Text := Text + ', but for what it would not take for that restart: any old file it would not take to put ' +
          'back stays under the name Setup set it aside under, and any new file it would not take to delete stays ' +
          'after the restart (Setup''s log names each), so PoolWatch may not start after that restart until the ' +
          'setup has run again';
      if LeftNew <> '' then
        Text := Text + '. As a new file stays after the restart under its own name, the files that start the version ' +
          'you had stay set aside then too, so that they start nothing from old and new files mixed';
    end;
    Text := Text + '. Until that restart ' + UntilRestart(App) + '.';
    if not Held then
      Text := Text + ' Do not uninstall PoolWatch before that restart: the uninstaller of the version you had could not ' +
        'be kept from running until then (Setup''s log says why), and it would not wait for the restart. It would ' +
        'remove PoolWatch''s Apps and Features entry and keep its changes and its sign-in task, and the files Windows ' +
        'puts back at the restart would then have no entry.'
    else if UninstallerProgramHeld then
      Text := Text + ' Until that restart PoolWatch''s uninstaller is not there to start, so it removes nothing: if ' +
        'Windows offers to remove PoolWatch from its list of apps before then, answer No.'
    else if OldUninstaller or (Refused = 0) then
      Text := Text + ' Until that restart PoolWatch''s uninstaller removes nothing.';
    if Refused > 0 then
      Text := Text + ' Run the setup again before you uninstall PoolWatch.'
    else
      Text := Text + ' Uninstall PoolWatch, if you want to, after the restart.';
    Text := Text + ' Setup does not restart Windows: restart it when it suits you.';
    if HeldFiles(App, True) > 0 then
      Text := Text + ' The files in use now, each with what has it open (Setup''s log, ' + ExpandConstant('{log}') +
        ', names every one):' + HeldList + MoreLine(HeldCount)
    else
      Text := Text + ' Setup''s log, ' + ExpandConstant('{log}') + ', names each file that was in use, and what had it open.';
    Log(Text);
    if not WizardSilent then
      MsgBox(Text, mbInformation, MB_OK);
  end;
end;

{ Lets the setup's page draw itself and answer Windows during a wait: the wait's page (WaitPage) moves its bar to Step
  of Steps and, as it does, takes the messages waiting for the wizard. Without it the page stopped drawing until the
  wait was over. The uninstaller has no such page (it waits only a few seconds when started by hand). }
procedure KeepDrawn(Step, Steps: Integer);
begin
  if WaitPage <> nil then
    WaitPage.SetProgress(Step, Steps);
end;

{ Sets the exit signal of a PoolWatch installed before the launcher (OldExitEvent: a global name of its own, which only
  an administrator can set), without starting any PoolWatch. With Logged, says what happened; quietly otherwise (the
  wait sets it again each quarter of a second, WaitForApp). }
procedure SetOldExit(Logged: Boolean);
var
  Event: THandle;
begin
  Event := OpenEvent(EVENT_MODIFY_STATE, False, OldExitEvent);
  if Event = 0 then
  begin
    if Logged then
      Log(Format('No PoolWatch made before the launcher is listening for its exit signal (error %d): the files decide.', [DLLGetLastError]));
  end
  else
  begin
    if SetEvent(Event) then
    begin
      if Logged then
        Log('The installed PoolWatch, made before the launcher, was asked to exit through its exit signal.');
    end
    else if Logged then
      Log(Format('The exit signal of the installed PoolWatch could not be set (error %d): the files decide.', [DLLGetLastError]));
    CloseHandle(Event);
  end;
end;

{ Waits, up to Seconds, until no file of PoolWatch's program in the install folder App is in use any more (HeldFiles),
  for as long as a process with PoolWatch's name runs from it (RunningFromApp: in the launcher's layout PoolWatch itself
  is app\PoolWatch.exe and the launcher the folder's PoolWatch.exe and, where it is there, PoolWatch-selfcontained.exe;
  in an install made before the launcher, the folder's PoolWatch.exe, or the second name where only that is there, is
  PoolWatch itself and there is no launcher; by their names, LauncherFile), and for BriefHold seconds when none does (a
  virus scanner or the search indexer has a file open only for a moment); it logs the processes it waits for. The
  result is how many such processes still run when it stops: 0 when none does, and -1 when that could not be known (no
  caller takes that for none). Once no file is in use the result is 0, whatever the last look at the list found: a
  process that runs one of PoolWatch's programs has its file in use, and a token-leak test helper this wait ended may
  still be listed for a moment after it ended. One WMI connection (ConnectWmi) is made and used for every look, so the
  wait does not reconnect each second; when it cannot be made, the result is -1 and the wait goes by the files alone.
  Each look at the list ends the token-leak test helpers when nothing else of PoolWatch runs (RunningFromApp). Each
  quarter of a second, the setup's page moves its bar and draws (KeepDrawn), and, once the setup has asked a PoolWatch
  installed before the launcher to exit (StoppingOld), its exit signal is set again, as that version's own stop did, so
  one that was still starting when it was first set is asked too. RunningNames is emptied first, so it never names what
  an earlier wait found. }
function WaitForApp(const App: String; Seconds: Integer): Integer;
var
  Waited, Quarter: Integer;
  Service: Variant;
  Listed, Held: Boolean;
  Exe, Launcher, Second: String;
begin
  RunningNames := '';
  Second := '';
  if HasLauncher(App) then
  begin
    Exe := ProgramPath(App);
    Launcher := LauncherFile(App);
    if FileExists(SecondLauncherPath(App)) and (CompareText(Launcher, SecondLauncherPath(App)) <> 0) then
      Second := SecondLauncherPath(App);
  end
  else
  begin
    Exe := LauncherFile(App);
    Launcher := '';
  end;
  Listed := True;
  try
    Service := ConnectWmi;
  except
    Log('The processes that run from the install folder could not be listed: ' + GetExceptionMessage);
    Listed := False;
  end;
  if Listed then
    Result := RunningFromApp(Service, Exe, Launcher, Second, True, True)
  else
    Result := -1;
  Waited := 0;
  Held := HeldFiles(App, False) > 0;
  while Held and (Waited < Seconds) and ((Result <> 0) or (Waited < BriefHold)) do
  begin
    for Quarter := 1 to 4 do
    begin
      KeepDrawn(Waited * 4 + Quarter, Seconds * 4);
      Sleep(250);
      if StoppingOld then
        SetOldExit(False);
    end;
    Waited := Waited + 1;
    if Listed then
      Result := RunningFromApp(Service, Exe, Launcher, Second, False, True);
    Held := HeldFiles(App, False) > 0;
  end;
  if not Held and (Result <> 0) then
  begin
    Log('No file of PoolWatch''s program in the install folder is in use, so no process runs one; what the last look at ' +
      'the list of processes found is not counted.');
    Result := 0;
  end;
  if Waited > 0 then
  begin
    if Result > 0 then
      Log(Format('%d PoolWatch processes still run from the install folder after %d s: %s.', [Result, Waited, RunningNames]))
    else if Result = 0 then
      Log(Format('After %d s no process of PoolWatch''s runs from the install folder.', [Waited]))
    else
      Log(Format('After %d s whether PoolWatch still runs from the install folder could still not be checked.', [Waited]));
  end;
end;

{ Run by hand: the wait before files are replaced (the installed PoolWatch asked to exit, then WaitForApp) on a page of
  its own, which keeps drawing. A page that cannot be shown or hidden leaves the wait on the Preparing page, and says
  so in the log. }
procedure ShowWaitPage;
begin
  if (WaitPage = nil) or WizardSilent then
    Exit;
  try
    WaitPage.SetText('Waiting for PoolWatch to close...', '');
    WaitPage.SetProgress(0, RunningWait);
    WaitPage.Show;
  except
    Log('The page for the wait could not be shown: ' + GetExceptionMessage);
  end;
end;

procedure HideWaitPage;
begin
  if (WaitPage = nil) or WizardSilent then
    Exit;
  try
    WaitPage.Hide;
  except
    Log('The page for the wait could not be hidden: ' + GetExceptionMessage);
  end;
end;

{ Whether the setup puts PoolWatch's files in place now or at the next restart, once the installed PoolWatch was asked
  to exit and waited for. Now, as [Files] does, only when Windows has no work in the install folder App for its next
  restart (LookForRestartWork) and no file of PoolWatch's program there is in use (HeldFiles: no process runs one or has
  it loaded, and no other program has one open). The files decide this, not the list of processes: a process that runs
  one of PoolWatch's programs has its file in use, and a token-leak test helper the wait has just ended no longer does.
  A file in use at the first look (which is logged) decides only when files are still in use at every look for
  BriefHold seconds after it, the last of them logged too: a virus scanner or the search indexer has a file open only
  for a moment. Otherwise the setup finishes at the next restart (FinishAtRestart): [Files] puts the files in StageDir,
  and Windows moves them all into place at that restart (SwapAtRestart); FinishFiles names each file in use and what
  has it open. Nothing here stops the setup: one of the uninstaller's files in use at any of these looks sends the
  uninstaller to a folder of its own (UninstallDir). }
procedure WhenToReplace(const App: String);
var
  Held, Uninstaller: Boolean;
  Quarter: Integer;
begin
  LookForRestartWork(App);
  Held := HeldFiles(App, True) > 0;
  Uninstaller := HeldUninstaller;
  Quarter := 0;
  while Held and (Quarter < BriefHold * 4) do
  begin
    Sleep(250);
    Quarter := Quarter + 1;
    Held := HeldFiles(App, Quarter = BriefHold * 4) > 0;
    Uninstaller := Uninstaller or HeldUninstaller;
  end;
  HeldUninstaller := Uninstaller;
  if Held then
  begin
    FinishFiles := HeldAll;
    FinishAtNextRestart('Some of PoolWatch''s files are in use, so they cannot be replaced now. Each is listed below with what has it open.');
  end
  else if not FinishAtRestart then
  begin
    Log('No file in the install folder is in use: Setup replaces them now.');
    if DirExists(App + '\' + StageFolder) then
      if not DelTree(App + '\' + StageFolder, True, True, True) then
        Log('Files an earlier setup left for a restart that has happened since could not all be deleted: ' + App + '\' + StageFolder);
  end;
  if HeldUninstaller then
    Log('One of the uninstaller''s files in the install folder is in use, so Setup writes its uninstaller in a folder of its own.');
end;

{ Asks a PoolWatch installed before the launcher to exit without starting it, when the install folder has no launcher of
  its program's version to ask through (LauncherPath is ''): an install made before the launcher, or an older program
  left under the launcher's name. Started from here, such a program would get the setup's environment, not the clean
  one the launcher gives. A PoolWatch installed before the launcher listens on a global exit signal of its own
  (OldExitEvent), which only an administrator can set; the setup sets it (SetOldExit), and WaitForApp then waits as for
  any PoolWatch, setting it again each quarter of a second (StoppingOld). Any other program there is not asked: the
  files then decide, as for any stop. }
procedure StopBeforeLauncher;
begin
  SetOldExit(True);
  StoppingOld := True;
end;

{ Before any file is copied. Only PoolWatch's own folder in Program Files: /DIR= still picks another one despite
  DisableDirPage, and PoolWatch refuses to run from most of them. An upgrade: the installed PoolWatch is asked to exit
  (PoolWatch --stop-running, through its launcher, LauncherPath: the one of the install folder's two launcher names that
  is the launcher of the program below it; from this version on it also ends the token-leak test helpers still running
  from its exe), or, when there is no such launcher, through the exit signal of a PoolWatch installed before the
  launcher (StopBeforeLauncher), then the setup waits until none of its files is in use, for as long as a process with
  PoolWatch's name runs from the install folder (WaitForApp, which ends such a helper itself when nothing else of
  PoolWatch runs, also one an earlier PoolWatch left). What --stop-running answered is only logged and never taken for
  "stopped": the files decide (WhenToReplace), so an install whose PoolWatch no longer runs can still be repaired.
  Inno Setup closes no program after this (CloseApplications is off): a file another program keeps in use sends the
  update to the next restart (WhenToReplace, SetAllAside). NeedsRestart is left as it is: no restart is asked for, and
  nothing but /DIR stops the setup here. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  App, Launcher: String;
begin
  Result := '';
  App := ExpandConstant('{app}');
  if CompareText(App, ExpandConstant('{autopf}\PoolWatch')) <> 0 then
  begin
    Result := 'PoolWatch installs only to ' + ExpandConstant('{autopf}\PoolWatch') + '. Run the setup without /DIR.';
    Exit;
  end;
  if FileExists(LauncherFile(App)) or HasLauncher(App) then
  begin
    ShowWaitPage;
    try
      Launcher := LauncherPath(App);
      if Launcher = '' then
        StopBeforeLauncher
      else if Exec(Launcher, '--stop-running', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        Log(Format('The installed PoolWatch answered --stop-running with exit code %d: the files decide.', [Code]))
      else
        Log(Format('The installed PoolWatch could not be started to ask it to exit (error %d): the files decide.', [Code]));
      WaitForApp(App, RunningWait);
    finally
      HideWaitPage;
    end;
  end;
  WhenToReplace(App);
end;

{ When Windows' list has Folder itself to delete at the next restart (an earlier uninstall gave it that: GoesAtRestart),
  a file (KeepName) is made in it now, so that it is not empty, and so not deleted, when that delete comes, before this
  setup's moves into it, which come after; the file is given to Windows to delete after the moves (SwapAtRestart). When
  it cannot be made, the update is counted as not given to Windows (NotScheduled), so that the restart moves none of
  its files. }
procedure KeepFolder(const Folder: String);
var
  Keep: String;
  N: Integer;
begin
  if not GoesAtRestart(Folder) then
    Exit;
  Keep := Folder + '\' + KeepName;
  ForceDirectories(Folder);
  if SaveStringToFile(Keep, '', False) then
  begin
    N := GetArrayLength(Keeps);
    SetArrayLength(Keeps, N + 1);
    Keeps[N] := Keep;
    Log('An earlier uninstall gave Windows ' + Folder + ' to delete at its next restart, before the moves into it: ' + Keep +
      ' keeps it there until after the moves.');
  end
  else
  begin
    NotScheduled := NotScheduled + 1;
    Log('Could not make ' + Keep + ', so at the next restart Windows would delete ' + Folder + ' before the moves into it.');
  end;
end;

{ Gives Windows each file below Dir (part of a setup's StageDir) to move at its next restart to the same place below
  Target (the install folder), replacing the file there, then each folder below Dir, and Dir, to delete then once it is
  empty. RestartReplace is MoveFileEx with MOVEFILE_DELAY_UNTIL_REBOOT and MOVEFILE_REPLACE_EXISTING; it leaves Inno
  Setup's restart state alone, so no restart is asked for. The folders the files go to are made now, and one an earlier
  uninstall would delete first at that restart is kept (KeepFolder). Each step is logged; a file Windows could not be
  given is counted in NotScheduled. }
procedure ScheduleMoves(const Dir, Target: String);
var
  Find: TFindRec;
  Name: String;
begin
  KeepFolder(Target);
  if FindFirst(Dir + '\*', Find) then
  try
    repeat
      Name := Find.Name;
      if (Name <> '.') and (Name <> '..') then
      begin
        if (Find.Attributes and AttrFolder) <> 0 then
          ScheduleMoves(Dir + '\' + Name, Target + '\' + Name)
        else
          try
            ForceDirectories(Target);
            RestartReplace(Dir + '\' + Name, Target + '\' + Name);
            Log('At the next restart Windows moves ' + Dir + '\' + Name + ' to ' + Target + '\' + Name + '.');
          except
            NotScheduled := NotScheduled + 1;
            Log('Windows could not be given ' + Dir + '\' + Name + ' to move at the next restart: ' + GetExceptionMessage);
          end;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
  try
    RestartReplace(Dir, '');
  except
    Log('Windows could not be given the folder ' + Dir + ' to delete at the next restart: ' + GetExceptionMessage);
  end;
end;

{ The Start menu entry, which [Icons] has just made: when Windows' list has it to delete at the next restart (an earlier
  uninstall could not delete it: GoesAtRestart), a copy of it goes in the install folder's StageFolder, and Windows is
  given that copy to move back over it after that delete. True when it did that, so that the caller gives Windows
  StageFolder to delete after it. Each step is logged. }
function KeepLink(const App: String): Boolean;
var
  Link, Saved: String;
begin
  Result := False;
  Link := ExpandConstant('{autoprograms}\PoolWatch.lnk');
  if not FileExists(Link) or not GoesAtRestart(Link) then
    Exit;
  Saved := App + '\' + StageFolder + '\' + RunStamp + '.lnk';
  try
    ForceDirectories(App + '\' + StageFolder);
    if not FileCopy(Link, Saved, False) then
      RaiseException('it could not be copied to ' + Saved);
    RestartReplace(Saved, Link);
    Result := True;
    Log('An earlier uninstall gave Windows the Start menu entry to delete at its next restart: Windows moves a copy of ' +
      'the new one back after that, from ' + Saved + '.');
  except
    Log('The Start menu entry goes at the next restart, where an earlier uninstall gave Windows it to delete, and ' +
      'could not be kept: ' + GetExceptionMessage + '. Run the setup again after that restart.');
  end;
end;

{ For Unstage: the file whose content Path holds at the next restart just before this run's own moves (those out of
  StageDir), from Windows' list as it is now (ReadRestartWork), worked through in its order: Path itself when it is
  there; then, for each earlier entry about Path, the file a move into it comes from, when that file is there now
  (Windows skips a move whose file is gone), or nothing, after a delete of Path or a move of it elsewhere. '' when
  nothing is there then. So the file an earlier setup that did not finish left set aside to go back into Path at that
  restart, which is not at Path now, counts as what this run's move into Path replaces. }
function FilledAtRestart(const Path: String): String;
var
  Froms, Tos: TArrayOfString;
  Own, Name: String;
  I: Integer;
begin
  Result := '';
  if FileExists(Path) then
    Result := Path;
  Name := Lowercase(Path);
  Own := Lowercase(StageDir) + '\';
  ReadRestartWork(Froms, Tos);
  for I := 0 to GetArrayLength(Froms) - 1 do
    if Copy(Froms[I], 1, Length(Own)) <> Own then
    begin
      if Tos[I] = Name then
      begin
        if FileExists(Froms[I]) then
          Result := Froms[I];
      end
      else if Froms[I] = Name then
        Result := '';
    end;
end;

{ After Windows could not be given every new file (SwapAtRestart), for a new file below Dir (part of StageDir) that
  DelTree could not delete (another program has it open): its move to the same place below Target (the install folder)
  is already in Windows' list, and would put it among the old files at the restart. So it is renamed, and Windows,
  finding no file to move then, moves nothing; the renamed file is deleted, at once or at that restart. One that cannot
  be renamed either goes in place at the restart, so the file it replaces then (FilledAtRestart: what is there now, or
  what an earlier setup's entry in Windows' list moves there first) is kept now, as a copy or, when it cannot be read,
  under a second name of the same file (CreateHardLinkW, which another program's sharing does not stop), and Windows is
  given that to move back over it after the move. Only where nothing would be there then is that place given to
  Windows to delete after the move, so no delete is ever given for a place an earlier entry fills. Only when that cannot
  be done either does the new file stay in place after the restart: the file it replaces could be neither copied nor
  given a second name, or Windows would not take that move back, as it would not take one of the new files' before
  (MoveFileEx fails when it cannot write its list in the registry). Such a file is counted (Unkept) and named in
  FinishFiles, and SwapAtRestart then keeps every file that starts PoolWatch away from the launcher's names after the
  restart (KeepLauncherOut). The file it would replace, when neither a copy nor a second name of it could be made, is
  renamed aside now (to its name followed by .<RunStamp>.out.old), so that the new file does not replace it: one an
  earlier entry would move there then finds nothing to move, and one in place now (counted in TakenOut and named in
  FinishFiles, as the version in place until the restart is without it) leaves that place empty for the move. One at a
  launcher's name is left to KeepLauncherOut, which sets it aside now after giving Windows that name to set aside at
  the restart. The folder is listed first and its files renamed after, so a renamed file is never listed again. Each
  step is logged. }
procedure Unstage(const Dir, Target: String);
var
  Find: TFindRec;
  Found: TArrayOfString;
  Name, Path, Place, Saved, Before: String;
  I, N: Integer;
begin
  if FindFirst(Dir + '\*', Find) then
  try
    repeat
      if (Find.Name <> '.') and (Find.Name <> '..') then
      begin
        N := GetArrayLength(Found);
        SetArrayLength(Found, N + 1);
        if (Find.Attributes and AttrFolder) <> 0 then
          Found[N] := '\' + Find.Name
        else
          Found[N] := Find.Name;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
  for I := 0 to GetArrayLength(Found) - 1 do
  begin
    Name := Found[I];
    if Copy(Name, 1, 1) = '\' then
    begin
      Name := Copy(Name, 2, Length(Name) - 1);
      Unstage(Dir + '\' + Name, Target + '\' + Name);
    end
    else
    begin
      Path := Dir + '\' + Name;
      Place := Target + '\' + Name;
      if RenameFile(Path, Path + '.unmoved') then
        DeleteNowOrAtRestart(Path + '.unmoved')
      else
      begin
        Before := '';
        Saved := '';
        try
          Before := FilledAtRestart(Place);
          if Before <> '' then
          begin
            if not FileCopy(Before, Place + '.' + RunStamp + '.old', False) then
              if not CreateHardLinkW(Place + '.' + RunStamp + '.old', Before, 0) then
                RaiseException('it could be neither copied nor given a second name, ' + Place + '.' + RunStamp + '.old');
            Saved := Place + '.' + RunStamp + '.old';
            RestartReplace(Saved, Place);
          end
          else
            RestartReplace(Place, '');
          Log('Another program has ' + Path + ' open' + HoldersOf(Path) + ', so Windows moves it to ' + Place +
            ' at its next restart, and then puts back what would be there then.');
        except
          Unkept := Unkept + 1;
          FinishFiles := FinishFiles + #13#10 + Place + ', which Windows still moves into place at the restart' + HoldersOf(Path);
          Log('Another program has ' + Path + ' open' + HoldersOf(Path) + ', so Windows moves it to ' + Place +
            ' at its next restart, and what would be there then could not be kept: ' + GetExceptionMessage);
          if (Saved = '') and (Before <> '') and not StartsPoolWatch(Before) then
            if RenameFile(Before, Before + '.' + RunStamp + '.out.old') then
            begin
              if CompareText(Before, Place) = 0 then
              begin
                TakenOut := TakenOut + 1;
                FinishFiles := FinishFiles + #13#10 + Place + ', which Setup set aside now, as ' + Before + '.' +
                  RunStamp + '.out.old, so that the restart does not replace it';
                Log('Set aside ' + Place + ' now, as ' + Before + '.' + RunStamp + '.out.old, so that this setup''s ' +
                  'file does not replace it at the restart.');
              end
              else
                Log('Renamed ' + Before + ' aside, so that its move to ' + Place + ' at the restart finds nothing ' +
                  'and this setup''s file does not replace it there.');
            end;
        end;
      end;
    end;
  end;
end;

{ For KeepLauncherOut: gives Windows the launcher's name Path to rename aside at the next restart (to
  Path.<RunStamp>.later.old), after every move to it, which come first in its list, as another program has Held open
  (Path itself, or a file Windows' list moves there then) without letting it be renamed (without FILE_SHARE_DELETE).
  When Windows will not take that either, a file that starts PoolWatch can be at Path after the restart: the name is
  counted (LauncherKept) and named in FinishFiles with what has Held open. Logged. }
procedure SetAsideAtRestart(const Path, Held: String);
begin
  try
    RestartReplace(Path, Path + '.' + RunStamp + '.later.old');
    Log('Another program has ' + Held + ' open' + HoldersOf(Held) + ', so Windows sets ' + Path + ' aside at its ' +
      'next restart, after every move to it.');
  except
    LauncherKept := LauncherKept + 1;
    if CompareText(Held, Path) = 0 then
      FinishFiles := FinishFiles + #13#10 + Path + ', which could not be set aside' + HoldersOf(Held)
    else
      FinishFiles := FinishFiles + #13#10 + Path + ', where Windows moves ' + Held + ' at the restart: that file ' +
        'could not be set aside' + HoldersOf(Held);
    Log('Another program has ' + Held + ' open' + HoldersOf(Held) + ', and Windows could not be given ' + Path +
      ' to set aside at its next restart (' + GetExceptionMessage + '), so after the restart it may start PoolWatch ' +
      'from old and new files mixed until a setup has replaced the files.');
  end;
end;

{ After Unstage left a new file that Windows still moves into place at the next restart (Unkept): no file that starts
  PoolWatch may be at either of the install folder App's launcher names after that restart, so that nothing starts it
  from that new file and the old ones mixed. For each name, each file an entry of Windows' list moves to that name at
  the restart (ReadRestartWork: an old launcher an earlier setup that did not finish left to go back then, one an
  earlier setup put in its own folder, this run's own one, a copy Unstage keeps) is renamed aside now (to its own name
  followed by .<RunStamp>.out.old; a rename needs nothing from Windows' list), so that its move finds nothing. When
  another program keeps one of them from being renamed (it has it open without FILE_SHARE_DELETE), Windows is given the
  name to rename aside at the restart, after every move to it (SetAsideAtRestart), before anything else is done with
  the file at that name, so that this is asked while that file is still there. Then the file at the name now is
  renamed aside the same way; when another program keeps that one from being renamed, Windows is given the name to
  rename aside at the restart (SetAsideAtRestart), unless it was given it already. Only when Windows will not take that
  either can a file that starts PoolWatch be there after the restart (LauncherKept). Nothing is deleted: a setup that
  replaces the files later, as one run after that restart does, deletes what was set aside, a file in the install
  folder (DropAside) and one in the folder of an earlier setup that finished at a restart, with that folder
  (WhenToReplace). Each step is logged. }
procedure KeepLauncherOut(const App: String);
var
  Froms, Tos: TArrayOfString;
  I, J: Integer;
  Path, Held: String;
begin
  ReadRestartWork(Froms, Tos);
  for I := 1 to 2 do
  begin
    if I = 1 then
      Path := App + '\' + PoolWatchName
    else
      Path := SecondLauncherPath(App);
    Held := '';
    for J := 0 to GetArrayLength(Froms) - 1 do
      if Tos[J] = Lowercase(Path) then
        if FileExists(Froms[J]) then
          if not RenameFile(Froms[J], Froms[J] + '.' + RunStamp + '.out.old') then
            Held := Froms[J];
    if Held <> '' then
      SetAsideAtRestart(Path, Held);
    if FileExists(Path) then
      if not RenameFile(Path, Path + '.' + RunStamp + '.out.old') then
        if Held = '' then
        begin
          Held := Path;
          SetAsideAtRestart(Path, Held);
        end;
    if Held = '' then
      Log('Nothing is at ' + Path + ' after the next restart, so nothing starts PoolWatch from old and new files ' +
        'mixed.');
  end;
end;

{ After a setup that finishes at the next restart has put PoolWatch's files in StageDir: gives Windows every one of them
  to move into place at that restart, all together (ScheduleMoves), the Start menu entry to move back after an earlier
  uninstall's delete of it (KeepLink), the files that keep folders until those moves to delete after them (Keeps), and
  the folder StageFolder to delete then once it is empty. If Windows could not be given one of the files, the new files
  in StageDir and the Keeps are deleted, and the moves of any new file that could not be deleted are undone (Unstage),
  so that the restart puts none of them in place, but for one whose move could not be undone (Unkept), for which no
  file that starts PoolWatch is left at the launcher's names after the restart (KeepLauncherOut); the Finished page and
  the exit code (ExitNotFinished) then say so, name each such file, and say that running the setup again after the
  restart puts the whole version in place. }
procedure SwapAtRestart;
var
  App: String;
  I: Integer;
begin
  App := ExpandConstant('{app}');
  NotScheduled := 0;
  ScheduleMoves(StageDir, App);
  KeepLink(App);
  for I := 0 to GetArrayLength(Keeps) - 1 do
    try
      RestartReplace(Keeps[I], '');
    except
      Log('Windows could not be given ' + Keeps[I] + ' to delete at the next restart: ' + GetExceptionMessage);
    end;
  try
    RestartReplace(App + '\' + StageFolder, '');
  except
    Log('Windows could not be given the folder ' + App + '\' + StageFolder + ' to delete at the next restart: ' + GetExceptionMessage);
  end;
  if NotScheduled > 0 then
  begin
    Log(Format('Windows could not be given %d of the new files to move at the next restart, so none of them is moved: ' +
      'the new files in %s are deleted.', [NotScheduled, StageDir]));
    if not DelTree(StageDir, True, True, True) then
    begin
      Log('Some of the new files could not be deleted: ' + StageDir);
      Unstage(StageDir, App);
      if Unkept > 0 then
        KeepLauncherOut(App);
    end;
    for I := 0 to GetArrayLength(Keeps) - 1 do
      DeleteFile(Keeps[I]);
  end;
end;

{ What the Finished page and the log say when the setup finishes at the next restart: that Windows puts the new version
  in place then, and why (FinishReason); or, when Windows could not be given the files (NotScheduled), that the setup
  took them back but for any whose move could not be undone (Unkept: those go in place at the restart, among the old
  files; a file of the old version one of them would replace, of which no copy could be kept, is set aside now,
  TakenOut; and the files that start PoolWatch are set aside, now or at that restart, so that PoolWatch does not start
  after it until the setup has run again, KeepLauncherOut, unless another program keeps one from that and Windows will
  not take it to set aside at the restart either, LauncherKept), that an old file Windows would not take to put back
  at the restart stays set aside (Refused), what Apps and Features shows until then, and what to do. What starts
  PoolWatch until that restart is read from the install folder (UntilRestart), so that it holds after an earlier setup
  or uninstall that left work for that restart too. }
function FinishHeading: String;
begin
  if NotScheduled > 0 then
    Result := 'PoolWatch is not fully updated'
  else
    Result := 'PoolWatch updates when Windows restarts';
end;

function FinishText: String;
var
  Meanwhile: String;
begin
  Meanwhile := 'until then ' + UntilRestart(ExpandConstant('{app}'));
  if NotScheduled > 0 then
  begin
    Result := 'Windows could not be given the new files to put in place at its next restart, so Setup took them back; ' +
      Meanwhile + '.';
    if Unkept > 0 then
    begin
      if Unkept = 1 then
        Result := Result + ' But one of them, which another program has open (listed below), could not be taken back: ' +
          'Windows still puts it in place at that restart, among the files of the version you had.'
      else
        Result := Result + Format(' But %d of them, which another program has open (listed below), could not be taken ' +
          'back: Windows still puts those in place at that restart, among the files of the version you had.', [Unkept]);
      if TakenOut = 1 then
        Result := Result + ' Setup set one file of the version you had aside now (listed below), as it could keep no ' +
          'copy of it, so that the restart does not replace it.'
      else if TakenOut > 1 then
        Result := Result + Format(' Setup set %d files of the version you had aside now (listed below), as it could ' +
          'keep no copy of them, so that the restart does not replace them.', [TakenOut]);
      if LauncherKept > 0 then
        Result := Result + ' Another program also keeps one of the files that start PoolWatch from being set aside, ' +
          'and Windows would not take it to set aside at that restart either (listed below), so after that restart ' +
          'PoolWatch may start from old and new files mixed until the setup has run again.'
      else
        Result := Result + ' So that PoolWatch never starts from old and new files mixed, Setup set aside the files ' +
          'that start it, now or, where another program has one open, at that restart: after that restart PoolWatch ' +
          'does not start until the setup has run again.';
    end;
    if Refused > 0 then
      Result := Result + ' Windows would not take every old file Setup could not put back now to put back at that ' +
        'restart either, so PoolWatch may not start after it until the setup has run again.';
    Result := Result + ' Until the setup has run again, Apps > Installed apps shows this version''s number. Restart Windows, ' +
      'then run the setup again: it then puts the whole version in place. Its log says what happened.' + #13#10#13#10 +
      FinishReason;
  end
  else
    Result := 'Windows puts this version of PoolWatch in place the next time it restarts; ' + Meanwhile + '. Setup does ' +
      'not restart Windows: restart it when it suits you.' + #13#10#13#10 + FinishReason;
end;

{ The I-th file (from 0) that an earlier layout of the install folder had and this one has elsewhere or not at all, by
  its path below the install folder; '' past the last. The native DLLs PoolWatch loads from its own folder were next to
  the install folder's PoolWatch.exe before the launcher took that name, and are below app\ now. PoolWatchLauncher.exe
  is a name the launcher had in builds made before this layout, none of them released. (PoolWatch-selfcontained.exe, the
  self-contained zip's name, is not deleted: [Files] puts the launcher there, SelfContainedNameThere.) }
function OldLayoutFile(I: Integer): String;
begin
  case I of
    0: Result := 'amd64\KernelTraceControl.dll';
    1: Result := 'amd64\msdia140.dll';
    2: Result := 'PoolWatchLauncher.exe';
  else
    Result := '';
  end;
end;

{ For RemoveOldLayout when the setup finishes at the next restart: the file Path of an earlier layout, which a setup
  that could not put it back now (this one, or an earlier one that did not finish) left set aside (Path.<run>.old) for
  Windows to move back at that restart (MovesAtRestart), is deleted now under that name, so that the move finds nothing
  then. The version it belongs to is not in place until then either: a setup leaves the launcher's names for the
  restart whenever it leaves another old file for it (PutAllBack). One that cannot be deleted now (another program has
  it open) has Path given to Windows to delete at that restart, after that move, which comes first in Windows' list.
  Each step is logged. }
procedure DropOldLayoutAside(const Path: String);
var
  Find: TFindRec;
  Aside: String;
begin
  if FindFirst(Path + '.*.old', Find) then
  try
    repeat
      Aside := ExtractFileDir(Path) + '\' + Find.Name;
      if ((Find.Attributes and AttrFolder) = 0) and MovesAtRestart(Aside) then
      begin
        if DeleteFile(Aside) then
          Log('Deleted ' + Aside + ', which was to go back at the next restart as ' + Path + ', a file of an earlier ' +
            'layout of the install folder.')
        else
          try
            RestartReplace(Path, '');
            Log('Windows deletes ' + Path + ', a file of an earlier layout of the install folder, at its next restart, ' +
              'after it goes back there from ' + Aside + HoldersOf(Aside) + '.');
          except
            Log('Windows could not be given ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
          end;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

{ Deletes what an earlier layout left in the install folder App (OldLayoutFile), and then its amd64 folder once it is
  empty, once the new files are in place. When the setup finishes at the next restart (AtRestart), or a file cannot be
  deleted now, Windows is given it to delete at that restart instead (after the moves SwapAtRestart gave it, so until
  then the version that was there keeps every file); then such a file an earlier setup set aside to go back at that
  restart goes too (DropOldLayoutAside), as that move would bring it back. A link is deleted itself, never followed.
  Each step is logged. }
procedure RemoveOldLayout(const App: String; AtRestart: Boolean);
var
  I: Integer;
  Path: String;
begin
  I := 0;
  while OldLayoutFile(I) <> '' do
  begin
    Path := App + '\' + OldLayoutFile(I);
    if FileExists(Path) then
    begin
      if not AtRestart and DeleteFile(Path) then
        Log('Deleted ' + Path + ', which an earlier layout of the install folder left.')
      else
        try
          RestartReplace(Path, '');
          Log('Windows deletes ' + Path + ', which an earlier layout of the install folder left, at its next restart.');
        except
          Log('Windows could not be given ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
        end;
    end
    else if AtRestart then
      DropOldLayoutAside(Path);
    I := I + 1;
  end;
  Path := App + '\amd64';
  if DirExists(Path) then
  begin
    if not AtRestart and RemoveDir(Path) then
      Log('Deleted the folder ' + Path + ', which an earlier layout of the install folder left.')
    else
      try
        RestartReplace(Path, '');
        Log('Windows deletes the folder ' + Path + ' at its next restart, once it is empty.');
      except
        Log('Windows could not be given the folder ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
      end;
  end;
end;

{ Once the setup has written its uninstaller (UninstallDir) and put its files in place: every other uninstaller in the
  install folder App goes, as Apps and Features now starts this one: each of the install folder's own files (unins*)
  but this one's (its name before the extension, which Inno Setup numbers: unins000, unins001 ...), all of them when
  this one is elsewhere; and each folder in UninstallFolder but this one's, then UninstallFolder itself once it is
  empty. What cannot be deleted now (another program has it open) stays, and goes with a later setup or with the
  uninstall; it is never left for the restart, which a later setup would then have to wait for. Each step is logged.
  Nothing goes when Inno Setup never asked where to write this run's uninstaller (UninstallerDir is ''). }
procedure SweepUninstallers(const App: String);
var
  Find: TFindRec;
  Path, Mine: String;
begin
  if UninstallerDir = '' then
    Exit;
  Mine := ChangeFileExt(ExtractFileName(ExpandConstant('{uninstallexe}')), '');
  if FindFirst(App + '\unins*', Find) then
  try
    repeat
      Path := App + '\' + Find.Name;
      if ((Find.Attributes and AttrFolder) = 0) and
         ((CompareText(UninstallerDir, App) <> 0) or (CompareText(ChangeFileExt(Find.Name, ''), Mine) <> 0)) then
        if DeleteFile(Path) then
          Log('Deleted ' + Path + ', which the uninstaller ' + ExpandConstant('{uninstallexe}') + ' replaces.')
        else
          Log('In use, so not deleted now' + HoldersOf(Path) + ': ' + Path + '. A later setup or the uninstall deletes it.');
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
  if FindFirst(App + '\' + UninstallFolder + '\*', Find) then
  try
    repeat
      Path := App + '\' + UninstallFolder + '\' + Find.Name;
      if (Find.Name <> '.') and (Find.Name <> '..') and (CompareText(Path, UninstallerDir) <> 0) then
        if DelTree(Path, True, True, True) then
          Log('Deleted ' + Path + ', an earlier setup''s uninstaller.')
        else
          Log('Not all of ' + Path + ', an earlier setup''s uninstaller, could be deleted now: a later setup or the uninstall deletes it.');
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
  RemoveDir(App + '\' + UninstallFolder);
end;

{ At ssInstall: that the setup got there is noted first (InstallBegun, for DeinitializeSetup), then whether the install
  folder has a launcher of its program's version (OldUninstaller), and the Start menu entry and the Apps and Features
  entry are copied (SaveLinkBefore, SaveEntryBefore); when the setup replaces the files now, every old file is set
  aside (SetAllAside); then, once it is known where this run writes its uninstaller, the one it replaces in the install
  folder is copied (SaveUninstallerBefore). At ssPostInstall, once the files are in place: a setup that finishes at the
  next restart first gives Windows its files to move then (SwapAtRestart), and the earlier layout's files to delete
  after that (RemoveOldLayout, which also deletes one set aside to go back then), unless Windows could not be given
  them all (the restart then puts in place only a file another program keeps from being taken back whose move could not
  be undone, and no file that starts PoolWatch is left at the launcher's names after that restart unless Windows would
  not take that either: Unstage, KeepLauncherOut); it logs what the Finished page says, with every file that was in use
  (FinishFiles). One that replaced them now deletes the old files it set aside (DropAside), what an earlier layout left
  (RemoveOldLayout), and keeps a Start menu entry an earlier uninstall gave Windows to delete (KeepLink). Then the
  uninstaller this one replaces goes (SweepUninstallers). }
procedure CurStepChanged(CurStep: TSetupStep);
var
  App: String;
begin
  App := ExpandConstant('{app}');
  if CurStep = ssInstall then
  begin
    InstallBegun := True;
    OldUninstaller := LauncherPath(App) = '';
    SaveLinkBefore;
    SaveEntryBefore;
    SetAllAside(App);
    SaveUninstallerBefore(App);
  end
  else if CurStep = ssPostInstall then
  begin
    InstallDone := True;
    if FinishAtRestart then
    begin
      SwapAtRestart;
      Log(FinishHeading + '. ' + FinishText + FinishFiles);
      if NotScheduled = 0 then
        RemoveOldLayout(App, True);
    end
    else
    begin
      DropAside;
      RemoveOldLayout(App, False);
      if KeepLink(App) then
        try
          RestartReplace(App + '\' + StageFolder, '');
        except
          Log('Windows could not be given the folder ' + App + '\' + StageFolder + ' to delete at the next restart: ' + GetExceptionMessage);
        end;
    end;
    SweepUninstallers(App);
  end;
end;

{ The setup's exit code when it has run to its end (Inno Setup asks only then; 0 keeps its own): ExitFinishesAtRestart
  when it finishes at the next restart, ExitNotFinished when Windows could not be given its files for that. }
function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if FinishAtRestart then
  begin
    if NotScheduled > 0 then
      Result := ExitNotFinished
    else
      Result := ExitFinishesAtRestart;
  end;
end;

{ The wizard's pages. Their words are in the Messages and CustomMessages sections; this lays out what Inno Setup has no
  place for: the Welcome page's paragraphs, the Ready page's three steps and its "You stay in control" panel (where Inno
  Setup shows its list of settings), and the Finished page's one paragraph above the tick box. Every position and size
  goes through ScaleX or ScaleY, and each text is measured (AdjustHeight) before what comes below it is placed, so the
  pages fit at every display scale. }
const
  { Colours as TColor ($BBGGRR): grey #59636E for the quieter lines; green #1A7F37 on #EEF7F0 for the panel, with its
    sentence in ink #1F2328 (a fixed colour on the panel's fixed background, so it stays readable in a high-contrast
    theme, where the page's own text colour can be white). }
  MutedColor = $6E6359;
  SafeColor = $377F1A;
  SafeBackColor = $F0F7EE;
  InkColor = $28231F;
  { The Ready page's icons, in pixels at 100% display scale. installer\ready holds each at the size Setup draws it at
    100, 125, 150, 175 and 200% display scale (StoredIconSize), named ready-<name>-<pixels>.png. }
  StepIconSize = 30;
  NextIconSize = 14;
  SafeIconSize = 22;
  { Setup scales by its font, not by the display scale itself: with 9-point Segoe UI, ScaleX(N) is N times the font's
    height over 15, and the font is 15 pixels high at 100% and 20, 25, 30 and 32 at 125, 150, 175 and 200% (the steps of
    Inno Setup's own table of image sizes). }
  FontHeightAt100 = 15;

var
  StepIcons: array[0..2] of TBitmapImage;
  StepTitles: array[0..2] of TNewStaticText;
  StepTexts: array[0..2] of TNewStaticText;
  NextIcons: array[0..1] of TBitmapImage;
  SafePanel, SafeBar: TPanel;
  SafeIcon: TBitmapImage;
  SafeTitle, SafeText: TNewStaticText;

{ A label on Parent at Left, Top, Width wide, its words wrapped and its height measured; bold if asked, and in
  TextColor unless that is clNone (the usual colour). }
function NewLabel(Parent: TWinControl; Left, Top, Width: Integer; const Caption: String; Bold: Boolean;
  TextColor: TColor): TNewStaticText;
begin
  Result := TNewStaticText.Create(WizardForm);
  Result.Parent := Parent;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.Left := Left;
  Result.Top := Top;
  Result.Width := Width;
  if Bold then
    Result.Font.Style := [fsBold];
  if TextColor <> clNone then
    Result.Font.Color := TextColor;
  Result.Caption := Caption;
  Result.AdjustHeight;
end;

{ The font's height at the display scales the icons are stored for: Level 0 to 4 is 100, 125, 150, 175 and 200%. }
function StoredFontHeight(Level: Integer): Integer;
begin
  case Level of
    0: Result := FontHeightAt100;
    1: Result := 20;
    2: Result := 25;
    3: Result := 30;
  else
    Result := 32;
  end;
end;

{ The stored size of an icon drawn Base pixels wide at 100% to use for one Size pixels wide: the smallest that is at
  least Size, else the largest (200%). Each stored size is Base scaled as Setup scales it at that display scale (to the
  nearest pixel, as ScaleX does), so at 100 to 200% the stored size is exactly Size and the icon is not stretched. }
function StoredIconSize(Base, Size: Integer): Integer;
var
  Level: Integer;
begin
  Level := 0;
  Result := Base;
  while (Result < Size) and (Level < 4) do
  begin
    Level := Level + 1;
    Result := (2 * Base * StoredFontHeight(Level) + FontHeightAt100) div (2 * FontHeightAt100);
  end;
end;

{ One of the Ready page's icons, Base pixels at 100% and scaled, at Left, Top on Parent. When it cannot be shown the
  page still has every word: the reason is logged and the icon left out. }
function NewIcon(Parent: TWinControl; const Name: String; Base, Left, Top: Integer): TBitmapImage;
var
  Size, Stored: Integer;
  FileName, Path: String;
begin
  Size := ScaleX(Base);
  Stored := StoredIconSize(Base, Size);
  FileName := Format('ready-%s-%d.png', [Name, Stored]);
  Path := ExpandConstant('{tmp}\') + FileName;
  Result := TBitmapImage.Create(WizardForm);
  Result.Parent := Parent;
  Result.BackColor := clNone;
  Result.Stretch := Stored <> Size;
  Result.Left := Left;
  Result.Top := Top;
  Result.Width := Size;
  Result.Height := Size;
  try
    if not FileExists(Path) then
      ExtractTemporaryFile(FileName);
    Result.PngImage.LoadFromFile(Path);
  except
    Log('The Ready page''s icon ' + FileName + ' could not be shown: ' + GetExceptionMessage);
  end;
end;

{ The Welcome page: the mission (WelcomeLabel2) a size up, then the setup sentence, then "Click Next to continue." in
  grey, each a paragraph of its own. Inno Setup would put all three in WelcomeLabel2, in one size. The log says where
  the text ends on the page. }
procedure LayOutWelcomePage;
var
  Mission, SetupText, ClickNextText: TNewStaticText;
begin
  Mission := WizardForm.WelcomeLabel2;
  Mission.Font.Size := Mission.Font.Size + 1;
  Mission.Caption := SetupMessage(msgWelcomeLabel2);
  Mission.AdjustHeight;
  SetupText := NewLabel(Mission.Parent, Mission.Left, Mission.Top + Mission.Height + ScaleY(12), Mission.Width,
    CustomMessage('WelcomeSetup'), False, clNone);
  ClickNextText := NewLabel(Mission.Parent, Mission.Left, SetupText.Top + SetupText.Height + ScaleY(12), Mission.Width,
    SetupMessage(msgClickNext), False, MutedColor);
  Log(Format('Welcome page, %d x %d: the text ends at %d.', [
    Mission.Parent.Width, Mission.Parent.Height, ClickNextText.Top + ClickNextText.Height]));
end;

{ Step I (0 to 2) of the Ready page's three, StepWidth wide: its icon, its name in bold and a line in grey, with an
  arrow to the next step after it. Placed across the page here; down the page when it shows (LayOutReadyPage). }
procedure NewStep(I, StepWidth: Integer; const IconName, Title, Line: String);
var
  X: Integer;
begin
  X := I * (StepWidth + ScaleX(28));
  StepIcons[I] := NewIcon(WizardForm.ReadyPage, IconName, StepIconSize, X, ScaleY(4));
  StepTitles[I] := NewLabel(WizardForm.ReadyPage, X, 0, StepWidth, Title, True, clNone);
  StepTexts[I] := NewLabel(WizardForm.ReadyPage, X, 0, StepWidth, Line, False, MutedColor);
  if I < 2 then
    NextIcons[I] := NewIcon(WizardForm.ReadyPage, 'next', NextIconSize, X + StepWidth + ScaleX(6),
      ScaleY(4) + (ScaleX(StepIconSize) - ScaleX(NextIconSize)) div 2);
end;

{ The Ready page's parts, made once: the three steps across the page (an arrow and some room between each two), and the
  green panel below them, a bar down its left side, its shield, its name in green and its sentence. }
procedure CreateReadyPage;
var
  PageWidth, StepWidth, TextLeft: Integer;
begin
  PageWidth := WizardForm.InnerNotebook.Width;
  StepWidth := (PageWidth - 2 * ScaleX(28)) div 3;
  NewStep(0, StepWidth, 'install', CustomMessage('ReadyInstallTitle'), '');
  NewStep(1, StepWidth, 'tray', CustomMessage('ReadyTrayTitle'), CustomMessage('ReadyTrayText'));
  NewStep(2, StepWidth, 'setup', CustomMessage('ReadySetupTitle'), CustomMessage('ReadySetupText'));

  SafePanel := TPanel.Create(WizardForm);
  SafePanel.Parent := WizardForm.ReadyPage;
  SafePanel.BevelOuter := bvNone;
  SafePanel.ParentBackground := False;
  SafePanel.Color := SafeBackColor;
  SafePanel.Caption := '';
  SafePanel.Left := 0;
  SafePanel.Width := PageWidth;
  SafeBar := TPanel.Create(WizardForm);
  SafeBar.Parent := SafePanel;
  SafeBar.BevelOuter := bvNone;
  SafeBar.ParentBackground := False;
  SafeBar.Color := SafeColor;
  SafeBar.Caption := '';
  SafeBar.Left := 0;
  SafeBar.Top := 0;
  SafeBar.Width := ScaleX(3);
  SafeIcon := NewIcon(SafePanel, 'safe', SafeIconSize, ScaleX(3 + 14), ScaleY(12));
  TextLeft := SafeIcon.Left + SafeIcon.Width + ScaleX(12);
  SafeTitle := NewLabel(SafePanel, TextLeft, ScaleY(12), PageWidth - TextLeft - ScaleX(14),
    CustomMessage('ReadySafeTitle'), True, SafeColor);
  SafeText := NewLabel(SafePanel, TextLeft, 0, SafeTitle.Width, CustomMessage('ReadySafeText'), False, InkColor);
  SafeTitle.Color := SafeBackColor;
  SafeText.Color := SafeBackColor;
  WizardForm.ReadyLabel.Font.Color := MutedColor;
end;

{ Places the Ready page's parts down the page when it shows (after Inno Setup has set the page up), each below what it
  follows by its measured height; the first step names the folder Setup installs into. Inno Setup's list of settings
  stays hidden, and its "Click Install to continue." (ReadyLabel) goes to the foot of the page, or just below the panel
  on a page too short for that. The log says where each part went. }
procedure LayOutReadyPage;
var
  I, Bottom: Integer;
begin
  WizardForm.ReadyMemo.Visible := False;
  StepTexts[0].Caption := FmtMessage(CustomMessage('ReadyInstallText'), [WizardDirValue]);
  Bottom := 0;
  for I := 0 to 2 do
  begin
    StepTitles[I].Top := StepIcons[I].Top + StepIcons[I].Height + ScaleY(9);
    StepTitles[I].AdjustHeight;
    StepTexts[I].Top := StepTitles[I].Top + StepTitles[I].Height + ScaleY(4);
    StepTexts[I].AdjustHeight;
    if StepTexts[I].Top + StepTexts[I].Height > Bottom then
      Bottom := StepTexts[I].Top + StepTexts[I].Height;
  end;
  SafePanel.Top := Bottom + ScaleY(26);
  SafeText.Top := SafeTitle.Top + SafeTitle.Height + ScaleY(3);
  SafeText.AdjustHeight;
  Bottom := SafeText.Top + SafeText.Height;
  if SafeIcon.Top + SafeIcon.Height > Bottom then
    Bottom := SafeIcon.Top + SafeIcon.Height;
  SafePanel.Height := Bottom + ScaleY(12);
  SafeBar.Height := SafePanel.Height;
  WizardForm.ReadyLabel.Top := WizardForm.InnerNotebook.Height - WizardForm.ReadyLabel.Height - ScaleY(10);
  if WizardForm.ReadyLabel.Top < SafePanel.Top + SafePanel.Height + ScaleY(8) then
    WizardForm.ReadyLabel.Top := SafePanel.Top + SafePanel.Height + ScaleY(8);
  Log(Format('Ready page, %d x %d: steps end at %d, the panel runs from %d to %d, the last line runs from %d to %d.', [
    WizardForm.InnerNotebook.Width, WizardForm.InnerNotebook.Height, SafePanel.Top - ScaleY(26), SafePanel.Top,
    SafePanel.Top + SafePanel.Height, WizardForm.ReadyLabel.Top,
    WizardForm.ReadyLabel.Top + WizardForm.ReadyLabel.Height]));
end;

{ The Finished page: PoolWatch's one paragraph, then the "Start PoolWatch now" tick box. Inno Setup adds "Click Finish
  to exit Setup." to the paragraph; this sets it without that and moves the tick box up to follow it as Inno Setup
  places it (a blank line below). A finish that asks to restart Windows keeps Inno Setup's own words (this setup never
  asks for one). Inno Setup picks FinishedLabel or FinishedLabelNoIcons; this always uses FinishedLabel, so the two must
  stay the same words. When the setup finishes at the next restart, the page says that instead, and why (FinishText);
  there is no tick box then (StartsNow), and where it would be, a list that scrolls names every file that was in use,
  each with what had it open (FinishFiles), however many there are; where it runs is logged. }
procedure LayOutFinishedPage;
var
  Delta: Integer;
  Files: TNewMemo;
begin
  if WizardForm.YesRadio.Visible then
    Exit;
  Delta := WizardForm.FinishedLabel.Height;
  if FinishAtRestart then
  begin
    WizardForm.FinishedHeadingLabel.Caption := FinishHeading;
    WizardForm.FinishedLabel.Caption := FinishText + #13#10;
  end
  else
    WizardForm.FinishedLabel.Caption := SetupMessage(msgFinishedLabel) + #13#10;
  WizardForm.FinishedLabel.AdjustHeight;
  Delta := WizardForm.FinishedLabel.Height - Delta;
  WizardForm.RunList.Top := WizardForm.RunList.Top + Delta;
  WizardForm.RunList.Height := WizardForm.RunList.Height - Delta;
  if FinishAtRestart and (FinishFiles <> '') then
  begin
    Files := TNewMemo.Create(WizardForm);
    Files.Parent := WizardForm.FinishedPage;
    Files.Left := WizardForm.RunList.Left;
    Files.Top := WizardForm.RunList.Top;
    Files.Width := WizardForm.RunList.Width;
    Files.Height := WizardForm.RunList.Height;
    if Files.Height < ScaleY(48) then
      Files.Height := ScaleY(48);
    Files.ReadOnly := True;
    Files.ScrollBars := ssVertical;
    Files.Text := Copy(FinishFiles, 3, Length(FinishFiles));
    Log(Format('Finished page, %d high: the files in use are listed from %d to %d.', [WizardForm.FinishedPage.Height,
      Files.Top, Files.Top + Files.Height]));
  end;
end;

procedure InitializeWizard;
begin
  LayOutWelcomePage;
  CreateReadyPage;
  { Shown only while the setup waits for PoolWatch to close (ShowWaitPage, in PrepareToInstall). }
  WaitPage := CreateOutputProgressPage('Closing PoolWatch', 'Setup waits until no part of PoolWatch runs, so that its files can be replaced.');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  case CurPageID of
    wpReady: LayOutReadyPage;
    wpFinished: LayOutFinishedPage;
  end;
end;

{ Stops PoolWatch, ends the token-leak test helpers still running from its exe, and puts back what it changed (with
  --keep-changes only the logon task and the desktop shortcut). With Append (--append-summary), what it did is added to
  uninstall-summary.txt, which an earlier cleanup of this uninstall wrote, instead of taking its place. It runs through
  the launcher of the install folder's program (LauncherPath: same product version), whatever the version of this
  uninstaller: before the restart that puts an update in place, that is still the version that was there, and every
  version with the launcher knows --append-summary. Its exit code, which the launcher passes on, logged; -1 when it
  could not be started; NotInPlace when the install folder has no launcher of its program's version (an install made
  before the launcher whose update finishes at the next restart, a setup that did not finish and left its old files to
  go back at that restart, or a damaged install: NotRemovedText says which), as PoolWatch is never started without its
  launcher; NotInPlace too while one of the launcher's names goes back only at the next restart (NameGoesBack), as
  that restart would bring it back into a folder the uninstall has removed. }
function RunCleanup(KeepChanges, Append: Boolean): Integer;
var
  Params, Launcher: String;
begin
  Params := '--uninstall-cleanup';
  if KeepChanges then
    Params := Params + ' --keep-changes';
  if Append then
    Params := Params + ' --append-summary';
  Launcher := LauncherPath(ExpandConstant('{app}'));
  if Launcher = '' then
  begin
    Log('The install folder has no launcher of the same version as PoolWatch''s program, ' + ProgramPath(ExpandConstant('{app}')) +
      ', so PoolWatch''s cleanup was not started.');
    Result := NotInPlace;
  end
  else if NameGoesBack(ExpandConstant('{app}'), PoolWatchName) or NameGoesBack(ExpandConstant('{app}'), SelfContainedName) then
  begin
    Log('One of the launcher''s names in the install folder goes back only at the next restart, so PoolWatch''s cleanup ' +
      'was not started.');
    Result := NotInPlace;
  end
  else if Exec(Launcher, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Log(Format('PoolWatch --uninstall-cleanup: exit code %d.', [Result]))
  else
  begin
    Log(Format('PoolWatch could not be started for its cleanup (error %d).', [Result]));
    Result := -1;
  end;
end;

{ Whether the cleanup did what it was asked: 0; or 8, when something could not be put back, which then stays listed in
  the data folder so that a later PoolWatch can still put it back; or 12, either of those on the folder PoolWatch keeps
  its data in while it could not use the usual one (ExitOtherDataFolder). Only then may the uninstaller remove
  PoolWatch's files. Any other exit code is a failure, also one the uninstaller does not know: the cleanup could not be
  started (-1), the install folder has no launcher of its program's version (NotInPlace), an error stopped it (1),
  PoolWatch did not take its command line (2), someone who is not an administrator could change its files (5), its data
  folder was not safe to use (6), PoolWatch still runs (7), PoolWatch could not open its private names (9), the launcher
  did not start PoolWatch (10) or PoolWatch refused the start (11), or it broke. }
function CleanupDone(Code: Integer): Boolean;
begin
  Result := (Code = 0) or (Code = ExitNotAllPutBack) or (Code = ExitOtherDataFolder);
end;

{ Why the uninstaller has removed nothing (the cleanup failed: CleanupDone), for its log and its Retry box, with what to
  do for each reason: PoolWatch still runs, so its cleanup changed nothing (7); PoolWatch did not run, or stopped before
  it changed anything, so it changed nothing (-1, NotInPlace, 2, 5, 6, 9, and the launcher's 10 and 11); or an error
  stopped the cleanup (1), or it failed for a reason the uninstaller does not know: then it may have put back part of
  the changes, and the uninstaller cannot tell which. }
function NotRemovedText(Code: Integer): String;
begin
  if Code = ExitStillRunning then
    Result := 'PoolWatch is still running, so it put nothing back and the uninstaller has removed nothing. Close it ' +
      'from its tray icon (right-click it, choose Exit), or wait a minute, then click Retry.'
  else if Code = -1 then
    Result := 'PoolWatch could not be started for its cleanup, so it put nothing back and the uninstaller has removed ' +
      'nothing. Click Retry to try again. If this keeps happening, run PoolWatch''s setup again to repair it, then ' +
      'uninstall it.'
  else if Code = NotInPlace then
  begin
    if RestartWorkPending(ExpandConstant('{app}')) then
      Result := 'An update of PoolWatch, a setup of it that did not finish, or an earlier uninstall finishes when Windows ' +
        'next restarts, so its files are not in place yet: PoolWatch put nothing back and the uninstaller has removed ' +
        'nothing. Restart Windows, then uninstall PoolWatch.'
    else
      Result := 'PoolWatch''s program, ' + ProgramPath(ExpandConstant('{app}')) + ', is not there, or the launcher beside it ' +
        'is of another version, so PoolWatch put nothing back and the uninstaller has removed nothing. Run PoolWatch''s ' +
        'setup again to repair it, then uninstall it.';
  end
  else if Code = ExitNotStarted then
    Result := 'PoolWatch''s launcher could not start PoolWatch for its cleanup (error 10), so it put nothing back and ' +
      'the uninstaller has removed nothing. Click Retry to try again. If this keeps happening, run PoolWatch''s setup ' +
      'again to repair it, then uninstall it.'
  else if Code = ExitRefused then
    Result := 'PoolWatch refused to start its cleanup (error 11: it was started without administrator rights, or with ' +
      '.NET''s start-up variables set), so it put nothing back and the uninstaller has removed nothing. Click Retry to ' +
      'try again. If this keeps happening, run PoolWatch''s setup again to repair it, then uninstall it.'
  else if Code = ExitBadCommandLine then
    Result := 'PoolWatch did not take the command line of its cleanup (error 2), so it put nothing back and the ' +
      'uninstaller has removed nothing. Click Cancel, run PoolWatch''s setup again to repair it, then uninstall it.'
  else if Code = ExitNotInAdminFolder then
    Result := 'PoolWatch did not run its cleanup, because someone who is not an administrator could change its program, ' +
      'its launcher or a folder they are in (error 5), so it put nothing back and the uninstaller has removed nothing. ' +
      'Click Cancel, make sure only administrators can change ' + ExpandConstant('{app}') + ' and the files in it, then ' +
      'uninstall PoolWatch again.'
  else if Code = ExitDataNotSafe then
    Result := 'PoolWatch could not make its data folder (' + ExpandConstant('{commonappdata}\PoolWatch') + ') safe to ' +
      'use, so it put nothing back and the uninstaller has removed nothing. Close any program that may have that folder ' +
      'open, then click Retry. If this keeps happening, restart Windows, then uninstall PoolWatch again.'
  else if Code = ExitNamesNotSafe then
    Result := 'PoolWatch could not open its private names, so it could not ask a running PoolWatch to exit: it put ' +
      'nothing back and the uninstaller has removed nothing. Click Retry to try again. If this keeps happening, restart ' +
      'Windows, then uninstall PoolWatch again.'
  else if Code = ExitFailed then
    Result := 'An error stopped PoolWatch''s cleanup (error 1). It may have put back some of its changes, and the ' +
      'uninstaller cannot tell which, so it has removed nothing. Click Retry to try again. If this keeps happening, run ' +
      'PoolWatch''s setup again to repair it, then uninstall it.'
  else
    Result := Format('PoolWatch''s cleanup failed (error %d). It may have put back some of its changes, and the ' +
      'uninstaller cannot tell which, so it has removed nothing. Click Retry to try again. If this keeps happening, run ' +
      'PoolWatch''s setup again to repair it, then uninstall it.', [Code]);
end;

{ What the uninstaller does with what PoolWatch changed. /KEEPCHANGES keeps it; a silent uninstall (/SILENT,
  /VERYSILENT) and /DELETEDATA choose to put the changes back without asking (one PoolWatch cannot put back stays);
  otherwise the uninstaller lists the changes and asks, and putting them back is the default. }
function PutBackChoice: Integer;
begin
  if HasSwitch('KEEPCHANGES') then
    Result := ChoiceKeep
  else if UninstallSilent or HasSwitch('DELETEDATA') then
    Result := ChoicePutBack
  else
    Result := ChoiceAsk;
end;

{ The JSON text that starts at S[P], just after its opening quote, up to its closing quote. PoolWatch escapes only what
  JSON needs: a quote and a backslash read as themselves, a control character (or any other \u escape) as a space.
  The text is only shown and logged, never acted on. }
function JsonText(const S: String; P: Integer): String;
var
  C: Char;
begin
  Result := '';
  while P <= Length(S) do
  begin
    C := S[P];
    if C = '"' then
      Exit;
    if C = '\' then
    begin
      P := P + 1;
      if P > Length(S) then
        Exit;
      C := S[P];
      if C = 'u' then
      begin
        Result := Result + ' ';
        P := P + 4;
      end
      else if (C = 'b') or (C = 'f') or (C = 'n') or (C = 'r') or (C = 't') then
        Result := Result + ' '
      else
        Result := Result + C;
    end
    else
      Result := Result + C;
    P := P + 1;
  end;
end;

{ Asks PoolWatch for its list of changes (--changes-dump: the Active changes list as JSON, which it writes in its
  admin-only data folder) and reads what each change is called, whether uninstalling puts it back, and what stays for
  a while after. Each one is logged. ListRead is False when the list could not be made or read; only the words shown
  depend on it. }
procedure ReadChangeList(const DataDir: String);
var
  ListFile, Line, What, Leaves, Caveat, Shown, Launcher: String;
  Lines: TArrayOfString;
  Code, I: Integer;
begin
  ListRead := False;
  PutBackList := '';
  LeftList := '';
  CaveatList := '';
  PutBackCount := 0;
  LeftCount := 0;
  ListFile := DataDir + '\' + ListFileName;
  { Through the launcher of the install folder's program, and only when there is one (PoolWatch is never started
    without it: LauncherPath). }
  Launcher := LauncherPath(ExpandConstant('{app}'));
  if (Launcher = '') or
     not Exec(Launcher, '--changes-dump "' + ListFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  { No list: the uninstaller touches nothing in the data folder, not even to tidy up. PoolWatch did not use that
    folder this time (it could not start, refused to use it, or keeps its data in a folder of its own), so a file
    there need not be PoolWatch's, and PoolWatch wrote none. }
  if Code <> 0 then
  begin
    Log(Format('PoolWatch could not list its changes (exit %d).', [Code]));
    Exit;
  end;
  { A list: PoolWatch has just checked that only administrators can change its data folder, and written the file there
    itself. }
  ListRead := LoadStringsFromFile(ListFile, Lines);
  DeleteFile(ListFile);
  if not ListRead then
  begin
    Log('PoolWatch''s list of changes could not be read.');
    Exit;
  end;
  What := '';
  Leaves := '';
  Caveat := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := TrimRight(Lines[I]);
    if Line = '  {' then
    begin
      What := '';
      Leaves := '';
      Caveat := '';
    end
    else if Copy(Line, 1, Length(WhatKey)) = WhatKey then
      What := JsonText(Line, Length(WhatKey) + 1)
    else if Copy(Line, 1, Length(LeavesKey)) = LeavesKey then
      Leaves := JsonText(Line, Length(LeavesKey) + 1)
    else if Copy(Line, 1, Length(CaveatKey)) = CaveatKey then
      Caveat := JsonText(Line, Length(CaveatKey) + 1)
    else if ((Line = '  }') or (Line = '  },')) and (What <> '') then
    begin
      if Leaves = '' then
      begin
        Log('Uninstalling puts back: ' + What);
        AddShown(PutBackList, PutBackCount, Shorten(What, MaxNameShown));
        if Caveat <> '' then
        begin
          Log('After it is put back: ' + Caveat);
          Shown := Shorten(Caveat, MaxLineShown);
          if Pos(Shown, CaveatList) = 0 then
            CaveatList := CaveatList + #13#10 + Shown;
        end;
      end
      else
      begin
        Log('Uninstalling cannot put back: ' + What + '. ' + Leaves);
        AddShown(LeftList, LeftCount, Shorten(What, MaxNameShown) + '. ' + Leaves);
      end;
      What := '';
      Leaves := '';
      Caveat := '';
    end;
  end;
  if PutBackCount + LeftCount = 0 then
    Log('PoolWatch has no changes on this PC to put back.');
end;

{ Started by hand: says what uninstalling puts back (and what stays for a while after) and asks, with putting the
  changes back as the default (the first button; the box cannot be closed without a choice). True: put them back, also
  when the box could not be shown. Nothing is asked when the list says there is nothing to put back. }
function PutBackAnswer: Boolean;
var
  Text: String;
  Labels: TArrayOfString;
begin
  Result := True;
  if ListRead and (PutBackCount = 0) then
    Exit;
  if ListRead then
  begin
    Text := 'These are the changes PoolWatch made on this PC that are still in effect. Uninstalling puts each one back ' +
      'as it was before PoolWatch changed it:' + #13#10 + PutBackList + MoreLine(PutBackCount);
    if CaveatList <> '' then
      Text := Text + #13#10 + CaveatList;
    if LeftCount > 0 then
      Text := Text + #13#10#13#10 + 'Uninstalling cannot put these back:' + #13#10 + LeftList + MoreLine(LeftCount);
  end
  else
    Text := 'PoolWatch could not list its changes just now. Uninstalling puts back each change it recorded, or says ' +
      'why it could not.' + #13#10#13#10 + CaveatIfGuardWasOn;
  SetArrayLength(Labels, 2);
  Labels[0] := 'Put them back (recommended)' + #10 + 'Each change goes back to how it was before PoolWatch.';
  Labels[1] := 'Keep my changes' + #10 + 'They stay as they are. PoolWatch still removes its own sign-in task and desktop ' +
    'shortcut, which would start files that are about to be removed.';
  try
    Result := TaskDialogMsgBox('Put back what PoolWatch changed?', Text, mbConfirmation, MB_YESNO, Labels, 0) <> IDNO;
  except
    Log('The question could not be shown (' + GetExceptionMessage + '): every change is to be put back, the default.');
    Result := True;
  end;
end;

{ Whether to keep what PoolWatch changed (PutBackChoice; started by hand, the answer to PutBackAnswer). }
function KeepChangesWanted: Boolean;
begin
  case PutBackChoice of
    ChoiceKeep: Result := True;
    ChoicePutBack: Result := False;
  else
    Result := not PutBackAnswer;
  end;
end;

{ The lines of uninstall-summary.txt (which --uninstall-cleanup writes in the data folder) that start with Prefix,
  without it and cut to Max characters, as lines to show, each once (a cleanup that ran a second time in the same
  uninstall added its own lines, and may say again what could not be put back); Count is how many there are, or -1 when
  the file could not be read. It is read only after the cleanup returned 0 or 8, so PoolWatch has just secured that
  folder. }
function SummaryLines(const DataDir, Prefix: String; const Max: Integer; var Count: Integer): String;
var
  Lines: TArrayOfString;
  Shown, Seen: String;
  I: Integer;
begin
  Result := '';
  Shown := '';
  Seen := #10;
  Count := -1;
  if not LoadStringsFromFile(DataDir + '\uninstall-summary.txt', Lines) then
    Exit;
  Count := 0;
  for I := 0 to GetArrayLength(Lines) - 1 do
    if (Copy(Lines[I], 1, Length(Prefix)) = Prefix) and (Pos(#10 + Lines[I] + #10, Seen) = 0) then
    begin
      Seen := Seen + Lines[I] + #10;
      AddShown(Shown, Count, Shorten(Copy(Lines[I], Length(Prefix) + 1, Length(Lines[I])), Max));
    end;
  Result := Shown + MoreLine(Count);
end;

{ Once every change was put back (the cleanup returned 0): first what it put back only in part, each with what was not
  done (a service whose start type is back but that was not started again, and why), under a heading of its own; then
  what it put back, each named; each list names its first MaxShown and counts the rest under its own heading, so a
  change put back only in part is never shown as plainly put back. Then what stays for a while after (CaveatList). Or
  that there was nothing to put back. For the question about the data folder, and the notice after a /DELETEDATA
  uninstall started by hand. }
function PutBackDone(const DataDir: String): String;
var
  Count, PartCount: Integer;
  Lines, Part: String;
begin
  Lines := SummaryLines(DataDir, PutBackPrefix, MaxSummaryShown, Count);
  Part := SummaryLines(DataDir, PartlyPutBackPrefix, MaxLineShown, PartCount);
  if (Count < 0) or (PartCount < 0) then
    Result := 'PoolWatch put back every change it recorded. Its summary could not be read, so this does not say whether one ' +
      'was put back only in part (a service not started again, say).'
  else if Count + PartCount = 0 then
    Result := 'PoolWatch had no changes on this PC to put back.'
  else if Count = 0 then
    Result := PartlyPutBackHeading + #13#10 + Part
  else if PartCount = 0 then
    Result := 'PoolWatch put back each of these changes:' + #13#10 + Lines
  else
    Result := PartlyPutBackHeading + #13#10 + Part + #13#10#13#10 + 'PoolWatch put back each of these changes:' + #13#10 + Lines;
  if (Count + PartCount <> 0) and (CaveatList <> '') then
    Result := Result + #13#10 + CaveatList
  else if (Count + PartCount <> 0) and not ListRead then
    Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;
end;

{ The notice when something could not be put back: what, and why (or where to read it, when the summary cannot be read);
  and what was put back only in part, each with what was not done, as PutBackDone lists it. }
function NotAllPutBack(const DataDir: String): String;
var
  Count, PartCount: Integer;
  Lines, Part: String;
begin
  Lines := SummaryLines(DataDir, NotPutBackPrefix, MaxSummaryShown, Count);
  if Count > 0 then
    Result := 'Some changes could not be put back:' + #13#10 + Lines + #13#10#13#10
  else
    Result := 'Some changes could not be put back. ';
  Result := Result + 'They stay listed in ' + DataDir + ', so PoolWatch can still put them back if you install it again. ' +
    'uninstall-summary.txt there says what was put back and what was not, and why.';
  Part := SummaryLines(DataDir, PartlyPutBackPrefix, MaxLineShown, PartCount);
  if PartCount > 0 then
    Result := Result + #13#10#13#10 + PartlyPutBackHeading + #13#10 + Part;
  { What stays for a while after the ones that were put back, as before the cleanup. }
  if CaveatList <> '' then
    Result := Result + #13#10 + CaveatList
  else if not ListRead then
    Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;
end;

{ Whether PutBackDone names only the first MaxShown of the changes put back, or of those put back only in part (it
  counts the rest): uninstall-summary.txt names every one. PartCount is how many were put back only in part (-1 when the
  summary could not be read). }
function SummaryNamesMore(const DataDir: String; var PartCount: Integer): Boolean;
var
  Count: Integer;
  Lines: String;
begin
  Lines := SummaryLines(DataDir, PutBackPrefix, MaxSummaryShown, Count);
  Lines := SummaryLines(DataDir, PartlyPutBackPrefix, MaxLineShown, PartCount);
  Result := (Count > MaxShown) or (PartCount > MaxShown);
end;

{ After a /DELETEDATA uninstall started by hand, which deletes uninstall-summary.txt with the data folder: the changes
  put back only in part that PutBackDone counts rather than names (all past the first MaxShown), each with what was not
  done as PutBackDone shows it, in the summary's order, MaxShown to a box so each box fits on the screen. Nothing when
  PutBackDone named them all, or when the summary cannot be read. }
procedure ShowRestPutBackInPart(const DataDir: String);
var
  Lines: TArrayOfString;
  Shown: String;
  I, Count, Total, First: Integer;
begin
  if not LoadStringsFromFile(DataDir + '\uninstall-summary.txt', Lines) then
    Exit;
  Total := 0;
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Copy(Lines[I], 1, Length(PartlyPutBackPrefix)) = PartlyPutBackPrefix then
      Total := Total + 1;
  Count := 0;
  First := 0;
  Shown := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Copy(Lines[I], 1, Length(PartlyPutBackPrefix)) = PartlyPutBackPrefix then
    begin
      Count := Count + 1;
      if Count > MaxShown then
      begin
        if Shown = '' then
          First := Count;
        Shown := Shown + #13#10 + '- ' + Shorten(Copy(Lines[I], Length(PartlyPutBackPrefix) + 1, Length(Lines[I])), MaxLineShown);
        if (Count - First + 1 = MaxShown) or (Count = Total) then
        begin
          if First = Count then
            MsgBox(Format(PartlyPutBackOneMoreHeading, [Count, Total]) + #13#10 + Shown, mbInformation, MB_OK)
          else
            MsgBox(Format(PartlyPutBackMoreHeading, [First, Count, Total]) + #13#10 + Shown, mbInformation, MB_OK);
          Shown := '';
        end;
      end;
    end;
end;

{ Whether to delete the data folder once every change was put back: /DELETEDATA, or Yes to the question (No is the
  default); never when silent otherwise. When the question names only some of the changes, it says that
  uninstall-summary.txt, in the folder it asks about, names them all, and, when any was put back only in part, with what
  was not done. A /DELETEDATA uninstall started by hand asked nothing about the changes, so it says here what it put
  back, and that the folder is to be deleted, as the box comes before the delete (what is left after it is said then,
  SayDataLeft); the folder is to go as asked, uninstall-summary.txt with it, so it names every change put back only in
  part, the ones PutBackDone counts in the boxes after (ShowRestPutBackInPart). }
function DeleteDataWanted(const DataDir: String): Boolean;
var
  Text: String;
  PartCount: Integer;
begin
  if HasSwitch('DELETEDATA') then
  begin
    if not UninstallSilent then
    begin
      Text := PutBackDone(DataDir);
      if SummaryNamesMore(DataDir, PartCount) then
        if PartCount > MaxShown + 1 then
          Text := Text + #13#10#13#10 + Format('The other %d changes put back only in part are named next, ' +
            'with what was not done.', [PartCount - MaxShown])
        else if PartCount = MaxShown + 1 then
          Text := Text + #13#10#13#10 + 'The other change put back only in part is named next, with what was not done.';
      MsgBox(Text + #13#10#13#10 + 'PoolWatch''s reports, logs and settings (' + DataDir + ') are to be deleted too.',
        mbInformation, MB_OK);
      ShowRestPutBackInPart(DataDir);
    end;
    Result := True;
  end
  else if UninstallSilent then
    Result := False
  else
  begin
    Text := PutBackDone(DataDir);
    if SummaryNamesMore(DataDir, PartCount) then
    begin
      Text := Text + #13#10#13#10 + 'uninstall-summary.txt in ' + DataDir + ' names them all';
      if PartCount > 0 then
        Text := Text + ', with what was not done';
      Text := Text + '. Answer No to keep it.';
    end;
    Result := MsgBox(Text + #13#10#13#10 + 'Also delete PoolWatch''s reports, logs and settings (' + DataDir + ')?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;
end;

{ Whether Path is a link (a reparse point, such as a junction): the uninstaller never looks inside one. }
function IsLink(const Path: String): Boolean;
var
  Find: TFindRec;
begin
  Result := False;
  if FindFirst(Path, Find) then
  try
    Result := (Find.Attributes and AttrLink) <> 0;
  finally
    FindClose(Find);
  end;
end;

{ Below Dir, a folder of a setup run in StageFolder (Rel: the path below it): a file still there now (in use, so that
  DelTree could not delete it) is one that setup may have given Windows to move into the install folder App at the next
  restart (it undoes such a move when it takes its new files back, Unstage, unless that cannot be done), where it would
  come back. So the place it would go is made to exist now (an empty file, when nothing is there), and DeleteAtRestart
  then gives Windows that place to delete too, after the move. When that place cannot be made, the file may stay after
  the restart: it is counted in RestartStays, which the notice and the log's note say. A link is never followed. }
procedure KeepOut(const Dir, App, Rel: String);
var
  Find: TFindRec;
  Name, Target: String;
begin
  if FindFirst(Dir + '\*', Find) then
  try
    repeat
      Name := Find.Name;
      if (Name <> '.') and (Name <> '..') and ((Find.Attributes and AttrLink) = 0) then
      begin
        if (Find.Attributes and AttrFolder) <> 0 then
          KeepOut(Dir + '\' + Name, App, Rel + Name + '\')
        else
        begin
          Target := App + '\' + Rel + Name;
          if not FileExists(Target) then
          begin
            ForceDirectories(ExtractFileDir(Target));
            if SaveStringToFile(Target, '', False) then
              Log('Still in use: ' + Dir + '\' + Name + ', which an earlier setup gave Windows to move to ' + Target +
                ' at the next restart. ' + Target + ' is made now, so that Windows deletes it after that move.')
            else
            begin
              RestartStays := RestartStays + 1;
              Log('May stay after the next restart: ' + Target + ', if an earlier setup gave Windows ' + Dir + '\' +
                Name + ' (still in use) to move there at that restart: ' + Target + ' could not be made now, so ' +
                'Windows is not given it to delete after such a move.');
            end;
          end;
        end;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

{ KeepOut for each setup run's folder in the install folder App's StageFolder. }
procedure KeepOutAtRestart(const App: String);
var
  Find: TFindRec;
begin
  if FindFirst(App + '\' + StageFolder + '\*', Find) then
  try
    repeat
      if (Find.Name <> '.') and (Find.Name <> '..') and ((Find.Attributes and AttrFolder) <> 0) and
         ((Find.Attributes and AttrLink) = 0) then
        KeepOut(App + '\' + StageFolder + '\' + Find.Name, App, '');
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

{ Gives Windows the file or folder Path to delete at its next restart (a folder only once it is empty). RestartGiven
  counts what Windows was given, folders included. Shown, when not '', names it in RestartList (the first MaxShown;
  RestartCount counts them), followed by Holders, what has it open (HoldersOf), which the log also gives; a folder has
  none. What Windows could not be given is counted in RestartFailed. Each is logged. RestartReplace is MoveFileEx with
  MOVEFILE_DELAY_UNTIL_REBOOT; it leaves Inno Setup's restart state alone. }
procedure DeleteOneAtRestart(const Path, Shown, Holders: String);
begin
  try
    RestartReplace(Path, '');
    RestartGiven := RestartGiven + 1;
    if Shown <> '' then
      AddShown(RestartList, RestartCount, Shown + Holders);
    Log('Windows deletes it at its next restart: ' + Path + Holders);
  except
    RestartFailed := RestartFailed + 1;
    Log('Windows could not be given ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
  end;
end;

{ Gives Windows what is left of the folder Dir (Rel: its path below the install folder) to delete at its next restart:
  each file, with what has it open (HoldersOf), each folder below it once it is empty, then Dir. A link is never
  followed; it is deleted itself. }
procedure DeleteAtRestart(const Dir, Rel: String);
var
  Find: TFindRec;
  Name: String;
begin
  if FindFirst(Dir + '\*', Find) then
  try
    repeat
      Name := Find.Name;
      if (Name <> '.') and (Name <> '..') then
      begin
        if ((Find.Attributes and AttrFolder) <> 0) and ((Find.Attributes and AttrLink) = 0) then
          DeleteAtRestart(Dir + '\' + Name, Rel + Name + '\')
        else
          DeleteOneAtRestart(Dir + '\' + Name, Rel + Name, HoldersOf(Dir + '\' + Name));
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
  DeleteOneAtRestart(Dir, '', '');
end;

{ After Inno Setup has removed what the setup installed (usPostUninstall): what is left of the install folder App goes
  too, also files that a setup which finished at a restart put there, and earlier setups' uninstallers, which the
  uninstall log does not name. Only PoolWatch's own folder in Program Files, never a link. Everything that can be
  deleted now is (DelTree, which deletes a link and never follows it). What is left, a file another program still has
  in use, is given to Windows to delete at its next restart, with each folder once it is empty and App itself
  (DeleteAtRestart), after an earlier setup's moves (KeepOutAtRestart). The Start menu entry, if it is still there, goes
  the same way. Nothing restarts the PC, and no restart is asked for. The accounts of what has a file open are read
  afresh (Accounts). }
procedure RemoveRest(const App: String);
var
  Link: String;
begin
  RestartList := '';
  RestartCount := 0;
  RestartGiven := 0;
  RestartStays := 0;
  RestartFailed := 0;
  Accounts := '';
  if (CompareText(App, ExpandConstant('{autopf}\PoolWatch')) = 0) and DirExists(App) and not IsLink(App) then
  begin
    DelTree(App, True, True, True);
    if DirExists(App + '\' + StageFolder) then
      KeepOutAtRestart(App);
    if DirExists(App) then
      DeleteAtRestart(App, '');
  end;
  Link := ExpandConstant('{autoprograms}\PoolWatch.lnk');
  if FileExists(Link) then
    if not DeleteFile(Link) then
      DeleteOneAtRestart(Link, Link, HoldersOf(Link));
end;

{ What RemoveRest left for the next restart of Windows, and why: what Windows deletes then, the first MaxShown files
  each with what had it open, and how many more; how many things Windows could not be given; and how many files an
  earlier update may have Windows move into the install folder then, which would stay after it (RestartStays,
  KeepOut). When either of the last two is not 0, what is left after the restart is to delete by hand. For the log, and
  the notice of an uninstall started by hand, which has to fit on the screen. The uninstaller writes a log only when it
  was started with /LOG (or /LOG=<file>), as Apps and Features starts it (the log constant is empty otherwise); only
  then does the notice say that its log, which it names, names each file (DeleteOneAtRestart logs each). That the
  install folder goes at the restart (FolderGoes) is said only when nothing stays and Windows was given a folder: the
  folders RemoveRest gives are the install folder's, in no list (DeleteAtRestart, which gives the install folder itself
  last), so Windows was then given the install folder too. No line of the text starts with the words a message of
  DeleteOneAtRestart or KeepOut starts with, which RestartLogNote quotes (Inno Setup's Log indents each further line of
  a message to where the message starts, a list line starts with '- ', and no program's name in it has a line break,
  HoldersOf). A silent uninstall still ends with exit code 0 then: Inno Setup's uninstaller has no way for its script
  to set another (it has no counterpart of the setup's GetCustomSetupExitCode); its log, when it writes one, says what
  Windows deletes at the restart, and then that its exit code is still 0 and how a script tells (RestartLogNote). }
function RestartText(const App: String): String;
var
  FolderGoes: Boolean;
begin
  FolderGoes := (RestartFailed + RestartStays = 0) and (RestartGiven > RestartCount);
  Result := 'Some of PoolWatch''s files or folders could not be removed now: another program has them in use, or an ' +
    'earlier update of PoolWatch finishes at the next restart.';
  if (RestartCount > 0) and FolderGoes then
    Result := Result + ' Windows deletes them, and the folder ' + App + ', when it next restarts:' + RestartList +
      MoreLine(RestartCount)
  else if RestartCount > 0 then
    Result := Result + ' Windows deletes these when it next restarts:' + RestartList + MoreLine(RestartCount)
  else if FolderGoes then
    Result := Result + ' Windows deletes the folder ' + App + ', with the folders left in it, when it next restarts.';
  if (RestartCount > MaxShown) and (ExpandConstant('{log}') <> '') then
    Result := Result + ' The uninstaller''s log, ' + ExpandConstant('{log}') + ', names each of them.';
  if RestartFailed > 0 then
    Result := Result + #13#10#13#10 + Format('The uninstaller could not give Windows %d of PoolWatch''s files or ' +
      'folders to delete at the restart.', [RestartFailed]);
  if RestartStays > 0 then
    Result := Result + #13#10#13#10 + Format('An earlier update of PoolWatch may have Windows move %d of its files ' +
      'into %s at the restart, where they would stay after it: the uninstaller could not make their place ready for ' +
      'Windows to delete after the move.', [RestartStays, App]);
  if RestartFailed + RestartStays > 0 then
    Result := Result + ' After the restart, delete what is left of ' + App + ', and PoolWatch''s Start menu entry if ' +
      'it is still there.';
  Result := Result + #13#10#13#10 + 'The uninstaller does not restart Windows: restart it when it suits you.';
end;

{ The log's line after RestartText when this uninstall leaves anything for the next restart: what that means for a
  script that ran it silently. The uninstall finishes at that restart only when Windows was given everything left
  (RestartFailed is 0) and no file an earlier update may have Windows move into the install folder then would stay
  (RestartStays is 0). What Windows could not be given stays after the restart, so the uninstall does not finish then;
  such a file stays if that move happens, which the uninstaller cannot tell (KeepOut), so the uninstall may not finish
  then. Each is said on its own.
  Its exit code is 0 all the same: Inno Setup's uninstaller returns 0 once its own removal has finished and gives its
  script no way to set another code (Inno Setup 6.7.3, Setup.Uninstall.pas: UninstallExitCode starts at 1, line 44, is
  set to 0 as the uninstall's data files are deleted during its removal, lines 187 and 271, before usPostUninstall,
  and becomes the exit code at line 908; no uninstall event function or support function sets it). So a script tells
  from the log, which names each file or folder left for the restart, and each that stays or may stay, in a message
  DeleteOneAtRestart or KeepOut writes, starting with the words quoted here. Inno Setup's Log writes a time stamp
  before each message (Setup.LoggingFunc.pas, Log, line 146), so these words start each such line after its time
  stamp, not the line itself. No message here starts with them, nor any line of RestartText. }
function RestartLogNote: String;
begin
  if RestartFailed > 0 then
    Result := 'This uninstall does not finish at the next restart of Windows: Windows deletes then only what it was ' +
      'given, and what it could not be given stays after that restart, to delete by hand.'
  else if RestartStays > 0 then
    Result := 'This uninstall may not finish at the next restart of Windows: Windows deletes then what it was given, ' +
      'but a file an earlier update of PoolWatch may have it move into the install folder then would stay, to delete ' +
      'by hand.'
  else
    Result := 'This uninstall finishes at the next restart of Windows, which deletes what is named above.';
  Result := Result + ' Its exit code is 0 all the same: Inno Setup''s uninstaller lets its script set no other. A ' +
    'script that started it with /LOG=<file> can tell from that log, which puts a time stamp before each message: ' +
    'each file or folder left for the restart is named in a message that starts ' +
    '"Windows deletes it at its next restart: ", each thing Windows could not be given in one that starts ' +
    '"Windows could not be given ", and each file that may stay in one that starts ' +
    '"May stay after the next restart: ".';
end;

{ Adds each file left in the folder Dir (Rel: its path below the data folder) after DelTree to List and Count
  (AddShown), with what has it open (HoldersOf), and logs each, and does the same in each folder below it. A link is
  never followed: it is named as left, with nothing read through it. }
procedure FindDataLeft(const Dir, Rel: String; var List: String; var Count: Integer);
var
  Find: TFindRec;
  Name, Holders: String;
begin
  if FindFirst(Dir + '\*', Find) then
  try
    repeat
      Name := Find.Name;
      if (Name <> '.') and (Name <> '..') then
      begin
        if ((Find.Attributes and AttrFolder) <> 0) and ((Find.Attributes and AttrLink) = 0) then
          FindDataLeft(Dir + '\' + Name, Rel + Name + '\', List, Count)
        else
        begin
          Holders := '';
          if (Find.Attributes and AttrLink) = 0 then
            Holders := HoldersOf(Dir + '\' + Name);
          AddShown(List, Count, Rel + Name + Holders);
          Log('Left in the data folder: ' + Dir + '\' + Name + Holders);
        end;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

{ After DelTree of the data folder Dir: whether any of it is left (a file another program still has open, or the
  folder itself while a program holds it open), and if so, what. It is said in the log, each file left with what has
  it open (FindDataLeft), and in a box when someone is there, the first MaxShown of them and how many more, with what
  to do: delete what is left once no program has it in use. A link is never followed, Dir itself included (IsLink).
  What is left stays: it is not given to Windows to delete at the next restart, since a PoolWatch installed again
  before then keeps its data in the same folder, under the same names, which Windows would then delete. The accounts
  of what has a file open are read afresh (Accounts). }
procedure SayDataLeft(const Dir: String);
var
  List, Text: String;
  Count: Integer;
begin
  if not DirExists(Dir) then
    Exit;
  List := '';
  Count := 0;
  Accounts := '';
  if not IsLink(Dir) then
    FindDataLeft(Dir, '', List, Count);
  Text := 'PoolWatch''s reports, logs and settings could not all be deleted: what is left of ' + Dir + ' is still in ' +
    'use, or could not be deleted for another reason.';
  if Count > 0 then
    Text := Text + ' These files are left:' + List + MoreLine(Count);
  Text := Text + #13#10#13#10 + 'Delete what is left of ' + Dir + ' yourself once no program has it in use.';
  Log(Text);
  if not UninstallSilent then
    MsgBox(Text, mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  KeepChanges, Summarized, RanAgain, Again: Boolean;
  Code, Seconds: Integer;
  DataDir, App: String;
begin
  DataDir := ExpandConstant('{commonappdata}\PoolWatch');
  App := ExpandConstant('{app}');
  case CurUninstallStep of
    { After "Are you sure?" and before any file is removed: Abort here ends the uninstall with nothing removed. }
    usAppMutexCheck:
      begin
        { The list is made before the cleanup stops PoolWatch, so it can differ from what the cleanup then finds (a
          change made or put back in between): it is only shown and logged. What was really put back is
          uninstall-summary.txt, which every notice after the cleanup reads. The choice is logged as what is to be done:
          the cleanup has not run yet, and a change PoolWatch cannot put back then stays. }
        ReadChangeList(DataDir);
        KeepChanges := KeepChangesWanted;
        if KeepChanges then
          Log('The changes are to be kept: only the logon task and the desktop shortcut are to go.')
        else
          Log('Every change is to be put back (one PoolWatch cannot put back stays).');
        { No file is removed unless the cleanup did what it was asked (CleanupDone: 0 or 8). A cleanup that failed may
          have put back only part of the changes, and the uninstaller cannot tell which, so removing the files that
          could put back the rest would lose them. A silent uninstall then ends with nothing removed; one started by
          hand says why and offers Retry, which runs the cleanup again (adding to uninstall-summary.txt once a cleanup
          of this uninstall wrote it). Once the cleanup is done, the uninstaller waits until no file of PoolWatch's
          program in the install folder is in use, for as long as a process with PoolWatch's name runs from it
          (WaitForApp: a silent uninstall up to RunningWait, one started by hand HandWait, as it has no page that keeps
          drawing). If part of PoolWatch may still run then (a file of it is still in use, and a process with its name
          runs or may run), the cleanup runs once more, adding to uninstall-summary.txt: it is to stop a PoolWatch
          started since the first (whose service profile can apply at its start) and to undo what that one changed (with
          the changes kept, only by removing the logon task and the desktop shortcut, as in the first run). Whatever
          still has a file in use after that does not keep the uninstall from going on: what is still in use when the
          files are removed is given to Windows to delete at the next restart, and what Windows could not be given is
          said (RemoveRest, RestartText). Each line logged here before the result it names says what is to be done, as
          that result is not known yet. }
        Code := RunCleanup(KeepChanges, False);
        Summarized := False;
        RanAgain := False;
        if UninstallSilent then
          Seconds := RunningWait
        else
          Seconds := HandWait;
        repeat
          while not CleanupDone(Code) do
          begin
            Log(NotRemovedText(Code));
            if UninstallSilent then
              Abort;
            if MsgBox(NotRemovedText(Code), mbError, MB_RETRYCANCEL) <> IDRETRY then
              Abort;
            Code := RunCleanup(KeepChanges, Summarized);
          end;
          Summarized := True;
          AppCount := WaitForApp(App, Seconds);
          Again := (AppCount <> 0) and not RanAgain;
          if Again then
          begin
            RanAgain := True;
            Seconds := HandWait;
            Log('Part of PoolWatch may still run after its cleanup, so the cleanup is to run once more: it is to stop a ' +
              'PoolWatch started since and to undo what that one changed (when the changes are to be kept, only to remove the ' +
              'logon task and the desktop shortcut).');
            Code := RunCleanup(KeepChanges, True);
          end;
        until not Again;
        if AppCount <> 0 then
          Log('Part of PoolWatch may still run (' + RunningNames + '): what it still has in use when the uninstaller ' +
            'comes to remove the files is to be given to Windows to delete at the next restart.');
        ChangesPutBack := (Code = 0) and not KeepChanges;
        if not UninstallSilent then
        begin
          if Code = ExitNotAllPutBack then
            MsgBox(NotAllPutBack(DataDir), mbInformation, MB_OK)
          else if (Code = ExitOtherDataFolder) and KeepChanges then
            MsgBox('PoolWatch could not use ' + DataDir + ', so it kept its data in a folder of its own next to it (its name ' +
              'starts with PoolWatch.safe-). Your changes were kept: their list stays in that folder, so PoolWatch can still ' +
              'put them back if you install it again. Neither folder is deleted: check them, then delete them yourself.',
              mbInformation, MB_OK)
          else if Code = ExitOtherDataFolder then
            MsgBox('PoolWatch could not use ' + DataDir + ', so it kept its data in a folder of its own next to it (its name ' +
              'starts with PoolWatch.safe-). PoolWatch put back what it could from there; uninstall-summary.txt in that folder ' +
              'says what was put back and what was not, and why. Neither folder is deleted: check them, then delete them ' +
              'yourself.', mbInformation, MB_OK)
          else if KeepChanges and (PutBackChoice = ChoiceAsk) then
            MsgBox('Your changes were kept. Their list stays in ' + DataDir + ', so PoolWatch can still put them back ' +
              'if you install it again.', mbInformation, MB_OK);
        end;
      end;
    usPostUninstall:
      begin
        RemoveRest(App);
        if RestartGiven + RestartFailed + RestartStays > 0 then
        begin
          Log(RestartText(App));
          Log(RestartLogNote);
          if not UninstallSilent then
            MsgBox(RestartText(App), mbInformation, MB_OK);
        end;
        if ChangesPutBack then
          if DeleteDataWanted(DataDir) then
          begin
            DelTree(DataDir, True, True, True);
            SayDataLeft(DataDir);
          end;
      end;
  end;
end;
