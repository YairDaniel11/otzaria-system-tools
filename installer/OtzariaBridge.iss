; מתקין "תוכנת חסימת אוצריא - גמח תורת אסתר". בנייה:
;   ISCC.exe /DAppVersion=3.0.0 /DExePath=<נתיב ל-OtzariaBridge.exe שפורסם> OtzariaBridge.iss
; ההתקנה פר-משתמש (ללא הרשאות מנהל): הקובץ נכנס ל-%LOCALAPPDATA%\OtzariaBridge, שם הגשר מצפה להיות.
; הרכיב המוקשח (שירות SYSTEM) נשאר אופציונלי ומותקן מתוך הגדרות המערכת, כמו עד היום.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef ExePath
  #error "חובה לציין /DExePath=<נתיב ל-OtzariaBridge.exe>"
#endif

[Setup]
AppId={{6F1C2B7E-4D0A-4B8E-9C53-0E2A7B5D91A4}
AppName=אחת שאלתי - גמח תורת אסתר
AppVersion={#AppVersion}
AppPublisher=גמח תורת אסתר
DefaultDirName={localappdata}\OtzariaBridge
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
OutputBaseFilename=OtzariaBridge-Setup-{#AppVersion}
OutputDir=Output
UninstallDisplayIcon={app}\OtzariaBridge.exe
UninstallDisplayName=אחת שאלתי

WizardStyle=modern
CloseApplications=no

[Languages]
Name: "hebrew"; MessagesFile: "compiler:Languages\Hebrew.isl"

[Messages]
FinishedLabel=ההתקנה הושלמה. הגשר רץ ברקע (סמל במגש המערכת) ויעלה אוטומטית עם הכניסה ל-Windows.%n%nאם אוצריא הותקנה ופעלה בעבר, היא נפתחה כדי להתקין את התוספים "סייר קבצים" ו"הגדרות מערכת" — אשרו בחלון ההתקנה של אוצריא.%n%nאם התוספים לא הותקנו: פתחו את אוצריא פעם אחת, ואז בחרו בסמל הגשר במגש "התקן/עדכן את התוספים באוצריא".

[Files]
Source: "{#ExePath}"; DestDir: "{app}"; DestName: "OtzariaBridge.exe"; Flags: ignoreversion

; ACL (הקשחה): ההתקנה פר-משתמש (בלי UAC), ולכן כאן אי אפשר לקבוע ACL על ProgramData או Program Files.
; את %ProgramData%\OtzariaBridge (SYSTEM+Admins) ואת השירות מקימה ההתקנה המוקשחת (--install-service, HardLock.SecureDir, עם UAC).
; תיקיית העדכון {localappdata}\OtzariaBridge\update נוצרת ומוגנת ב-ACL ללא ירושה ע"י UpdateApi.ProtectedDir בכל עדכון;
; ה-installer לא יוצר אותה מראש כדי לא להשאיר אותה עם ACL יורש.
[Run]
Filename: "{app}\OtzariaBridge.exe"; Parameters: "--install --quiet"; StatusMsg: "מגדיר הפעלה אוטומטית ומתקין את התוספים באוצריא..."; Flags: waituntilterminated runhidden

[Code]
// ---- אישור בפועל שהתוספים הותקנו באוצריא (לא רק שהזרימה הופעלה) ----
// otzaria://plugin/install-local הוא חד-כיווני, בלי ערוץ חזרה - אבל אוצריא עצמה
// כן מתקינה כל תוסף לתיקייה תחת %APPDATA%\otzaria\plugins\installed\<id>\ -
// בשם תת-תיקייה אקראי כמו ".release-1538578c" (לא "current"; זה רק שם נתיב
// מושגי בקוד הדארט של אוצריא - מה שבאמת "פעיל" נקבע ב-DB הפנימי שלה, לא לפי
// השם בדיסק), אז סורקים את כל תת-התיקיות של <id>\ ובודקים את המניפסט של כל אחת.
// רק אם הבדיקה האוטומטית לא מצליחה תוך זמן סביר, שואלים את המשתמש כגיבוי.
const
  FxPluginId = 'com.chadbedera.fileexplorer';
  SettingsPluginId = 'com.chadbedera.systemsettings';

var
  PluginsConfirmed: Boolean;

// Inno Setup's Pascal Script אין בו PosEx מובנה - חיפוש תת-מחרוזת החל ממיקום נתון
function FindFrom(const SubStr, S: String; Offset: Integer): Integer;
var
  r: Integer;
begin
  if Offset < 1 then Offset := 1;
  if Offset > Length(S) then begin Result := 0; exit; end;
  r := Pos(SubStr, Copy(S, Offset, Length(S) - Offset + 1));
  if r = 0 then Result := 0 else Result := r + Offset - 1;
end;

// חילוץ פשטני של "version": "..." מתוך manifest.json - מספיק למניפסטים שלנו (JSON שורה-אחר-שורה)
function ManifestVersion(const InstallDir: String): String;
var
  lines: TArrayOfString;
  i, p1, p2: Integer;
begin
  Result := '';
  if not LoadStringsFromFile(InstallDir + '\manifest.json', lines) then exit;
  for i := 0 to GetArrayLength(lines) - 1 do
  begin
    if Pos('"version"', lines[i]) = 0 then continue;
    p1 := Pos(':', lines[i]);
    if p1 = 0 then continue;
    p1 := FindFrom('"', lines[i], p1);
    if p1 = 0 then continue;
    p2 := FindFrom('"', lines[i], p1 + 1);
    if p2 <= p1 then continue;
    Result := Copy(lines[i], p1 + 1, p2 - p1 - 1);
    exit;
  end;
end;

function PluginUpToDate(const PluginId: String): Boolean;
var
  parentDir, subDir: String;
  rec: TFindRec;
begin
  Result := False;
  parentDir := ExpandConstant('{userappdata}') + '\otzaria\plugins\installed\' + PluginId;
  if not DirExists(parentDir) then exit;
  if not FindFirst(parentDir + '\*', rec) then exit;
  try
    repeat
      if (rec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0) or (rec.Name = '.') or (rec.Name = '..') then
        continue;
      subDir := parentDir + '\' + rec.Name;
      if ManifestVersion(subDir) = '{#AppVersion}' then begin Result := True; exit; end;
    until not FindNext(rec);
  finally
    FindClose(rec);
  end;
end;

function BothPluginsUpToDate(): Boolean;
begin
  Result := PluginUpToDate(FxPluginId) and PluginUpToDate(SettingsPluginId);
end;

procedure RerunPluginInstall();
var
  rc: Integer;
begin
  Exec(ExpandConstant('{app}\OtzariaBridge.exe'), '--install --quiet', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

// ממתינים עד ~20 שניות לאחר ההתקנה - אוצריא צריכה להיפתח והמשתמש צריך ללחוץ "אישור"
// בחלון שלה; תוסף שכבר מותקן ועדכני נראה מיד, בלי המתנה מיותרת
function WaitForPluginsAutoDetected(): Boolean;
var
  i: Integer;
begin
  for i := 1 to 20 do
  begin
    if BothPluginsUpToDate() then begin Result := True; exit; end;
    Sleep(1000);
  end;
  Result := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  choice: Integer;
begin
  if CurStep <> ssPostInstall then exit;
  PluginsConfirmed := WaitForPluginsAutoDetected();
  while not PluginsConfirmed do
  begin
    choice := MsgBox(
      'לא זיהיתי שהתוספים "סייר קבצים" ו"הגדרות מערכת" הותקנו/עודכנו בפועל באוצריא - ' +
      'בלעדיהם החסימה לא תעבוד!' + #13#10#13#10 +
      'נתיב התוספים לפתיחה ידנית: ' + ExpandConstant('{app}') + '\plugins' + #13#10#13#10 +
      'ללחוץ "כן" כדי להפעיל שוב את ההתקנה באוצריא (ודא שאוצריא פתוחה ואשר את החלון שנפתח), ' +
      'או "לא" כדי להמשיך בלעדיה (לא מומלץ).',
      mbError, MB_YESNO);
    if choice = IDNO then break;
    RerunPluginInstall();
    PluginsConfirmed := WaitForPluginsAutoDetected();
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID <> wpFinished) or PluginsConfirmed then exit;
  WizardForm.FinishedLabel.Caption :=
    'התקנת רכיב החסימה הושלמה - אבל התוספים באוצריא עדיין לא אושרו!' + #13#10#13#10 +
    'יש להתקין אותם כדי שהחסימה תעבוד בפועל: פתחו את אוצריא, ואז מתפריט הגשר במגש המערכת ' +
    'בחרו "התקן/עדכן את התוספים באוצריא" - או התקינו ידנית מהנתיב:' + #13#10 +
    ExpandConstant('{app}') + '\plugins';
end;

// עוצרים גשר שרץ מהתקנה קודמת, כדי שאפשר יהיה להחליף את הקובץ
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM OtzariaBridge.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(800);
  Result := '';
end;

// ---- הסרה: מחייבת את סיסמת הנעילה, כדי שהסרה מ"אפליקציות מותקנות" לא תעקוף את הנעילה ----

function RunUninstall(const Password: String): Boolean;
var
  rc: Integer;
  exe, args: String;
begin
  exe := ExpandConstant('{app}\OtzariaBridge.exe');
  args := '--uninstall';
  if Password <> '' then
    args := args + ' --password "' + Password + '"';
  Result := Exec(exe, args, '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
end;

function AskPassword(var Password: String): Boolean;
var
  Form: TSetupForm;
  Lbl: TNewStaticText;
  Edit: TPasswordEdit;
  Ok, Cancel: TNewButton;
begin
  Form := CreateCustomForm(ScaleX(380), ScaleY(150), False, False);
  try
    Form.Caption := 'הסרת אחת שאלתי';
    Lbl := TNewStaticText.Create(Form);
    Lbl.Parent := Form;
    Lbl.Caption := 'הקלידו את סיסמת הנעילה (או קוד שחזור) כדי להסיר:';
    Lbl.Left := ScaleX(16);
    Lbl.Top := ScaleY(16);
    Lbl.Width := Form.ClientWidth - ScaleX(32);
    Edit := TPasswordEdit.Create(Form);
    Edit.Parent := Form;
    Edit.Left := ScaleX(16);
    Edit.Top := ScaleY(48);
    Edit.Width := Form.ClientWidth - ScaleX(32);
    Ok := TNewButton.Create(Form);
    Ok.Parent := Form;
    Ok.Caption := 'הסרה';
    Ok.ModalResult := mrOk;
    Ok.Default := True;
    Ok.Left := Form.ClientWidth - ScaleX(16) - Ok.Width;
    Ok.Top := ScaleY(100);
    Cancel := TNewButton.Create(Form);
    Cancel.Parent := Form;
    Cancel.Caption := 'ביטול';
    Cancel.ModalResult := mrCancel;
    Cancel.Cancel := True;
    Cancel.Left := Ok.Left - Cancel.Width - ScaleX(8);
    Cancel.Top := ScaleY(100);
    Form.ActiveControl := Edit;
    Result := Form.ShowModal = mrOk;
    if Result then
      Password := Edit.Text;
  finally
    Form.Free;
  end;
end;

function InitializeUninstall(): Boolean;
var
  pw: String;
  tries: Integer;
begin
  // בלי סיסמת נעילה ההסרה עוברת מיד; אחרת מבקשים סיסמה (עד 5 ניסיונות)
  Result := RunUninstall('');
  tries := 0;
  while (not Result) and (tries < 5) do
  begin
    if not AskPassword(pw) then
    begin
      Result := False;
      Exit;
    end;
    Result := RunUninstall(pw);
    if not Result then
      MsgBox('הסיסמה שגויה, או שההסרה נכשלה.', mbError, MB_OK);
    tries := tries + 1;
  end;
end;
