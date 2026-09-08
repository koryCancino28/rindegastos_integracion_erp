@echo off
REM ============================================================================
REM  Ejecuta UN solo ciclo de integracion y termina.
REM  Con Integracion:SoloLectura = true (valor por defecto) unicamente
REM  descarga los gastos a la tabla rg_gasto: no toca contabilidad.
REM ============================================================================
cd /d "%~dp0"

dotnet run --project src\Rindegastos.Worker -c Debug -- --una-vez

echo.
pause
