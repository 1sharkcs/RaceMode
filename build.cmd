@echo off
rem Builds RaceMode.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
cd /d "%~dp0"
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:RaceMode.exe /win32icon:assets\RaceMode.ico /resource:assets\icon-128.png,icon.png /resource:assets\graphics-catalog.json,graphics-catalog.json /resource:assets\close-catalog.json,close-catalog.json /resource:src\RaceMode.xaml,RaceMode.xaml /resource:assets\dock.html,dock.html ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Web.Extensions.dll" /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\App.cs src\Monitor.cs src\IniFile.cs src\DisplayGuard.cs src\GraphicsPresets.cs src\Irsdk.cs src\GraphicsCatalog.cs src\Dashboard.cs src\DataDirs.cs src\Updater.cs src\Version.cs src\Setup.cs src\FirstRun.cs src\Obs.cs src\ObsClient.cs src\ObsDock.cs src\StreamUi.cs src\Tray.cs src\AppScan.cs src\HwinfoSetup.cs src\Layout.cs src\SideMenu.cs src\Pages.cs src\Overview.cs src\SmallPanel.cs src\LayoutTest.cs src\UiTest.cs
if errorlevel 1 exit /b 1
copy /y src\RaceMode.xaml RaceMode.xaml >nul
echo Built RaceMode.exe
