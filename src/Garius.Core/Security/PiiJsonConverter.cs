using System.Text.Json;
using System.Text.Json.Serialization;

namespace Garius.Core.Security;

/// <summary>
/// Serializa <see cref="Pii"/> <b>sempre mascarado</b>, e proíbe deserializar.
///
/// <para>
/// <b>Saída:</b> se um <see cref="Pii"/> acabar num DTO de resposta por descuido, o JSON sai
/// <c>"j***@empresa.com"</c> — não o e-mail. Não existe caminho em que serializar vaze o
/// dado em claro. Expor PII exige um passo explícito: chamar <c>Reveal()</c> (com
/// autorização e auditoria) e pôr o resultado numa <c>string</c> comum do DTO.
/// </para>
///
/// <para>
/// <b>Entrada:</b> deserializar é proibido de propósito. PII entra como <c>string</c> num DTO
/// de request e só vira <see cref="Pii"/> dentro do domínio, onde o escopo
/// (<see cref="PiiScope"/>) é conhecido. Deserializar direto exigiria adivinhar o escopo —
/// e um escopo errado produz a máscara errada, que é como se vaza um CPF achando que se
/// mascarou um e-mail.
/// </para>
/// </summary>
internal sealed class PiiJsonConverter : JsonConverter<Pii>
{
    public override Pii Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "Não deserialize Pii diretamente. Receba o valor como string no DTO de request e " +
            "converta com Pii.Create(PiiScope.X, valor) no domínio, onde o escopo é conhecido.");

    public override void Write(Utf8JsonWriter writer, Pii value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.Masked);
    }
}
