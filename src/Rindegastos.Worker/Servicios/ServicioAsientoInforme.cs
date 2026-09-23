using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Arma las lineas del comprobante de una rendicion.
///
/// La estructura es siempre la misma, cambie el tipo de rendicion o no:
///
///   1..N  una linea por gasto        cargo por el monto del gasto
///   N+1   la contrapartida           abono por el total del informe
///
/// Verificado contra los comprobantes que Contabilidad ingreso a mano:
///
///   ENTREGA A RENDIR (2606122, total 150.00)
///     6311015  analisis=1       cc=250  td=27  numdoc=0          cargo 150.00
///     1413110  analisis=129863  cc=250  td=26  numdoc=04092026   abono 150.00
///
///   VIATICOS (2606123, total 200.00)
///     6311015  analisis=1       cc=288  td=27  numdoc=0          cargo 200.00
///     1413110  analisis=129863  cc=288  td=26  numdoc=TR-130     abono 200.00
///
///   REEMBOLSO (2606124, total 100.00)
///     6311015  analisis=1       cc=288  td=27  numdoc=0          cargo 100.00
///     4699210  analisis=129863  cc=288  td=26  numdoc=1109202601 abono 100.00  vence 11/09
///
///   CAJA CHICA (2606125, total 120.00)
///     6311015  analisis=1       cc=250  td=27  numdoc=0          cargo 120.00
///     4690210  analisis=129863  cc=250  td=26  numdoc=2026-0001  abono 120.00
///
/// En la linea del gasto el ERP graba analisis 1, tipo de documento 27 y numero
/// "0" porque la cuenta 6311015 tiene mpc_requiere_analisis = 0 y
/// mpc_requiere_tipo_documento = 0, lo que deja esos campos deshabilitados en la
/// pantalla. Aqui se aplica la misma regla leyendo las banderas de la cuenta,
/// para que siga funcionando si manana usan otra cuenta de gasto.
///
/// Cuando el gasto es una factura, boleta o RxH, el documento ya se registro en
/// compras y el informe lo CANCELA: cargo a la cuenta del proveedor por el total
/// del documento y, si es parcial, abono a 1419010 por lo que paga el trabajador.
///
///   DOCUMENTOS TOTALES (2606175, entrega a rendir, total 1218.33)
///     4212030  analisis=proveedor  td=1   numdoc=E001-2151  cargo  413.33  glosa=nota
///     4212030  analisis=proveedor  td=14  numdoc=0003-362   cargo   55.00  glosa=nota
///     4240010  analisis=proveedor  td=15  numdoc=E001-16    cargo  750.00  glosa=nota
///     1413110  analisis=empleado   td=26  numdoc=15092026   abono 1218.33  glosa=titulo
///
///   FACTURA PARCIAL (2606128, empresa 30.00, trabajador 6.00)
///     4212030  analisis=proveedor  td=1   numdoc=E001-3527  cargo   36.00  glosa=nota
///     1419010  analisis=proveedor  td=1   numdoc=E001-3527  abono    6.00  glosa=titulo
///     1413110  analisis=empleado   td=26                    abono   30.00  glosa=titulo
///
/// En esas lineas el centro de costo va vacio, no hay documento de origen y el
/// vencimiento es la fecha del comprobante. Igual en 2606130 (boleta parcial),
/// 2606166 (RxH parcial) y 2606152 (factura en dolares, cuenta 4212040).
/// </summary>
public sealed class ServicioAsientoInforme
{
    public List<LineaDetalle> Construir(InformeHomologado inf)
    {
        var lineas = new List<LineaDetalle>();

        // ---- Una linea por cada gasto de la rendicion ------------------------------
        foreach (var g in inf.Gastos)
        {
            if (g.Documento is not null)
            {
                AgregarDocumento(lineas, g, g.Documento, inf);
                continue;
            }

            lineas.Add(new LineaDetalle
            {
                CodPlanCuenta = g.CodPlanCuenta,
                CodAnalisis = g.CuentaRequiereAnalisis
                    ? inf.CodAnalisisEmpleado
                    : ConstantesErp.AnalisisGenerico,
                CodCentroCosto = g.CodCentroCosto,
                CodTipoDocumento = g.CuentaRequiereTipoDocumento
                    ? ReglaRendicion.TipoDocOtros
                    : ConstantesErp.TipoDocSinDocumento,
                NumeroDocumento = g.CuentaRequiereNumeroDocumento
                    ? inf.NumeroDocumentoContrapartida
                    : "0",
                FechaVencimiento = g.CuentaRequiereFechaVencimiento
                    ? inf.FechaContabilizacion
                    : null,
                // En este modulo ninguna linea apunta a un documento de origen:
                // no hay factura detras de la rendicion.
                CodDocumentoOrigen = null,
                Cargo = g.Monto,
                Abono = 0m,
                CargoSoles = ASoles(g.Monto, inf),
                AbonoSoles = 0m,
                CodMoneda = inf.CodMoneda,
                TipoCambio = inf.TipoCambio,
                Glosa = g.Glosa
            });
        }

        // ---- La contrapartida: una sola linea por el total del informe -------------
        // En soles el abono es lo que falta para cuadrar (cargos menos los abonos
        // a 1419010, ya convertidos), no el total multiplicado por el tipo de
        // cambio: asi el comprobante cuadra al centimo aunque cada linea se haya
        // redondeado por separado. Es lo que muestra el comprobante real 2598232
        // en dolares: 151.01 + 236.99 = 388.00, mientras que 114.49 x 3.389
        // habria dado 388.01.
        var abonoSoles = lineas.Sum(l => l.CargoSoles) - lineas.Sum(l => l.AbonoSoles);

        lineas.Add(new LineaDetalle
        {
            CodPlanCuenta = inf.CodPlanCuentaContrapartida,
            CodAnalisis = inf.CodAnalisisEmpleado,
            CodCentroCosto = inf.CodCentroCostoContrapartida,
            CodTipoDocumento = ReglaRendicion.TipoDocOtros,
            NumeroDocumento = inf.NumeroDocumentoContrapartida,
            FechaVencimiento = inf.FechaVencimientoContrapartida,
            CodDocumentoOrigen = null,
            Cargo = 0m,
            Abono = inf.Total,
            CargoSoles = 0m,
            AbonoSoles = abonoSoles,
            CodMoneda = inf.CodMoneda,
            TipoCambio = inf.TipoCambio,
            // La contrapartida lleva el titulo del informe, no la nota del gasto.
            Glosa = inf.Titulo
        });

        return lineas;
    }

    /// <summary>
    /// Cancela un documento ya registrado en compras. Cuenta, analisis, tipo y
    /// numero de documento se copian de la linea del proveedor en compras.
    /// </summary>
    private static void AgregarDocumento(
        List<LineaDetalle> lineas, GastoRendicion g, DocumentoCompra doc, InformeHomologado inf)
    {
        // Cargo al proveedor por el TOTAL del documento, aunque sea parcial:
        // en compras se le abono el total, y aqui se deja en cero.
        lineas.Add(new LineaDetalle
        {
            CodPlanCuenta = doc.CodPlanCuentaProveedor,
            CodAnalisis = doc.CodAnalisisProveedor,
            CodCentroCosto = null,
            CodTipoDocumento = doc.CodTipoDocumento,
            NumeroDocumento = doc.NumeroDocumento,
            FechaVencimiento = inf.FechaContabilizacion,
            CodDocumentoOrigen = null,
            Cargo = doc.MontoDocumento,
            Abono = 0m,
            CargoSoles = ASoles(doc.MontoDocumento, inf),
            AbonoSoles = 0m,
            CodMoneda = inf.CodMoneda,
            TipoCambio = inf.TipoCambio,
            Glosa = g.Glosa
        });

        // Gasto parcial: la parte del trabajador no se le devuelve. Se abona a
        // 1419010 para cerrar lo que en compras se le cargo al registrar el documento.
        if (doc.MontoPersonal > 0 && doc.CodPlanCuentaPersonal is not null)
        {
            lineas.Add(new LineaDetalle
            {
                CodPlanCuenta = doc.CodPlanCuentaPersonal.Value,
                CodAnalisis = doc.CodAnalisisProveedor,
                CodCentroCosto = null,
                CodTipoDocumento = doc.CodTipoDocumento,
                NumeroDocumento = doc.NumeroDocumento,
                FechaVencimiento = inf.FechaContabilizacion,
                CodDocumentoOrigen = null,
                Cargo = 0m,
                Abono = doc.MontoPersonal,
                CargoSoles = 0m,
                AbonoSoles = ASoles(doc.MontoPersonal, inf),
                CodMoneda = inf.CodMoneda,
                TipoCambio = inf.TipoCambio,
                // Como en 2606128, 2606130 y 2606166: el titulo del informe.
                Glosa = inf.Titulo
            });
        }
    }

    /// <summary>
    /// Convierte a soles como la pantalla: tipo de cambio x monto (lineas 1245 y
    /// 1250 de wctrComprobanteContable.ascx.vb), redondeado a 2 decimales que es
    /// lo que guarda mdco_cargo_soles. En soles el tipo de cambio es 1.
    /// </summary>
    private static decimal ASoles(decimal monto, InformeHomologado inf)
        => inf.CodMoneda == ConstantesErp.MonedaSoles
            ? monto
            : Math.Round(monto * inf.TipoCambio, 2, MidpointRounding.AwayFromZero);
}
