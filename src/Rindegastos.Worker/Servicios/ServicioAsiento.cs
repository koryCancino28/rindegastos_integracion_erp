using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Arma las lineas del comprobante contable segun el tipo de documento.
///
/// Los tres patrones fueron verificados contra comprobantes reales del ERP
/// (bd_epysa_peru), ingresados manualmente desde la pantalla:
///
///   FACTURA  (comprobante 2605751, total 161.07)
///     4212030  analisis=proveedor  tipoDoc=1   numDoc=folio   abono 161.07
///     4011020  analisis=1          tipoDoc=1   numDoc=folio   cargo  24.57  (IGV)
///     6291020  analisis=1          tipoDoc=27  numDoc=0  cc=29  cargo 136.50 (neto)
///
///   BOLETA   (comprobante 2605348, total 348.00)
///     4212030  analisis=proveedor  tipoDoc=14  numDoc=folio   abono 348.00
///     6251110  analisis=1          tipoDoc=27  numDoc=0  cc=247 cargo 348.00
///
///   RECIBO POR HONORARIOS (comprobante 2605935, total 180.00)
///     4240010  analisis=proveedor  tipoDoc=15  numDoc=folio   abono 180.00
///     6399010  analisis=1          tipoDoc=27  numDoc=0  cc=310 cargo 180.00
///
/// Detalle importante: en la linea de gasto el ERP graba analisis = 1,
/// tipo de documento = 27 y numero de documento = 0, porque esas cuentas
/// tienen mpc_requiere_analisis = 0 y mpc_requiere_tipo_documento = 0,
/// lo que deja esos campos deshabilitados en la pantalla.
/// </summary>
public sealed class ServicioAsiento
{
    private readonly RepositorioCatalogo _catalogo;

    public ServicioAsiento(RepositorioCatalogo catalogo) => _catalogo = catalogo;

    /// <summary>Marca que se usa para indicar "el documento de origen es la factura recien creada".</summary>
    private const int OrigenFacturaNueva = -1;

    public async Task<List<LineaDetalle>> ConstruirAsync(GastoHomologado g, CancellationToken ct)
    {
        var lineas = new List<LineaDetalle>();

        // ---- Linea 1: cuenta por pagar al proveedor (abono por el total) -----------
        // La cuenta depende del tipo de documento y de la moneda: cada una tiene su
        // version en moneda nacional (MN) y en moneda extranjera (ME).
        var esSoles = g.CodMoneda == ConstantesErp.MonedaSoles;

        var codigoCuentaProveedor = g.EsReciboHonorarios
            ? (esSoles ? ConstantesErp.CuentaHonorariosMN : ConstantesErp.CuentaHonorariosME)
            : (esSoles ? ConstantesErp.CuentaProveedorMN : ConstantesErp.CuentaProveedorME);

        var cuentaProveedor = await _catalogo.BuscarCuentaAsync(codigoCuentaProveedor, ct)
            ?? throw new InvalidOperationException(
                $"La cuenta {codigoCuentaProveedor} no existe en mae_plan_cuenta.");

        lineas.Add(new LineaDetalle
        {
            CodPlanCuenta = cuentaProveedor.CodPlanCuenta,
            CodAnalisis = g.CodAnalisis,                  // esta cuenta si requiere analisis
            CodCentroCosto = null,
            CodTipoDocumento = g.CodTipoDocumento,        // el mismo del encabezado
            NumeroDocumento = g.Folio,
            FechaVencimiento = g.FechaVencimiento,
            CodDocumentoOrigen = OrigenFacturaNueva,
            Cargo = 0m,
            Abono = g.MontoTotal,
            CargoSoles = 0m,
            AbonoSoles = ASoles(g.MontoTotal, g),
            CodMoneda = g.CodMoneda,
            TipoCambio = g.TipoCambio,
            Glosa = g.GlosaCabecera
        });

        // ---- Linea 2: IGV credito fiscal ------------------------------------------
        // La pantalla la genera sola para los tipos de TiposConLineaIgv, AUNQUE EL
        // IGV SEA CERO. En el comprobante no domiciliado 2606151 queda una linea de
        // 4011020 con 0.00. Boleta y recibo por honorarios no la llevan.
        if (g.LlevaLineaIgv)
        {
            var cuentaIgv = await _catalogo.BuscarCuentaAsync(ConstantesErp.CuentaIgv, ct)
                ?? throw new InvalidOperationException(
                    $"La cuenta {ConstantesErp.CuentaIgv} no existe en mae_plan_cuenta.");

            lineas.Add(new LineaDetalle
            {
                CodPlanCuenta = cuentaIgv.CodPlanCuenta,
                CodAnalisis = ConstantesErp.AnalisisGenerico,
                CodCentroCosto = null,
                CodTipoDocumento = g.CodTipoDocumento,
                NumeroDocumento = g.Folio,
                FechaVencimiento = g.FechaVencimiento,
                CodDocumentoOrigen = null,
                Cargo = g.MontoIgv,
                Abono = 0m,
                CargoSoles = ASoles(g.MontoIgv, g),
                AbonoSoles = 0m,
                CodMoneda = g.CodMoneda,
                TipoCambio = g.TipoCambio,
                Glosa = g.GlosaCabecera
            });
        }

        // ---- Linea 3: el gasto propiamente dicho -----------------------------------
        // Factura: el cargo es el neto (el IGV ya fue a su propia cuenta).
        // Boleta y Recibo por Honorarios: el cargo es lo que asume la empresa.
        //
        // En un gasto normal MontoEmpresa es igual al total, asi que esto no
        // cambia nada. En uno parcial deja fuera la parte del trabajador, que se
        // va a su propia linea mas abajo.
        var montoGasto = g.EsFactura ? g.MontoNeto : g.MontoEmpresa;

        var cuentaGasto = await _catalogo.BuscarCuentaAsync(g.CuentaGasto, ct)
            ?? throw new InvalidOperationException(
                $"La cuenta de gasto {g.CuentaGasto} no existe en mae_plan_cuenta.");

        // Analisis, tipo de documento y numero se llenan SOLO si la cuenta los pide.
        // Es lo mismo que hace CargaCuenta() en wctrFacturaCompra2.ascx.vb: si la
        // bandera esta en 0 el campo queda deshabilitado en la pantalla y el ERP
        // graba el valor por defecto (analisis 1, tipo documento 27, numero 0).
        lineas.Add(new LineaDetalle
        {
            CodPlanCuenta = g.CodPlanCuentaGasto,
            CodAnalisis = cuentaGasto.RequiereAnalisis
                ? g.CodAnalisis
                : ConstantesErp.AnalisisGenerico,
            CodCentroCosto = g.CodCentroCosto,
            CodTipoDocumento = cuentaGasto.RequiereTipoDocumento
                ? g.CodTipoDocumento
                : ConstantesErp.TipoDocSinDocumento,
            NumeroDocumento = cuentaGasto.RequiereNumeroDocumento ? g.Folio : "0",
            FechaVencimiento = cuentaGasto.RequiereFechaVencimiento ? g.FechaVencimiento : null,
            // Misma regla que btnGrabaDetalle (lineas 2013-2018): si la cuenta pide
            // tipo de documento, la linea queda enlazada a la factura que se esta
            // ingresando. Las cuentas de gasto de hoy no lo piden, asi que queda vacio.
            CodDocumentoOrigen = cuentaGasto.RequiereTipoDocumento ? OrigenFacturaNueva : null,
            Cargo = montoGasto,
            Abono = 0m,
            CargoSoles = ASoles(montoGasto, g),
            AbonoSoles = 0m,
            CodMoneda = g.CodMoneda,
            TipoCambio = g.TipoCambio,
            Glosa = g.Glosa
        });

        // ---- Linea 4 (solo gasto parcial): lo que se le cobra al trabajador --------
        // Cuando la empresa asume solo una parte del documento, el resto queda
        // como cuenta por cobrar al personal. Esta linea la agrega el contador a
        // mano desde la pantalla: no la genera el ERP sola.
        //
        // Verificado en la factura E001-3527 (comprobante 2606127):
        //   4212030  abono 36.00   total del documento
        //   4011020  cargo  4.58   IGV sobre lo que asume la empresa
        //   6251010  cargo 25.42   el gasto
        //   1419010  cargo  6.00   al trabajador   <-- esta linea
        if (g.EsParcial)
        {
            var cuentaPersonal = await _catalogo.BuscarCuentaAsync(
                ConstantesErp.CuentaPorCobrarPersonal, ct)
                ?? throw new InvalidOperationException(
                    $"La cuenta {ConstantesErp.CuentaPorCobrarPersonal} no existe en mae_plan_cuenta.");

            lineas.Add(new LineaDetalle
            {
                CodPlanCuenta = cuentaPersonal.CodPlanCuenta,
                // Esta cuenta si requiere analisis y tipo de documento, y lleva
                // los mismos del encabezado. No lleva centro de costo.
                CodAnalisis = g.CodAnalisis,
                CodCentroCosto = null,
                CodTipoDocumento = g.CodTipoDocumento,
                NumeroDocumento = g.Folio,
                FechaVencimiento = g.FechaVencimiento,
                // Apunta a la factura, igual que la linea del proveedor. Asi lo deja
                // la pantalla al agregarla con "Graba Detalle", porque la cuenta pide
                // tipo de documento. Verificado: 427 de 427 lineas de 1419010 en
                // compras de 2026 apuntan a su propia factura.
                CodDocumentoOrigen = OrigenFacturaNueva,
                Cargo = g.MontoPersonal,
                Abono = 0m,
                CargoSoles = ASoles(g.MontoPersonal, g),
                AbonoSoles = 0m,
                CodMoneda = g.CodMoneda,
                TipoCambio = g.TipoCambio,
                Glosa = g.GlosaCabecera
            });
        }

        return lineas;
    }

    /// <summary>
    /// Convierte a soles. En moneda nacional el monto no cambia; en moneda
    /// extranjera se multiplica por el tipo de cambio, igual que hace el ERP.
    /// </summary>
    private static decimal ASoles(decimal monto, GastoHomologado g)
        => g.CodMoneda == ConstantesErp.MonedaSoles
            ? monto
            : Math.Round(monto * g.TipoCambio, 2, MidpointRounding.AwayFromZero);
}
