@echo off
rem release.bat - publish the current Release build of App Store to GitHub.
rem Usage:  release.bat ["release notes"]
rem
rem The release is just AppStore.exe: CeHttpsNative.dll and cacert.pem are
rem built into it and unpacked on the device (Embedded.cs).
rem
rem Bump [assembly: AssemblyVersion("x.y.z.0")] in AppStore\Properties\AssemblyInfo.cs
rem and build Release in VS2008 first.
setlocal
cd /d "%~dp0"

set REPO=oseart55/AppStore
set APP=AppStore
set EXE=%APP%\bin\Release\AppStore.exe
set NATIVE=..\CeHttps\VS\CeHttpsNative\Windows Mobile 6 Professional SDK (ARMV4I)\Release\CeHttpsNative.dll
set NOTES=%~1
if "%NOTES%"=="" set NOTES=App Store update

where gh >nul 2>nul || (echo GitHub CLI 'gh' not found. & exit /b 1)

set FULLVER=
for /f tokens^=2^ delims^=^" %%v in ('findstr /b /c:"[assembly: AssemblyVersion(" "%APP%\Properties\AssemblyInfo.cs"') do set FULLVER=%%v
if "%FULLVER%"=="" (echo Could not read AssemblyVersion from %APP%\Properties\AssemblyInfo.cs & exit /b 1)
for /f "tokens=1-3 delims=." %%a in ("%FULLVER%") do set TAG=v%%a.%%b.%%c

rem Stop before anything else if this version is already on GitHub.
gh release view %TAG% --repo %REPO% >nul 2>nul
if not errorlevel 1 (
    echo.
    echo %TAG% is already published. Bump AssemblyVersion in %APP%\Properties\AssemblyInfo.cs,
    echo rebuild Release, then run this again.
    exit /b 1
)

if not exist "%EXE%" (echo Missing %EXE% - build Release first. & exit /b 1)
if not exist "%NATIVE%" (echo Missing CeHttpsNative.dll - build Release first. & exit /b 1)

rem Make sure the exe really is this version (i.e. Release was rebuilt after the bump).
set EXEVER=
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "[Reflection.AssemblyName]::GetAssemblyName((Resolve-Path '%EXE%')).Version.ToString()"`) do set EXEVER=%%v
if not "%EXEVER%"=="%FULLVER%" (
    echo.
    echo The built exe is version %EXEVER%, but AssemblyInfo.cs says %FULLVER%.
    echo Rebuild Release in Visual Studio first, then run this again.
    exit /b 1
)

rem The exe carries its own copy of the TLS DLL, so it must be built after it.
set STALE=
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "if ((Get-Item -LiteralPath '%NATIVE%').LastWriteTime -gt (Get-Item -LiteralPath '%EXE%').LastWriteTime) { 'yes' }"`) do set STALE=%%v
if "%STALE%"=="yes" (
    echo.
    echo CeHttpsNative.dll is newer than the exe, so the exe holds an old copy.
    echo Rebuild Release in Visual Studio, then run this again.
    exit /b 1
)

echo.
echo   Repository : %REPO%
echo   Version    : %FULLVER%  (tag %TAG%)
echo   Files      : AppStore.exe
echo   Notes      : %NOTES%
echo.
choice /m "Publish %TAG%"
if errorlevel 2 exit /b 1

gh release create %TAG% "%EXE%" --repo %REPO% --title %TAG% --notes "%NOTES%"
if errorlevel 1 (echo. & echo Release failed. & exit /b 1)
echo.
echo Published %TAG%. On the device: App Store - Menu - Refresh.
