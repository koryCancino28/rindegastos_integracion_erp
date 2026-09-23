using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Calcula el numero de documento y la fecha de vencimiento de la linea de
/// contrapartida. Ninguno de los dos viene de Rindegastos: los arma Contabilidad
/// a mano siguiendo reglas distintas segun el tipo de rendicion.
///
/// Esta clase no toca la base de datos ni la API a proposito: recibe la fecha y
/// el correlativo ya resueltos y devuelve el texto. Asi las reglas se pueden leer
/// y corregir en un solo sitio.
/// </summary>
public static class CalculadorDocumentoRendicion
{
    /// <summary>
    /// Numero de documento de la contrapartida.
    ///
    ///   Entrega a rendir  ddMMyyyy del dia en que se registra   -> 09092026
    ///   Viaticos          ddMMyyyy del dia en que se registra   -> 09092026
    ///   Reembolso         ddMMyyyy del viernes de pago + correlativo de 2 digitos
    ///                     por persona                           -> 1109202601
    ///   Caja chica        anio + correlativo de 4 digitos       -> 2026-0001
    /// </summary>
    /// <param name="regla">Tipo de rendicion.</param>
    /// <param name="fechaRegistro">Dia en que se contabiliza.</param>
    /// <param name="fechaVencimiento">Para reembolso, el viernes calculado.</param>
    /// <param name="correlativo">
    /// Reembolso: cuantos reembolsos lleva esa persona para ese mismo viernes, mas uno.
    /// Caja chica: cuantas liquidaciones lleva el anio, mas uno.
    /// </param>
    public static string NumeroDocumento(
        ReglaRendicion regla, DateTime fechaRegistro, DateTime fechaVencimiento, int correlativo)
        => regla.Tipo switch
        {
            TipoRendicion.EntregaARendir => fechaRegistro.ToString("ddMMyyyy"),
            TipoRendicion.Viaticos       => fechaRegistro.ToString("ddMMyyyy"),

            // El correlativo va pegado a la fecha: 11092026 + 01 = 1109202601.
            // Se usan 2 digitos, pero si alguna vez pasara de 99 crece solo en
            // vez de truncarse, que dejaria dos documentos con el mismo numero.
            TipoRendicion.Reembolso      => fechaVencimiento.ToString("ddMMyyyy") + Correlativo(correlativo, 2),

            // El anio da el prefijo y el correlativo reinicia cada 1 de enero.
            // Si llegara a 9999 pasa a 5 digitos, como pidio Contabilidad.
            TipoRendicion.CajaChica      => $"{fechaRegistro:yyyy}-{Correlativo(correlativo, 4)}",

            _ => throw new ArgumentOutOfRangeException(nameof(regla))
        };

    /// <summary>
    /// Fecha de vencimiento de la contrapartida.
    /// Solo el reembolso tiene regla propia; los demas usan la fecha del comprobante.
    /// </summary>
    public static DateTime FechaVencimiento(ReglaRendicion regla, DateTime fechaRegistro)
        => regla.Tipo == TipoRendicion.Reembolso
            ? ViernesDePago(fechaRegistro)
            : fechaRegistro.Date;

    /// <summary>
    /// Viernes en que se paga un reembolso registrado un dia dado.
    ///
    /// REGLA (confirmada por Contabilidad):
    ///
    ///   Lunes a jueves      se deja pasar el viernes de esa semana y se toma el
    ///                       siguiente.   07 al 10 de septiembre -> viernes 18
    ///
    ///   Viernes a domingo   se deja pasar ese viernes, el que le sigue, y se
    ///                       toma el tercero.   viernes 11 -> viernes 25
    ///
    /// OJO AL COMPARAR CON REGISTROS ANTIGUOS: el historico de 2026 no sigue esta
    /// regla en los reembolsos registrados un viernes. Los tres que hay usan +7
    /// en vez de +14 (vie 22/05 -> 29/05, vie 12/06 -> 19/06, vie 04/09 -> 11/09),
    /// mientras que los de lunes a jueves si coinciden. Contabilidad confirmo que
    /// la regla correcta es la de arriba, asi que desde la integracion los
    /// reembolsos cargados en fin de semana venceran una semana mas tarde que los
    /// que se venian ingresando a mano.
    /// </summary>
    public static DateTime ViernesDePago(DateTime fechaRegistro)
    {
        var dia = fechaRegistro.Date;

        // Viernes de la semana del registro (semana de lunes a domingo).
        var diasDesdeLunes = ((int)dia.DayOfWeek + 6) % 7;   // lunes = 0 ... domingo = 6
        var viernesDeLaSemana = dia.AddDays(4 - diasDesdeLunes);

        var esFinDeSemana = dia.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday or DayOfWeek.Sunday;
        return viernesDeLaSemana.AddDays(esFinDeSemana ? SaltoViernesFinDeSemana : SaltoViernesEntreSemana);
    }

    /// <summary>
    /// Registro de lunes a jueves: se salta el viernes de esa semana y se toma
    /// el siguiente. Miercoles 09/09 -> viernes de la semana 11/09 -> vence 18/09.
    /// </summary>
    private const int SaltoViernesEntreSemana = 7;

    /// <summary>
    /// Registro de viernes a domingo: se saltan dos viernes.
    /// Viernes 11/09 -> viernes de la semana 11/09 -> vence 25/09.
    /// </summary>
    private const int SaltoViernesFinDeSemana = 14;

    /// <summary>
    /// Formatea el correlativo con ceros a la izquierda. Si se pasa del ancho
    /// previsto no lo recorta: prefiere un numero mas largo antes que repetir uno.
    /// </summary>
    private static string Correlativo(int valor, int ancho)
    {
        if (valor < 1) valor = 1;
        return valor.ToString(new string('0', ancho));
    }
}
