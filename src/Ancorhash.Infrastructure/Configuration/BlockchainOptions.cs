using System.ComponentModel.DataAnnotations;
using Ancorhash.Core.Exceptions;

namespace Ancorhash.Infrastructure.Configuration;

/// <summary>Configurazione di un singolo network EVM supportato.</summary>
public sealed class NetworkConfig
{
    /// <summary>Endpoint JSON-RPC del network.</summary>
    public string RpcUrl { get; init; } = string.Empty;

    /// <summary>Nome leggibile del network (solo per logging/diagnostica).</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Identificativo CoinGecko della coin nativa (es. "ethereum", "matic-network"),
    /// usato dall'oracolo per la stima costi in USD. Irrilevante per le testnet.
    /// </summary>
    public string? NativeCoinId { get; init; }

    /// <summary>
    /// True se è una testnet: la notarizzazione è gratuita (il gas non ha
    /// valore reale) e la stima costi restituisce 0.
    /// </summary>
    public bool IsTestnet { get; init; }

    /// <summary>
    /// Indirizzo di AncorhashRegistry su questa rete, solo se diverso da
    /// <see cref="BlockchainOptions.RegistryAddress"/>. Checksum EIP-55.
    /// </summary>
    public string? RegistryAddress { get; init; }

    /// <summary>
    /// URL base del block explorer (es. https://sepolia.etherscan.io), da cui
    /// nasce explorer_url nelle risposte. Facoltativo: senza, nessun link.
    /// </summary>
    public string? ExplorerUrl { get; init; }
}

/// <summary>
/// Configurazione multi-chain del relayer EVM.
/// I network sono bindati dalla sezione "Blockchain:Networks" (chiave = chain id
/// EIP-155); la master key arriva da Azure Key Vault (segreto Evm--RelayerPrivateKey
/// → chiave di configurazione Evm:RelayerPrivateKey) ed è iniettata qui via
/// PostConfigure. Con ValidateOnStart l'app non parte se la configurazione è
/// incompleta (vedi <see cref="BlockchainOptionsValidator"/>).
/// </summary>
public sealed class BlockchainOptions
{
    public const string SectionName = "Blockchain";

    /// <summary>Chiave di configurazione della master key (segreto Key Vault Evm--RelayerPrivateKey).</summary>
    public const string RelayerPrivateKeyConfigKey = "Evm:RelayerPrivateKey";

    /// <summary>Network EVM supportati, indicizzati per chain id EIP-155.</summary>
    public Dictionary<int, NetworkConfig> Networks { get; init; } = [];

    /// <summary>
    /// Indirizzo di AncorhashRegistry, uguale su ogni rete perché il deploy passa
    /// dal deployer CREATE2 deterministico. Checksum EIP-55. Una rete può
    /// sovrascriverlo con <see cref="NetworkConfig.RegistryAddress"/>.
    /// </summary>
    public string? RegistryAddress { get; init; }

    /// <summary>
    /// Attesa massima della receipt dopo l'invio, in secondi: oltre, la transazione
    /// torna come pending con il suo hash invece di fallire. Su Sepolia un blocco
    /// arriva ogni 12 secondi circa. Va tenuto sotto i 230 secondi del timeout
    /// HTTP di Azure Functions, insieme alle letture e all'invio.
    /// </summary>
    [Range(1, 180)]
    public int ReceiptTimeoutSeconds { get; init; } = 90;

    /// <summary>
    /// Master key del wallet relayer, valida su tutti i network EVM.
    /// Popolata da Azure Key Vault (DefaultAzureCredential); in locale può
    /// arrivare dalla variabile d'ambiente Evm__RelayerPrivateKey.
    /// </summary>
    [Required]
    [RegularExpression("^0x[0-9a-fA-F]{64}$",
        ErrorMessage = "RelayerPrivateKey deve essere di 66 caratteri: prefisso 0x + 64 hex digits.")]
    public string RelayerPrivateKey { get; set; } = string.Empty;

    /// <summary>Indirizzo del registry sulla rete: l'override di rete se presente, altrimenti quello globale.</summary>
    /// <exception cref="UnsupportedChainException">Chain id non configurato.</exception>
    /// <exception cref="InvalidOperationException">Nessun indirizzo configurato (lo esclude la validazione all'avvio).</exception>
    public string GetRegistryAddress(int chainId)
    {
        if (!Networks.TryGetValue(chainId, out var network))
        {
            throw new UnsupportedChainException(chainId);
        }

        return ResolveRegistryAddress(network)
            ?? throw new InvalidOperationException(
                $"Nessun indirizzo di AncorhashRegistry configurato per la chain {chainId}.");
    }

    internal string? ResolveRegistryAddress(NetworkConfig network) =>
        !string.IsNullOrWhiteSpace(network.RegistryAddress) ? network.RegistryAddress
        : !string.IsNullOrWhiteSpace(RegistryAddress) ? RegistryAddress
        : null;
}
