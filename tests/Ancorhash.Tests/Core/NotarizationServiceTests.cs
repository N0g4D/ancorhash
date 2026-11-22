using Ancorhash.Core.Exceptions;
using Ancorhash.Core.Models;
using Ancorhash.Core.Services;
using Ancorhash.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static Ancorhash.Tests.TestVectors;

namespace Ancorhash.Tests.Core;

public sealed class NotarizationServiceTests
{
    private const int ChainId = FakeRegistryRelayer.ChainId;
    private const string OtherHex = "0x8d3a1c6f2b9e4d7a5c0b3e6f9a2d5c8b1e4f7a0d3c6b9e2f5a8d1c4b7e0f3a6d";

    private readonly FakeRegistryRelayer _relayer = new();
    private readonly NotarizationService _service;

    public NotarizationServiceTests()
    {
        _service = new NotarizationService(_relayer, NullLogger<NotarizationService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NotarizeAsync_AnchorsTheParsedDigest_AndReturnsTheReceipt()
    {
        var result = await _service.NotarizeAsync(
            new NotarizationCommand("fattura-42.pdf", AncorhashBase64, ChainId), Ct);

        Assert.Equal(AncorhashHex, Assert.Single(Assert.Single(_relayer.Calls)).ToString());
        Assert.Equal(ChainId, result.ChainId);
        Assert.Equal(FakeRegistryRelayer.Registry, result.RegistryAddress);
        Assert.Equal(FakeRegistryRelayer.Anchorer, result.AnchorerAddress);
        Assert.False(result.IsPending);

        var document = Assert.Single(result.Documents);
        Assert.Equal("fattura-42.pdf", document.DocumentId);
        Assert.Equal(AncorhashHex, document.Anchoring.Digest.ToString());
        Assert.Equal(_relayer.BlockTime, document.Anchoring.AnchoredAt);
        Assert.Equal(FakeRegistryRelayer.TransactionHash(1), document.Anchoring.TransactionHash);
        Assert.False(document.Anchoring.AlreadyAnchored);
    }

    [Fact]
    public async Task NotarizeAsync_SameDocumentTwice_SucceedsBothTimes_WithTheOriginalTimeAndOneTransaction()
    {
        var first = await _service.NotarizeAsync(new NotarizationCommand("a.pdf", AncorhashHex, ChainId), Ct);
        _relayer.BlockTime = _relayer.BlockTime.AddMinutes(5);

        // Stesso file, ma nella forma Base64 di Power Automate Desktop: è lo stesso digest.
        var second = await _service.NotarizeAsync(new NotarizationCommand("a.pdf", AncorhashBase64, ChainId), Ct);

        var original = Assert.Single(first.Documents).Anchoring;
        var retry = Assert.Single(second.Documents).Anchoring;
        Assert.False(original.AlreadyAnchored);
        Assert.True(retry.AlreadyAnchored);
        Assert.Null(retry.TransactionHash);
        Assert.Equal(original.AnchoredAt, retry.AnchoredAt);
        Assert.Equal(1, _relayer.TransactionCount);
    }

    [Theory]
    [InlineData(null, AncorhashHex, ChainId, "document_id è obbligatorio.")]
    [InlineData(" ", AncorhashHex, ChainId, "document_id è obbligatorio.")]
    [InlineData("a.pdf", null, ChainId, "document_hash deve essere un digest di 32 byte")]
    [InlineData("a.pdf", "abc", ChainId, "document_hash deve essere un digest di 32 byte")]
    [InlineData("a.pdf", "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045", ChainId, "document_hash deve essere un digest di 32 byte")]
    [InlineData("a.pdf", "0x0000000000000000000000000000000000000000000000000000000000000000", ChainId, "document_hash deve essere un digest di 32 byte")]
    [InlineData("a.pdf", AncorhashHex, 0, "chain_id è obbligatorio")]
    [InlineData("a.pdf", AncorhashHex, -1, "chain_id è obbligatorio")]
    public async Task NotarizeAsync_RejectsInvalidCommands_BeforeReachingTheRelayer(
        string? documentId, string? documentHash, int chainId, string expectedMessage)
    {
        var command = new NotarizationCommand(documentId!, documentHash!, chainId);

        var ex = await Assert.ThrowsAsync<NotarizationValidationException>(
            () => _service.NotarizeAsync(command, Ct));

        Assert.StartsWith(expectedMessage, ex.Message);
        Assert.Empty(_relayer.Calls);
    }

    [Fact]
    public async Task NotarizeBatchAsync_KeepsTheOrderAndTheDuplicates_InOneTransaction()
    {
        var command = new BatchNotarizationCommand(
            [
                new NotarizationItem("a.pdf", AncorhashHex),
                new NotarizationItem("b.pdf", OtherHex),
                new NotarizationItem("a-copia.pdf", AncorhashBase64),
            ],
            ChainId);

        var result = await _service.NotarizeBatchAsync(command, Ct);

        Assert.Equal(1, _relayer.TransactionCount);
        Assert.Equal(3, Assert.Single(_relayer.Calls).Count);
        Assert.Collection(
            result.Documents,
            document =>
            {
                Assert.Equal("a.pdf", document.DocumentId);
                Assert.Equal(AncorhashHex, document.Anchoring.Digest.ToString());
                Assert.False(document.Anchoring.AlreadyAnchored);
            },
            document =>
            {
                Assert.Equal("b.pdf", document.DocumentId);
                Assert.Equal(OtherHex, document.Anchoring.Digest.ToString());
                Assert.False(document.Anchoring.AlreadyAnchored);
            },
            document =>
            {
                Assert.Equal("a-copia.pdf", document.DocumentId);
                Assert.Equal(AncorhashHex, document.Anchoring.Digest.ToString());
                Assert.True(document.Anchoring.AlreadyAnchored);
                Assert.Null(document.Anchoring.TransactionHash);
            });
    }

    [Fact]
    public async Task NotarizeBatchAsync_AcceptsUpToMaxBatchSizeItems()
    {
        var items = Enumerable.Range(1, NotarizationService.MaxBatchSize)
            .Select(i => new NotarizationItem($"doc-{i}", $"0x{i:x64}"))
            .ToList();

        var result = await _service.NotarizeBatchAsync(new BatchNotarizationCommand(items, ChainId), Ct);

        Assert.Equal(NotarizationService.MaxBatchSize, result.Documents.Count);
        Assert.Equal(1, _relayer.TransactionCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(NotarizationService.MaxBatchSize + 1)]
    public async Task NotarizeBatchAsync_RejectsEmptyOrOversizedBatches(int count)
    {
        var items = Enumerable.Range(1, count)
            .Select(i => new NotarizationItem($"doc-{i}", $"0x{i:x64}"))
            .ToList();

        var ex = await Assert.ThrowsAsync<NotarizationValidationException>(
            () => _service.NotarizeBatchAsync(new BatchNotarizationCommand(items, ChainId), Ct));

        Assert.Equal("items deve contenere da 1 a 100 documenti.", ex.Message);
        Assert.Empty(_relayer.Calls);
    }

    [Fact]
    public async Task NotarizeBatchAsync_NamesTheInvalidItem()
    {
        var command = new BatchNotarizationCommand(
            [new NotarizationItem("a.pdf", AncorhashHex), new NotarizationItem("b.pdf", "abc")],
            ChainId);

        var ex = await Assert.ThrowsAsync<NotarizationValidationException>(
            () => _service.NotarizeBatchAsync(command, Ct));

        Assert.StartsWith("items[1].document_hash deve essere un digest di 32 byte", ex.Message);
        Assert.Empty(_relayer.Calls);
    }

    [Fact]
    public async Task NotarizeAsync_ReportsPendingTransactions()
    {
        _relayer.LeaveTransactionsPending = true;

        var result = await _service.NotarizeAsync(new NotarizationCommand("a.pdf", AncorhashHex, ChainId), Ct);

        Assert.True(result.IsPending);
        var anchoring = Assert.Single(result.Documents).Anchoring;
        Assert.Null(anchoring.AnchoredAt);
        Assert.Equal(FakeRegistryRelayer.TransactionHash(1), anchoring.TransactionHash);
        Assert.False(anchoring.AlreadyAnchored);
    }
}
