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

    /// <summary>Maximo de reintentos de contabilizacion antes de dejar el gasto en estado ERROR.</summary>
    public int MaxIntentos { get; set; } = 3;
}
