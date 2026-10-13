@echo off
rem Two-player Ring Draft test on ONE PC, no second Steam account needed.
rem Uses the game's built-in dev launch options: Steam off (offline profiles 1001 and 1002), local ENet co-op on 127.0.0.1.
rem Run build.bat first. The build is copied into mods\RingDraft for the test and removed when both games close,
rem so normal Steam play keeps using the Workshop copy.
title Ring Draft local co-op test - close both games to clean up
set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
set "DATA=%APPDATA%\SlayTheSpire2\default"
cd /d "%~dp0"
tasklist /fi "imagename eq SlayTheSpire2.exe" 2>nul | find /i "SlayTheSpire2.exe" >nul && (echo Close Slay the Spire 2 first. & pause & exit /b 1)
if not exist "workshop\content\RingDraft.dll" (echo Run build.bat first. & pause & exit /b 1)
xcopy /y /i /q "workshop\content\*" "%GAME%\mods\RingDraft\" >nul
echo Starting host (player 1001)...
start "" /d "%GAME%" "%GAME%\SlayTheSpire2.exe" --force-steam=off --fastmp=host --clientId=1001
timeout /t 20 /nobreak >nul
echo Starting client (player 1002)...
start "" /d "%GAME%" "%GAME%\SlayTheSpire2.exe" --force-steam=off --fastmp=join --clientId=1002
echo.
echo Pick characters in each window, then start the run from the host.
echo If either game asks to enable mods, choose NOT to restart: close both games and run this again.
echo Leave this window open; it removes the test copy of the mod when both games close.
:wait
timeout /t 5 /nobreak >nul
tasklist /fi "imagename eq SlayTheSpire2.exe" 2>nul | find /i "SlayTheSpire2.exe" >nul && goto wait
rmdir /s /q "%GAME%\mods\RingDraft"
echo Cleaned up.
timeout /t 3 >nul
