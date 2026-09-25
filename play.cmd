@echo off
setlocal

rem Builds the game, then launches it. Godot runs whatever assembly was last
rem compiled and does not rebuild on its own, so always start through this.

set "ROOT=%~dp0"
set "GODOT=%GODOT4%"
if not defined GODOT set "GODOT=%USERPROFILE%\Godot\Godot_v4.7.2-stable_mono_win64.exe"

if not exist "%GODOT%" (
    echo Could not find Godot at "%GODOT%".
    echo Set the GODOT4 environment variable to the Godot .NET executable.
    exit /b 1
)

echo Building...
dotnet build "%ROOT%Ridgeline.csproj" -v quiet --nologo
if errorlevel 1 (
    echo.
    echo Build failed - not launching.
    exit /b 1
)

"%GODOT%" --path "%ROOT%." %*
