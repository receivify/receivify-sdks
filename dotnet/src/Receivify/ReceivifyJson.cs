using System.Text.Json;
using System.Text.Json.Serialization;

namespace Receivify;

/// <summary>
/// The one <see cref="JsonSerializerOptions"/> instance the SDK serialises and deserialises with.
/// </summary>
/// <remarks>
/// Unknown response fields are ignored, which is exactly what the API's additive-change policy
/// requires: a deployment newer than your SDK can add fields without breaking you. When you need
/// the bytes anyway, every call can be made through
/// <see cref="ReceivifyClient.SendAsync{T}"/>, whose <see cref="ReceivifyResponse{T}.RawBody"/>
/// carries the untouched payload.
/// </remarks>
public static class ReceivifyJson
{
    /// <summary>The shared, thread-safe options instance.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Build a fresh copy of the SDK's serializer configuration.</summary>
    public static JsonSerializerOptions Create()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            WriteIndented = false,
        };
    }
}

/// <summary>
/// Reads a money field without ever routing it through a floating-point type.
/// </summary>
/// <remarks>
/// <para>
/// Every monetary quantity on the Receivify wire is a <c>string</c> — exact-integer base units
/// (satoshis, wei, ...) for amounts, and a decimal string for the handful of display prices.
/// This converter therefore surfaces money as <see cref="string"/> and nothing else.
/// </para>
/// <para>
/// If a JSON <em>number</em> ever turns up in a money position, the raw token text is captured
/// verbatim rather than parsed. A <see cref="double"/> cannot hold a 256-bit wei value, and a
/// <see cref="decimal"/> would invite arithmetic this SDK deliberately does not want to imply.
/// Use <see cref="Money"/> for exact arithmetic on the string.
/// </para>
/// </remarks>
public sealed class MoneyStringConverter : JsonConverter<string?>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
            {
                // GetRawText() is the exact digits the server sent. No double, no decimal, no loss.
                using JsonDocument document = JsonDocument.ParseValue(ref reader);
                return document.RootElement.GetRawText();
            }
            default:
                throw new JsonException(
                    "Expected a money value as a JSON string, got " + reader.TokenType + ".");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

internal static class SdkVersion
{
    public const string Value = "1.0.0";

    public static string UserAgent { get; } = "receivify-dotnet/" + Value;
}
