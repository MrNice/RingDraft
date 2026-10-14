@echo off
rem Starts only the second local test player (1002) and joins the host started by test-coop.bat.
set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
start "" /d "%GAME%" "%GAME%\SlayTheSpire2.exe" --force-steam=off --fastmp=join --clientId=1002
