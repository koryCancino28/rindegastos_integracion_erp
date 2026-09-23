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

        // ---- Gasto parcial ---------------------------------------------------------
        // Cuando la empresa asume solo una parte del documento, Rindegastos manda
        // en OriginalAmount lo que asume la empresa y en el campo extra
        // "Monto total" el total real. La diferencia se le cobra al trabajador.
        //   Ejemplo real (factura E001-3527):
        //     OriginalAmount 30.00   Monto total 36.00   al trabajador 6.00
        var montoEmpresa = g.OriginalAmount;
        var montoTotal = g.OriginalAmount;

        if (g.EsGastoParcial)
        {
            var totalDocumento = g.MontoTotalDocumento;

            if (totalDocumento is null)
                errores.Add("El gasto esta marcado como parcial pero el campo extra " +
                            "'Monto total' llego vacio o no es un numero. Sin el no se sabe " +
                            "cuanto del documento se le cobra al trabajador.");
            else if (totalDocumento.Value < g.OriginalAmount)
                errores.Add($"El gasto es parcial pero el 'Monto total' ({totalDocumento:N2}) es menor " +
                            $"que lo que asume la empresa ({g.OriginalAmount:N2}). Debe ser mayor o igual.");
            else
                montoTotal = totalDocumento.Value;
        }

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

        // Si el gasto dice que es factura no domiciliada, el tipo tiene que ser el
        // 37. Si no, se registraria como un documento nacional y quedaria fuera del
        // Registro de Compras No Domiciliados.
        else if (g.DiceNoDomiciliada && codTipoDoc != ConstantesErp.TipoDocNoDomiciliado)
            errores.Add($"El gasto dice '¿Es factura no domiciliada? = Si', pero su tipo de documento " +
                        $"'{tipoDocCode}' corresponde al tipo {codTipoDoc} del ERP y no al 37 " +
                        $"(Comprobante no domiciliado). Corregir el tipo de documento en Rindegastos.");

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
        // La tasa de IGV es la que eligio quien rindio el gasto en Rindegastos, no
        // la del parametro del ERP: hay facturas con 10.5% (gasto 79222741) y el
        // documento manda. Si el gasto no trae tasa, se usa la del ERP.
        var tasaErp = await _catalogo.ObtenerTasaIgvAsync(ct);
        var tasaRindegastos = g.Taxes?.Porcentaje;
        var tasaIgv = tasaRindegastos ?? tasaErp;

        var (neto, igv, exento) = CalcularMontos(codTipoDoc!.Value, montoTotal, montoEmpresa, tasaIgv);

        if (codTipoDoc == ConstantesErp.TipoDocFactura)
        {
            if (tasaRindegastos is null)
                _logger.LogWarning(
                    "Gasto {Id}: la factura no trae tasa de IGV en Taxes; se usa la del ERP ({Tasa}%).",
                    g.Id, tasaErp);
            else if (tasaRindegastos != tasaErp)
                _logger.LogInformation(
                    "Gasto {Id}: la factura lleva {Tasa}% de IGV segun Rindegastos ('{Nombre}'), " +
                    "no el {TasaErp}% del ERP.",
                    g.Id, tasaRindegastos, g.Taxes?.taxName, tasaErp);

            // El IGV calculado tiene que coincidir con el que muestra Rindegastos,
            // pero solo se compara cuando los montos que manda son coherentes:
            // Net + taxAmount tiene que dar el monto del gasto. Hay gastos donde
            // quedaron desactualizados porque el monto se corrigio despues (el
            // 78988720 manda 57.04 + 10.26 para un gasto de 20.00).
            //
            // En un gasto parcial tampoco se comparan: ahi Net y taxAmount son
            // del documento completo y el ERP desagrega solo la parte de la empresa.
            var netoRindegastos = g.Net;
            var igvRindegastos = g.Taxes?.taxAmount ?? 0m;
            var montosCoherentes = Math.Abs(netoRindegastos + igvRindegastos - g.OriginalAmount) <= 0.05m;

            if (!g.EsGastoParcial && montosCoherentes && Math.Abs(igv - igvRindegastos) > 0.05m)
                errores.Add($"El IGV calculado ({igv:N2}) no coincide con el que trae Rindegastos " +
                            $"({igvRindegastos:N2}) para la tasa {tasaIgv}%. Revisar el gasto antes de integrarlo.");
            else if (!g.EsGastoParcial && !montosCoherentes && igvRindegastos > 0)
                _logger.LogWarning(
                    "Gasto {Id}: Rindegastos manda neto {Neto:N2} + IGV {Igv:N2}, que no suman el monto del " +
                    "gasto ({Total:N2}); se desagrega con la tasa {Tasa}%.",
                    g.Id, netoRindegastos, igvRindegastos, g.OriginalAmount, tasaIgv);
        }

        if (errores.Count > 0) return new ResultadoHomologacion { Errores = errores };

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
            MontoTotal = montoTotal,
            MontoEmpresa = montoEmpresa,
            CodPlanCuentaGasto = cuentaGasto!.CodPlanCuenta,
            CuentaGasto = cuentaGasto.CodigoCuenta,
            CodCentroCosto = codCentroCosto,
            Glosa = GlosaDelGasto(g, $"{proveedor.Value.Nombre} Nº{nroDocumento}")
        };

        return new ResultadoHomologacion { Gasto = gasto };
    }

    /// <summary>
    /// Glosa de la linea del gasto: la nota completa que escribio quien rindio.
    ///
    /// En un gasto parcial la nota incluye la aclaracion del reparto
    /// ("... / SOLO SE CONSIDERA S/30.00 DEL TOTAL DE S/36.00") y se deja entera
    /// a proposito, por pedido de Contabilidad: asi el comprobante explica por si
    /// solo por que el gasto es menor que el documento.
    ///
    /// mdco_glosa acepta 150 caracteres; si la nota es mas larga, el recorte lo
    /// hace RepositorioComprobante al grabar.
    /// </summary>
    /// <param name="respaldo">Glosa a usar si el gasto no trae nota.</param>
    public static string GlosaDelGasto(GastoApi g, string respaldo)
        => string.IsNullOrWhiteSpace(g.Note) ? respaldo : g.Note!.Trim();

    /// <summary>
    /// Reparte el total del documento entre neto, IGV y exento.
    ///
    ///   Factura                -> se desagrega: neto = empresa / (1 + tasa), IGV = empresa - neto
    ///   Boleta y Recibo Honor. -> todo va a exento, sin credito fiscal
    ///
    /// La tasa la decide el gasto en Rindegastos (Taxes.taxPercentage), no el
    /// parametro del ERP: la factura FAA1-32729996 del gasto 79222741 lleva
    /// 10.5% y quedaria mal con el 18% (neto 457.63 en vez de 488.69).
    ///
    /// En un gasto PARCIAL el neto y el IGV se calculan solo sobre la parte que
    /// asume la empresa, y lo que se le cobra al trabajador va a exento: sobre
    /// esa parte no se toma credito fiscal.
    ///
    /// Verificado contra registros reales del ERP:
    ///   Factura F003-4254: total 161.07              -> neto 136.50 + IGV 24.57
    ///   Boleta  0001-5082: total 180.00              -> exento 180.00
    ///   Factura E001-3527: total 36.00, empresa 30.00 -> neto 25.42 + IGV 4.58 + exento 6.00
    ///   Boleta  B001-2020: total 180.00, empresa 100.00 -> exento 180.00
    /// </summary>
    /// <param name="total">Total del documento.</param>
    /// <param name="montoEmpresa">
    /// Parte que asume la empresa. En un gasto normal es igual al total.
    /// </param>
    public static (decimal Neto, decimal Igv, decimal Exento) CalcularMontos(
        short codTipoDoc, decimal total, decimal montoEmpresa, decimal tasaIgv)
    {
        // Boleta y recibo por honorarios no dan credito fiscal: todo el documento
        // va a exento, tambien cuando es parcial (verificado en la boleta B001-2020,
        // donde el exento es el total y no solo la parte de la empresa).
        if (codTipoDoc != ConstantesErp.TipoDocFactura)
            return (0m, 0m, total);

        var factor = 1m + (tasaIgv / 100m);
        var neto = Math.Round(montoEmpresa / factor, 2, MidpointRounding.AwayFromZero);
        var igv = Math.Round(neto * (tasaIgv / 100m), 2, MidpointRounding.AwayFromZero);

        // El ERP exige que neto + IGV = lo que asume la empresa. Si el redondeo
        // deja centimos, se ajustan en el IGV, que es como queda en los
        // comprobantes reales.
        var diferencia = montoEmpresa - (neto + igv);
        if (diferencia != 0) igv += diferencia;

        // Lo que no asume la empresa queda exento. En un gasto normal da cero.
        return (neto, igv, total - montoEmpresa);
    }
}
