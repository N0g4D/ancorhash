using System.Globalization;
using System.Text.Json.Serialization;
using Ancorhash.Core.Models;

namespace Ancorhash.Api.Contracts;

/// <summary>
/// Risposta di POST /api/v1/notarize, e forma di ogni elemento della risposta
/// batch. È una ricevuta autosufficiente: con digest, rete, registry e anchorer
/// chiunque può verificarla leggendo anchoredAt(digest, anchorer) sul registry,
/// senza fidarsi di Ancorhash.
/// </summary>
public sealed record NotarizeHttpResponse
{
    public const string StatusSuccess = "success";
    public const string StatusPending = "pending";

    /// <summary>
    /// "success" se il record è on-chain (ancorato ora o già prima), "pending" se
    /// la transazione è partita ma la receipt non è arrivata in tempo.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("document_id")]
    public required string DocumentId { get; init; }

    /// <summary>Digest in forma canonica: esadecimale minuscolo con prefisso 0x.</summary>
    [JsonPropertyName("digest")]
    public required string Digest { get; init; }

    [JsonPropertyName("chain_id")]
    public required int ChainId { get; init; }

    /// <summary>Indirizzo di AncorhashRegistry sulla rete.</summary>
    [JsonPropertyName("registry")]
    public required string Registry { get; init; }

    /// <summary>Indirizzo a cui è intestato il record.</summary>
    [JsonPropertyName("anchorer")]
    public required string Anchorer { get; init; }

    /// <summary>Orario del primo ancoraggio, ISO 8601 in UTC. Null finché la transazione è pending.</summary>
    [JsonPropertyName("anchored_at")]
    public required string? AnchoredAt { get; init; }

    /// <summary>Lo stesso orario in secondi Unix, come lo restituisce anchoredAt sul registry.</summary>
    [JsonPropertyName("anchored_at_unix")]
    public required long? AnchoredAtUnix { get; init; }

    /// <summary>Transazione dell'ancoraggio. Null se il digest era già ancorato.</summary>
    [JsonPropertyName("tx_hash")]
    public required string? TxHash { get; init; }

    [JsonPropertyName("already_anchored")]
    public required bool AlreadyAnchored { get; init; }

    /// <summary>Link alla transazione sul block explorer, se la rete ne ha uno e c'è una transazione.</summary>
    [JsonPropertyName("explorer_url")]
    public required string? ExplorerUrl { get; init; }

    /// <param name="result">Esito della notarizzazione (rete, registry, anchorer).</param>
    /// <param name="document">Il documento da descrivere, uno di quelli dell'esito.</param>
    /// <param name="explorerBaseUrl">URL base del block explorer della rete, se configurato.</param>
    public static NotarizeHttpResponse From(
        NotarizationResult result,
        NotarizedDocument document,
        string? explorerBaseUrl)
    {
        var anchoring = document.Anchoring;
        return new()
        {
            Status = anchoring.IsPending ? StatusPending : StatusSuccess,
            DocumentId = document.DocumentId,
            Digest = anchoring.Digest.ToString(),
            ChainId = result.ChainId,
            Registry = result.RegistryAddress,
            Anchorer = result.AnchorerAddress,
            AnchoredAt = anchoring.AnchoredAt?.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            AnchoredAtUnix = anchoring.AnchoredAt?.ToUnixTimeSeconds(),
            TxHash = anchoring.TransactionHash,
            AlreadyAnchored = anchoring.AlreadyAnchored,
            ExplorerUrl = TransactionUrl(explorerBaseUrl, anchoring.TransactionHash),
        };
    }

    private static string? TransactionUrl(string? explorerBaseUrl, string? transactionHash) =>
        string.IsNullOrWhiteSpace(explorerBaseUrl) || transactionHash is null
            ? null
            : $"{explorerBaseUrl.TrimEnd('/')}/tx/{transactionHash}";
}

/// <summary>
/// Risposta di POST /api/v1/notarize/batch: un elemento per documento richiesto,
/// nello stesso ordine, ciascuno con la forma di <see cref="NotarizeHttpResponse"/>.
/// </summary>
public sealed record NotarizeBatchHttpResponse
{
    /// <summary>"pending" se almeno un elemento attende la conferma, altrimenti "success".</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<NotarizeHttpResponse> Items { get; init; }

    public static NotarizeBatchHttpResponse From(NotarizationResult result, string? explorerBaseUrl) => new()
    {
        Status = result.IsPending ? NotarizeHttpResponse.StatusPending : NotarizeHttpResponse.StatusSuccess,
        Items = result.Documents
            .Select(document => NotarizeHttpResponse.From(result, document, explorerBaseUrl))
            .ToList(),
    };
}
