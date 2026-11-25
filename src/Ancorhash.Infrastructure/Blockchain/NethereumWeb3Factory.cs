using System.Collections.Concurrent;
using Ancorhash.Core.Exceptions;
using Ancorhash.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Accounts;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace Ancorhash.Infrastructure.Blockchain;

/// <summary>
/// Factory singleton con cache per chain id: ogni client Web3 (e il suo pool
/// HTTP) è creato una sola volta e riusato tra le richieste. L'account è
/// istanziato per network con il chain id EIP-155 in firma, così la stessa
/// chiave non è soggetta a replay cross-chain.
/// Con <c>relayerSigner</c> l'account è un ExternalAccount che chiede
/// le firme al signer, cioè alla chiave in Azure Key Vault; senza, firma la chiave
/// in chiaro Evm:RelayerPrivateKey, ammessa solo per lo sviluppo locale.
/// </summary>
public sealed class NethereumWeb3Factory(
    IOptions<BlockchainOptions> options,
    IEthExternalSigner? relayerSigner,
    ILogger<NethereumWeb3Factory> logger) : IWeb3Factory
{
    private readonly BlockchainOptions _options = options.Value;
    private readonly ConcurrentDictionary<int, Lazy<Task<IWeb3>>> _clients = new();

    public async Task<IWeb3> GetWeb3Async(int chainId, CancellationToken cancellationToken = default)
    {
        if (!_options.Networks.TryGetValue(chainId, out var network))
        {
            throw new UnsupportedChainException(chainId);
        }

        // Lazy: richieste concorrenti sulla stessa chain attendono la stessa inizializzazione.
        var client = _clients.GetOrAdd(chainId, id => new Lazy<Task<IWeb3>>(() => CreateWeb3Async(id, network)));
        try
        {
            return await client.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (client.Value is { IsCompleted: true, IsCompletedSuccessfully: false })
        {
            // Un'inizializzazione fallita, ad esempio con Key Vault non raggiungibile, non
            // resta in cache: la richiesta successiva riprova invece di ricevere lo stesso errore.
            _clients.TryRemove(KeyValuePair.Create(chainId, client));
            throw;
        }
    }

    private async Task<IWeb3> CreateWeb3Async(int chainId, NetworkConfig network)
    {
        var rpcClient = new RpcClient(new Uri(network.RpcUrl));

        IAccount account;
        if (relayerSigner is null)
        {
            account = new Account(_options.RelayerPrivateKey, chainId);
        }
        else
        {
            // InitialiseAsync legge solo l'indirizzo dalla chiave pubblica del signer e non
            // crea il transaction manager: senza, new Web3(account, …) fallirebbe. Il nonce
            // resta quello che il relayer legge dal nodo e mette in ogni transazione.
            var externalAccount = new ExternalAccount(relayerSigner, chainId);
            await externalAccount.InitialiseAsync().ConfigureAwait(false);
            externalAccount.InitialiseDefaultTransactionManager(rpcClient);
            account = externalAccount;
        }

        // L'indirizzo serve per finanziare il relayer: è quello che paga il gas.
        logger.LogInformation(
            "Client Web3 per chain {ChainId} ({NetworkName}): relayer {RelayerAddress}, firma con {Signer}",
            chainId, network.Name ?? "n/d", account.Address, relayerSigner?.GetType().Name ?? "chiave locale");
        return new Web3(account, rpcClient);
    }
}
