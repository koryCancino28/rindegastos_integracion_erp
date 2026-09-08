using Microsoft.Extensions.Options;
using Rindegastos.Worker.Configuracion;
using Rindegastos.Worker.Servicios;

namespace Rindegastos.Worker;

/// <summary>
/// Servicio de fondo: ejecuta un ciclo de integracion cada N minutos.
/// Cada ciclo abre su propio scope para no arrastrar estado entre ejecuciones.
/// </summary>
public sealed class WorkerIntegracion : BackgroundService
{
    private readonly IServiceProvider _servicios;
    private readonly OpcionesIntegracion _opciones;
    private readonly ILogger<WorkerIntegracion> _logger;

    public WorkerIntegracion(
        IServiceProvider servicios,
        IOptions<OpcionesIntegracion> opciones,
        ILogger<WorkerIntegracion> logger)
    {
        _servicios = servicios;
        _opciones = opciones.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromMinutes(Math.Max(1, _opciones.IntervaloMinutos));
        _logger.LogInformation("Worker Rindegastos iniciado. Intervalo: {Min} minutos", intervalo.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _servicios.CreateScope();
                var ciclo = scope.ServiceProvider.GetRequiredService<ServicioCiclo>();
                await ciclo.EjecutarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // apagado normal del servicio
            }
            catch (Exception ex)
            {
                // Un error no debe detener el worker: se registra y se espera al proximo ciclo.
                _logger.LogError(ex, "Error no controlado en el ciclo de integracion");
            }

            try
            {
                await Task.Delay(intervalo, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Worker Rindegastos detenido.");
    }
}
