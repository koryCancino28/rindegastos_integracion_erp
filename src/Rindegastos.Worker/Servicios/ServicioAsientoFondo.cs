using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Arma las dos lineas de la entrega de un fondo. El dinero sale del banco de
/// caja chica y entra a la cuenta del fondo, a nombre de quien lo recibe.
///
/// Verificado contra el comprobante 2606159 que Contabilidad ingreso a mano por
/// el fondo 926276 "CAJA CHICA CUSCO" (1000.00 el 11/09/2026):
///
///   tipo 19, folio 3, glosa "TR-3 KORY CANCINO - CAJA CHICA CUSCO"
///     1020152  analisis=149671 (DNI 76775158)  td=26  num=11092026  cargo 1000.00
///     1041060  analisis=765 (BCP)              td=55  num=TR-3      abono 1000.00
///
/// La glosa y el numero de la linea del banco llevan el folio, que el ERP asigna
/// al grabar. Por eso se recibe como parametro: el folio se calcula primero,
/// dentro de la misma transaccion, y recien despues se arman las lineas.
/// </summary>
public sealed class ServicioAsientoFondo
{
    public (string Glosa, List<LineaDetalle> Lineas) Construir(FondoHomologado f, int folio)
    {
        var glosa = ConstantesFondo.Glosa(folio, f.NombreCorto, f.Titulo);

        var lineas = new List<LineaDetalle>
        {
            // Entra a la cuenta del fondo, a nombre de quien lo recibe.
            new()
            {
                CodPlanCuenta = f.CodPlanCuentaFondo,
                CodAnalisis = f.CodAnalisisEmpleado,
                CodTipoDocumento = ConstantesFondo.TipoDocOtros,
                NumeroDocumento = f.NumeroDocumentoFondo,
                FechaVencimiento = f.FechaDeposito,
                Cargo = f.Monto,
                CargoSoles = f.MontoSoles,
                CodMoneda = f.CodMoneda,
                TipoCambio = f.TipoCambio,
                Glosa = glosa
            },
            // Sale del banco de caja chica.
            new()
            {
                CodPlanCuenta = f.CodPlanCuentaBanco,
                CodAnalisis = f.CodAnalisisBanco,
                CodTipoDocumento = ConstantesFondo.TipoDocTransferencia,
                NumeroDocumento = ConstantesFondo.NumeroTransferencia(folio),
                FechaVencimiento = f.FechaDeposito,
                Abono = f.Monto,
                AbonoSoles = f.MontoSoles,
                CodMoneda = f.CodMoneda,
                TipoCambio = f.TipoCambio,
                Glosa = glosa
            }
        };

        return (glosa, lineas);
    }
}
