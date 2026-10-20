@echo off
setlocal DisableDelayedExpansion
rem Process-local policy for the fixed repository script; no elevation or registry changes.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0manual-p1.ps1" %*
exit /b %errorlevel%
