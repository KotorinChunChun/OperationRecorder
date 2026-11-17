@echo off
pwsh -NoProfile -File "%~dp0Start-OperationRecording.ps1" %*
exit /b %ERRORLEVEL%
