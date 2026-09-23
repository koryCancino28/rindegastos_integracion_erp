using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Traduce un deposito de fondo a codigos del ERP.
///
/// Un fondo de Rindegastos (una caja chica, por ejemplo) se entrega a una
/// persona. En el ERP eso es una transferencia: sale del banco de caja chica y
/// entra a la cuenta del fondo, a nombre de quien lo recibe.
///
/// Los dos datos que definen el asiento los escribe Contabilidad en el fondo:
///   Description = la cuenta contable del ERP (1020152 Fondo Fijo Cusco MN)
///   Code        = el documento de identidad de quien recibe el fondo
/// </summary>
public sealed class ServicioHomologacionFondo
{
    private readonly RepositorioCatalogo _catalogo;
    private readonly ILogger<ServicioHomologacionFondo> _logger;

    public ServicioHomologacionFondo(
        RepositorioCatalogo catalogo, ILogger<ServicioHomologacionFondo> logger)
    {
        _catalogo = catalogo;
        _logger = logger;
    }

    public async Task<ResultadoHomologacionFondo> HomologarAsync(
        FondoApi fondo, short numeroDeposito, TransaccionFondoApi deposito, CancellationToken ct)
    {
        var errores = new List<string>();

        if (string.IsNullOrWhiteSpace(fondo.Title))
            errores.Add("El fondo no trae Title, que es parte de la glosa del comprobante.");

        if (deposito.TransactionAmount <= 0)
            errores.Add($"El deposito es de {deposito.TransactionAmount}: debe ser mayor que cero.");

        if (deposito.TransactionDate is null)
            errores.Add("El deposito no trae fecha (TransactionDate).");

        // ---- Cuenta del fondo (campo Description) ----------------------------------
        var codigoCuenta = fondo.Description?.Trim();
        CuentaContable? cuentaFondo = null;

        if (string.IsNullOrWhiteSpace(codigoCuenta))
        {
            errores.Add("El fondo no trae la cuenta contable en el campo 'Descripcion'. " +
                        "Ahi debe ir el codigo de la cuenta del ERP, por ejemplo 1020152.");
        }
        else
        {
            cuentaFondo = await _catalogo.BuscarCuentaAsync(codigoCuenta, ct);
            if (cuentaFondo is null)
                errores.Add($"La cuenta '{codigoCuenta}' que trae el fondo en 'Descripcion' no existe " +
                            $"en mae_plan_cuenta.");
            else if (!cuentaFondo.Vigente)
                errores.Add($"La cuenta {cuentaFondo.CodigoCuenta} ({cuentaFondo.Nombre}) no esta vigente.");
        }

        // ---- Persona que recibe el fondo (campo Code) ------------------------------
        var documento = fondo.Code?.Trim();
        long codAnalisis = 0;
        var nombreCorto = "";

        if (string.IsNullOrWhiteSpace(documento))
        {
            errores.Add("El fondo no trae el documento de identidad de quien lo recibe en el campo " +
                        "'Codigo'. Sin el no se sabe a nombre de quien va la cuenta del fondo.");
        }
        else
        {
            var analisis = await _catalogo.BuscarAnalisisEmpleadoAsync(documento, ct);
            if (analisis is null)
            {
                var nombre = await _catalogo.BuscarNombreEmpleadoAsync(documento, ct);
                errores.Add(nombre is null
                    ? $"No hay ningun empleado con documento {documento} en mae_empleado. " +
                      $"Revisar el campo 'Codigo' del fondo."
                    : $"El empleado {nombre} (documento {documento}) existe pero no hay ningun analisis " +
                      $"contable con ese documento en tran_analisis.");
            }
            else
            {
                codAnalisis = analisis.Value.Cod;
            }

            // El nombre corto es el que Contabilidad pone en la glosa. Si el
            // empleado no lo tiene cargado, se usa el nombre del analisis.
            nombreCorto = await _catalogo.BuscarNombreCortoEmpleadoAsync(documento, ct)
                          ?? analisis?.Nombre
                          ?? "";
        }

        // ---- Moneda y tipo de cambio -----------------------------------------------
        var iso = string.IsNullOrWhiteSpace(deposito.CurrencyCode)
            ? (string.IsNullOrWhiteSpace(fondo.Currency) ? "PEN" : fondo.Currency!.Trim())
            : deposito.CurrencyCode!.Trim();

        var codMoneda = await _catalogo.BuscarMonedaAsync(iso, ct);
        if (codMoneda is null)
            errores.Add($"La moneda '{iso}' del fondo no existe en ref_moneda.");

        var fecha = deposito.TransactionDate?.Date ?? DateTime.Today;

        decimal tipoCambio = 1m;
        if (codMoneda is not null)
        {
            var tc = await _catalogo.BuscarTipoCambioAsync(codMoneda.Value, fecha, ct);
            if (tc is null or 0)
                errores.Add($"No hay tipo de cambio en tran_tipo_cambio para {iso} al {fecha:yyyy-MM-dd}.");
            else
                tipoCambio = tc.Value;
        }

        // ---- Cuenta del banco de donde sale el dinero ------------------------------
        var codigoBanco = ConstantesFondo.CuentaBancoPara(codMoneda ?? ConstantesErp.MonedaSoles);
        var cuentaBanco = await _catalogo.BuscarCuentaAsync(codigoBanco, ct);
        long codAnalisisBanco = 0;

        if (cuentaBanco is null)
        {
            errores.Add($"La cuenta de banco {codigoBanco} no existe en mae_plan_cuenta.");
        }
        else
        {
            // En la pantalla el analisis del banco se llena solo al elegir la cuenta.
            var analisisBanco = await _catalogo.BuscarAnalisisDeCuentaAsync(codigoBanco, ct);
            if (analisisBanco is null)
                errores.Add($"La cuenta de banco {codigoBanco} no tiene analisis enlazado " +
                            $"(usp_enlazaCuentaconAnalisis no devuelve nada).");
            else
                codAnalisisBanco = analisisBanco.Value;
        }

        // ---- Cierre contable -------------------------------------------------------
        var fechaCierre = await _catalogo.ObtenerFechaCierreContableAsync(ct);
        if (fechaCierre is not null && fecha.Date <= fechaCierre.Value.Date)
            errores.Add($"El deposito es del {fecha:yyyy-MM-dd} y el periodo esta cerrado hasta " +
                        $"el {fechaCierre:yyyy-MM-dd}.");

        if (errores.Count > 0) return new ResultadoHomologacionFondo { Errores = errores };

        var resultado = new FondoHomologado
        {
            IdFondo = fondo.Id,
            Deposito = numeroDeposito,
            Titulo = fondo.Title!.Trim(),
            DocumentoEmpleado = documento!,
            CodAnalisisEmpleado = codAnalisis,
            NombreCorto = nombreCorto,
            CuentaFondo = cuentaFondo!.CodigoCuenta,
            CodPlanCuentaFondo = cuentaFondo.CodPlanCuenta,
            CuentaBanco = cuentaBanco!.CodigoCuenta,
            CodPlanCuentaBanco = cuentaBanco.CodPlanCuenta,
            CodAnalisisBanco = codAnalisisBanco,
            Monto = deposito.TransactionAmount,
            CodMoneda = codMoneda!.Value,
            TipoCambio = tipoCambio,
            FechaDeposito = fecha
        };

        // ---- Ya registrado a mano --------------------------------------------------
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
                    $"numero {resultado.NumeroDocumentoFondo}. Parece que esta entrega ya se registro " +
                    $"(a mano o en otro ciclo); se omite para no duplicarla."
                }
            };

        _logger.LogInformation(
            "Fondo {Id} '{Titulo}' deposito {N} homologado: {Monto:N2} {Moneda} del {Fecha:yyyy-MM-dd}, " +
            "cuenta {Cuenta} a nombre de {Nombre} ({Doc}), banco {Banco}",
            fondo.Id, resultado.Titulo, numeroDeposito, resultado.Monto, iso, fecha,
            resultado.CuentaFondo, resultado.NombreCorto, resultado.DocumentoEmpleado, resultado.CuentaBanco);

        return new ResultadoHomologacionFondo { Fondo = resultado };
    }
}
