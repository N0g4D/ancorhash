using Ancorhash.Core.Abstractions;
using Ancorhash.Infrastructure.Blockchain;
using Ancorhash.Infrastructure.Configuration;
using Ancorhash.Infrastructure.DocumentIntelligence;
using Ancorhash.Infrastructure.Pricing;
using Ancorhash.Infrastructure.Security;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Signer.AzureKeyVault;

namespace Ancorhash.Infrastructure;

public static class DependencyInjection
{
    // Variabile con cui Azure Functions indica l'ambiente di esecuzione.
    private const string FunctionsEnvironmentConfigKey = "AZURE_FUNCTIONS_ENVIRONMENT";

    /// <summary>
    /// Registra le implementazioni Infrastructure (Nethereum, Azure AI) e le
    /// configurazioni con validazione fail-fast all'avvio.
    /// Il relayer firma con la chiave in Azure Key Vault di Blockchain:Relayer;
    /// Evm:RelayerPrivateKey, in chiaro, è ammessa solo nello sviluppo locale e
    /// solo con reti di test.
    /// </summary>
    public static IServiceCollection AddAncorhashInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Credenziale unica per tutti i servizi Azure (Managed Identity in cloud).
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        services.AddOptions<BlockchainOptions>()
            .Bind(configuration.GetSection(BlockchainOptions.SectionName))
            .PostConfigure(options => options.RelayerPrivateKey =
                configuration[BlockchainOptions.RelayerPrivateKeyConfigKey] ?? string.Empty)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        // Network, RPC URL, indirizzi del registry (checksum EIP-55), explorer e
        // chiave del relayer. Lo sviluppo locale si riconosce dall'ambiente Development:
        // Core Tools lo imposta in locale e local.settings.json non può cambiarlo; in
        // Azure, senza AZURE_FUNCTIONS_ENVIRONMENT, l'ambiente è Production su ogni
        // piano, Flex Consumption compreso.
        var runsLocally = string.Equals(
            configuration[FunctionsEnvironmentConfigKey], "Development", StringComparison.OrdinalIgnoreCase);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<BlockchainOptions>>(new BlockchainOptionsValidator(runsLocally)));

        services.AddOptions<DocumentIntelligenceOptions>()
            .Bind(configuration.GetSection(DocumentIntelligenceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<TurnstileOptions>()
            .Bind(configuration.GetSection(TurnstileOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHttpClient<ITurnstileValidator, CloudflareTurnstileValidator>();

        // Oracolo prezzi: HttpClient nominato (safe per il consumo da singleton)
        // + IMemoryCache condiviso con il rate limiter.
        services.AddMemoryCache();
        services.AddHttpClient(CoinGeckoPriceOracleService.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.coingecko.com/api/v3/");
            client.Timeout = TimeSpan.FromSeconds(10);
            // CoinGecko risponde 403 alle richieste prive di User-Agent, che
            // HttpClient non invia di default: senza questo header ogni stima
            // costi ricade silenziosamente a 0.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Ancorhash/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        services.AddSingleton<IPriceOracleService, CoinGeckoPriceOracleService>();

        services.AddSingleton<IWeb3Factory>(provider => new NethereumWeb3Factory(
            provider.GetRequiredService<IOptions<BlockchainOptions>>(),
            CreateRelayerSigner(provider),
            provider.GetRequiredService<ILogger<NethereumWeb3Factory>>()));
        services.TryAddSingleton(TimeProvider.System);
        // Singleton: le transazioni pending valgono per tutta l'istanza.
        services.AddSingleton<PendingAnchorings>();
        services.AddSingleton<IBlockchainRelayer, NethereumBlockchainRelayer>();
        services.AddSingleton<IDocumentExtractionService, AzureDocumentExtractionService>();
        return services;
    }

    /// <summary>
    /// Signer della chiave del relayer in Azure Key Vault, uno per processo e
    /// condiviso dalle chain. Null senza Blockchain:Relayer: allora il relayer firma
    /// con Evm:RelayerPrivateKey. La validazione all'avvio garantisce che ci sia
    /// esattamente una delle due.
    /// </summary>
    private static AzureKeyVaultExternalSigner? CreateRelayerSigner(IServiceProvider provider)
    {
        var relayer = provider.GetRequiredService<IOptions<BlockchainOptions>>().Value.Relayer;
        if (!relayer.UsesKeyVault)
        {
            return null;
        }

        return new AzureKeyVaultExternalSigner(
            relayer.KeyName!, relayer.KeyVaultUri!, provider.GetRequiredService<TokenCredential>())
        {
            // ES256K invece del default di Nethereum, ECDSA256. Oggi az crea le chiavi
            // secp256k1 solo con la curva P-256K, e Key Vault abbina P-256K all'algoritmo
            // ES256K. ECDSA256 è il nome legacy che andava con il vecchio nome di curva
            // SECP256K1: con una chiave P-256K rischia l'errore "Key and signing
            // algorithm are incompatible".
            UseLegacyECDSA256 = false,
        };
    }
}
