using Ancorhash.Core.Models;

namespace Ancorhash.Core.Abstractions;

/// <summary>
/// Relayer on-chain EVM-agnostic: ancora digest su AncorhashRegistry per conto
/// dei client, pagando il gas. Il relayer è l'anchorer: i record sono intestati
/// al suo indirizzo. Implementato in Infrastructure (Nethereum).
/// </summary>
public interface IBlockchainRelayer
{
    /// <summary>
    /// Ancora uno o più digest sul registry della rete indicata. Idempotente: un
    /// digest già ancorato dal relayer non genera transazioni e restituisce
    /// l'orario originale. I digest nuovi partono tutti in una sola transazione
    /// anchorBatch. Se la receipt non arriva in tempo, i digest nuovi tornano
    /// come pending con il transaction hash, invece di un errore.
    /// </summary>
    /// <param name="chainId">Chain ID EIP-155 della rete EVM di destinazione.</param>
    /// <param name="digests">Almeno un digest; i duplicati sono ammessi.</param>
    /// <returns>Un esito per digest, nello stesso ordine di <paramref name="digests"/>.</returns>
    /// <exception cref="Exceptions.UnsupportedChainException">Chain id non configurato.</exception>
    /// <exception cref="Exceptions.BlockchainUnavailableException">Nodo RPC non raggiungibile.</exception>
    /// <exception cref="Exceptions.BlockchainTransactionException">Transazione rifiutata dopo i retry, o revertita.</exception>
    Task<AnchoringOutcome> AnchorAsync(
        int chainId,
        IReadOnlyList<Digest> digests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stima il costo in USD di un ancoraggio sulla rete indicata: (gas stimato
    /// per anchor(bytes32) × gas price corrente) convertito in USD tramite
    /// l'oracolo dei prezzi. Per le testnet restituisce 0 (gas senza valore).
    /// </summary>
    /// <param name="chainId">Chain ID EIP-155 della rete EVM.</param>
    /// <returns>Costo stimato in USD (0 per le testnet).</returns>
    /// <exception cref="Exceptions.UnsupportedChainException">Chain id non configurato.</exception>
    /// <exception cref="Exceptions.BlockchainUnavailableException">Nodo RPC non raggiungibile.</exception>
    Task<decimal> EstimateFiatCostAsync(
        int chainId,
        CancellationToken cancellationToken = default);
}
