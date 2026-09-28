@echo off
REM ============================================================================
REM  Ejecuta UN solo ciclo de integracion y termina.
REM
REM  OJO: este archivo NO decide si escribe o no. Eso lo decide
REM  Integracion:SoloLectura en appsettings.json:
REM
REM     SoloLectura = true    descarga y traduce, NO toca contabilidad
REM     SoloLectura = false   graba facturas y comprobantes de verdad
REM
REM  Para correr un ciclo sin escribir, sin tener que editar el appsettings:
REM     ejecutar-una-vez.cmd --solo-lectura
REM ============================================================================
cd /d "%~dp0"

if /i "%~1"=="--solo-lectura" (
    set Integracion__SoloLectura=true
    echo.
    echo  *** MODO SOLO LECTURA: no se escribe nada en contabilidad ***
    echo.
)

dotnet run --project src\Rindegastos.Worker -c Debug -- --una-vez

echo.
pause
