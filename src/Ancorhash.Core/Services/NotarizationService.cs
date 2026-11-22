using Ancorhash.Core.Abstractions;
using Ancorhash.Core.Exceptions;
using Ancorhash.Core.Models;
using Microsoft.Extensions.Logging;

namespace Ancorhash.Core.Services;

/// <summary>
/// Orchestrazione della notarizzazione: validazione strict dei comandi (nessun
/// dato sporco raggiunge la blockchain) e delega al relayer.
/// </summary>
public sealed class NotarizationService(
    IBlockchainRelayer blockchainRelayer,
    ILogger<NotarizationService> logger) : INotarizationService
{
    /// <summary>
    /// Documenti al massimo per batch: tutti finiscono in una sola transazione,
    /// quindi il limite tiene sotto controllo gas e dimensione della risposta.
    /// </summary>
    public const int MaxBatchSize = 100;

    private const string DigestFormat =
        "deve essere un digest di 32 byte: 64 caratteri esadecimali (con o senza prefisso 0x) oppure Base64. Il digest zero non è ammesso.";

    public Task<NotarizationResult> NotarizeAsync(
        NotarizationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var document = Parse(command.DocumentId, command.DocumentHash, fieldPrefix: string.Empty);
        ValidateChainId(command.ChainId);

        return AnchorAsync(command.ChainId, [document], cancellationToken);
    }

    public Task<NotarizationResult> NotarizeBatchAsync(
        BatchNotarizationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Items is not { Count: > 0 and <= MaxBatchSize })
        {
            throw new NotarizationValidationException(
                $"items deve contenere da 1 a {MaxBatchSize} documenti.");
        }

        var documents = command.Items
            .Select((item, index) => Parse(item?.DocumentId, item?.DocumentHash, $"items[{index}]."))
            .ToList();
        ValidateChainId(command.ChainId);

        return AnchorAsync(command.ChainId, documents, cancellationToken);
    }

    private async Task<NotarizationResult> AnchorAsync(
        int chainId,
        IReadOnlyList<(string DocumentId, Digest Digest)> documents,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Notarize: avvio per {DocumentCount} documenti su chain {ChainId}",
            documents.Count, chainId);

        var outcome = await blockchainRelayer
            .AnchorAsync(chainId, documents.Select(document => document.Digest).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        // Il relayer promette un esito per digest, nello stesso ordine: se così
        // non fosse, abbinare per posizione darebbe a un documento l'esito di un altro.
        if (outcome.Results.Count != documents.Count)
        {
            throw new InvalidOperationException(
                $"Il relayer ha restituito {outcome.Results.Count} esiti per {documents.Count} digest.");
        }

        var notarized = documents
            .Zip(outcome.Results, (document, anchoring) => new NotarizedDocument(document.DocumentId, anchoring))
            .ToList();

        logger.LogInformation(
            "Notarize: completata su chain {ChainId}: {AnchoredCount} ancorati ora, {ExistingCount} già ancorati, {PendingCount} in attesa di conferma",
            chainId,
            notarized.Count(document => document.Anchoring is { AlreadyAnchored: false, IsPending: false }),
            notarized.Count(document => document.Anchoring.AlreadyAnchored),
            notarized.Count(document => document.Anchoring.IsPending));

        return new NotarizationResult(
            outcome.ChainId, outcome.RegistryAddress, outcome.AnchorerAddress, notarized);
    }

    private static (string DocumentId, Digest Digest) Parse(
        string? documentId,
        string? documentHash,
        string fieldPrefix)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new NotarizationValidationException($"{fieldPrefix}document_id è obbligatorio.");
        }

        if (!Digest.TryParse(documentHash, out var digest))
        {
            throw new NotarizationValidationException($"{fieldPrefix}document_hash {DigestFormat}");
        }

        return (documentId, digest);
    }

    private static void ValidateChainId(int chainId)
    {
        if (chainId <= 0)
        {
            throw new NotarizationValidationException(
                "chain_id è obbligatorio e deve essere un intero positivo (EIP-155).");
        }
    }
}
