@echo off
rem Race Mode installer. Downloads the latest release, checks it against its published SHA-256,
rem then runs Race Mode's own setup (RaceMode.exe --install), which installs it per-user in
rem %LOCALAPPDATA%\RaceMode. No admin rights, no PowerShell. Safe to run again (repair / reinstall).
setlocal EnableExtensions
set REPO=1sharkcs/RaceMode
set BASE=https://github.com/%REPO%/releases/latest/download
rem Windows tools by full path, so a file with the same name in the current folder is never run instead.
set "SYS=%SystemRoot%\System32"

echo.
echo Race Mode installer
echo.

rem ---- 1. check this PC ----
set ARCH=%PROCESSOR_ARCHITECTURE%
if defined PROCESSOR_ARCHITEW6432 set ARCH=%PROCESSOR_ARCHITEW6432%
if /i not "%ARCH%"=="AMD64" (
  echo Race Mode needs Windows 10 or 11 on an Intel or AMD ^(x64^) PC. This PC reports %ARCH%.
  goto :fail
)
for /f "tokens=4 delims=. " %%v in ('ver') do set WINMAJOR=%%v
if %WINMAJOR% lss 10 (
  echo Race Mode needs Windows 10 or 11.
  goto :fail
)
set NETREL=0
rem No quotes around the tool paths here: a for /f command that starts with a quote gets its quotes stripped by cmd.
rem %SystemRoot%\System32 has no spaces, so it's safe unquoted.
for /f "tokens=3" %%r in ('%SYS%\reg.exe query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| %SYS%\find.exe "Release"') do set /a NETREL=%%r
if not exist "%SYS%\curl.exe" (echo This Windows is missing curl. Update Windows, then try again. & goto :fail)
if not exist "%SYS%\tar.exe" (echo This Windows is missing tar. Update Windows, then try again. & goto :fail)
if %NETREL% lss 528040 goto :needdotnet
:havedotnet

rem ---- 2. download into a new, empty folder ----
set "WORK=%TEMP%\RaceMode-setup-%RANDOM%%RANDOM%"
if exist "%WORK%" set "WORK=%WORK%-%RANDOM%"
mkdir "%WORK%" || (echo Couldn't create a temporary folder. & goto :fail)

rem If Windows can't reach the certificate-revocation server (seen right after a PC starts), each download is
rem tried once more without that check; the SHA-256 check below still guards the file.
echo Downloading Race Mode...
"%SYS%\curl.exe" -fsSL --proto =https --tlsv1.2 -o "%WORK%\RaceMode.zip" "%BASE%/RaceMode.zip" || "%SYS%\curl.exe" -fsSL --proto =https --tlsv1.2 --ssl-revoke-best-effort -o "%WORK%\RaceMode.zip" "%BASE%/RaceMode.zip" || goto :dlfail
"%SYS%\curl.exe" -fsSL --proto =https --tlsv1.2 -o "%WORK%\RaceMode.zip.sha256" "%BASE%/RaceMode.zip.sha256" || "%SYS%\curl.exe" -fsSL --proto =https --tlsv1.2 --ssl-revoke-best-effort -o "%WORK%\RaceMode.zip.sha256" "%BASE%/RaceMode.zip.sha256" || goto :dlfail

rem ---- 3. check the download ----
echo Checking the download...
rem only hex digits, spaces and the letters of "RaceMode.zip" are allowed, checked by findstr on the file
rem itself, before cmd reads the line, so quotes or & in a bad file can't break or change this script
"%SYS%\findstr.exe" /r /c:"[^0123456789abcdefABCDEF RMaceodzip.]" "%WORK%\RaceMode.zip.sha256" >nul && goto :badhash
set EXPECTED=
set /p EXPECTED=<"%WORK%\RaceMode.zip.sha256"
for /f "tokens=1" %%a in ("%EXPECTED%") do set EXPECTED=%%a
if not defined EXPECTED goto :badhash
rem exactly 64 hex characters, compared as plain text (not a pattern)
if "%EXPECTED:~63,1%"=="" goto :badhash
if not "%EXPECTED:~64,1%"=="" goto :badhash
echo %EXPECTED%| "%SYS%\findstr.exe" /r /x "[0-9a-fA-F]*" >nul || goto :badhash
"%SYS%\certutil.exe" -hashfile "%WORK%\RaceMode.zip" SHA256 | "%SYS%\findstr.exe" /l /i /x /c:"%EXPECTED%" >nul || goto :badhash

"%SYS%\tar.exe" -xf "%WORK%\RaceMode.zip" -C "%WORK%" RaceMode.exe || (echo Couldn't unpack the download. & goto :cleanfail)
if not exist "%WORK%\RaceMode.exe" (echo The download doesn't contain RaceMode.exe. & goto :cleanfail)

rem ---- 4. hand over to Race Mode's own setup ----
echo Opening Race Mode setup...
start "" /wait "%WORK%\RaceMode.exe" --install
set SETUPRC=%errorlevel%
rmdir /s /q "%WORK%" >nul 2>&1
set "INST=%LOCALAPPDATA%\RaceMode\RaceMode.exe"
if defined RACEMODE_TEST_ROOT set "INST=%RACEMODE_TEST_ROOT%\LocalAppData\RaceMode\RaceMode.exe"
if not "%SETUPRC%"=="0" (echo Setup didn't finish, so Race Mode may not be installed. Run this again to install it. & exit /b 1)
if not exist "%INST%" (echo Setup was closed before Race Mode was installed. Run this again to install it. & exit /b 1)
echo Done.
exit /b 0
rem ---- .NET Framework 4.8 is missing: offer Microsoft's own installer ----
rem The file is Microsoft's offline installer at a fixed address. Its SHA-256 is pinned below, so nothing else
rem can be run in its place. Microsoft's installer then asks Windows for permission (an admin prompt).
:needdotnet
set "NETURL=https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe"
set "NETSHA=0a3a390c47e639d0f7fc65b21195fee6b7f65b066f80f70c60fab191d14b7e40"
echo Race Mode needs .NET Framework 4.8, a free part of Windows from Microsoft. This PC doesn't have it yet.
echo Race Mode can download Microsoft's installer for you (116 MB). Windows will ask for permission to install it,
echo and you may need to restart your PC afterwards.
echo.
"%SYS%\choice.exe" /c YN /n /m "Download and install .NET Framework 4.8 now? [Y/N] "
if errorlevel 2 goto :nodotnet
set "NETWORK=%TEMP%\RaceMode-dotnet-%RANDOM%%RANDOM%"
if exist "%NETWORK%" set "NETWORK=%NETWORK%-%RANDOM%"
mkdir "%NETWORK%" || (echo Couldn't create a temporary folder. & goto :fail)
echo Downloading .NET Framework 4.8 from Microsoft...
"%SYS%\curl.exe" -fsSL --proto =https --tlsv1.2 -o "%NETWORK%\ndp48.exe" "%NETURL%" || (echo The download failed. Check your internet connection and try again. & goto :dotnetclean)
"%SYS%\certutil.exe" -hashfile "%NETWORK%\ndp48.exe" SHA256 | "%SYS%\findstr.exe" /l /i /x /c:"%NETSHA%" >nul || (echo The download isn't Microsoft's .NET Framework 4.8 installer, so it wasn't run. & goto :dotnetclean)
echo Installing .NET Framework 4.8. Windows will ask for permission; this can take several minutes...
start "" /wait "%NETWORK%\ndp48.exe" /passive /norestart
set NETRC=%errorlevel%
rmdir /s /q "%NETWORK%" >nul 2>&1
if "%NETRC%"=="3010" goto :dotnetrestart
if "%NETRC%"=="1641" goto :dotnetrestart
if not "%NETRC%"=="0" (echo .NET Framework 4.8 didn't install ^(code %NETRC%^). & goto :nodotnet)
set NETREL=0
for /f "tokens=3" %%r in ('%SYS%\reg.exe query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| %SYS%\find.exe "Release"') do set /a NETREL=%%r
if %NETREL% lss 528040 goto :dotnetrestart
echo .NET Framework 4.8 is installed.
echo.
goto :havedotnet

:dotnetrestart
echo .NET Framework 4.8 is installed. Restart your PC, then run the Race Mode install command again.
goto :fail

:dotnetclean
rmdir /s /q "%NETWORK%" >nul 2>&1

:nodotnet
echo You can also get it through Windows Update, or from https://dotnet.microsoft.com/download/dotnet-framework/net48
goto :fail


:dlfail
echo Download failed. Check your internet connection and try again.
goto :cleanfail

:badhash
echo The download doesn't match its published checksum, so nothing was installed.

:cleanfail
if defined WORK rmdir /s /q "%WORK%" >nul 2>&1

:fail
echo.
echo Race Mode was not installed.
exit /b 1
