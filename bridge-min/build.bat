@echo off
chcp 65001 >nul
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 10 SDK לא נמצא. הורידו מ: https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)

if /i "%~1"=="light" goto light

echo.
echo === בנייה נייידת מלאה: exe אחד, לא דורש התקנת .NET (כ-60-70MB) ===
dotnet publish OtzariaBridge.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist
goto done

:light
if exist obj rmdir /s /q obj
echo.
echo === בנייה קלה: exe קטן, דורש .NET 10 Desktop Runtime מותקן במחשב ===
dotnet publish OtzariaBridge.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -o dist-light

:done
if errorlevel 1 (
  echo הבנייה נכשלה.
  pause
  exit /b 1
)
echo.
echo הסתיים. ה-exe נמצא בתיקיית dist (או dist-light). שמרו אותו בתיקייה שבה תרצו -
echo התיקייה data תיווצר לידו בהפעלה הראשונה.
pause
