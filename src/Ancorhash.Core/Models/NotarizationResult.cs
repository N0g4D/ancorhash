namespace Ancorhash.Core.Models;

/// <summary>
/// Esito di una notarizzazione: un documento per ogni elemento richiesto, nello
/// stesso ordine, con la rete, il registry e l'anchorer che servono a verificarli.
/// </summary>
/// <param name="ChainId">Chain ID EIP-155 della rete.</param>
/// <param name="RegistryAddress">Indirizzo di AncorhashRegistry su quella rete.</param>
/// <param name="AnchorerAddress">Indirizzo a cui sono intestati i record.</param>
/// <param name="Documents">Un documento per elemento richiesto.</param>
public sealed record NotarizationResult(
    int ChainId,
    string RegistryAddress,
    string AnchorerAddress,
    IReadOnlyList<NotarizedDocument> Documents)
{
    /// <summary>True se almeno un digest attende ancora la conferma della sua transazione.</summary>
    public bool IsPending => Documents.Any(document => document.Anchoring.IsPending);
}

/// <summary>Documento notarizzato: l'identificativo del chiamante e l'esito on-chain del suo digest.</summary>
/// <param name="DocumentId">Identificativo del documento per il chiamante.</param>
/// <param name="Anchoring">Esito dell'ancoraggio del digest.</param>
public sealed record NotarizedDocument(
    string DocumentId,
    AnchoringResult Anchoring);
