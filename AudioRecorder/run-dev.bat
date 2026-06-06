@echo off
REM Roda em modo desenvolvimento (sem compilar single-file)
setlocal
cd /d "%~dp0"
dotnet run -c Release
