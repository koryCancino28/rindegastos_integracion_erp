using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Traduce un gasto de Rindegastos a los codigos del ERP y calcula los montos.
/// Si algo no resuelve, devuelve la lista de errores en lugar de inventar valores.
/// </summary>
public sealed class ServicioHomologacion
{
    private readonly RepositorioCatalogo _catalogo;
    private readonly ILogger<ServicioHomologacion> _logger;

    public ServicioHomologacion(RepositorioCatalogo catalogo, ILogger<ServicioHomologacion> logger)
    {
        _catalogo = catalogo;
        _logger = logger;
    }

    public async Task<ResultadoHomologacion> HomologarAsync(GastoApi g, CancellationToken ct)
    {
        var errores = new List<string>();

        // ---- Campos extra que la politica de Rindegastos debe traer ---------------
        var rucExtra = g.CampoExtra("Ruc Proveedor")?.Value?.Trim();
        var tipoDocCode = g.CampoExtra("Tipo de Documento")?.Code?.Trim();
        var nroDocumento = g.CampoExtra("Nro Documento")?.Value?.Trim();
        var centroCostoCode = g.CampoExtra("Centro de Costos 1")?.Code?.Trim();

        // El RUC del campo extra manda; SunatInfo queda como respaldo.
        var ruc = !string.IsNullOrWhiteSpace(rucExtra) ? rucExtra : g.SunatInfo?.Ruc?.Trim();

        if (string.IsNullOrWhiteSpace(ruc))
            errores.Add("El gasto no trae RUC de proveedor (ni en el campo extra 'Ruc Proveedor' ni en SunatInfo).");
        if (string.IsNullOrWhiteSpace(tipoDocCode))
            errores.Add("El gasto no trae el campo extra 'Tipo de Documento'.");
        if (string.IsNullOrWhiteSpace(nroDocumento))
            errores.Add("El gasto no trae el campo extra 'Nro Documento' (se usa como folio).");
        if (string.IsNullOrWhiteSpace(g.CategoryCode))
            errores.Add("El gasto no trae CategoryCode (cuenta contable de gasto).");
        if (g.IssueDate is null)
            errores.Add("El gasto no trae IssueDate.");

        // IMPORTANTE: se usa OriginalAmount, no Total.
        //   OriginalAmount / OriginalCurrency = el documento tal como se emitio.
        //   Total / Currency = lo mismo convertido a la moneda de la politica.
        // Cuando Rindegastos no tiene tipo de cambio (ExchangeRate = 0) no convierte
        // y deja Total en 0, aunque el gasto tenga monto. En soles ambos coinciden,
        // asi que tomar OriginalAmount siempre es correcto y ademas cubre los dolares.
        if (g.OriginalAmount <= 0)
            errores.Add($"El monto del gasto (OriginalAmount) es {g.OriginalAmount}: debe ser mayor que cero.");

        if (errores.Count > 0) return new ResultadoHomologacion { Errores = errores };

        // ---- Proveedor -------------------------------------------------------------
        var proveedor = await _catalogo.BuscarProveedorPorRucAsync(ruc!, ct);
        if (proveedor is null)
        {
            errores.Add($"El proveedor con RUC {ruc} no existe en mae_proveedor. Debe crearse primero en el ERP.");
            return new ResultadoHomologacion { Errores = errores };
        }

        var codAnalisis = await _catalogo.BuscarAnalisisProveedorAsync(proveedor.Value.Cod, ct);
        if (codAnalisis is null)
            errores.Add($"El proveedor {ruc} no tiene analisis contable en tran_analisis.");

        // ---- Tipo de documento -----------------------------------------------------
        var codTipoDoc = await _catalogo.BuscarTipoDocumentoAsync(tipoDocCode!, ct);
        if (codTipoDoc is null)
            errores.Add($"El tipo de documento '{tipoDocCode}' no tiene equivalencia. " +
                        $"Agregar una fila en rg_homologacion con rgh_tipo='TIPO_DOCUMENTO'.");

        // ---- Moneda y tipo de cambio -----------------------------------------------
        // Se usa OriginalCurrency por el mismo motivo que OriginalAmount: Currency
        // trae la moneda de la politica (PEN) aunque el gasto se haya emitido en USD.
        var iso = string.IsNullOrWhiteSpace(g.OriginalCurrency) ? "PEN" : g.OriginalCurrency!;
        var codMoneda = await _catalogo.BuscarMonedaAsync(iso, ct);
        if (codMoneda is null)
            errores.Add($"La moneda '{iso}' no existe en ref_moneda (columna rmo_iso).");

        decimal tipoCambio = 1m;
        if (codMoneda is not null)
        {
            // ExchangeRate de Rindegastos llega en 0, asi que se usa el del ERP.
            var tc = await _catalogo.BuscarTipoCambioAsync(codMoneda.Value, g.IssueDate!.Value, ct);
            if (tc is null or 0)
                errores.Add($"No hay tipo de cambio en tran_tipo_cambio para {iso} al {g.IssueDate:yyyy-MM-dd}.");
            else
                tipoCambio = tc.Value;
        }

        // ---- Cuenta de gasto -------------------------------------------------------
        var cuentaGasto = await _catalogo.BuscarCuentaAsync(g.CategoryCode!, ct);
        if (cuentaGasto is null)
            errores.Add($"La cuenta '{g.CategoryCode}' (categoria '{g.Category}') no existe en mae_plan_cuenta.");
        else if (!cuentaGasto.Vigente)
            errores.Add($"La cuenta {cuentaGasto.CodigoCuenta} no esta vigente.");

        // ---- Centro de costo -------------------------------------------------------
        short? codCentroCosto = null;
        if (cuentaGasto is not null && cuentaGasto.RequiereCentroCosto)
        {
            if (!short.TryParse(centroCostoCode, out var cc))
                errores.Add($"La cuenta {cuentaGasto.CodigoCuenta} requiere centro de costo, " +
                            $"pero el campo extra 'Centro de Costos 1' llego vacio o no numerico ('{centroCostoCode}').");
            else if (!await _catalogo.ExisteCentroCostoAsync(cc, ct))
                errores.Add($"El centro de costo {cc} no existe en ref_centro_costo.");
            else
                codCentroCosto = cc;
        }

        // ---- Cierre contable -------------------------------------------------------
        var fechaContabilizacion = DateTime.Today;   // el ERP usa el dia en que se registra
        var fechaCierre = await _catalogo.ObtenerFechaCierreContableAsync(ct);
        if (fechaCierre is not null && fechaContabilizacion.Date <= fechaCierre.Value.Date)
            errores.Add($"El periodo esta cerrado hasta el {fechaCierre:yyyy-MM-dd}: no se pueden ingresar facturas.");

        // ---- Documento ya registrado en el ERP -------------------------------------
        // Misma llave natural que usa la pantalla: folio + proveedor + tipo de documento.
        // Protege contra el caso de una boleta cargada varias veces en Rindegastos:
        // cada copia trae un Id distinto, asi que el candado por Id no la detecta.
        if (codTipoDoc is not null)
        {
            var facturaExistente = await _catalogo.BuscarFacturaExistenteAsync(
                nroDocumento!, proveedor.Value.Cod, codTipoDoc.Value, ct);

            if (facturaExistente is not null)
                errores.Add($"El documento {nroDocumento} del proveedor {ruc} ya esta registrado en el ERP " +
                            $"(mfb_cod_factura_boleta = {facturaExistente}). Se omite para no duplicarlo.");
        }

        if (errores.Count > 0) return new ResultadoHomologacion { Errores = errores };

        // ---- Montos ----------------------------------------------------------------
        var tasaIgv = await _catalogo.ObtenerTasaIgvAsync(ct);
        var (neto, igv, exento) = CalcularMontos(codTipoDoc!.Value, g.OriginalAmount, tasaIgv);

        var gasto = new GastoHomologado
        {
            IdRindegastos = g.Id,
            Folio = nroDocumento!,
            CodProveedor = proveedor.Value.Cod,
            RucProveedor = ruc!,
            RazonSocial = proveedor.Value.Nombre,
            CodAnalisis = codAnalisis!.Value,
            CodTipoDocumento = codTipoDoc.Value,
            CodMoneda = codMoneda!.Value,
            TipoCambio = tipoCambio,
            FechaEmision = g.IssueDate!.Value.Date,
            FechaVencimiento = g.IssueDate!.Value.Date,   // el ERP replica la fecha de emision
            FechaContabilizacion = fechaContabilizacion,
            MontoNeto = neto,
            MontoIgv = igv,
            MontoExento = exento,
            MontoTotal = g.OriginalAmount,
            CodPlanCuentaGasto = cuentaGasto!.CodPlanCuenta,
            CuentaGasto = cuentaGasto.CodigoCuenta,
            CodCentroCosto = codCentroCosto,
            Glosa = string.IsNullOrWhiteSpace(g.Note) ? $"{proveedor.Value.Nombre} Nº{nroDocumento}" : g.Note!.Trim()
        };

        return new ResultadoHomologacion { Gasto = gasto };
    }

    /// <summary>
    /// Reparte el total entre neto, IGV y exento segun el tipo de documento.
    ///
    ///   Factura                -> se desagrega: neto = total / (1 + tasa), IGV = total - neto
    ///   Boleta y Recibo Honor. -> todo va a exento, sin credito fiscal
    ///
    /// Verificado contra registros reales del ERP:
    ///   Factura F003-4254: total 161.07 -> neto 136.50 + IGV 24.57
    ///   Boleta  0001-5082: total 180.00 -> exento 180.00
    /// </summary>
    public static (decimal Neto, decimal Igv, decimal Exento) CalcularMontos(
        short codTipoDoc, decimal total, decimal tasaIgv)
    {
        if (codTipoDoc != ConstantesErp.TipoDocFactura)
            return (0m, 0m, total);

        var factor = 1m + (tasaIgv / 100m);
        var neto = Math.Round(total / factor, 2, MidpointRounding.AwayFromZero);
        var igv = Math.Round(neto * (tasaIgv / 100m), 2, MidpointRounding.AwayFromZero);

        // El ERP exige que neto + IGV = total. Si el redondeo deja centimos,
        // se ajustan en el IGV, que es como queda en los comprobantes reales.
        var diferencia = total - (neto + igv);
        if (diferencia != 0) igv += diferencia;

        return (neto, igv, 0m);
    }
}
