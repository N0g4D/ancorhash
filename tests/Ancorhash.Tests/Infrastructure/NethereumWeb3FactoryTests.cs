using Ancorhash.Infrastructure.Blockchain;
using Ancorhash.Infrastructure.Configuration;
using Ancorhash.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Web3.Accounts;

namespace Ancorhash.Tests.Infrastructure;

/// <summary>
/// La factory con un signer esterno, come con Key Vault, senza nodo: creare il
/// client non fa chiamate RPC, e l'inizializzazione dell'account chiede al signer
/// solo l'indirizzo.
/// </summary>
public sealed class NethereumWeb3FactoryTests
{
    private const int ChainId = 11155111;
    private const string SignerKey = "0x5de4111afa1a4b94908f83103eb1f1706367c2e68ca870fc3fb9a804cdab365a";

    private static readonly string SignerAddress = EthECKey.GetPublicAddress(SignerKey);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithAnExternalSigner_TheAccountIsAnInitialisedExternalAccountForTheChain()
    {
        var factory = CreateFactory(new ProbeSigner());

        var web3 = await factory.GetWeb3Async(ChainId, Ct);

        var account = Assert.IsType<ExternalAccount>(web3.TransactionManager.Account);
        Assert.Equal(SignerAddress, account.Address);
        Assert.Equal(ChainId, (int)account.ChainId!.Value);
        // Il transaction manager dell'account è quello del client, e ha il suo RPC.
        Assert.Same(account.TransactionManager, web3.TransactionManager);
        Assert.Same(web3.Client, web3.TransactionManager.Client);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameChain_ShareOneInitialisation()
    {
        var signer = new ProbeSigner(held: true);
        var factory = CreateFactory(signer);

        var first = factory.GetWeb3Async(ChainId, Ct);
        var second = factory.GetWeb3Async(ChainId, Ct);
        signer.Release();

        Assert.Same(await first, await second);
        Assert.Same(await first, await factory.GetWeb3Async(ChainId, Ct));
        Assert.Equal(1, signer.AddressReads);
    }

    [Fact]
    public async Task FailedInitialisation_IsNotCached_AndTheNextRequestRetries()
    {
        var signer = new ProbeSigner(failures: 1);
        var factory = CreateFactory(signer);

        await Assert.ThrowsAsync<HttpRequestException>(() => factory.GetWeb3Async(ChainId, Ct));
        var web3 = await factory.GetWeb3Async(ChainId, Ct);

        Assert.Equal(SignerAddress, web3.TransactionManager.Account.Address);
        Assert.Equal(2, signer.AddressReads);
    }

    private static NethereumWeb3Factory CreateFactory(IEthExternalSigner signer) => new(
        Options.Create(new BlockchainOptions
        {
            Networks = { [ChainId] = new NetworkConfig { RpcUrl = "https://sepolia.example.org" } },
        }),
        signer,
        NullLogger<NethereumWeb3Factory>.Instance);

    /// <summary>
    /// Signer con una chiave locale che conta le letture dell'indirizzo, cioè le
    /// inizializzazioni dell'account. Le prime <c>failures</c> falliscono come con un
    /// Key Vault non raggiungibile; con <c>held</c> le letture aspettano <see cref="Release"/>.
    /// </summary>
    private sealed class ProbeSigner(int failures = 0, bool held = false) : LocalKeyExternalSigner(SignerKey)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _addressReads;

        public int AddressReads => Volatile.Read(ref _addressReads);

        public void Release() => _release.TrySetResult();

        public override async Task<string> GetAddressAsync()
        {
            var read = Interlocked.Increment(ref _addressReads);
            if (held)
            {
                await _release.Task;
            }

            return read <= failures
                ? throw new HttpRequestException("Key Vault non raggiungibile.")
                : await base.GetAddressAsync();
        }
    }
}
