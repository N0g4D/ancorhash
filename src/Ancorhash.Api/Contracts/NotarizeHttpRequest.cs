using System.Text.Json.Serialization;

namespace Ancorhash.Api.Contracts;

/// <summary>
/// Payload di POST /api/v1/notarize. Zero Data Leakage: nessun URL o contenuto
/// del documento, solo il suo digest calcolato lato client.
/// Chiavi snake_case per compatibilità con i flussi Power Automate.
/// </summary>
public sealed record NotarizeHttpRequest
{
    [JsonPropertyName("document_id")]
    public string? DocumentId { get; init; }

    /// <summary>
    /// Digest di 32 byte: 64 caratteri esadecimali (con o senza 0x, maiuscoli o
    /// minuscoli) oppure Base64, come lo restituisce Power Automate Desktop.
    /// </summary>
    [JsonPropertyName("document_hash")]
    public string? DocumentHash { get; init; }

    /// <summary>Chain ID EIP-155 della rete EVM di destinazione (obbligatorio, es. 11155111).</summary>
    [JsonPropertyName("chain_id")]
    public int? ChainId { get; init; }

    /// <summary>Token Cloudflare Turnstile emesso dal widget (obbligatorio, verificato server-side).</summary>
    [JsonPropertyName("turnstile_token")]
    public string? TurnstileToken { get; init; }
}

/// <summary>
/// Payload di POST /api/v1/notarize/batch: da 1 a 100 documenti, ancorati con
/// una sola transazione.
/// </summary>
public sealed record NotarizeBatchHttpRequest
{
    [JsonPropertyName("items")]
    public IReadOnlyList<NotarizeBatchHttpItem?>? Items { get; init; }

    /// <summary>Chain ID EIP-155 della rete EVM di destinazione (obbligatorio, es. 11155111).</summary>
    [JsonPropertyName("chain_id")]
    public int? ChainId { get; init; }

    /// <summary>Token Cloudflare Turnstile emesso dal widget (obbligatorio, verificato server-side).</summary>
    [JsonPropertyName("turnstile_token")]
    public string? TurnstileToken { get; init; }
}

/// <summary>Un documento di POST /api/v1/notarize/batch.</summary>
public sealed record NotarizeBatchHttpItem
{
    [JsonPropertyName("document_id")]
    public string? DocumentId { get; init; }

    /// <summary>Digest di 32 byte, in esadecimale o Base64 come in <see cref="NotarizeHttpRequest.DocumentHash"/>.</summary>
    [JsonPropertyName("document_hash")]
    public string? DocumentHash { get; init; }
}
