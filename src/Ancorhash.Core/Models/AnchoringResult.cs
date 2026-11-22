namespace Ancorhash.Core.Models;

/// <summary>
/// Esito dell'ancoraggio di un digest su AncorhashRegistry. Si costruisce solo con
/// i factory method, che ammettono tre stati coerenti:
/// <list type="bullet">
/// <item>ancorato ora: orario del blocco e transaction hash;</item>
/// <item>già ancorato: orario del primo ancoraggio, nessuna transazione;</item>
/// <item>pending: transazione inviata, receipt non arrivata in tempo, orario ignoto.</item>
/// </list>
/// </summary>
public sealed record AnchoringResult
{
    private AnchoringResult(
        Digest digest,
        DateTimeOffset? anchoredAt,
        string? transactionHash,
        bool alreadyAnchored)
    {
        Digest = digest;
        AnchoredAt = anchoredAt;
        TransactionHash = transactionHash;
        AlreadyAnchored = alreadyAnchored;
    }

    public Digest Digest { get; }

    /// <summary>
    /// Orario del primo ancoraggio (block timestamp, UTC): quello originale se il
    /// digest era già ancorato. Null finché la transazione è pending.
    /// </summary>
    public DateTimeOffset? AnchoredAt { get; }

    /// <summary>Transaction hash con prefisso 0x. Null se il digest era già ancorato.</summary>
    public string? TransactionHash { get; }

    /// <summary>True se il digest era già ancorato: per lui non è partita alcuna transazione.</summary>
    public bool AlreadyAnchored { get; }

    /// <summary>True se la transazione è partita ma la receipt non è arrivata in tempo.</summary>
    public bool IsPending => AnchoredAt is null;

    /// <summary>Digest ancorato da questa richiesta, nella transazione indicata.</summary>
    public static AnchoringResult Anchored(Digest digest, DateTimeOffset anchoredAt, string transactionHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        return new(digest, anchoredAt, transactionHash, alreadyAnchored: false);
    }

    /// <summary>Digest già ancorato in precedenza: vale l'orario originale.</summary>
    public static AnchoringResult AlreadyAnchoredAt(Digest digest, DateTimeOffset anchoredAt) =>
        new(digest, anchoredAt, transactionHash: null, alreadyAnchored: true);

    /// <summary>Transazione inviata, ma la receipt non è arrivata entro il timeout.</summary>
    public static AnchoringResult Pending(Digest digest, string transactionHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        return new(digest, anchoredAt: null, transactionHash, alreadyAnchored: false);
    }
}
