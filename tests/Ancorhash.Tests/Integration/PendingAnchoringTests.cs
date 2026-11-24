using System.Security.Cryptography;
using Ancorhash.Core.Models;
using Ancorhash.Infrastructure.Blockchain;
using Ancorhash.Tests.Api;
using Ancorhash.Tests.Fakes;
using Microsoft.AspNetCore.Http;

namespace Ancorhash.Tests.Integration;

/// <summary>
/// Digest con una transazione partita ma non confermata: le richieste successive
/// non mandano una seconda transazione finché la voce pending non scade.
/// </summary>
[Collection(AnvilCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PendingAnchoringTests(AnvilFixture anvil)
{
    private const int ChainId = AnvilFixture.ChainId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameDigestTwiceWhileAnvilDoesNotMine_SendsOneTransaction_AndBothGet202WithTheSameTxHash()
    {
        SkipWithoutAnvil();
        var function = anvil.CreateNotarizeFunction(anvil.CreateRelayer(receiptTimeoutSeconds: 1));
        var payload = new
        {
            document_id = "report.pdf",
            document_hash = RandomDigest().ToString(),
            chain_id = ChainId,
            turnstile_token = FakeTurnstileValidator.ValidToken,
        };
        var nonceBefore = await anvil.GetNonceAsync(anvil.RelayerAddress);

        (int Status, System.Text.Json.JsonElement Body) first, second;
        await anvil.SetAutomineAsync(false);
        try
        {
            first = await FunctionHttp.PostJsonAsync(function.RunAsync, payload);
            second = await FunctionHttp.PostJsonAsync(function.RunAsync, payload);

            // Il nonce "pending" conta anche le transazioni in mempool: una sola.
            Assert.Equal(nonceBefore + 1, await anvil.GetNonceAsync(anvil.RelayerAddress));
        }
        finally
        {
            await anvil.MineAsync();
            await anvil.SetAutomineAsync(true);
        }

        Assert.Equal(StatusCodes.Status202Accepted, first.Status);
        Assert.Equal(StatusCodes.Status202Accepted, second.Status);
        Assert.Equal("pending", second.Body.GetProperty("status").GetString());
        Assert.Equal(first.Body.GetProperty("tx_hash").GetString(), second.Body.GetProperty("tx_hash").GetString());

        var afterMining = await FunctionHttp.PostJsonAsync(function.RunAsync, payload);

        Assert.Equal(StatusCodes.Status200OK, afterMining.Status);
        Assert.True(afterMining.Body.GetProperty("already_anchored").GetBoolean());
        Assert.Equal(nonceBefore + 1, await anvil.GetNonceAsync(anvil.RelayerAddress));
    }

    [Fact]
    public async Task TransactionDroppedFromTheMempool_BlocksTheDigestOnlyUntilItsEntryExpires()
    {
        SkipWithoutAnvil();
        var clock = new ManualTimeProvider();

        // Un solo relayer per tutto il test, perché conta anche il nonce dopo lo scarto:
        // deve rileggerlo dal nodo lo stesso relayer che ha mandato la transazione,
        // invece di usare un contatore tenuto in memoria, nel relayer o nel suo account.
        // Un relayer nuovo partirebbe comunque dal nonce del nodo, e un contatore del
        // genere passerebbe inosservato. Il timeout della receipt è di 3 secondi: il
        // primo invio aspetta comunque tutto il timeout e torna pending, perché anvil
        // non mina, quindi un timeout più lungo allungherebbe solo il test. Nell'ultimo
        // invio, con l'automine, anvil mina il blocco in modo asincrono e la prima
        // lettura della receipt può arrivare prima del blocco e tornare null; con 3
        // secondi le letture sono tre, a 0, 1 e 2 secondi, e il ritardo del blocco non
        // basta più a lasciare il digest pending. Con 1 secondo la lettura sarebbe una
        // sola: il timeout annullerebbe l'attesa prima della seconda.
        var relayer = anvil.CreateRelayer(receiptTimeoutSeconds: 3, pendingAnchorings: new PendingAnchorings(clock));
        var digest = RandomDigest();
        var nonceBefore = await anvil.GetNonceAsync(anvil.RelayerAddress);

        AnchoringResult pending, stillPending;
        await anvil.SetAutomineAsync(false);
        try
        {
            pending = Assert.Single((await relayer.AnchorAsync(ChainId, [digest], Ct)).Results);
            await anvil.DropTransactionAsync(pending.TransactionHash!);

            clock.Advance(PendingAnchorings.Lifetime - TimeSpan.FromMinutes(1));
            stillPending = Assert.Single((await relayer.AnchorAsync(ChainId, [digest], Ct)).Results);
        }
        finally
        {
            await anvil.SetAutomineAsync(true);
        }

        Assert.True(pending.IsPending);
        Assert.True(stillPending.IsPending);
        Assert.Equal(pending.TransactionHash, stillPending.TransactionHash);

        // Scaduta la voce, il digest riparte in una nuova transazione, che viene minata.
        // Riusa il nonce rimasto libero: un nonce già avanzato la lascerebbe in coda
        // per sempre. Con stessi dati e stesse fee la firma, deterministica, può
        // anche ridare lo stesso hash.
        clock.Advance(TimeSpan.FromMinutes(1));
        var retried = Assert.Single((await relayer.AnchorAsync(ChainId, [digest], Ct)).Results);

        Assert.False(retried.IsPending);
        Assert.False(retried.AlreadyAnchored);
        Assert.Equal(
            await anvil.GetAnchoredAtAsync(digest, anvil.RelayerAddress),
            retried.AnchoredAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(nonceBefore + 1, await anvil.GetNonceAsync(anvil.RelayerAddress));
    }

    private void SkipWithoutAnvil() =>
        Assert.SkipWhen(anvil.SkipReason is not null, anvil.SkipReason ?? string.Empty);

    private static Digest RandomDigest() => Digest.FromBytes(RandomNumberGenerator.GetBytes(Digest.Length));
}
