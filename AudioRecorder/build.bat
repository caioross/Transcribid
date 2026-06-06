@echo off
REM Compila Transcribid em UM unico .exe self-contained (Windows 10/11 x64)
REM Saida final: bin\publish\Transcribid.exe

setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo [ERRO] .NET SDK nao encontrado.
    echo Instale o ".NET 8 SDK" em: https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

echo.
echo === Restaurando pacotes ===
dotnet restore || goto :err

echo.
echo === Publicando .exe self-contained (win-x64) ===
dotnet publish AudioRecorder.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o bin\publish || goto :err

echo.
echo === OK ===
echo Executavel: %CD%\bin\publish\Transcribid.exe
echo.
explorer "%CD%\bin\publish"
exit /b 0

:err
echo.
echo [ERRO] Build falhou. Veja a saida acima.
pause
exit /b 1
