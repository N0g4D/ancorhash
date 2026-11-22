using Ancorhash.Core.Models;

namespace Ancorhash.Core.Abstractions;

/// <summary>
/// Caso d'uso di notarizzazione: valida i comandi e delega l'ancoraggio al relayer.
/// Idempotente: un documento già notarizzato è un successo con l'orario originale.
/// </summary>
public interface INotarizationService
{
    /// <summary>Notarizza un singolo documento.</summary>
    /// <exception cref="Exceptions.NotarizationValidationException">Comando non valido.</exception>
    /// <exception cref="Exceptions.UnsupportedChainException">Chain id non configurato.</exception>
    /// <exception cref="Exceptions.BlockchainUnavailableException">Nodo RPC non raggiungibile.</exception>
    /// <exception cref="Exceptions.BlockchainTransactionException">Transazione rifiutata dopo i retry, o revertita.</exception>
    Task<NotarizationResult> NotarizeAsync(
        NotarizationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Notarizza più documenti con una sola transazione.</summary>
    /// <exception cref="Exceptions.NotarizationValidationException">Comando non valido.</exception>
    /// <exception cref="Exceptions.UnsupportedChainException">Chain id non configurato.</exception>
    /// <exception cref="Exceptions.BlockchainUnavailableException">Nodo RPC non raggiungibile.</exception>
    /// <exception cref="Exceptions.BlockchainTransactionException">Transazione rifiutata dopo i retry, o revertita.</exception>
    Task<NotarizationResult> NotarizeBatchAsync(
        BatchNotarizationCommand command,
        CancellationToken cancellationToken = default);
}
