using Nethereum.Web3;

namespace Ancorhash.Infrastructure.Blockchain;

/// <summary>
/// Factory di client Web3 per network EVM. Contratto interno a Infrastructure:
/// il Core non deve conoscere Nethereum.
/// </summary>
public interface IWeb3Factory
{
    /// <summary>
    /// Restituisce il client Web3 per il chain id richiesto, con l'account del
    /// relayer sull'RPC configurato per quel network. La prima chiamata per chain
    /// inizializza l'account: con la chiave in Key Vault ne legge la chiave pubblica.
    /// </summary>
    /// <exception cref="Ancorhash.Core.Exceptions.UnsupportedChainException">
    /// Chain id assente dalla configurazione.
    /// </exception>
    Task<IWeb3> GetWeb3Async(int chainId, CancellationToken cancellationToken = default);
}
