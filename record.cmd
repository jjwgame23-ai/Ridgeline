@echo off
setlocal EnableDelayedExpansion

rem Plays the game with every Territory match recorded, then turns each new recording into
rem a replay you can watch in a browser, with fight storyboards next to it.
rem
rem   record.cmd                                 play normally (pick a mode in the menu)
rem   record.cmd mode=tspec33 level=valley       straight into a 33-a-side match you watch
rem
rem Recordings go to recordings\ (one .jsonl per match, named for the map and the time);
rem replays are made next to them as <name>_replay.html, storyboards in <name>_fights\.
rem Only Territory matches (including spectated ones) are recorded.

set "ROOT=%~dp0"
set "GODOT=%GODOT4%"
if not defined GODOT set "GODOT=%USERPROFILE%\Godot\Godot_v4.7.2-stable_mono_win64.exe"
set "OUT=%ROOT%recordings"

if not exist "%GODOT%" (
    echo Could not find Godot at "%GODOT%".
    echo Set the GODOT4 environment variable to the Godot .NET executable.
    exit /b 1
)
if not exist "%OUT%" mkdir "%OUT%"

echo Building...
dotnet build "%ROOT%Ridgeline.csproj" -v quiet --nologo
if errorlevel 1 (
    echo.
    echo Build failed - not launching.
    exit /b 1
)

echo Recording to %OUT%
"%GODOT%" --path "%ROOT%." -- "telemetry=%OUT%" %*

rem ---- after the game closes: a replay for every recording that doesn't have one yet
where python >nul 2>nul
if errorlevel 1 (
    echo.
    echo Python isn't on the PATH, so no replays were made. The recordings are in %OUT%
    echo To make one yourself:  python tools\replay.py recordings\^<file^>.jsonl
    exit /b 0
)
set "LAST="
for %%F in ("%OUT%\*.jsonl") do (
    if not exist "%%~dpnF_replay.html" (
        echo.
        echo Making a replay of %%~nxF
        python "%ROOT%tools\replay.py" "%%~fF"
        if not errorlevel 1 (
            set "LAST=%%~dpnF_replay.html"
            python "%ROOT%tools\telemetry.py" "%%~fF" --max-boards 12 >nul 2>nul
        )
    )
)
if defined LAST (
    echo.
    echo Opening !LAST!
    start "" "!LAST!"
) else (
    echo.
    echo No new recordings this time. Replays so far are in %OUT%
)
endlocal
