namespace Ancorhash.Core.Models;

/// <summary>
/// Esito di una richiesta di ancoraggio: un <see cref="AnchoringResult"/> per ogni
/// digest richiesto, nello stesso ordine e con gli stessi duplicati, più quanto
/// serve a verificarli senza fidarsi di Ancorhash: rete, registry e anchorer.
/// Chiunque può rileggere <c>anchoredAt(digest, anchorer)</c> sul registry.
/// </summary>
/// <param name="ChainId">Chain ID EIP-155 della rete.</param>
/// <param name="RegistryAddress">Indirizzo di AncorhashRegistry su quella rete.</param>
/// <param name="AnchorerAddress">Indirizzo a cui sono intestati i record (il relayer).</param>
/// <param name="Results">Un esito per digest richiesto.</param>
public sealed record AnchoringOutcome(
    int ChainId,
    string RegistryAddress,
    string AnchorerAddress,
    IReadOnlyList<AnchoringResult> Results);
