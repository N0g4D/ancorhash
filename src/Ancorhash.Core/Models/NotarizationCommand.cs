namespace Ancorhash.Core.Models;

/// <summary>
/// Comando di notarizzazione di un documento, ricevuto dal layer API.
/// Zero Data Leakage: il documento non transita mai dal backend, arriva solo il
/// suo digest già calcolato lato client.
/// </summary>
/// <param name="DocumentId">Identificativo del documento per il chiamante (es. dalla Power App).</param>
/// <param name="DocumentHash">
/// Digest di 32 byte: 64 caratteri esadecimali (con o senza 0x) oppure Base64.
/// </param>
/// <param name="ChainId">Chain ID EIP-155 della rete EVM di destinazione (es. 11155111).</param>
public sealed record NotarizationCommand(
    string DocumentId,
    string DocumentHash,
    int ChainId);

/// <summary>
/// Comando di notarizzazione di più documenti con una sola transazione.
/// </summary>
/// <param name="Items">Da 1 a <see cref="Services.NotarizationService.MaxBatchSize"/> documenti; i duplicati sono ammessi.</param>
/// <param name="ChainId">Chain ID EIP-155 della rete EVM di destinazione.</param>
public sealed record BatchNotarizationCommand(
    IReadOnlyList<NotarizationItem> Items,
    int ChainId);

/// <summary>Un documento di un batch: identificativo del chiamante e digest.</summary>
/// <param name="DocumentId">Identificativo del documento per il chiamante.</param>
/// <param name="DocumentHash">Digest di 32 byte, in esadecimale o Base64.</param>
public sealed record NotarizationItem(
    string DocumentId,
    string DocumentHash);
