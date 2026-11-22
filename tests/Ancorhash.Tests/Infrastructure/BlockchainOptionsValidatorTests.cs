using Ancorhash.Core.Exceptions;
using Ancorhash.Infrastructure;
using Ancorhash.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ancorhash.Tests.Infrastructure;

public sealed class BlockchainOptionsValidatorTests
{
    private const string Registry = "0x929673BD5629c84453aA1d2d733aA31836E62fCE";
    private const string OtherRegistry = "0x70997970C51812dc3A010C7d01b50e0d17dc79C8";

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
                ["Evm:RelayerPrivateKey"] = "0x" + new string('1', 64),
                ["Blockchain:RegistryAddress"] = Registry.ToLowerInvariant(),
                ["Blockchain:Networks:11155111:RpcUrl"] = "https://sepolia.example.org",
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddAncorhashInfrastructure(configuration)
            .BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<BlockchainOptions>>().Value);

        Assert.Contains(ex.Failures, failure => failure.Contains("checksum EIP-55"));
    }

    private static BlockchainOptions Options(string? registryAddress, NetworkConfig network) => new()
    {
        RegistryAddress = registryAddress,
        Networks = { [11155111] = network },
    };
}
