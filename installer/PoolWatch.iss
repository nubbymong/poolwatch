; The PoolWatch installer (Inno Setup 6.7). CI builds it from the self-contained build, with the files that ship next to
; PoolWatch.exe staged in the same folder (installer\build.ps1):
;   ISCC.exe /DAppVersion=<version> /DPublishDir=<staged folder> installer\PoolWatch.iss
; A release adds /DSign and /Spwsign=<sign command>, so the setup and its uninstaller are signed as they are made.
;
; The uninstaller puts back everything PoolWatch changed, by default: it stops PoolWatch through its exit signal and has
; it put back every change in its list before any file is removed (PoolWatch.exe --uninstall-cleanup). Started by hand,
; it first lists what it will put back (PoolWatch.exe --changes-dump) and asks, with putting it all back as the default
; and keeping the changes as the other choice. A silent uninstall (/SILENT, /VERYSILENT) and /DELETEDATA put everything
; back without asking (/DELETEDATA started by hand then says what it put back). /KEEPCHANGES keeps the changes (the
; logon task and the desktop shortcut still go); /DELETEDATA also deletes the data folder, once every change was put back.

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
OutputBaseFilename=PoolWatch-v{#AppVersion}-setup-win-x64
Compression=lzma2/max
SolidCompression=yes
; A fallback only: PrepareToInstall first asks a running PoolWatch to exit.
CloseApplications=yes
CloseApplicationsFilter=PoolWatch.exe
RestartApplications=no
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
ReadySafeText=Nothing changes without your OK. Anything PoolWatch changes can be put back, and uninstalling puts everything back.

[InstallDelete]
; Left by an install of the self-contained zip by hand: the installer installs that exe as PoolWatch.exe.
Type: files; Name: "{app}\PoolWatch-selfcontained.exe"

[Files]
; The Ready page's icons, unpacked for the wizard and never installed. First in this section: with solid compression,
; unpacking one file means unpacking every file above it first.
Source: "ready\*.png"; Flags: dontcopy
Source: "{#PublishDir}\PoolWatch-selfcontained.exe"; DestDir: "{app}"; DestName: "PoolWatch.exe"; Flags: ignoreversion
; Named one by one, so a build without one of them fails here instead of shipping without it.
Source: "{#PublishDir}\chatgpt-no-shader-cache.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
; The rest of the build: the native DLLs PoolWatch loads from its own folder, and the .NET runtime's licences (licenses\).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "\PoolWatch-selfcontained.exe,\chatgpt-no-shader-cache.ps1,\LICENSE,\THIRD-PARTY-NOTICES.md"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PoolWatch"; Filename: "{app}\PoolWatch.exe"

[Run]
; The setup guide opens at this first start. PoolWatch requires administrator rights, so it starts with Setup's: started
; as the original user (the default for postinstall) Windows would refuse it with error 740.
Filename: "{app}\PoolWatch.exe"; Description: "Start PoolWatch now"; Flags: postinstall nowait skipifsilent runascurrentuser

[Code]
const
  { PoolWatch.exe's exit codes for --stop-running and --uninstall-cleanup. }
  ExitStillRunning = 7;
  ExitNotAllPutBack = 8;
  { The mutex a running PoolWatch holds. }
  MonitorMutex = 'Global\PoolWatch-Monitor';
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
  { The lines of uninstall-summary.txt (Program.UninstallSummary writes it) that say what was put back and what could
    not be; a test holds them to what PoolWatch writes. }
  PutBackPrefix = 'Put back: ';
  NotPutBackPrefix = 'Could not put back: ';
  { At most this many changes are named in a question or a notice, so each box fits on the screen. }
  MaxShown = 10;
  { A name shown in a box is cut to MaxNameShown characters, and a whole line to MaxLineShown: a startup app's name is
    chosen by whoever made the entry, and a box must still fit on the screen. The log keeps the whole text. }
  MaxNameShown = 120;
  MaxLineShown = 360;
  { A line of the summary in a notice after the cleanup ("Put back: ...", "Could not put back: ... why"): a name and some
    room for why, so ten of them still fit on the screen. }
  MaxSummaryShown = 180;
  { When PoolWatch could not list its changes, the uninstaller cannot tell whether the token-leak guard was on, so it
    says this in its place (the list's own words are ChangeWords.TokenGuardSettingStays). }
  CaveatIfGuardWasOn = 'If the token-leak guard was on, its Windows setting stays until you sign out.';

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

function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/' + Name) = 0 then
      Result := True;
end;

function InitializeSetup: Boolean;
begin
  { Shown only to someone at the setup: a silent install must never wait for a click. }
  if IsArm64 and not WizardSilent then
    MsgBox('PoolWatch is built for x64 PCs. Windows on Arm runs it through its x64 emulation, where PoolWatch has not been tested yet.',
      mbInformation, MB_OK);
  Result := True;
end;

{ Before any file is copied. Only PoolWatch's own folder in Program Files: /DIR= still picks another one despite
  DisableDirPage, and PoolWatch refuses to run from most of them. An upgrade: ask the running PoolWatch to exit before
  its files are replaced. An older PoolWatch without the option exits with 2; Inno Setup's own close-applications step
  handles that one. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{autopf}\PoolWatch')) <> 0 then
  begin
    Result := 'PoolWatch installs only to ' + ExpandConstant('{autopf}\PoolWatch') + '. Run the setup without /DIR.';
    Exit;
  end;
  if not FileExists(ExpandConstant('{app}\PoolWatch.exe')) then
    Exit;
  if Exec(ExpandConstant('{app}\PoolWatch.exe'), '--stop-running', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = ExitStillRunning) then
    Result := 'PoolWatch is still running. Right-click its tray icon, choose Exit, then run the setup again.';
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
  Log(Format('Welcome page, %d x %d: the text ends at %d.',
    [Mission.Parent.Width, Mission.Parent.Height, ClickNextText.Top + ClickNextText.Height]));
end;

{ Step I (0 to 2) of the Ready page's three, StepWidth wide: its icon, its name in bold and a line in grey, with an arrow
  to the next step after it. Placed across the page here; down the page when it shows (LayOutReadyPage). }
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
  Log(Format('Ready page, %d x %d: steps end at %d, the panel runs from %d to %d, the last line runs from %d to %d.',
    [WizardForm.InnerNotebook.Width, WizardForm.InnerNotebook.Height, SafePanel.Top - ScaleY(26), SafePanel.Top,
    SafePanel.Top + SafePanel.Height, WizardForm.ReadyLabel.Top,
    WizardForm.ReadyLabel.Top + WizardForm.ReadyLabel.Height]));
end;

{ The Finished page: PoolWatch's one paragraph, then the "Start PoolWatch now" tick box. Inno Setup adds "Click Finish
  to exit Setup." to the paragraph; this sets it without that and moves the tick box up to follow it as Inno Setup
  places it (a blank line below). A finish that asks to restart Windows keeps Inno Setup's own words. Inno Setup picks
  FinishedLabel or FinishedLabelNoIcons; this always uses FinishedLabel, so the two must stay the same words. }
procedure LayOutFinishedPage;
var
  Delta: Integer;
begin
  if WizardForm.YesRadio.Visible then
    Exit;
  Delta := WizardForm.FinishedLabel.Height;
  WizardForm.FinishedLabel.Caption := SetupMessage(msgFinishedLabel) + #13#10;
  WizardForm.FinishedLabel.AdjustHeight;
  Delta := WizardForm.FinishedLabel.Height - Delta;
  WizardForm.RunList.Top := WizardForm.RunList.Top + Delta;
  WizardForm.RunList.Height := WizardForm.RunList.Height - Delta;
end;

procedure InitializeWizard;
begin
  LayOutWelcomePage;
  CreateReadyPage;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  case CurPageID of
    wpReady: LayOutReadyPage;
    wpFinished: LayOutFinishedPage;
  end;
end;

{ Stops PoolWatch and puts back what it changed (with --keep-changes only the logon task and the desktop shortcut). Its
  exit code; -1 when it could not be started. }
function RunCleanup(KeepChanges: Boolean): Integer;
var
  Params: String;
begin
  Params := '--uninstall-cleanup';
  if KeepChanges then
    Params := Params + ' --keep-changes';
  if not Exec(ExpandConstant('{app}\PoolWatch.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

{ What the uninstaller does with what PoolWatch changed. /KEEPCHANGES keeps it; a silent uninstall (/SILENT,
  /VERYSILENT) and /DELETEDATA put it all back without asking; otherwise the uninstaller lists it and asks, and putting
  it all back is the default. }
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

{ Asks PoolWatch for its list of changes (--changes-dump: the Active changes list as JSON, which it writes in its
  admin-only data folder) and reads what each change is called, whether uninstalling puts it back, and what stays for
  a while after. Each one is logged. ListRead is False when the list could not be made or read; only the words shown
  depend on it. }
procedure ReadChangeList(const DataDir: String);
var
  ListFile, Line, What, Leaves, Caveat, Shown: String;
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
  if not Exec(ExpandConstant('{app}\PoolWatch.exe'), '--changes-dump "' + ListFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  { No list: the uninstaller touches nothing in the data folder, not even to tidy up. PoolWatch did not secure that
    folder this time (it could not start, or it refused a folder it could not make safe), so a file there need not be
    PoolWatch's, and PoolWatch wrote none. }
  if Code <> 0 then
  begin
    Log(Format('PoolWatch could not list its changes (exit %d).', [Code]));
    Exit;
  end;
  { A list: PoolWatch has just secured its data folder (administrators only) and written the file there itself. }
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

{ Started by hand: says what uninstalling puts back (and what stays for a while after) and asks, with putting it all
  back as the default (the first button; the box cannot be closed without a choice). True: put it back, also when the
  box could not be shown. Nothing is asked when the list says there is nothing to put back. }
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
    Text := 'PoolWatch could not list its changes just now. Uninstalling puts back every change it recorded.' + #13#10#13#10 +
      CaveatIfGuardWasOn;
  SetArrayLength(Labels, 2);
  Labels[0] := 'Put them back (recommended)' + #10 + 'Each change goes back to how it was before PoolWatch.';
  Labels[1] := 'Keep my changes' + #10 + 'They stay as they are. PoolWatch still removes its own sign-in task and desktop ' +
    'shortcut, which would start files that are about to be removed.';
  try
    Result := TaskDialogMsgBox('Put back what PoolWatch changed?', Text, mbConfirmation, MB_YESNO, Labels, 0) <> IDNO;
  except
    Log('The question could not be shown (' + GetExceptionMessage + '): every change is put back, the default.');
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
  without it, as lines to show; Count is how many there are, or -1 when the file could not be read. It is read only
  after the cleanup returned 0 or 8, so PoolWatch has just secured that folder. }
function SummaryLines(const DataDir, Prefix: String; var Count: Integer): String;
var
  Lines: TArrayOfString;
  Shown: String;
  I: Integer;
begin
  Result := '';
  Shown := '';
  Count := -1;
  if not LoadStringsFromFile(DataDir + '\uninstall-summary.txt', Lines) then
    Exit;
  Count := 0;
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Copy(Lines[I], 1, Length(Prefix)) = Prefix then
      AddShown(Shown, Count, Shorten(Copy(Lines[I], Length(Prefix) + 1, Length(Lines[I])), MaxSummaryShown));
  Result := Shown + MoreLine(Count);
end;

{ Once every change was put back (the cleanup returned 0): what it put back, each named (the first MaxShown), and what
  stays for a while after (CaveatList); or that there was nothing to put back. For the question about the data folder,
  and the notice after a /DELETEDATA uninstall started by hand. }
function PutBackDone(const DataDir: String): String;
var
  Count: Integer;
  Lines: String;
begin
  Lines := SummaryLines(DataDir, PutBackPrefix, Count);
  if Count = 0 then
    Result := 'PoolWatch had no changes on this PC to put back.'
  else if Count < 0 then
    Result := 'PoolWatch put back every change it recorded.'
  else
    Result := 'PoolWatch put back each of these changes:' + #13#10 + Lines;
  if (Count <> 0) and (CaveatList <> '') then
    Result := Result + #13#10 + CaveatList
  else if (Count <> 0) and not ListRead then
    Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;
end;

{ The notice when something could not be put back: what, and why (or where to read it, when the summary cannot be read). }
function NotAllPutBack(const DataDir: String): String;
var
  Count: Integer;
  Lines: String;
begin
  Lines := SummaryLines(DataDir, NotPutBackPrefix, Count);
  if Count > 0 then
    Result := 'Some changes could not be put back:' + #13#10 + Lines + #13#10#13#10
  else
    Result := 'Some changes could not be put back. ';
  Result := Result + 'They stay listed in ' + DataDir + ', so PoolWatch can still put them back if you install it again. ' +
    'uninstall-summary.txt there says what was put back and what was not, and why.';
  { What stays for a while after the ones that were put back, as before the cleanup. }
  if CaveatList <> '' then
    Result := Result + #13#10 + CaveatList
  else if not ListRead then
    Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;
end;

{ Whether to delete the data folder once every change was put back: /DELETEDATA, or Yes to the question (No is the
  default); never when silent otherwise. A /DELETEDATA uninstall started by hand asked nothing about the changes, so it
  says here what it put back. }
function DeleteDataWanted(const DataDir: String): Boolean;
begin
  if HasSwitch('DELETEDATA') then
  begin
    if not UninstallSilent then
      MsgBox(PutBackDone(DataDir) + #13#10#13#10 + 'PoolWatch''s reports, logs and settings (' + DataDir + ') are deleted too.',
        mbInformation, MB_OK);
    Result := True;
  end
  else if UninstallSilent then
    Result := False
  else
    Result := MsgBox(PutBackDone(DataDir) + #13#10#13#10 + 'Also delete PoolWatch''s reports, logs and settings (' + DataDir + ')?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  KeepChanges: Boolean;
  Code: Integer;
  DataDir: String;
begin
  DataDir := ExpandConstant('{commonappdata}\PoolWatch');
  case CurUninstallStep of
    { After "Are you sure?" and before any file is removed: Abort here ends the uninstall with nothing removed. }
    usAppMutexCheck:
      begin
        { The list is made before the cleanup stops PoolWatch, so it can differ from what the cleanup then finds (a change
          made or put back in between): it is only shown and logged. What was really put back is uninstall-summary.txt,
          which every notice after the cleanup reads. }
        ReadChangeList(DataDir);
        KeepChanges := KeepChangesWanted;
        if KeepChanges then
          Log('The changes are kept: only the logon task and the desktop shortcut go.')
        else
          Log('Every change is put back.');
        Code := RunCleanup(KeepChanges);
        { Whatever the cleanup said, no file is removed while a PoolWatch still holds its mutex. }
        while (Code = ExitStillRunning) or CheckForMutexes(MonitorMutex) do
        begin
          if UninstallSilent then
            Abort;
          if MsgBox('PoolWatch is still running. Right-click its tray icon, choose Exit, then click Retry.',
            mbError, MB_RETRYCANCEL) <> IDRETRY then
            Abort;
          Code := RunCleanup(KeepChanges);
        end;
        ChangesPutBack := (Code = 0) and not KeepChanges;
        if not UninstallSilent then
        begin
          if Code = ExitNotAllPutBack then
            MsgBox(NotAllPutBack(DataDir), mbInformation, MB_OK)
          else if Code <> 0 then
            MsgBox(Format('PoolWatch could not put back its changes (error %d). They stay listed in %s, so PoolWatch can ' +
              'still put them back if you install it again.', [Code, DataDir]), mbError, MB_OK)
          else if KeepChanges and (PutBackChoice = ChoiceAsk) then
            MsgBox('Your changes were kept. Their list stays in ' + DataDir + ', so PoolWatch can still put them back ' +
              'if you install it again.', mbInformation, MB_OK);
        end;
      end;
    usPostUninstall:
      if ChangesPutBack then
        if DeleteDataWanted(DataDir) then
          DelTree(DataDir, True, True, True);
  end;
end;
