using System.Security.Cryptography;
using Ancorhash.Core.Models;
using Ancorhash.Infrastructure.Blockchain.Registry;
using Ancorhash.Tests.Fakes;
using Nethereum.Contracts;

namespace Ancorhash.Tests.Integration;

/// <summary>
/// Il relayer reale contro AncorhashRegistry deployato su anvil. Ogni test usa
/// digest casuali, quindi l'ordine e lo stato lasciato dagli altri test non contano.
/// </summary>
[Collection(AnvilCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RegistryRelayerTests(AnvilFixture anvil)
{
    private const int ChainId = AnvilFixture.ChainId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewDigest_GetsATransactionToTheRegistry_AndItsTimeMatchesAnchoredAtOnChain()
    {
        SkipWithoutAnvil();
        var relayer = anvil.CreateRelayer();
        var digest = RandomDigest();

        var outcome = await relayer.AnchorAsync(ChainId, [digest], Ct);

        Assert.Equal(anvil.RegistryAddress, outcome.RegistryAddress);
        Assert.Equal(anvil.RelayerAddress, outcome.AnchorerAddress);
        var result = Assert.Single(outcome.Results);
        Assert.Equal(digest, result.Digest);
        Assert.False(result.AlreadyAnchored);
        Assert.False(result.IsPending);
        Assert.NotNull(result.TransactionHash);
        Assert.Equal(await anvil.GetAnchoredAtAsync(digest, anvil.RelayerAddress), result.AnchoredAt!.Value.ToUnixTimeSeconds());

        // Una chiamata al registry, in EIP-1559: non una transazione con il digest nel campo data.
        var receipt = await anvil.Web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(result.TransactionHash);
        Assert.Equal(anvil.RegistryAddress, receipt.To, ignoreCase: true);
        Assert.Equal(2, (int)receipt.Type.Value);
        Assert.Single(receipt.DecodeAllEvents<AnchoredEventDTO>());
    }

    [Fact]
    public async Task ExternalAccountWithALocalKeyInPlaceOfKeyVault_AnchorsTheDigestUnderThatKeysAddress()
    {
        SkipWithoutAnvil();
        var relayer = anvil.CreateRelayer(account: RelayerAccount.ExternalSigner);
        var digest = RandomDigest();

        var outcome = await relayer.AnchorAsync(ChainId, [digest], Ct);

        Assert.Equal(anvil.ExternalSignerAddress, outcome.AnchorerAddress);
        var result = Assert.Single(outcome.Results);
        Assert.False(result.AlreadyAnchored);
        Assert.False(result.IsPending);
        Assert.Equal(
            await anvil.GetAnchoredAtAsync(digest, anvil.ExternalSignerAddress),
            result.AnchoredAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(0, await anvil.GetAnchoredAtAsync(digest, anvil.RelayerAddress));

        // Firmata dalla chiave dietro l'ExternalAccount, in EIP-1559 come con la chiave locale.
        var transaction = await anvil.Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(result.TransactionHash);
        Assert.Equal(anvil.ExternalSignerAddress, transaction.From, ignoreCase: true);
        Assert.Equal(2, (int)transaction.Type.Value);
    }

    [Fact]
    public async Task SameDigestAgain_IsAlreadyAnchoredWithTheOriginalTime_AndTheRelayerNonceDoesNotChange()
    {
        SkipWithoutAnvil();
        var relayer = anvil.CreateRelayer();
        var digest = RandomDigest();
        var first = Assert.Single((await relayer.AnchorAsync(ChainId, [digest], Ct)).Results);
        var nonceBefore = await anvil.GetNonceAsync(anvil.RelayerAddress);

        var second = Assert.Single((await relayer.AnchorAsync(ChainId, [digest], Ct)).Results);

        Assert.True(second.AlreadyAnchored);
        Assert.Null(second.TransactionHash);
        Assert.Equal(first.AnchoredAt, second.AnchoredAt);
        Assert.Equal(nonceBefore, await anvil.GetNonceAsync(anvil.RelayerAddress));
    }

    [Fact]
    public async Task BatchWithDuplicates_ReportsWhatTheContractReports()
    {
        SkipWithoutAnvil();
        var relayer = anvil.CreateRelayer();
        var (a, b, c) = (RandomDigest(), RandomDigest(), RandomDigest());
        var earlier = Assert.Single((await relayer.AnchorAsync(ChainId, [c], Ct)).Results);
        Digest[] batch = [a, b, a, c];

        // Il valore di ritorno di anchorBatch per lo stesso batch, simulato con eth_call.
        var anchoredByContract = await anvil.SimulateAnchorBatchAsync(batch, anvil.RelayerAddress);

        var outcome = await relayer.AnchorAsync(ChainId, batch, Ct);

        Assert.Equal(2, anchoredByContract);
        Assert.Equal(anchoredByContract, outcome.Results.Count(result => !result.AlreadyAnchored));
        var transactionHash = outcome.Results[0].TransactionHash;
        Assert.NotNull(transactionHash);
        Assert.Collection(
            outcome.Results,
            result =>
            {
                Assert.Equal(a, result.Digest);
                Assert.False(result.AlreadyAnchored);
                Assert.Equal(transactionHash, result.TransactionHash);
            },
            result =>
            {
                Assert.Equal(b, result.Digest);
                Assert.False(result.AlreadyAnchored);
                Assert.Equal(transactionHash, result.TransactionHash);
            },
            result =>
            {
                Assert.Equal(a, result.Digest);
                Assert.True(result.AlreadyAnchored);
                Assert.Null(result.TransactionHash);
                Assert.Equal(outcome.Results[0].AnchoredAt, result.AnchoredAt);
            },
            result =>
            {
                Assert.Equal(c, result.Digest);
                Assert.True(result.AlreadyAnchored);
                Assert.Null(result.TransactionHash);
                Assert.Equal(earlier.AnchoredAt, result.AnchoredAt);
            });

        // Un evento Anchored per digest scritto, e gli orari coincidono con i record on-chain.
        var receipt = await anvil.Web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(transactionHash);
        Assert.Equal(anchoredByContract, receipt.DecodeAllEvents<AnchoredEventDTO>().Count);
        foreach (var result in outcome.Results)
        {
            Assert.Equal(
                await anvil.GetAnchoredAtAsync(result.Digest, anvil.RelayerAddress),
                result.AnchoredAt!.Value.ToUnixTimeSeconds());
        }
    }

    [Fact]
    public async Task LateReceipt_ReturnsTheTransactionAsPending_AndARetryGetsTheTimeOnceMined()
    {
        SkipWithoutAnvil();
        var relayer = anvil.CreateRelayer(receiptTimeoutSeconds: 1);
        var digest = RandomDigest();

        AnchoringResult pending;
        await anvil.SetAutomineAsync(false);
        try
        {
            pending = Assert.Single((await relayer.AnchorAsync(ChainId, [digest], Ct)).Results);
        }
        finally
        {
            await anvil.MineAsync();
            await anvil.SetAutomineAsync(true);
        }

        Assert.True(pending.IsPending);
        Assert.Null(pending.AnchoredAt);
        Assert.False(pending.AlreadyAnchored);
        Assert.NotNull(pending.TransactionHash);

        var retry = Assert.Single((await relayer.AnchorAsync(ChainId, [digest], Ct)).Results);

        Assert.True(retry.AlreadyAnchored);
        Assert.Equal(await anvil.GetAnchoredAtAsync(digest, anvil.RelayerAddress), retry.AnchoredAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task RegistryAddressWithoutCode_FailsBeforeSendingAnything()
    {
        SkipWithoutAnvil();
        // Account 9 di anvil: un indirizzo valido ma senza codice.
        var relayer = anvil.CreateRelayer(registryAddress: "0xa0Ee7A142d267C1f36714E4a8F75612F20a79720");
        var nonceBefore = await anvil.GetNonceAsync(anvil.RelayerAddress);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => relayer.AnchorAsync(ChainId, [RandomDigest()], Ct));

        Assert.Contains("non risulta deployato", ex.Message);
        Assert.Equal(nonceBefore, await anvil.GetNonceAsync(anvil.RelayerAddress));
    }

    [Fact]
    public async Task EstimateFiatCost_EstimatesAnAnchorCall_NotAPlainTransaction()
    {
        SkipWithoutAnvil();
        const decimal usdPerEth = 1000m;
        // Una rete che non è una testnet vuole la chiave in Key Vault: qui l'ExternalAccount.
        var relayer = anvil.CreateRelayer(
            isTestnet: false, priceOracle: new FixedPriceOracle(usdPerEth), account: RelayerAccount.ExternalSigner);

        var costUsd = await relayer.EstimateFiatCostAsync(ChainId, Ct);
        var gasPrice = (await anvil.Web3.Eth.GasPrice.SendRequestAsync()).Value;

        // Il primo ancoraggio con anchor(bytes32) costa circa 46k gas (README del
        // contratto); una transazione semplice con 32 byte di data circa 21.5k.
        var impliedGas = costUsd / usdPerEth * 1_000_000_000_000_000_000m / (decimal)gasPrice;
        Assert.InRange(impliedGas, 40_000m, 60_000m);
    }

    private void SkipWithoutAnvil() =>
        Assert.SkipWhen(anvil.SkipReason is not null, anvil.SkipReason ?? string.Empty);

    private static Digest RandomDigest() => Digest.FromBytes(RandomNumberGenerator.GetBytes(Digest.Length));
}
