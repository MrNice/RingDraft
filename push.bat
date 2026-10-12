@echo off
rem Pushes this repo to github.com/MrNice/RingDraft using your Windows git login. Output goes to push-log.txt.
cd /d "%~dp0"
(
  git remote get-url origin 2>nul || git remote add origin https://github.com/MrNice/RingDraft.git
  git push -u origin main
) > push-log.txt 2>&1
echo EXIT %ERRORLEVEL% >> push-log.txt
