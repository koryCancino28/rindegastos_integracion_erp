using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Traduce un informe de gastos (rendicion) y sus gastos a codigos del ERP.
/// Si algo no resuelve, devuelve la lista de errores en lugar de inventar valores.
///
/// El informe se registra en Ingreso de Comprobante cuando se CIERRA en
/// Rindegastos (Status 1), con una linea por gasto mas la contrapartida:
///
///   - Planilla de movilidad: la linea va a la cuenta de gasto (6311015). No
///     pasa por compras porque no hay documento de un proveedor.
///   - Factura, boleta o RxH (totales o parciales): el documento ya entro por
///     compras (Compra de Servicio sin Prorrateo). El informe lo cancela con
///     un cargo a la cuenta del proveedor y, si es parcial, un abono a 1419010
///     por lo que paga el trabajador.
/// </summary>
public sealed class ServicioHomologacionInforme
{
    private readonly RepositorioCatalogo _catalogo;
    private readonly ILogger<ServicioHomologacionInforme> _logger;

    public ServicioHomologacionInforme(
        RepositorioCatalogo catalogo, ILogger<ServicioHomologacionInforme> logger)
    {
        _catalogo = catalogo;
        _logger = logger;
    }

    public async Task<ResultadoHomologacionInforme> HomologarAsync(
        InformeApi informe, IReadOnlyList<GastoApi> todosLosGastos, CancellationToken ct)
    {
        var errores = new List<string>();

        // ---- Informe cerrado -------------------------------------------------------
        // Mientras falte un aprobador el informe sigue en Status 0 y todavia puede
        // cambiar, asi que se espera. No es un error: no gasta reintentos.
        if (informe.Status != ConstantesRendicion.InformeCerrado)
            return Espera($"El informe todavia no esta cerrado en Rindegastos (Status {informe.Status}): " +
                          $"falta que lo aprueben todos. Se integra cuando se cierre.");

        // ---- Tipo de rendicion -----------------------------------------------------
        // Es lo que decide todo lo demas: tipo de comprobante y cuenta de contrapartida.
        var regla = ReglaRendicion.Reconocer(informe.TipoRendicionTexto);
        if (regla is null)
        {
            errores.Add($"El informe no trae un 'Tipo de rendición' reconocible " +
                        $"(llego '{informe.TipoRendicionTexto}'). Valores validos: {ReglaRendicion.NombresReconocidos}.");
            return new ResultadoHomologacionInforme { Errores = errores };
        }

        // ---- Gastos que entran -----------------------------------------------------
        // Los rechazados no forman parte de la rendicion. Si queda alguno sin
        // aprobar en un informe cerrado, se espera en vez de adivinar.
        var gastos = todosLosGastos.Where(g => g.Status != ConstantesRendicion.GastoRechazado).ToList();

        var sinAprobar = gastos.Where(g => g.Status != ConstantesRendicion.GastoAprobado).ToList();
        if (sinAprobar.Count > 0)
            return Espera($"El informe esta cerrado pero {sinAprobar.Count} gasto(s) no figuran aprobados " +
                          $"({string.Join(", ", sinAprobar.Select(g => $"{g.Id} Status {g.Status}"))}).");

        if (string.IsNullOrWhiteSpace(informe.Title))
            errores.Add("El informe no trae Title, que es la glosa del comprobante.");

        if (gastos.Count == 0)
            errores.Add("El informe no tiene gastos aprobados.");

        var planillas = gastos.Where(g => g.EsPlanillaMovilidad).ToList();

        // ---- Persona que rinde -----------------------------------------------------
        // Siempre es el usuario que envio el informe: Employee.Identification.
        // Traiga o no planillas de movilidad.
        //
        // El campo extra "Ruc Proveedor" NO sirve para esto: en una factura es el
        // RUC del comercio y en una planilla es el DNI de quien se movilizo, que
        // puede ser otra persona. En el comprobante 2606225, la planilla es de
        // Karen Pereda (DNI 73833161) y la contrapartida quedo igual a nombre de
        // Hiroshi (73026091), que es quien envio el informe.
        var documentoEmpleado = informe.Employee?.Identification?.Trim();

        if (string.IsNullOrWhiteSpace(documentoEmpleado))
        {
            documentoEmpleado = null;
            errores.Add($"No se sabe quien rinde: el usuario '{informe.Employee?.Name}' de Rindegastos no " +
                        $"tiene documento de identidad (Identification) en su perfil. La contrapartida va " +
                        $"siempre a nombre de quien envia el informe, asi que hay que completarlo en Rindegastos.");
        }

        long codAnalisis = 0;
        var nombreEmpleado = "";

        if (documentoEmpleado is not null)
        {
            var analisis = await _catalogo.BuscarAnalisisEmpleadoAsync(documentoEmpleado, ct);

            if (analisis is null)
            {
                // Se distingue entre "no existe" y "existe sin analisis" porque
                // se resuelven de forma distinta.
                var nombre = await _catalogo.BuscarNombreEmpleadoAsync(documentoEmpleado, ct);
                errores.Add(nombre is null
                    ? $"No hay ningun empleado con documento {documentoEmpleado} en mae_empleado. " +
                      $"Debe darse de alta en el ERP, o corregir el documento en Rindegastos."
                    : $"El empleado {nombre} (documento {documentoEmpleado}) existe pero no hay ningun analisis " +
                      $"contable con ese documento en tran_analisis (ni de empleado ni de cliente). Sin analisis " +
                      $"no se puede grabar la contrapartida.");
            }
            else
            {
                codAnalisis = analisis.Value.Cod;
                nombreEmpleado = analisis.Value.Nombre;
            }
        }

        // ---- Moneda ----------------------------------------------------------------
        // Se toma la moneda ORIGINAL de los gastos, no la del informe: el informe
        // trae la moneda de la politica (PEN) aunque los gastos sean en dolares.
        // Es el mismo motivo por el que en compras se usa OriginalCurrency.
        // Todos los gastos de un comprobante van en una sola moneda, asi que si
        // el informe mezcla monedas se detiene.
        var monedas = gastos
            .Select(g => string.IsNullOrWhiteSpace(g.OriginalCurrency) ? "PEN" : g.OriginalCurrency!.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

        var iso = monedas.Count == 1 ? monedas[0]
                : string.IsNullOrWhiteSpace(informe.Currency) ? "PEN" : informe.Currency!.Trim();

        if (monedas.Count > 1)
            errores.Add($"Los gastos del informe vienen en monedas distintas ({string.Join(", ", monedas)}). " +
                        $"Un comprobante se registra en una sola moneda.");

        var codMoneda = await _catalogo.BuscarMonedaAsync(iso, ct);
        if (codMoneda is null)
            errores.Add($"La moneda '{iso}' no existe en ref_moneda.");

        // ---- Fecha y tipo de cambio ------------------------------------------------
        // La pantalla carga el tipo de cambio vigente a la FECHA DEL COMPROBANTE
        // (wctrComprobanteContable.ascx.vb, linea 1239), no a la del gasto.
        // Verificado en el comprobante 2598232 del 15/06/2026: tipo de cambio 3.389,
        // el vigente ese dia en tran_tipo_cambio. Tambien en 2606152 (factura en
        // dolares): 514.80 x 3.450 del dia del informe, no el de la factura.
        var fechaContabilizacion = DateTime.Today;

        decimal tipoCambio = 1m;
        if (codMoneda is not null)
        {
            var tc = await _catalogo.BuscarTipoCambioAsync(codMoneda.Value, fechaContabilizacion, ct);
            if (tc is null or 0)
                errores.Add($"No hay tipo de cambio en tran_tipo_cambio para {iso} al {fechaContabilizacion:yyyy-MM-dd}.");
            else
                tipoCambio = tc.Value;
        }

        // ---- Cuenta de contrapartida -----------------------------------------------
        // Cada rendicion tiene su cuenta en soles y en dolares:
        //   entrega a rendir y viaticos  1413110 / 1413010
        //   reembolso                    4699210 / 4699220
        //   caja chica                   4690210 / 4690215
        var codigoContrapartida = regla.CuentaPara(codMoneda ?? ConstantesErp.MonedaSoles);

        var cuentaContrapartida = await _catalogo.BuscarCuentaAsync(codigoContrapartida, ct);
        if (cuentaContrapartida is null)
            errores.Add($"La cuenta {codigoContrapartida} no existe en mae_plan_cuenta.");
        else if (!cuentaContrapartida.Vigente)
            errores.Add($"La cuenta {codigoContrapartida} no esta vigente.");

        // ---- Centro de costo del informe -------------------------------------------
        // Lo lleva la contrapartida, pero solo cuando hay planillas de movilidad:
        // asi lo registra Contabilidad. En 2606122-2606125 (planillas) la
        // contrapartida tiene el centro de costo; en 2606128, 2606130, 2606152,
        // 2606162, 2606166 y 2606175 (solo documentos) lo deja vacio.
        // Se toma tal cual del Code que manda Rindegastos, sin traducir por
        // nombre: hay 14 centros de costo distintos llamados "TIENDA CALLAO".
        short? ccInforme = null;
        var ccInformeCode = informe.CampoExtra("Centro de Costos")?.Code?.Trim();
        if (planillas.Count > 0 && short.TryParse(ccInformeCode, out var cci))
        {
            if (await _catalogo.ExisteCentroCostoAsync(cci, ct)) ccInforme = cci;
            else errores.Add($"El centro de costo {cci} del informe no existe en ref_centro_costo.");
        }

        // ---- Cierre contable -------------------------------------------------------
        var fechaCierre = await _catalogo.ObtenerFechaCierreContableAsync(ct);
        if (fechaCierre is not null && fechaContabilizacion.Date <= fechaCierre.Value.Date)
            errores.Add($"El periodo esta cerrado hasta el {fechaCierre:yyyy-MM-dd}.");

        // ---- Los gastos, uno por uno -----------------------------------------------
        var lineas = new List<GastoRendicion>();
        var esperas = new List<string>();

        foreach (var g in gastos)
        {
            if (g.EsPlanillaMovilidad)
            {
                var linea = await HomologarPlanillaAsync(g, informe, errores, ct);
                if (linea is not null) lineas.Add(linea);
            }
            else
            {
                var linea = await HomologarDocumentoAsync(g, informe, codMoneda, errores, esperas, ct);
                if (linea is not null) lineas.Add(linea);
            }
        }

        // Un error de verdad pesa mas que una espera: hay que corregir algo.
        if (errores.Count > 0)
            return new ResultadoHomologacionInforme
            {
                Errores = errores.Concat(esperas).ToList(),
                DocumentoEmpleado = documentoEmpleado
            };

        if (esperas.Count > 0)
            return Espera(string.Join(" | ", esperas), documentoEmpleado);

        // ---- Numero y vencimiento de la contrapartida ------------------------------
        // Ninguno viene de la API: se calculan segun el tipo de rendicion.
        var vencimiento = CalculadorDocumentoRendicion.FechaVencimiento(regla, fechaContabilizacion);

        // El correlativo se cuenta dentro de la cuenta que se va a usar: la de
        // soles y la de dolares llevan cada una su numeracion.
        var correlativo = regla.Tipo switch
        {
            TipoRendicion.Reembolso =>
                await _catalogo.SiguienteCorrelativoReembolsoAsync(
                    codigoContrapartida, codAnalisis, vencimiento, ct),
            TipoRendicion.CajaChica =>
                await _catalogo.SiguienteCorrelativoCajaChicaAsync(
                    codigoContrapartida, codAnalisis, fechaContabilizacion.Year, ct),
            _ => 1
        };

        var numeroDocumento = CalculadorDocumentoRendicion.NumeroDocumento(
            regla, fechaContabilizacion, vencimiento, correlativo);

        var resultado = new InformeHomologado
        {
            IdRindegastos = informe.Id,
            Regla = regla,
            Titulo = informe.Title!.Trim(),
            CodAnalisisEmpleado = codAnalisis,
            NombreEmpleado = nombreEmpleado,
            DocumentoEmpleado = documentoEmpleado!,
            FechaContabilizacion = fechaContabilizacion,
            CodMoneda = codMoneda!.Value,
            TipoCambio = tipoCambio,
            CuentaContrapartida = codigoContrapartida,
            CodPlanCuentaContrapartida = cuentaContrapartida!.CodPlanCuenta,
            NumeroDocumentoContrapartida = numeroDocumento,
            FechaVencimientoContrapartida = vencimiento,
            CodCentroCostoContrapartida = ccInforme,
            Gastos = lineas
        };

        // ---- Ya registrado a mano --------------------------------------------------
        // Mismo criterio que el candado de compras (folio + proveedor + tipo), con
        // lo que identifica a una rendicion: tipo, glosa, cuenta y monto.
        var existente = await _catalogo.BuscarComprobanteRendicionExistenteAsync(
            regla.TipoComprobante, resultado.GlosaCabecera, resultado.CodPlanCuentaContrapartida,
            resultado.Total, ct);

        if (existente is not null)
            return new ResultadoHomologacionInforme
            {
                Errores =
                {
                    $"Ya existe el comprobante {existente} con la misma glosa, tipo {regla.TipoComprobante}, " +
                    $"cuenta {codigoContrapartida} y monto {resultado.Total:N2}. Parece que este informe ya se " +
                    $"registro (a mano o en otro ciclo); se omite para no duplicarlo."
                },
                DocumentoEmpleado = documentoEmpleado
            };

        _logger.LogInformation(
            "Informe {Id} ({Tipo}) homologado: {N} gastos ({Pl} planillas, {Docs} documentos), total {Total:N2} " +
            "{Moneda} (tc {Tc}), cuenta {Cuenta}, documento {Doc}, vence {Venc:yyyy-MM-dd}",
            informe.Id, regla.Nombre, lineas.Count, planillas.Count, lineas.Count - planillas.Count,
            resultado.Total, iso, tipoCambio, codigoContrapartida, numeroDocumento, vencimiento);

        return new ResultadoHomologacionInforme { Informe = resultado, DocumentoEmpleado = documentoEmpleado };
    }

    /// <summary>Planilla de movilidad: una linea a la cuenta de gasto de la categoria.</summary>
    private async Task<GastoRendicion?> HomologarPlanillaAsync(
        GastoApi g, InformeApi informe, List<string> errores, CancellationToken ct)
    {
        var prefijo = $"Gasto {g.Id}";

        if (string.IsNullOrWhiteSpace(g.CategoryCode))
        {
            errores.Add($"{prefijo}: no trae CategoryCode (cuenta contable).");
            return null;
        }

        // Igual que en compras: se usa OriginalAmount, no Total. Cuando
        // Rindegastos no tiene tipo de cambio deja Total en 0.
        if (g.OriginalAmount <= 0)
        {
            errores.Add($"{prefijo}: el monto (OriginalAmount) es {g.OriginalAmount}, debe ser mayor que cero.");
            return null;
        }

        var cuenta = await _catalogo.BuscarCuentaAsync(g.CategoryCode!, ct);
        if (cuenta is null)
        {
            errores.Add($"{prefijo}: la cuenta {g.CategoryCode} (categoria '{g.Category}') " +
                        $"no existe en mae_plan_cuenta.");
            return null;
        }
        if (!cuenta.Vigente)
        {
            errores.Add($"{prefijo}: la cuenta {cuenta.CodigoCuenta} no esta vigente.");
            return null;
        }

        // Centro de costo del gasto. Se manda el Code de Rindegastos sin traducir.
        short? ccGasto = null;
        if (cuenta.RequiereCentroCosto)
        {
            var code = g.CampoExtra("Centro de Costos 1")?.Code?.Trim();
            if (!short.TryParse(code, out var cc))
                errores.Add($"{prefijo}: la cuenta {cuenta.CodigoCuenta} requiere centro de costo, " +
                            $"pero 'Centro de Costos 1' llego vacio o no numerico ('{code}').");
            else if (!await _catalogo.ExisteCentroCostoAsync(cc, ct))
                errores.Add($"{prefijo}: el centro de costo {cc} no existe en ref_centro_costo.");
            else
                ccGasto = cc;
        }

        return new GastoRendicion
        {
            IdRindegastos = g.Id,
            CodPlanCuenta = cuenta.CodPlanCuenta,
            CuentaGasto = cuenta.CodigoCuenta,
            CodCentroCosto = ccGasto,
            Monto = g.OriginalAmount,
            Glosa = GlosaDelGasto(g, informe),
            CuentaRequiereAnalisis = cuenta.RequiereAnalisis,
            CuentaRequiereTipoDocumento = cuenta.RequiereTipoDocumento,
            CuentaRequiereNumeroDocumento = cuenta.RequiereNumeroDocumento,
            CuentaRequiereFechaVencimiento = cuenta.RequiereFechaVencimiento
        };
    }

    /// <summary>
    /// Factura, boleta o RxH: busca el documento que ya se registro en compras.
    /// Si todavia no esta, el informe espera (no es error): normalmente entra en
    /// el mismo ciclo, porque el flujo de compras corre antes que este.
    /// </summary>
    private async Task<GastoRendicion?> HomologarDocumentoAsync(
        GastoApi g, InformeApi informe, byte? codMonedaInforme,
        List<string> errores, List<string> esperas, CancellationToken ct)
    {
        var folio = g.CampoExtra("Nro Documento")?.Value?.Trim();
        var tipoDocCode = g.TipoDocumentoCode;
        var ruc = g.CampoExtra("Ruc Proveedor")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(ruc)) ruc = g.SunatInfo?.Ruc?.Trim();

        var prefijo = $"Gasto {g.Id} ({g.CampoExtra("Tipo de Documento")?.Value?.Trim()} {folio})";

        // 1) Lo registro el worker: rg_gasto sabe que factura creo.
        var enCompras = await _catalogo.BuscarGastoEnComprasAsync(g.Id, ct);
        var codFactura = enCompras?.CodFactura;

        // 2) Lo registro Contabilidad a mano: misma llave que usa la pantalla.
        if (codFactura is null
            && !string.IsNullOrWhiteSpace(ruc) && !string.IsNullOrWhiteSpace(tipoDocCode)
            && !string.IsNullOrWhiteSpace(folio))
        {
            var codTipoDoc = await _catalogo.BuscarTipoDocumentoAsync(tipoDocCode!, ct);
            if (codTipoDoc is not null)
                codFactura = await _catalogo.BuscarFacturaPorRucAsync(ruc!, codTipoDoc.Value, folio!, ct);
        }

        if (codFactura is null)
        {
            var motivo = enCompras switch
            {
                null => "el flujo de compras todavia no lo descarga",
                { Estado: EstadoGasto.Error } e => $"esta en ERROR en el flujo de compras: {e.Error}",
                { Error: not null } e => $"el flujo de compras no pudo registrarlo todavia: {e.Error}",
                _ => "el flujo de compras todavia no lo registra"
            };
            esperas.Add($"{prefijo}: el documento aun no esta registrado en compras ({motivo}). " +
                        $"El informe se integra cuando lo este.");
            return null;
        }

        var doc = await _catalogo.LeerDocumentoCompraAsync(codFactura.Value, ct);
        if (doc is null)
        {
            errores.Add($"{prefijo}: el documento esta en el ERP (mfb_cod_factura_boleta {codFactura}) pero su " +
                        $"comprobante de compra no tiene la linea del proveedor, asi que no se sabe que cancelar.");
            return null;
        }

        if (codMonedaInforme is not null && doc.CodMoneda != codMonedaInforme)
        {
            errores.Add($"{prefijo}: el documento esta registrado en compras en otra moneda " +
                        $"(ref_moneda {doc.CodMoneda}) que el informe.");
            return null;
        }

        // Lo que asume la empresa en el ERP tiene que ser lo que dice Rindegastos.
        // Si alguien corrigio el monto despues de registrar el documento, se para.
        if (Math.Abs(doc.MontoEmpresa - g.OriginalAmount) > 0.01m)
        {
            errores.Add($"{prefijo}: en compras el documento esta por {doc.MontoDocumento:N2}" +
                        (doc.MontoPersonal > 0 ? $" (la empresa asume {doc.MontoEmpresa:N2})" : "") +
                        $", pero en Rindegastos el gasto es {g.OriginalAmount:N2}. Revisar antes de integrar.");
            return null;
        }

        var cancelacion = await _catalogo.BuscarCancelacionExistenteAsync(doc, ct);
        if (cancelacion is not null)
        {
            errores.Add($"{prefijo}: el documento ya se cancelo en el comprobante {cancelacion} (cargo a la " +
                        $"cuenta {doc.CuentaProveedor} del proveedor por {doc.MontoDocumento:N2}). Se omite el " +
                        $"informe para no cancelarlo dos veces.");
            return null;
        }

        return new GastoRendicion
        {
            IdRindegastos = g.Id,
            CodPlanCuenta = doc.CodPlanCuentaProveedor,
            CuentaGasto = doc.CuentaProveedor,
            Monto = doc.MontoEmpresa,
            Glosa = GlosaDelGasto(g, informe),
            Documento = doc
        };
    }

    /// <summary>La glosa de cada linea es la nota del gasto, igual que hace Contabilidad.</summary>
    private static string GlosaDelGasto(GastoApi g, InformeApi informe)
        => string.IsNullOrWhiteSpace(g.Note) ? informe.Title?.Trim() ?? "" : g.Note!.Trim();

    private static ResultadoHomologacionInforme Espera(string motivo, string? documentoEmpleado = null)
        => new() { MotivoEspera = motivo, DocumentoEmpleado = documentoEmpleado };
}
