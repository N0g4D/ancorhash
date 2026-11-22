using System.Text.Json;
using Ancorhash.Api.Functions;
using Ancorhash.Core.Exceptions;
using Ancorhash.Core.Services;
using Ancorhash.Infrastructure.Configuration;
using Ancorhash.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Ancorhash.Tests.TestVectors;

namespace Ancorhash.Tests.Api;

/// <summary>
/// Mapping HTTP di /notarize e /notarize/batch, con il servizio reale e un
/// relayer finto che segue le regole del registry. Le risposte sono serializzate
/// come nell'host (System.Text.Json, default web), per verificare il JSON esposto.
/// </summary>
public sealed class NotarizeFunctionTests
{
    private const int ChainId = FakeRegistryRelayer.ChainId;
    private const string OtherHex = "0x8d3a1c6f2b9e4d7a5c0b3e6f9a2d5c8b1e4f7a0d3c6b9e2f5a8d1c4b7e0f3a6d";

    private readonly FakeRegistryRelayer _relayer = new();
    private readonly NotarizeFunction _function;

    public NotarizeFunctionTests()
    {
        var options = Options.Create(new BlockchainOptions
        {
            RegistryAddress = FakeRegistryRelayer.Registry,
            Networks =
            {
                [ChainId] = new NetworkConfig
                {
                    RpcUrl = "https://sepolia.example.org",
                    ExplorerUrl = "https://sepolia.etherscan.io/",
                },
            },
        });

        _function = new NotarizeFunction(
            new NotarizationService(_relayer, NullLogger<NotarizationService>.Instance),
            new FakeTurnstileValidator(),
            options,
            NullLogger<NotarizeFunction>.Instance);
    }

    [Fact]
    public async Task Notarize_NewDigest_Returns200WithASelfContainedReceipt()
    {
        var (status, body) = await FunctionHttp.PostJsonAsync(_function.RunAsync, Single("fattura-42.pdf", AncorhashHex));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.Equal("fattura-42.pdf", body.GetProperty("document_id").GetString());
        Assert.Equal(AncorhashHex, body.GetProperty("digest").GetString());
        Assert.Equal(ChainId, body.GetProperty("chain_id").GetInt32());
        Assert.Equal(FakeRegistryRelayer.Registry, body.GetProperty("registry").GetString());
        Assert.Equal(FakeRegistryRelayer.Anchorer, body.GetProperty("anchorer").GetString());
        Assert.Equal("2026-11-22T10:15:30Z", body.GetProperty("anchored_at").GetString());
        Assert.Equal(1795342530, body.GetProperty("anchored_at_unix").GetInt64());
        Assert.Equal(FakeRegistryRelayer.TransactionHash(1), body.GetProperty("tx_hash").GetString());
        Assert.False(body.GetProperty("already_anchored").GetBoolean());
        Assert.Equal(
            "https://sepolia.etherscan.io/tx/" + FakeRegistryRelayer.TransactionHash(1),
            body.GetProperty("explorer_url").GetString());
        Assert.False(body.TryGetProperty("wallet_address", out _));
    }

    [Fact]
    public async Task Notarize_SameDigestTwice_Returns200BothTimes_WithTheSameTimeAndOneTransaction()
    {
        var (firstStatus, first) = await FunctionHttp.PostJsonAsync(_function.RunAsync, Single("a.pdf", AncorhashHex));
        _relayer.BlockTime = _relayer.BlockTime.AddMinutes(5);
        var (secondStatus, second) = await FunctionHttp.PostJsonAsync(_function.RunAsync, Single("a.pdf", AncorhashBase64));

        Assert.Equal(StatusCodes.Status200OK, firstStatus);
        Assert.Equal(StatusCodes.Status200OK, secondStatus);
        Assert.Equal(first.GetProperty("digest").GetString(), second.GetProperty("digest").GetString());
        Assert.Equal(first.GetProperty("anchored_at").GetString(), second.GetProperty("anchored_at").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("tx_hash").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("explorer_url").ValueKind);
        Assert.True(second.GetProperty("already_anchored").GetBoolean());
        Assert.Equal(1, _relayer.TransactionCount);
    }

    [Fact]
    public async Task Notarize_WhenTheReceiptIsLate_Returns202WithThePendingTransaction()
    {
        _relayer.LeaveTransactionsPending = true;

        var (status, body) = await FunctionHttp.PostJsonAsync(_function.RunAsync, Single("a.pdf", AncorhashHex));

        Assert.Equal(StatusCodes.Status202Accepted, status);
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal(FakeRegistryRelayer.TransactionHash(1), body.GetProperty("tx_hash").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("anchored_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("anchored_at_unix").ValueKind);
        Assert.False(body.GetProperty("already_anchored").GetBoolean());
        Assert.EndsWith(FakeRegistryRelayer.TransactionHash(1), body.GetProperty("explorer_url").GetString());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045")]
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Notarize_InvalidDigest_Returns400(string documentHash)
    {
        var (status, body) = await FunctionHttp.PostJsonAsync(_function.RunAsync, Single("a.pdf", documentHash));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("error", body.GetProperty("status").GetString());
        Assert.StartsWith("document_hash", body.GetProperty("detail").GetString());
        Assert.Empty(_relayer.Calls);
    }

    [Fact]
    public async Task Notarize_WithoutAValidTurnstileToken_Returns403_BeforeTouchingTheChain()
    {
        var payload = new { document_id = "a.pdf", document_hash = AncorhashHex, chain_id = ChainId, turnstile_token = "forged" };

        var (status, _) = await FunctionHttp.PostJsonAsync(_function.RunAsync, payload);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Empty(_relayer.Calls);
    }

    [Fact]
    public async Task Notarize_UnsupportedChain_Returns400()
    {
        var payload = new { document_id = "a.pdf", document_hash = AncorhashHex, chain_id = 1, turnstile_token = FakeTurnstileValidator.ValidToken };

        var (status, body) = await FunctionHttp.PostJsonAsync(_function.RunAsync, payload);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("chain_id 1", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Notarize_NodeUnreachable_Returns502()
    {
        _relayer.Failure = new BlockchainUnavailableException("RPC giù");

        var (status, _) = await FunctionHttp.PostJsonAsync(_function.RunAsync, Single("a.pdf", AncorhashHex));

        Assert.Equal(StatusCodes.Status502BadGateway, status);
    }

    [Fact]
    public async Task Notarize_TransactionRejected_Returns500()
    {
        _relayer.Failure = new BlockchainTransactionException("insufficient funds for gas");

        var (status, body) = await FunctionHttp.PostJsonAsync(_function.RunAsync, Single("a.pdf", AncorhashHex));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("insufficient funds for gas", body.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("text/plain", "{}", "Il body deve essere JSON")]
    [InlineData("application/json", "{ not json", "Body JSON non valido.")]
    [InlineData("application/json", "null", "Body della richiesta mancante.")]
    public async Task Notarize_MalformedBody_Returns400(string contentType, string rawBody, string expectedDetail)
    {
        var (status, body) = await FunctionHttp.PostRawAsync(_function.RunAsync, rawBody, contentType);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.StartsWith(expectedDetail, body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task NotarizeBatch_AnchorsEveryItemInOneTransaction_InTheRequestedOrder()
    {
        var payload = Batch(("a.pdf", AncorhashHex), ("b.pdf", OtherHex), ("a-copia.pdf", AncorhashBase64));

        var (status, body) = await FunctionHttp.PostJsonAsync(_function.RunBatchAsync, payload);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.Equal(1, _relayer.TransactionCount);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["a.pdf", "b.pdf", "a-copia.pdf"], items.Select(item => item.GetProperty("document_id").GetString()));
        Assert.All(items, item =>
        {
            Assert.Equal("success", item.GetProperty("status").GetString());
            Assert.Equal(ChainId, item.GetProperty("chain_id").GetInt32());
            Assert.Equal(FakeRegistryRelayer.Registry, item.GetProperty("registry").GetString());
            Assert.Equal(FakeRegistryRelayer.Anchorer, item.GetProperty("anchorer").GetString());
            Assert.Equal("2026-11-22T10:15:30Z", item.GetProperty("anchored_at").GetString());
        });
        Assert.Equal(FakeRegistryRelayer.TransactionHash(1), items[0].GetProperty("tx_hash").GetString());
        Assert.Equal(FakeRegistryRelayer.TransactionHash(1), items[1].GetProperty("tx_hash").GetString());
        Assert.True(items[2].GetProperty("already_anchored").GetBoolean());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("tx_hash").ValueKind);
    }

    [Fact]
    public async Task NotarizeBatch_WithAPendingTransaction_Returns202_AndKeepsTheKnownTimes()
    {
        await FunctionHttp.PostJsonAsync(_function.RunAsync, Single("a.pdf", AncorhashHex));
        _relayer.LeaveTransactionsPending = true;

        var (status, body) = await FunctionHttp.PostJsonAsync(_function.RunBatchAsync, Batch(("a.pdf", AncorhashHex), ("b.pdf", OtherHex)));

        Assert.Equal(StatusCodes.Status202Accepted, status);
        Assert.Equal("pending", body.GetProperty("status").GetString());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("success", items[0].GetProperty("status").GetString());
        Assert.True(items[0].GetProperty("already_anchored").GetBoolean());
        Assert.Equal("2026-11-22T10:15:30Z", items[0].GetProperty("anchored_at").GetString());
        Assert.Equal("pending", items[1].GetProperty("status").GetString());
        Assert.Equal(FakeRegistryRelayer.TransactionHash(2), items[1].GetProperty("tx_hash").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task NotarizeBatch_OutsideOneToOneHundredItems_Returns400(int count)
    {
        var payload = Batch(Enumerable.Range(1, count).Select(i => ($"doc-{i}", $"0x{i:x64}")).ToArray());

        var (status, body) = await FunctionHttp.PostJsonAsync(_function.RunBatchAsync, payload);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("items deve contenere da 1 a 100 documenti.", body.GetProperty("detail").GetString());
        Assert.Empty(_relayer.Calls);
    }

    [Fact]
    public async Task NotarizeBatch_InvalidItem_Returns400NamingTheItem()
    {
        var (status, body) = await FunctionHttp.PostJsonAsync(_function.RunBatchAsync, Batch(("a.pdf", AncorhashHex), ("b.pdf", "abc")));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.StartsWith("items[1].document_hash", body.GetProperty("detail").GetString());
    }

    private static object Single(string documentId, string documentHash) => new
    {
        document_id = documentId,
        document_hash = documentHash,
        chain_id = ChainId,
        turnstile_token = FakeTurnstileValidator.ValidToken,
    };

    private static object Batch(params (string DocumentId, string DocumentHash)[] items) => new
    {
        items = items.Select(item => new { document_id = item.DocumentId, document_hash = item.DocumentHash }),
        chain_id = ChainId,
        turnstile_token = FakeTurnstileValidator.ValidToken,
    };
}
