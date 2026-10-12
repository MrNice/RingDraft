@echo off
rem Uploads (or updates) Ring Draft on the Steam Workshop. Steam must be running and signed in.
rem Build first (build.bat) so workshop\content has the latest RingDraft.dll.
cd /d "%~dp0tools\ModUploader"
ModUploader.exe upload -w "%~dp0workshop"
echo.
if errorlevel 1 (echo UPLOAD FAILED. See tools\ModUploader\mod-uploader.log) else (echo Done. Workshop ID saved to workshop\mod_id.txt)
pause
