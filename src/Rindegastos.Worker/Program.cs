using System.Net;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Rindegastos.Worker;
using Rindegastos.Worker.Api;
using Rindegastos.Worker.Configuracion;
using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Servicios;
using Serilog;

// -------------------------------------------------------------------- Serilog
// La carpeta log-rindegastos se arma junto al ejecutable, no en el directorio de
// trabajo. Como servicio de Windows el directorio de trabajo es C:\Windows\System32,
// y con una ruta relativa los logs terminarian ahi.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "log-rindegastos", "rindegastos-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // appsettings.json se busca junto al ejecutable, NO en el directorio desde el
    // que se lanzo el programa. Sin esto, el worker solo funciona si uno se para
    // primero en su carpeta, y como servicio de Windows nunca funcionaria: los
    // servicios arrancan con el directorio de trabajo en C:\Windows\System32.
    builder.Configuration.SetBasePath(AppContext.BaseDirectory);
    builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

    // appsettings.Local.json guarda el token y cualquier dato sensible.
    // Va junto al ejecutable, asi que funciona sin importar quien lance el
    // programa ni desde donde, a diferencia de los user-secrets, que dependen
    // del perfil de Windows. Esta en .gitignore, por eso es optional.
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    // Los secretos de usuario (dotnet user-secrets) solo se cargan solos en entorno
    // Development. Se agregan aqui de forma explicita para que tambien funcionen al
    // ejecutar desde la consola. En el servidor no existe ese archivo y se ignora.
    //
    // Orden de prioridad de la configuracion (gana el ultimo):
    //   1. appsettings.json
    //   2. user-secrets          <- desarrollo
    //   3. variables de entorno  <- servidor de produccion
    builder.Configuration.AddUserSecrets(
        System.Reflection.Assembly.GetExecutingAssembly(), optional: true);
    builder.Configuration.AddEnvironmentVariables();

    builder.Services.AddSerilog();

    // Permite instalarlo como servicio de Windows sin cambiar codigo.
    builder.Services.AddWindowsService(o => o.ServiceName = "RindegastosIntegracion");

    // ---------------------------------------------------------------- Configuracion
    builder.Services.Configure<OpcionesRindegastos>(
        builder.Configuration.GetSection(OpcionesRindegastos.Seccion));
    builder.Services.Configure<OpcionesBaseDatos>(
        builder.Configuration.GetSection(OpcionesBaseDatos.Seccion));
    builder.Services.Configure<OpcionesIntegracion>(
        builder.Configuration.GetSection(OpcionesIntegracion.Seccion));

    // ---------------------------------------------------------------- Acceso a datos
    builder.Services.AddSingleton<FabricaConexion>();
    builder.Services.AddSingleton<RepositorioLog>();
    builder.Services.AddScoped<RepositorioStaging>();
    builder.Services.AddScoped<RepositorioStagingInforme>();
    builder.Services.AddScoped<RepositorioStagingFondo>();
    builder.Services.AddScoped<RepositorioStagingSolicitud>();
    builder.Services.AddScoped<RepositorioCatalogo>();
    builder.Services.AddScoped<RepositorioComprobante>();
    builder.Services.AddScoped<RepositorioProveedor>();

    // ---------------------------------------------------------------- Servicios
    // Flujo 1: gastos con documento -> factura de compra + comprobante.
    // ServicioProveedor da de alta el proveedor si no existe (solo ACTIVO y HABIDO).
    builder.Services.AddScoped<ServicioProveedor>();
    builder.Services.AddScoped<ServicioHomologacion>();
    builder.Services.AddScoped<ServicioAsiento>();

    // Flujo 2: informes cerrados -> comprobante de ingreso.
    builder.Services.AddScoped<ServicioHomologacionInforme>();
    builder.Services.AddScoped<ServicioAsientoInforme>();
    builder.Services.AddScoped<ServicioCicloRendicion>();

    // Flujo 3: entrega de fondos -> comprobante de transferencia.
    builder.Services.AddScoped<ServicioHomologacionFondo>();
    builder.Services.AddScoped<ServicioAsientoFondo>();
    builder.Services.AddScoped<ServicioCicloFondo>();

    // Flujo 4: solicitudes de fondo aprobadas -> comprobante de transferencia.
    builder.Services.AddScoped<ServicioHomologacionSolicitud>();
    builder.Services.AddScoped<ServicioCicloSolicitud>();

    builder.Services.AddScoped<ServicioCiclo>();

    // ---------------------------------------------------------------- Cliente HTTP
    // Reintentos solo para fallos transitorios (timeout, 5xx, 429).
    // Un 400 o 401 no se reintenta: el problema no se arregla repitiendo.
    builder.Services.AddHttpClient<ClienteRindegastos>()
        .AddPolicyHandler(HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(4, intento => TimeSpan.FromSeconds(Math.Pow(2, intento)),
                onRetry: (resultado, espera, intento, _) =>
                    Log.Warning("Reintento {Intento} en {Segundos}s. Motivo: {Motivo}",
                        intento, espera.TotalSeconds,
                        resultado.Exception?.Message ?? resultado.Result?.StatusCode.ToString())));

    builder.Services.AddHostedService<WorkerIntegracion>();

    var host = builder.Build();

    ValidarConfiguracion(host.Services);

    // Modo "un solo ciclo": util para probar desde consola o desde el Programador de tareas.
    if (args.Contains("--una-vez", StringComparer.OrdinalIgnoreCase))
    {
        using var scope = host.Services.CreateScope();
        var ciclo = scope.ServiceProvider.GetRequiredService<ServicioCiclo>();
        await ciclo.EjecutarAsync(CancellationToken.None);
        return 0;
    }

    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "El worker no pudo iniciar");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Avisa temprano si falta configuracion, en vez de fallar en medio de un ciclo.</summary>
static void ValidarConfiguracion(IServiceProvider servicios)
{
    var rg = servicios.GetRequiredService<IOptions<OpcionesRindegastos>>().Value;
    var bd = servicios.GetRequiredService<IOptions<OpcionesBaseDatos>>().Value;
    var it = servicios.GetRequiredService<IOptions<OpcionesIntegracion>>().Value;

    // Se muestran las rutas reales para que el mensaje sirva para resolver el problema,
    // no solo para avisar que existe.
    var carpeta = AppContext.BaseDirectory;
    var rutaSecretos = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "UserSecrets", "rindegastos-erp-ics", "secrets.json");

    if (string.IsNullOrWhiteSpace(bd.CadenaConexion))
        throw new InvalidOperationException(
            "Falta BaseDatos:CadenaConexion." + Environment.NewLine +
            $"  Archivo esperado: {Path.Combine(carpeta, "appsettings.json")}");

    var rutaLocal = Path.Combine(carpeta, "appsettings.Local.json");

    if (string.IsNullOrWhiteSpace(rg.Token))
        throw new InvalidOperationException(
            "Falta el token de Rindegastos. Se genera en Rindegastos -> Administracion -> Integracion."
            + Environment.NewLine + Environment.NewLine +
            "  Se busco en estos cuatro lugares, en este orden:" + Environment.NewLine +
            $"    1. {Path.Combine(carpeta, "appsettings.json")}" + Environment.NewLine +
            $"    2. {rutaLocal}   <- el recomendado" + Environment.NewLine +
            $"       (existe: {File.Exists(rutaLocal)})" + Environment.NewLine +
            $"    3. {rutaSecretos}" + Environment.NewLine +
            $"       (existe: {File.Exists(rutaSecretos)})" + Environment.NewLine +
            "    4. Variable de entorno Rindegastos__Token   (doble guion bajo)" + Environment.NewLine + Environment.NewLine +
            "  Solucion mas simple: crear appsettings.Local.json junto al ejecutable con" + Environment.NewLine +
            "    { \"Rindegastos\": { \"Token\": \"el-token\" } }");

    // Deja constancia de donde salio el token. Si algun dia vuelve a fallar,
    // el log dice exactamente que fuente lo aporto.
    var origenToken =
        File.Exists(rutaLocal) && LeeTokenDe(rutaLocal) == rg.Token ? "appsettings.Local.json"
        : Environment.GetEnvironmentVariable("Rindegastos__Token") == rg.Token ? "variable de entorno"
        : File.Exists(rutaSecretos) ? "user-secrets"
        : "appsettings.json";

    Log.Information("Token cargado desde: {Origen} ({Largo} caracteres)", origenToken, rg.Token.Length);

    if (!it.SoloLectura && it.CodUsuarioErp <= 0)
        throw new InvalidOperationException(
            "Integracion:CodUsuarioErp es obligatorio cuando SoloLectura es false. " +
            "Debe ser un mae_usuario.mus_cod_usuario valido.");

    // A que base apunta y con que usuario graba: es lo primero que hay que mirar
    // al instalar en un servidor. Solo servidor y base, nunca la contrasena.
    var cadena = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(bd.CadenaConexion);
    Log.Information("Base de datos: {Servidor} / {Base}. CodUsuarioErp: {Usuario}",
        cadena.DataSource, cadena.InitialCatalog, it.CodUsuarioErp);

    Log.Information("Configuracion validada. Modo solo lectura: {Modo}", it.SoloLectura);
}

/// <summary>Lee Rindegastos:Token de un archivo JSON, para saber de donde vino.</summary>
static string? LeeTokenDe(string ruta)
{
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ruta));
        return doc.RootElement.TryGetProperty("Rindegastos", out var rg)
            && rg.TryGetProperty("Token", out var t) ? t.GetString() : null;
    }
    catch { return null; }
}
