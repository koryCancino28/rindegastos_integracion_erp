namespace Rindegastos.Worker.Configuracion;

/// <summary>Datos de conexion a la API de Rindegastos.</summary>
public sealed class OpcionesRindegastos
{
    public const string Seccion = "Rindegastos";

    /// <summary>URL base. Por defecto https://api.rindegastos.com/v2</summary>
    public string UrlBase { get; set; } = "https://api.rindegastos.com/v2";

    /// <summary>Token que se genera en Rindegastos: Administracion -> Integracion.</summary>
    public string Token { get; set; } = "";

    /// <summary>Segundos de espera por llamada.</summary>
    public int TimeoutSegundos { get; set; } = 60;

    /// <summary>Resultados por pagina. La API acepta maximo 100.</summary>
    public int ResultadosPorPagina { get; set; } = 100;
}

/// <summary>Conexion a la base de datos del ERP.</summary>
public sealed class OpcionesBaseDatos
{
    public const string Seccion = "BaseDatos";

    public string CadenaConexion { get; set; } = "";
}

/// <summary>Parametros de negocio de la integracion.</summary>
public sealed class OpcionesIntegracion
{
    public const string Seccion = "Integracion";

    /// <summary>
    /// MODO SEGURO. Si es true el worker solo descarga y homologa:
    /// NO escribe nada en las tablas contables del ERP.
    /// Dejarlo en true durante la fase de validacion.
    /// </summary>
    public bool SoloLectura { get; set; } = true;

    /// <summary>Minutos entre ciclos.</summary>
    public int IntervaloMinutos { get; set; } = 15;

    /// <summary>Usuario del ERP con el que se graban los documentos (mae_usuario.mus_cod_usuario).</summary>
    public int CodUsuarioErp { get; set; }

    /// <summary>Solo se descargan gastos emitidos desde esta fecha (formato yyyy-MM-dd).</summary>
    public string? FechaDesde { get; set; }

    /// <summary>
    /// Estado del gasto que se descarga. 1 = Aprobado.
    /// Dejar en null para traer todos (util solo en pruebas).
    /// </summary>
    public int? StatusGasto { get; set; } = 1;

    /// <summary>Si es true, ademas del gasto se marca el informe completo como integrado.</summary>
    public bool MarcarInformeCompleto { get; set; } = false;

    /// <summary>
    /// Activa el segundo flujo: los informes cerrados (entrega a rendir,
    /// viaticos, reembolso y caja chica), que entran por el modulo Ingreso de
    /// Comprobante. Incluye las planillas de movilidad y la cancelacion de las
    /// facturas, boletas y RxH que ya entraron por Compras.
    /// Se puede apagar sin tocar el primer flujo.
    /// </summary>
    public bool ProcesarRendiciones { get; set; } = true;

    /// <summary>
    /// Activa el tercer flujo: la entrega de fondos (cajas chicas). Cada deposito
    /// de un fondo se registra como comprobante de transferencia (tipo 19).
    /// Solo entran los fondos que traen la cuenta contable en 'Descripcion' y el
    /// documento de quien lo recibe en 'Codigo'.
    /// </summary>
    public bool ProcesarFondos { get; set; } = true;

    /// <summary>
    /// Activa el cuarto flujo: las solicitudes de fondo aprobadas (viaticos y
    /// entregas a rendir pedidos por adelantado). Se registran como comprobante
    /// de transferencia (tipo 19) contra entregas a rendir.
    /// </summary>
    public bool ProcesarSolicitudesFondo { get; set; } = true;

    /// <summary>
    /// Estado del INFORME que se descarga en el flujo de rendiciones.
    ///
    /// Cuidado: en informes este campo no significa lo mismo que en gastos.
    ///   0 = Abierto o En proceso (falta algun aprobador)
    ///   1 = Cerrado (aprobado por todo el flujo)
    ///   null = los dos
    ///
    /// Va en 1 porque Contabilidad definio que el informe se integra cuando
    /// termina de aprobarse. Aunque se ponga null, la homologacion igual deja
    /// esperando cualquier informe que no este cerrado.
    /// </summary>
    public int? StatusInforme { get; set; } = 1;

    /// <summary>Maximo de reintentos de contabilizacion antes de dejar el gasto en estado ERROR.</summary>
    public int MaxIntentos { get; set; } = 3;
}
