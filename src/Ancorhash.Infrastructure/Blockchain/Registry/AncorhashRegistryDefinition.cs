using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Ancorhash.Infrastructure.Blockchain.Registry;

// Binding tipizzati di AncorhashRegistry, scritti a mano dall'ABI prodotta da
// `forge build` in src/Ancorhash.Contracts/out/AncorhashRegistry.sol/AncorhashRegistry.json.
// Se cambia l'ABI del contratto, vanno aggiornati qui. I bytes32 viaggiano come
// array di 32 byte.

/// <summary><c>anchor(bytes32 digest) returns (uint256 timestamp)</c></summary>
[Function("anchor", "uint256")]
public sealed class AnchorFunction : FunctionMessage
{
    [Parameter("bytes32", "digest", 1)]
    public byte[] Digest { get; set; } = [];
}

/// <summary><c>anchorBatch(bytes32[] digests) returns (uint256 anchored)</c></summary>
[Function("anchorBatch", "uint256")]
public sealed class AnchorBatchFunction : FunctionMessage
{
    [Parameter("bytes32[]", "digests", 1)]
    public List<byte[]> Digests { get; set; } = [];
}

/// <summary>
/// <c>anchorBySig(bytes32 digest, address anchorer, uint256 deadline, bytes signature) returns (uint256 timestamp)</c>
/// </summary>
[Function("anchorBySig", "uint256")]
public sealed class AnchorBySigFunction : FunctionMessage
{
    [Parameter("bytes32", "digest", 1)]
    public byte[] Digest { get; set; } = [];

    [Parameter("address", "anchorer", 2)]
    public string Anchorer { get; set; } = string.Empty;

    [Parameter("uint256", "deadline", 3)]
    public BigInteger Deadline { get; set; }

    [Parameter("bytes", "signature", 4)]
    public byte[] Signature { get; set; } = [];
}

/// <summary>
/// <c>anchorBatchBySig(bytes32[] digests, address anchorer, uint256 deadline, bytes signature) returns (uint256 anchored)</c>
/// </summary>
[Function("anchorBatchBySig", "uint256")]
public sealed class AnchorBatchBySigFunction : FunctionMessage
{
    [Parameter("bytes32[]", "digests", 1)]
    public List<byte[]> Digests { get; set; } = [];

    [Parameter("address", "anchorer", 2)]
    public string Anchorer { get; set; } = string.Empty;

    [Parameter("uint256", "deadline", 3)]
    public BigInteger Deadline { get; set; }

    [Parameter("bytes", "signature", 4)]
    public byte[] Signature { get; set; } = [];
}

/// <summary>
/// <c>anchoredAt(bytes32 digest, address anchorer) view returns (uint256)</c>: zero se mai ancorato.
/// </summary>
[Function("anchoredAt", "uint256")]
public sealed class AnchoredAtFunction : FunctionMessage
{
    [Parameter("bytes32", "digest", 1)]
    public byte[] Digest { get; set; } = [];

    [Parameter("address", "anchorer", 2)]
    public string Anchorer { get; set; } = string.Empty;
}

/// <summary>
/// <c>hashAnchorBatch(bytes32[] digests, address anchorer, uint256 deadline) view returns (bytes32)</c>:
/// l'hash EIP-712 che l'anchorer firma per anchorBatchBySig.
/// </summary>
[Function("hashAnchorBatch", "bytes32")]
public sealed class HashAnchorBatchFunction : FunctionMessage
{
    [Parameter("bytes32[]", "digests", 1)]
    public List<byte[]> Digests { get; set; } = [];

    [Parameter("address", "anchorer", 2)]
    public string Anchorer { get; set; } = string.Empty;

    [Parameter("uint256", "deadline", 3)]
    public BigInteger Deadline { get; set; }
}

/// <summary>
/// <c>Anchored(bytes32 indexed digest, address indexed anchorer, uint256 timestamp)</c>:
/// emesso una sola volta per coppia (digest, anchorer), al primo ancoraggio.
/// </summary>
[Event("Anchored")]
public sealed class AnchoredEventDTO : IEventDTO
{
    [Parameter("bytes32", "digest", 1, true)]
    public byte[] Digest { get; set; } = [];

    [Parameter("address", "anchorer", 2, true)]
    public string Anchorer { get; set; } = string.Empty;

    [Parameter("uint256", "timestamp", 3, false)]
    public BigInteger Timestamp { get; set; }
}
