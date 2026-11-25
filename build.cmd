@echo off
rem Build MaicongTray.exe and MaicongCheck.exe using Windows built-in .NET Framework 4.x csc.exe
rem Zero external dependencies required.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please ensure .NET Framework 4.x is installed.
    exit /b 1
)

if not exist "dist" mkdir "dist"

set "SRCS=src\Models\BatteryModels.cs src\Protocol\NativeHid.cs src\Protocol\BatteryReader.cs src\Protocol\DeviceWatcher.cs src\Core\BatteryHistory.cs src\Core\ThemeHelper.cs src\Core\FullscreenDetector.cs src\Core\SettingsStore.cs src\Core\UpdateChecker.cs src\Core\ErrorLog.cs src\Rendering\IconRenderer.cs src\UI\BatteryFlyoutForm.cs src\TrayApp.cs src\Program.cs"

echo [1/3] Building diagnostic tool (MaicongCheck.exe)...
"%CSC%" /nologo /codepage:65001 /target:exe /optimize+ /define:CHECK_MODE ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /out:dist\MaicongCheck.exe %SRCS%
if errorlevel 1 goto :fail

echo [2/3] Generating Fluent app icon (icon.ico)...
dist\MaicongCheck.exe --makeico icon.ico
if errorlevel 1 goto :fail

echo [3/3] Building single-file tray app (MaicongTray.exe)...
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:icon.ico ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /out:dist\MaicongTray.exe %SRCS%
if errorlevel 1 goto :fail

rem Re-embed icon into MaicongCheck.exe as well
"%CSC%" /nologo /codepage:65001 /target:exe /optimize+ /define:CHECK_MODE /win32icon:icon.ico ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /out:dist\MaicongCheck.exe %SRCS%
if errorlevel 1 goto :fail

echo.
echo Build OK:
echo   - dist\MaicongTray.exe
echo   - dist\MaicongCheck.exe
echo   - icon.ico
exit /b 0

:fail
echo Build FAILED.
exit /b 1
