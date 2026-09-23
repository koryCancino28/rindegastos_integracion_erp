using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Datos;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Se asegura de que el proveedor de un gasto exista en el ERP antes de
/// contabilizarlo. Si no existe, lo da de alta con los datos de SUNAT que manda
/// Rindegastos, pero SOLO si SUNAT lo reporta ACTIVO y HABIDO. En cualquier
/// otro caso no lo crea y deja el motivo, para que Contabilidad lo revise.
///
/// Datos que se usan (definidos por Contabilidad):
///   RUC y NIF          SunatInfo.Ruc
///   Nombre y razon     SunatInfo.BusinessName
///   Moneda / pais      Soles / Peru
///   Forma de pago      Efectivo
///   Tipo proveedor     Articulos y Servicios
/// </summary>
public sealed class ServicioProveedor
{
    private readonly RepositorioCatalogo _catalogo;
    private readonly RepositorioProveedor _proveedores;
    private readonly ILogger<ServicioProveedor> _logger;

    public ServicioProveedor(
        RepositorioCatalogo catalogo, RepositorioProveedor proveedores, ILogger<ServicioProveedor> logger)
    {
        _catalogo = catalogo;
        _proveedores = proveedores;
        _logger = logger;
    }

    /// <summary>
    /// Devuelve null si el proveedor ya existe o se pudo crear. Si no se puede
    /// crear, devuelve el motivo, que queda como error del gasto.
    /// </summary>
    /// <param name="soloLectura">En modo solo lectura no se crea nada, solo se avisa.</param>
    public async Task<string?> AsegurarAsync(GastoApi g, bool soloLectura, CancellationToken ct)
    {
        // Mismo orden que la homologacion: manda el campo extra, SUNAT es respaldo.
        var ruc = g.CampoExtra("Ruc Proveedor")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(ruc)) ruc = g.SunatInfo?.Ruc?.Trim();
        if (string.IsNullOrWhiteSpace(ruc)) return null;   // la homologacion ya avisa que falta el RUC

        if (await _catalogo.BuscarProveedorPorRucAsync(ruc, ct) is not null) return null;

        // ---- Validaciones antes de crear -------------------------------------------
        var sunat = g.SunatInfo;
        if (sunat is null)
            return $"El proveedor con RUC {ruc} no existe en el ERP y el gasto no trae la validacion " +
                   $"de SUNAT (SunatInfo), asi que no se puede crear automaticamente. Debe crearse a mano.";

        if (!string.Equals(sunat.Ruc?.Trim(), ruc, StringComparison.Ordinal))
            return $"El proveedor con RUC {ruc} no existe en el ERP, pero SUNAT devolvio otro RUC " +
                   $"({sunat.Ruc}). No se crea para no registrar datos de otro contribuyente. " +
                   $"Revisar el RUC en Rindegastos.";

        if (string.IsNullOrWhiteSpace(sunat.BusinessName))
            return $"El proveedor con RUC {ruc} no existe en el ERP y SUNAT no devolvio su razon social. " +
                   $"Debe crearse a mano.";

        // La regla de Contabilidad: solo se da de alta si esta ACTIVO y HABIDO.
        // Se mira primero la validacion del comprobante (DocTaxpayerStatus /
        // DocTaxpayerAddressCondition); si esos vienen vacios, el padron de
        // contribuyentes (ExtractedData: taxPayerStatus / condition).
        if (!sunat.ActivoYHabido)
        {
            var motivo = sunat.EstadoContribuyente is null && sunat.CondicionDomicilio is null
                ? $"ni la validacion del comprobante ni el padron de contribuyentes informan su estado " +
                  $"(validacion: '{sunat.DocStatusName ?? "sin resultado"}')"
                : $"segun {sunat.OrigenEstado} esta '{sunat.EstadoContribuyente ?? "sin estado"}' / " +
                  $"'{sunat.CondicionDomicilio ?? "sin condicion"}'";

            return $"El proveedor {sunat.BusinessName} (RUC {ruc}) no existe en el ERP y NO se creo " +
                   $"automaticamente: {motivo}. Solo se crean proveedores ACTIVO y HABIDO. " +
                   $"Revisar con Contabilidad antes de registrarlo.";
        }

        // Si el estado salio del padron es porque fallo la validacion del
        // comprobante. No impide el alta, pero se deja en el log para que se vea.
        if (!sunat.EstadoDeLaValidacion)
            _logger.LogWarning(
                "Gasto {Id}: el proveedor {Ruc} se valida con el padron de contribuyentes porque la " +
                "validacion del comprobante dio '{Estado}'{Obs}.",
                g.Id, ruc, sunat.DocStatusName,
                string.IsNullOrWhiteSpace(sunat.ObservacionComprobante) ? "" : $" ({sunat.ObservacionComprobante})");

        // ---- Alta ------------------------------------------------------------------
        if (soloLectura)
        {
            _logger.LogWarning(
                "MODO SOLO LECTURA: el proveedor {Ruc} {Nombre} no existe; se crearia automaticamente " +
                "(ACTIVO / HABIDO segun {Origen}).", ruc, sunat.BusinessName, sunat.OrigenEstado);
            return $"El proveedor {sunat.BusinessName} (RUC {ruc}) no existe. Se creara automaticamente " +
                   $"cuando se desactive el modo solo lectura.";
        }

        var (cod, creado) = await _proveedores.CrearAsync(ruc, sunat.BusinessName!, ct);
        if (creado)
            _logger.LogInformation(
                "Gasto {Id}: se dio de alta el proveedor {Nombre} (RUC {Ruc}) con codigo {Cod}.",
                g.Id, sunat.BusinessName, ruc, cod);

        return null;
    }
}
