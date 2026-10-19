@echo off
rem launch-se2.bat <game command...> - runs Space Engineers 2 with the dev launch args kept in one file. Steam's launch
rem options for SE2:  "C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\shared\launch-se2.bat" %command%
rem The args file (one line): %SE2_LAUNCH_ARGS% when set, else se2-launch-args.txt next to this file (personal, gitignored;
rem start from se2-launch-args.example.txt). The harness scripts rewrite its -start: for their test worlds. No file: no args.
setlocal
set "ARGSFILE=%SE2_LAUNCH_ARGS%"
if not defined ARGSFILE set "ARGSFILE=%~dp0se2-launch-args.txt"
set "ARGS="
if exist "%ARGSFILE%" set /p ARGS=<"%ARGSFILE%"
%* %ARGS%
