using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Signer.Crypto;

namespace Ancorhash.Tests.Fakes;

/// <summary>
/// Signer esterno con una chiave locale al posto di Azure Key Vault. Si comporta
/// come AzureKeyVaultExternalSigner: dà la chiave pubblica non compressa e firma
/// l'hash della transazione restituendo solo r e s, 32 byte ciascuno, come la
/// risposta di Key Vault. Il recovery id lo ricava EthExternalSignerBase dalla
/// chiave pubblica.
/// </summary>
internal class LocalKeyExternalSigner(string privateKey) : EthExternalSignerBase
{
    private readonly EthECKey _key = new(privateKey);

    public override bool CalculatesV { get; protected set; }

    public override ExternalSignerTransactionFormat ExternalSignerTransactionFormat { get; protected set; } =
        ExternalSignerTransactionFormat.Hash;

    public override bool Supported1559 => true;

    public override Task SignAsync(LegacyTransaction transaction) => SignHashTransactionAsync(transaction);

    public override Task SignAsync(LegacyTransactionChainId transaction) => SignHashTransactionAsync(transaction);

    public override Task SignAsync(Transaction1559 transaction) => SignHashTransactionAsync(transaction);

    public override Task SignAsync(Transaction7702 transaction) => SignHashTransactionAsync(transaction);

    public override Task SignAsync(Transaction4844 transaction) => SignHashTransactionAsync(transaction);

    protected override Task<byte[]> GetPublicKeyAsync() => Task.FromResult(_key.GetPubKey());

    protected override Task<ECDSASignature> SignExternallyAsync(byte[] bytes)
    {
        var signature = _key.Sign(bytes);
        var rs = new byte[64];
        signature.R.CopyTo(rs, 32 - signature.R.Length);
        signature.S.CopyTo(rs, 64 - signature.S.Length);
        return Task.FromResult(ECDSASignatureFactory.FromComponents(rs).MakeCanonical());
    }
}
