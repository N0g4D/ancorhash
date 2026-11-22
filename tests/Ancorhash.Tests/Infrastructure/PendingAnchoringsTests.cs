using System.Security.Cryptography;
using Ancorhash.Core.Models;
using Ancorhash.Infrastructure.Blockchain;
using Ancorhash.Tests.Fakes;

namespace Ancorhash.Tests.Infrastructure;

public sealed class PendingAnchoringsTests
{
    private const int ChainId = 11155111;
    private const string Anchorer = "0x70997970C51812dc3A010C7d01b50e0d17dc79C8";
    private const string TransactionHash = "0x4f7c044497786f13256a6e4a5a821e54e2634003a1362a430d8613d4b0f6c475";

    private readonly ManualTimeProvider _clock = new();
    private readonly PendingAnchorings _pending;
    private readonly Digest _digest = RandomDigest();

    public PendingAnchoringsTests()
    {
        _pending = new PendingAnchorings(_clock);
    }

    [Fact]
    public void TrackedDigest_ReturnsItsTransaction_UntilTheEntryExpires()
    {
        _pending.Track(ChainId, Anchorer, [_digest], TransactionHash);

        _clock.Advance(PendingAnchorings.Lifetime - TimeSpan.FromSeconds(1));
        Assert.True(_pending.TryGetTransaction(ChainId, Anchorer, _digest, out var transactionHash));
        Assert.Equal(TransactionHash, transactionHash);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_pending.TryGetTransaction(ChainId, Anchorer, _digest, out _));
    }

    [Fact]
    public void Forget_RemovesTheEntry()
    {
        _pending.Track(ChainId, Anchorer, [_digest], TransactionHash);

        _pending.Forget(ChainId, Anchorer, [_digest]);

        Assert.False(_pending.TryGetTransaction(ChainId, Anchorer, _digest, out _));
    }

    [Fact]
    public void Entries_AreSeparatePerChainAndAnchorer_AndTheAddressCaseDoesNotMatter()
    {
        _pending.Track(ChainId, Anchorer, [_digest], TransactionHash);

        Assert.True(_pending.TryGetTransaction(ChainId, Anchorer.ToLowerInvariant(), _digest, out _));
        Assert.False(_pending.TryGetTransaction(1, Anchorer, _digest, out _));
        Assert.False(_pending.TryGetTransaction(ChainId, "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266", _digest, out _));
        Assert.False(_pending.TryGetTransaction(ChainId, Anchorer, RandomDigest(), out _));
    }

    [Fact]
    public void TrackingAgain_ReplacesTheTransactionAndRestartsTheLifetime()
    {
        const string replacement = "0x5edc30b3fc0717572060a2883b579a8eb259a21522f55ac2a270c005f501c3ae";
        _pending.Track(ChainId, Anchorer, [_digest], TransactionHash);
        _clock.Advance(PendingAnchorings.Lifetime);

        _pending.Track(ChainId, Anchorer, [_digest], replacement);
        _clock.Advance(PendingAnchorings.Lifetime - TimeSpan.FromSeconds(1));

        Assert.True(_pending.TryGetTransaction(ChainId, Anchorer, _digest, out var transactionHash));
        Assert.Equal(replacement, transactionHash);
    }

    private static Digest RandomDigest() => Digest.FromBytes(RandomNumberGenerator.GetBytes(Digest.Length));
}
