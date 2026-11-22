using Ancorhash.Core.Abstractions;
using Ancorhash.Core.Exceptions;
using Ancorhash.Core.Models;

namespace Ancorhash.Tests.Fakes;

/// <summary>
/// Relayer finto che riproduce le regole di AncorhashRegistry per un solo
/// anchorer: vince la prima scrittura, i retry restituiscono l'orario originale,
/// una transazione per richiesta che contiene almeno un digest nuovo, e nei
/// duplicati solo la prima occorrenza risulta ancorata ora.
/// </summary>
internal sealed class FakeRegistryRelayer : IBlockchainRelayer
{
    public const int ChainId = 11155111;
    public const string Registry = "0x929673BD5629c84453aA1d2d733aA31836E62fCE";
    public const string Anchorer = "0x70997970C51812dc3A010C7d01b50e0d17dc79C8";

    private readonly Dictionary<Digest, DateTimeOffset> _records = [];

    /// <summary>Orario del "blocco" in cui finiscono le transazioni.</summary>
    public DateTimeOffset BlockTime { get; set; } = new(2026, 11, 22, 10, 15, 30, TimeSpan.Zero);

    /// <summary>Simula una receipt in ritardo: la transazione parte ma resta pending.</summary>
    public bool LeaveTransactionsPending { get; set; }

    /// <summary>Se valorizzata, ogni chiamata la lancia (errori del nodo, revert…).</summary>
    public Exception? Failure { get; set; }

    public int TransactionCount { get; private set; }

    public List<IReadOnlyList<Digest>> Calls { get; } = [];

    public static string TransactionHash(int number) => "0x" + number.ToString("x64");

    public Task<AnchoringOutcome> AnchorAsync(
        int chainId,
        IReadOnlyList<Digest> digests,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(digests);
        if (Failure is not null)
        {
            throw Failure;
        }

        if (chainId != ChainId)
        {
            throw new UnsupportedChainException(chainId);
        }

        var newDigests = digests.Distinct().Where(digest => !_records.ContainsKey(digest)).ToList();
        var transactionHash = newDigests.Count > 0 ? TransactionHash(++TransactionCount) : null;

        var results = new List<AnchoringResult>(digests.Count);
        var reported = new HashSet<Digest>();
        foreach (var digest in digests)
        {
            if (_records.TryGetValue(digest, out var existing))
            {
                results.Add(AnchoringResult.AlreadyAnchoredAt(digest, existing));
            }
            else if (LeaveTransactionsPending)
            {
                results.Add(AnchoringResult.Pending(digest, transactionHash!));
            }
            else
            {
                results.Add(reported.Add(digest)
                    ? AnchoringResult.Anchored(digest, BlockTime, transactionHash!)
                    : AnchoringResult.AlreadyAnchoredAt(digest, BlockTime));
            }
        }

        if (!LeaveTransactionsPending)
        {
            foreach (var digest in newDigests)
            {
                _records[digest] = BlockTime;
            }
        }

        return Task.FromResult(new AnchoringOutcome(chainId, Registry, Anchorer, results));
    }

    public Task<decimal> EstimateFiatCostAsync(int chainId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0m);
}
