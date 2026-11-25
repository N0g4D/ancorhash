using Ancorhash.Core.Exceptions;
using Ancorhash.Infrastructure;
using Ancorhash.Infrastructure.Blockchain;
using Ancorhash.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ancorhash.Tests.Infrastructure;

public sealed class BlockchainOptionsValidatorTests
{
    private const string Registry = "0x929673BD5629c84453aA1d2d733aA31836E62fCE";
    private const string OtherRegistry = "0x70997970C51812dc3A010C7d01b50e0d17dc79C8";
    private const string PrivateKey = "0x1111111111111111111111111111111111111111111111111111111111111111";
    private const string VaultUri = "https://ancorhash-test.vault.azure.net/";
    private const string KeyName = "ancorhash-relayer";

    private static readonly RelayerConfig KeyVaultKey = new() { KeyVaultUri = VaultUri, KeyName = KeyName };
    private static readonly RelayerConfig NoKeyVaultKey = new();

    private readonly BlockchainOptionsValidator _validator = new();

    [Fact]
    public void GlobalRegistryAddress_IsValidForEveryNetwork()
    {
        var options = Options(Registry, new NetworkConfig { RpcUrl = "https://sepolia.example.org" });

        Assert.True(_validator.Validate(null, options).Succeeded);
        Assert.Equal(Registry, options.GetRegistryAddress(11155111));
    }

    [Fact]
    public void NetworkOverride_WinsOverTheGlobalAddress_AndIsEnoughOnItsOwn()
    {
        var network = new NetworkConfig { RpcUrl = "https://sepolia.example.org", RegistryAddress = OtherRegistry };

        Assert.True(_validator.Validate(null, Options(null, network)).Succeeded);
        Assert.Equal(OtherRegistry, Options(Registry, network).GetRegistryAddress(11155111));
    }

    [Fact]
    public void MissingRegistryAddress_FailsAndSaysWhereToSetIt()
    {
        var result = _validator.Validate(null, Options(null, new NetworkConfig { RpcUrl = "https://sepolia.example.org" }));

        Assert.True(result.Failed);
        Assert.Contains("Blockchain:RegistryAddress", result.FailureMessage);
        Assert.Contains("Blockchain:Networks:11155111:RegistryAddress", result.FailureMessage);
    }

    [Theory]
    [InlineData("0x929673bd5629c84453aa1d2d733aa31836e62fce")] // tutto minuscolo: niente checksum
    [InlineData("0x929673BD5629c84453aA1d2d733aA31836E62fCe")] // checksum sbagliato
    [InlineData("0x929673BD5629c84453aA1d2d733aA31836E62f")]   // troppo corto
    [InlineData("929673BD5629c84453aA1d2d733aA31836E62fCE")]   // senza 0x
    [InlineData("vitalik.eth")]
    public void RegistryAddressWithoutAValidChecksum_Fails(string address)
    {
        var global = _validator.Validate(null, Options(address, new NetworkConfig { RpcUrl = "https://sepolia.example.org" }));
        var perNetwork = _validator.Validate(null, Options(Registry, new NetworkConfig
        {
            RpcUrl = "https://sepolia.example.org",
            RegistryAddress = address,
        }));

        Assert.Contains("Blockchain:RegistryAddress deve essere un indirizzo con checksum EIP-55.", global.FailureMessage);
        Assert.Contains("Blockchain:Networks:11155111:RegistryAddress deve essere un indirizzo con checksum EIP-55.", perNetwork.FailureMessage);
    }

    [Theory]
    [InlineData("sepolia.etherscan.io")]
    [InlineData("ftp://sepolia.etherscan.io")]
    public void ExplorerUrlThatIsNotAnAbsoluteHttpUrl_Fails(string explorerUrl)
    {
        var result = _validator.Validate(null, Options(Registry, new NetworkConfig
        {
            RpcUrl = "https://sepolia.example.org",
            ExplorerUrl = explorerUrl,
        }));

        Assert.Contains("Blockchain:Networks:11155111:ExplorerUrl", result.FailureMessage);
    }

    [Fact]
    public void NoNetworksOrAnInvalidRpcUrl_Fails()
    {
        Assert.True(_validator.Validate(null, new BlockchainOptions { RegistryAddress = Registry }).Failed);
        Assert.Contains(
            "Blockchain:Networks:11155111:RpcUrl",
            _validator.Validate(null, Options(Registry, new NetworkConfig { RpcUrl = "not a url" })).FailureMessage);
    }

    [Fact]
    public void GetRegistryAddress_ForAnUnknownChain_ThrowsUnsupportedChain()
    {
        var options = Options(Registry, new NetworkConfig { RpcUrl = "https://sepolia.example.org" });

        Assert.Equal(1, Assert.Throws<UnsupportedChainException>(() => options.GetRegistryAddress(1)).ChainId);
    }

    [Fact]
    public void Startup_FailsWhenTheRegistryAddressHasNoChecksum()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Evm:RelayerPrivateKey"] = PrivateKey,
                ["Blockchain:RegistryAddress"] = Registry.ToLowerInvariant(),
                ["Blockchain:Networks:11155111:RpcUrl"] = "https://sepolia.example.org",
                ["Blockchain:Networks:11155111:IsTestnet"] = "true",
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddAncorhashInfrastructure(configuration)
            .BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<BlockchainOptions>>().Value);

        Assert.Contains(ex.Failures, failure => failure.Contains("checksum EIP-55"));
    }

    [Fact]
    public void KeyInKeyVault_IsValidAlsoWithANetworkThatIsNotATestnet()
    {
        var options = Options(Registry, new NetworkConfig { RpcUrl = "https://mainnet.example.org", IsTestnet = false });

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void PrivateKey_IsValidWhenEveryNetworkIsATestnet()
    {
        var options = Options(
            Registry, new NetworkConfig { RpcUrl = "http://127.0.0.1:8545", IsTestnet = true }, NoKeyVaultKey, PrivateKey);

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void PrivateKey_FailsWhenANetworkIsNotATestnet_AndSaysWhichOne()
    {
        var options = new BlockchainOptions
        {
            RegistryAddress = Registry,
            RelayerPrivateKey = PrivateKey,
            Networks =
            {
                [31337] = new NetworkConfig { RpcUrl = "http://127.0.0.1:8545", IsTestnet = true },
                [1] = new NetworkConfig { RpcUrl = "https://mainnet.example.org", IsTestnet = false },
            },
        };

        var result = _validator.Validate(null, options);

        var failure = Assert.Single(result.Failures!);
        Assert.Contains("Evm:RelayerPrivateKey", failure);
        Assert.Contains("Blockchain:Networks:1 ", failure);
        Assert.Contains("Blockchain:Relayer", failure);
    }

    [Fact]
    public void NoRelayerKey_FailsAndNamesBothWaysToSetIt()
    {
        var options = Options(
            Registry, new NetworkConfig { RpcUrl = "https://sepolia.example.org", IsTestnet = true }, NoKeyVaultKey);

        var failure = Assert.Single(_validator.Validate(null, options).Failures!);

        Assert.Contains("Blockchain:Relayer:KeyVaultUri", failure);
        Assert.Contains("Blockchain:Relayer:KeyName", failure);
        Assert.Contains("Evm:RelayerPrivateKey", failure);
    }

    [Fact]
    public void KeyInKeyVaultAndPrivateKeyTogether_Fail()
    {
        var options = Options(
            Registry, new NetworkConfig { RpcUrl = "https://sepolia.example.org", IsTestnet = true }, KeyVaultKey, PrivateKey);

        var failure = Assert.Single(_validator.Validate(null, options).Failures!);

        Assert.Contains("Evm:RelayerPrivateKey e Blockchain:Relayer sono alternative", failure);
    }

    [Theory]
    [InlineData(VaultUri, null, "Blockchain:Relayer:KeyName mancante")]
    [InlineData(null, KeyName, "Blockchain:Relayer:KeyVaultUri mancante")]
    [InlineData("http://ancorhash-test.vault.azure.net/", KeyName, "Blockchain:Relayer:KeyVaultUri deve essere")]
    [InlineData("ancorhash-test.vault.azure.net", KeyName, "Blockchain:Relayer:KeyVaultUri deve essere")]
    [InlineData("https://ancorhash-test.vault.azure.net/keys/ancorhash-relayer", KeyName, "Blockchain:Relayer:KeyVaultUri deve essere")]
    [InlineData(VaultUri, "ancorhash-relayer/0123456789abcdef", "Blockchain:Relayer:KeyName deve essere")]
    [InlineData(VaultUri, "ancorhash relayer", "Blockchain:Relayer:KeyName deve essere")]
    public void IncompleteOrMalformedKeyVaultSettings_Fail(string? keyVaultUri, string? keyName, string expected)
    {
        var options = Options(
            Registry,
            new NetworkConfig { RpcUrl = "https://sepolia.example.org" },
            new RelayerConfig { KeyVaultUri = keyVaultUri, KeyName = keyName });

        var failure = Assert.Single(_validator.Validate(null, options).Failures!);

        Assert.StartsWith(expected, failure);
    }

    [Fact]
    public void Startup_FailsWhenThePrivateKeyIsSetAndANetworkIsNotATestnet()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Evm:RelayerPrivateKey"] = PrivateKey,
            ["Blockchain:RegistryAddress"] = Registry,
            ["Blockchain:Networks:1:RpcUrl"] = "https://mainnet.example.org",
            ["Blockchain:Networks:1:IsTestnet"] = "false",
        });

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<BlockchainOptions>>().Value);

        Assert.Contains(ex.Failures, failure => failure.Contains("Evm:RelayerPrivateKey è ammessa solo"));
    }

    [Fact]
    public void Startup_WithTheKeyInKeyVault_BindsBlockchainRelayer_AndBuildsTheWeb3Factory()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Blockchain:Relayer:KeyVaultUri"] = VaultUri,
            ["Blockchain:Relayer:KeyName"] = KeyName,
            ["Blockchain:RegistryAddress"] = Registry,
            ["Blockchain:Networks:1:RpcUrl"] = "https://mainnet.example.org",
        });

        var options = provider.GetRequiredService<IOptions<BlockchainOptions>>().Value;

        Assert.Equal(VaultUri, options.Relayer.KeyVaultUri);
        Assert.Equal(KeyName, options.Relayer.KeyName);
        Assert.Equal(string.Empty, options.RelayerPrivateKey);
        // Nessuna chiamata a Key Vault qui: la chiave pubblica si legge al primo ancoraggio.
        Assert.IsType<NethereumWeb3Factory>(provider.GetRequiredService<IWeb3Factory>());
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings) =>
        new ServiceCollection()
            .AddLogging()
            .AddAncorhashInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .BuildServiceProvider();

    // Di default la chiave è in Key Vault, valida con qualunque rete: i test che non
    // riguardano la chiave del relayer restano sulle loro regole.
    private static BlockchainOptions Options(
        string? registryAddress,
        NetworkConfig network,
        RelayerConfig? relayer = null,
        string privateKey = "") => new()
        {
            RegistryAddress = registryAddress,
            Relayer = relayer ?? KeyVaultKey,
            RelayerPrivateKey = privateKey,
            Networks = { [11155111] = network },
        };
}
