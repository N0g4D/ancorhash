using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Ancorhash.Core.Models;

/// <summary>
/// Digest di 32 byte da ancorare sul registry: di norma lo SHA-256 di un file,
/// calcolato dal client. Il backend non vede mai il contenuto, solo questi byte.
/// </summary>
/// <remarks>
/// Forme accettate in ingresso: 64 caratteri esadecimali, con o senza prefisso
/// <c>0x</c>, maiuscoli o minuscoli; oppure Base64 standard che decodifica in
/// esattamente 32 byte, come restituisce "Hash from file" di Power Automate Desktop.
/// Forma canonica in uscita: esadecimale minuscolo con prefisso <c>0x</c>.
/// Il digest zero non è mai un hash reale e il contratto lo rifiuterebbe:
/// <see cref="TryParse"/> e <see cref="FromBytes"/> non lo producono mai.
/// <c>default(Digest)</c> è il digest zero e non va usato.
/// </remarks>
public readonly struct Digest : IEquatable<Digest>
{
    /// <summary>Lunghezza del digest in byte.</summary>
    public const int Length = 32;

    private const int HexLength = Length * 2;

    // 32 byte in Base64 standard: 43 caratteri più un "=" di padding.
    private const int Base64Length = 44;

    private readonly Bytes32 _bytes;

    private Digest(ReadOnlySpan<byte> bytes)
    {
        var buffer = new Bytes32();
        bytes.CopyTo(buffer);
        _bytes = buffer;
    }

    /// <summary>
    /// Interpreta un digest in esadecimale o in Base64. Rifiuta ogni altra forma,
    /// spazi compresi, e il digest zero.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out Digest digest)
    {
        digest = default;
        if (value is null)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Length];
        if (!TryDecodeHex(value, bytes) && !TryDecodeBase64(value, bytes))
        {
            return false;
        }

        if (!bytes.ContainsAnyExcept((byte)0))
        {
            return false;
        }

        digest = new Digest(bytes);
        return true;
    }

    /// <summary>Digest dai 32 byte grezzi, ad esempio decodificati da un evento on-chain.</summary>
    /// <exception cref="ArgumentException">Lunghezza diversa da 32 byte, oppure digest zero.</exception>
    public static Digest FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException(
                $"Un digest è lungo {Length} byte, ricevuti {bytes.Length}.", nameof(bytes));
        }

        if (!bytes.ContainsAnyExcept((byte)0))
        {
            throw new ArgumentException("Il digest zero non è un digest valido.", nameof(bytes));
        }

        return new Digest(bytes);
    }

    /// <summary>Copia dei 32 byte, ad esempio per un parametro bytes32 del contratto.</summary>
    public byte[] ToArray() => ((ReadOnlySpan<byte>)_bytes).ToArray();

    /// <summary>Forma canonica: esadecimale minuscolo con prefisso 0x.</summary>
    public override string ToString() => "0x" + Convert.ToHexStringLower((ReadOnlySpan<byte>)_bytes);

    public bool Equals(Digest other) => ((ReadOnlySpan<byte>)_bytes).SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is Digest other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    public static bool operator ==(Digest left, Digest right) => left.Equals(right);

    public static bool operator !=(Digest left, Digest right) => !left.Equals(right);

    private static bool TryDecodeHex(string value, Span<byte> destination)
    {
        var hex = value.AsSpan();
        if (hex.StartsWith("0x", StringComparison.Ordinal))
        {
            hex = hex[2..];
        }

        return hex.Length == HexLength
            && Convert.FromHexString(hex, destination, out _, out var written) == OperationStatus.Done
            && written == Length;
    }

    // La lunghezza fissa esclude gli spazi, che Convert ignorerebbe, e il
    // controllo sui byte scritti esclude il doppio padding (31 byte).
    private static bool TryDecodeBase64(string value, Span<byte> destination) =>
        value.Length == Base64Length
        && Convert.TryFromBase64String(value, destination, out var written)
        && written == Length;

    /// <summary>Buffer inline di 32 byte: il digest resta un vero tipo valore, senza array sull'heap.</summary>
    [InlineArray(Length)]
    private struct Bytes32
    {
        private byte _element0;
    }
}
