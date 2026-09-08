@echo off
REM ============================================================================
REM  Compila el worker sin necesidad de abrir Visual Studio.
REM  Usa el SDK de .NET instalado en el equipo, no el MSBuild de VS 2019.
REM ============================================================================
cd /d "%~dp0"

echo.
echo === SDK de .NET en uso ===
dotnet --version
echo.

dotnet build RindegastosIntegracion.sln -c Debug --nologo

if errorlevel 1 (
    echo.
    echo *** LA COMPILACION FALLO ***
    pause
    exit /b 1
)

echo.
echo === Compilacion correcta ===
pause
