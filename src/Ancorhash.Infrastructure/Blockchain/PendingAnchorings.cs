using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Ancorhash.Core.Models;

namespace Ancorhash.Infrastructure.Blockchain;

/// <summary>
/// Digest con una transazione anchorBatch partita ma non ancora confermata, per
/// chain e anchorer. Una nuova richiesta per uno di questi digest non lo rimette
/// in un altro anchorBatch: torna pending con lo stesso transaction hash.
/// Una voce si toglie quando la receipt arriva o quando scade dopo
/// <see cref="Lifetime"/>, così una transazione scartata dalla mempool non blocca
/// il digest per sempre.
/// </summary>
/// <remarks>
/// Stato in memoria e per istanza: con più istanze, nel caso peggiore parte una
/// transazione in più, che il registry tratta come un no-op.
/// </remarks>
public sealed class PendingAnchorings(TimeProvider timeProvider)
{
    /// <summary>Per quanto una transazione partita blocca nuovi invii degli stessi digest.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Key, Entry> _entries = new();

    /// <summary>Registra la transazione appena partita per questi digest.</summary>
    public void Track(int chainId, string anchorer, IEnumerable<Digest> digests, string transactionHash)
    {
        var now = timeProvider.GetUtcNow();
        RemoveExpired(now);

        var entry = new Entry(transactionHash, now + Lifetime);
        foreach (var digest in digests)
        {
            _entries[new Key(chainId, anchorer.ToLowerInvariant(), digest)] = entry;
        }
    }

    /// <summary>Transazione ancora in attesa per il digest, se la voce non è scaduta.</summary>
    public bool TryGetTransaction(
        int chainId,
        string anchorer,
        Digest digest,
        [NotNullWhen(true)] out string? transactionHash)
    {
        var key = new Key(chainId, anchorer.ToLowerInvariant(), digest);
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > timeProvider.GetUtcNow())
            {
                transactionHash = entry.TransactionHash;
                return true;
            }

            // Rimuove solo se nessuno l'ha sostituita nel frattempo.
            _entries.TryRemove(KeyValuePair.Create(key, entry));
        }

        transactionHash = null;
        return false;
    }

    /// <summary>Toglie i digest la cui transazione è stata confermata, o che risultano già ancorati.</summary>
    public void Forget(int chainId, string anchorer, IEnumerable<Digest> digests)
    {
        var normalizedAnchorer = anchorer.ToLowerInvariant();
        foreach (var digest in digests)
        {
            _entries.TryRemove(new Key(chainId, normalizedAnchorer, digest), out _);
        }
    }

    // Le voci di digest mai più richiesti scadrebbero senza essere lette:
    // le toglie qui, a ogni nuova transazione, così la mappa non cresce.
    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(KeyValuePair.Create(key, entry));
            }
        }
    }

    private readonly record struct Key(int ChainId, string Anchorer, Digest Digest);

    private sealed record Entry(string TransactionHash, DateTimeOffset ExpiresAt);
}
