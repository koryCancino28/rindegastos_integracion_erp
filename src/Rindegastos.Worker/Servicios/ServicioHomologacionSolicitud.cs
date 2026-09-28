using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Traduce una solicitud de fondo aprobada a codigos del ERP.
///
/// Alguien pide dinero por adelantado (viaticos o entrega a rendir) y, cuando la
/// solicitud termina de aprobarse, se le transfiere. En el ERP es la misma
/// transferencia que la entrega de un fondo, pero cargada a ENTREGAS A RENDIR:
/// la solicitud no trae cuenta contable, y Contabilidad usa 1413110 para las dos
/// politicas (comprobante 2606190).
///
/// El DNI de quien recibe el dinero viene en el campo extra "DNI".
/// </summary>
public sealed class ServicioHomologacionSolicitud
{
    private readonly RepositorioCatalogo _catalogo;
    private readonly ILogger<ServicioHomologacionSolicitud> _logger;

    public ServicioHomologacionSolicitud(
        RepositorioCatalogo catalogo, ILogger<ServicioHomologacionSolicitud> logger)
    {
        _catalogo = catalogo;
        _logger = logger;
    }

    public async Task<ResultadoHomologacionFondo> HomologarAsync(
        SolicitudFondoApi solicitud, CancellationToken ct)
    {
        var errores = new List<string>();

        // ---- Solo las aprobadas -----------------------------------------------------
        if (!solicitud.Aprobada)
            return new ResultadoHomologacionFondo
            {
                Errores = { $"La solicitud esta en estado '{solicitud.Status}' y solo se integran las " +
                            $"aprobadas ({ConstantesSolicitudFondo.Aprobada})." }
            };

        if (string.IsNullOrWhiteSpace(solicitud.Title))
            errores.Add("La solicitud no trae Title, que es parte de la glosa del comprobante.");

        if (solicitud.Amount <= 0)
            errores.Add($"El monto de la solicitud es {solicitud.Amount}: debe ser mayor que cero.");

        // ---- Persona que recibe el dinero ------------------------------------------
        var documento = solicitud.Dni;
        long codAnalisis = 0;
        var nombreCorto = "";

        if (string.IsNullOrWhiteSpace(documento))
        {
            errores.Add("La solicitud no trae el campo extra 'DNI' con el documento de quien recibe el " +
                        "dinero. Sin el no se sabe a nombre de quien va la entrega a rendir.");
        }
        else
        {
            var analisis = await _catalogo.BuscarAnalisisEmpleadoAsync(documento, ct);
            if (analisis is null)
            {
                var nombre = await _catalogo.BuscarNombreEmpleadoAsync(documento, ct);
                errores.Add(nombre is null
                    ? $"No hay ningun empleado con documento {documento} en mae_empleado. " +
                      $"Revisar el campo 'DNI' de la solicitud."
                    : $"El empleado {nombre} (documento {documento}) existe pero no hay ningun analisis " +
                      $"contable con ese documento en tran_analisis.");
            }
            else
            {
                codAnalisis = analisis.Value.Cod;
            }

            nombreCorto = await _catalogo.BuscarNombreCortoEmpleadoAsync(documento, ct)
                          ?? analisis?.Nombre
                          ?? "";
        }

        // ---- Moneda y tipo de cambio -----------------------------------------------
        var iso = string.IsNullOrWhiteSpace(solicitud.Currency) ? "PEN" : solicitud.Currency!.Trim();
        var codMoneda = await _catalogo.BuscarMonedaAsync(iso, ct);
        if (codMoneda is null)
            errores.Add($"La moneda '{iso}' de la solicitud no existe en ref_moneda.");

        // La fecha del comprobante es la de aprobacion, que es cuando se entrega el
        // dinero. Las fechas de la API vienen en UTC, asi que se pasan a hora de Peru.
        var fechaOrigen = solicitud.ClosedDate ?? solicitud.SentDate;
        if (fechaOrigen is null)
            errores.Add("La solicitud no trae fecha de aprobacion (ClosedDate) ni de envio (SentDate).");

        var fecha = fechaOrigen is null ? DateTime.Today : ConstantesFondo.FechaEnPeru(fechaOrigen.Value);

        decimal tipoCambio = 1m;
        if (codMoneda is not null)
        {
            var tc = await _catalogo.BuscarTipoCambioAsync(codMoneda.Value, fecha, ct);
            if (tc is null or 0)
                errores.Add($"No hay tipo de cambio en tran_tipo_cambio para {iso} al {fecha:yyyy-MM-dd}.");
            else
                tipoCambio = tc.Value;
        }

        // ---- Cuentas ----------------------------------------------------------------
        var codigoDestino = ConstantesSolicitudFondo.CuentaPara(codMoneda ?? ConstantesErp.MonedaSoles);
        var cuentaDestino = await _catalogo.BuscarCuentaAsync(codigoDestino, ct);
        if (cuentaDestino is null)
            errores.Add($"La cuenta {codigoDestino} no existe en mae_plan_cuenta.");
        else if (!cuentaDestino.Vigente)
            errores.Add($"La cuenta {codigoDestino} no esta vigente.");

        var codigoBanco = ConstantesFondo.CuentaBancoPara(codMoneda ?? ConstantesErp.MonedaSoles);
        var cuentaBanco = await _catalogo.BuscarCuentaAsync(codigoBanco, ct);
        long codAnalisisBanco = 0;

        if (cuentaBanco is null)
        {
            errores.Add($"La cuenta de banco {codigoBanco} no existe en mae_plan_cuenta.");
        }
        else
        {
            var analisisBanco = await _catalogo.BuscarAnalisisDeCuentaAsync(codigoBanco, ct);
            if (analisisBanco is null)
                errores.Add($"La cuenta de banco {codigoBanco} no tiene analisis enlazado.");
            else
                codAnalisisBanco = analisisBanco.Value;
        }

        // ---- Cierre contable -------------------------------------------------------
        var fechaCierre = await _catalogo.ObtenerFechaCierreContableAsync(ct);
        if (fechaCierre is not null && fecha.Date <= fechaCierre.Value.Date)
            errores.Add($"La solicitud se aprobo el {fecha:yyyy-MM-dd} y el periodo esta cerrado hasta " +
                        $"el {fechaCierre:yyyy-MM-dd}.");

        if (errores.Count > 0) return new ResultadoHomologacionFondo { Errores = errores };

        var resultado = new FondoHomologado
        {
            IdFondo = solicitud.FundId ?? 0,
            Deposito = 0,
            IdSolicitud = solicitud.Id,
            Titulo = solicitud.Title!.Trim(),
            DocumentoEmpleado = documento!,
            CodAnalisisEmpleado = codAnalisis,
            NombreCorto = nombreCorto,
            CuentaFondo = cuentaDestino!.CodigoCuenta,
            CodPlanCuentaFondo = cuentaDestino.CodPlanCuenta,
            CuentaBanco = cuentaBanco!.CodigoCuenta,
            CodPlanCuentaBanco = cuentaBanco.CodPlanCuenta,
            CodAnalisisBanco = codAnalisisBanco,
            Monto = solicitud.Amount,
            CodMoneda = codMoneda!.Value,
            TipoCambio = tipoCambio,
            FechaDeposito = fecha,

            // En una solicitud el numero de documento es el Id del FONDO que
            // Rindegastos crea al aprobarla, no la fecha: asi la transferencia
            // queda enlazada con el fondo que despues se liquida.
            NumeroDocumentoFondo = solicitud.FundId?.ToString() ?? ""
        };

        if (solicitud.FundId is null)
            _logger.LogWarning(
                "Solicitud {Id}: no trae FundId, asi que el numero de documento queda con la fecha ({Numero}).",
                solicitud.Id, resultado.NumeroDocumentoFondo);

        // ---- Ya registrada a mano ---------------------------------------------------
        var existente = await _catalogo.BuscarTransferenciaFondoExistenteAsync(
            resultado.CodPlanCuentaFondo, resultado.CodAnalisisEmpleado,
            resultado.NumeroDocumentoFondo, resultado.Monto,
            resultado.Titulo, ConstantesFondo.TipoComprobante, ct);

        if (existente is not null)
            return new ResultadoHomologacionFondo
            {
                Errores =
                {
                    $"Ya existe el comprobante {existente} con un cargo de {resultado.Monto:N2} a la cuenta " +
                    $"{resultado.CuentaFondo}, a nombre del documento {resultado.DocumentoEmpleado} y con el " +
                    $"numero {resultado.NumeroDocumentoFondo}. Parece que esta solicitud ya se registro " +
                    $"(a mano o en otro ciclo); se omite para no duplicarla."
                }
            };

        _logger.LogInformation(
            "Solicitud {Id} '{Titulo}' ({Politica}) homologada: {Monto:N2} {Moneda} del {Fecha:yyyy-MM-dd}, " +
            "cuenta {Cuenta} a nombre de {Nombre} ({Doc})",
            solicitud.Id, resultado.Titulo, solicitud.TipoRendicionTexto, resultado.Monto, iso, fecha,
            resultado.CuentaFondo, resultado.NombreCorto, resultado.DocumentoEmpleado);

        return new ResultadoHomologacionFondo { Fondo = resultado };
    }
}
