@echo off
rem Builds Ring Draft and installs it into the games mods folder. Output goes to build-log.txt.
cd /d "%~dp0"
(
  echo === %DATE% %TIME%
  where dotnet
  dotnet --list-sdks
  dotnet build -c Release
) > build-log.txt 2>&1
echo EXIT %ERRORLEVEL% >> build-log.txt
