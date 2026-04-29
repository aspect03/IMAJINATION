@echo off
powershell.exe -ExecutionPolicy Bypass -File "%~dp0backup-db.ps1" >> "%~dp0..\backups\backup-task.log" 2>&1
