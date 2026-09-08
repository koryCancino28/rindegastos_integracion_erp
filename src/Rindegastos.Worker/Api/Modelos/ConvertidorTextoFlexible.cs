using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rindegastos.Worker.Api.Modelos;

/// <summary>
/// Lee un campo JSON como texto aunque llegue como numero.
/// Rindegastos devuelve algunos Id como 464242685 y otros como "464242685".
/// </summary>
public sealed class ConvertidorTextoFlexible : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader lector, Type tipo, JsonSerializerOptions opciones)
        => lector.TokenType switch
        {
            JsonTokenType.Null   => null,
            JsonTokenType.String => lector.GetString(),
            JsonTokenType.Number => lector.TryGetInt64(out var n) ? n.ToString() : lector.GetDouble().ToString(),
            JsonTokenType.True   => "true",
            JsonTokenType.False  => "false",
            _ => null
        };

    public override void Write(Utf8JsonWriter escritor, string? valor, JsonSerializerOptions opciones)
    {
        if (valor is null) escritor.WriteNullValue();
        else escritor.WriteStringValue(valor);
    }
}
