using System.Security.Cryptography;
using Ancorhash.Core.Models;
using static Ancorhash.Tests.TestVectors;

namespace Ancorhash.Tests.Core;

public sealed class DigestTests
{
    private const string Hex = "10fd2bb778aa0c869b203add42a3bec07e74e428153388b80db340a9df4744d1";

    [Fact]
    public void TestVector_IsTheSha256OfTheAsciiStringAncorhash()
    {
        var sha256 = SHA256.HashData("ancorhash"u8);

        Assert.Equal(AncorhashHex, "0x" + Convert.ToHexStringLower(sha256));
        Assert.Equal(AncorhashBase64, Convert.ToBase64String(sha256));
    }

    [Fact]
    public void HexAndBase64FormsOfTheTestVector_ParseToTheSameDigest()
    {
        Assert.True(Digest.TryParse(AncorhashHex, out var fromHex));
        Assert.True(Digest.TryParse(AncorhashBase64, out var fromBase64));

        Assert.Equal(fromHex, fromBase64);
        Assert.Equal(AncorhashHex, fromBase64.ToString());
    }

    [Theory]
    [InlineData(Hex)]
    [InlineData("0x" + Hex)]
    [InlineData("10FD2BB778AA0C869B203ADD42A3BEC07E74E428153388B80DB340A9DF4744D1")]
    [InlineData("0x10FD2BB778AA0C869B203ADD42A3BEC07E74E428153388B80DB340A9DF4744D1")]
    [InlineData("0x10Fd2Bb778aA0c869B203aDd42A3bEc07E74e428153388B80dB340a9Df4744D1")]
    [InlineData(AncorhashBase64)]
    public void TryParse_AcceptsHexInAnyCaseWithOrWithoutPrefix_AndBase64(string value)
    {
        Assert.True(Digest.TryParse(value, out var digest));
        Assert.Equal(AncorhashHex, digest.ToString());
    }

    public static TheoryData<string> NotADigest() =>
    [
        "",
        " ",
        "0x",
        "not a digest",
        Hex[..^2],                                   // 31 byte
        Hex + "00",                                  // 33 byte
        Hex[..^1],                                   // 63 caratteri
        "0X" + Hex,                                  // il prefisso è solo 0x minuscolo
        "0x0x" + Hex,
        " 0x" + Hex,                                 // spazi e a capo non sono ammessi
        "0x" + Hex + " ",
        "0x" + Hex + "\n",
        "0x" + Hex[..^1] + "g",                      // carattere non esadecimale
        "0x10fd2bb7-78aa-0c86-9b20-3add42a3bec0",
        AncorhashBase64.TrimEnd('='),                // Base64 senza padding
        AncorhashBase64.Replace('+', '-'),           // alfabeto URL-safe
        AncorhashBase64 + " ",
        " " + AncorhashBase64,
        Convert.ToBase64String(new byte[31] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31 }),
        Convert.ToBase64String(Enumerable.Repeat((byte)7, 33).ToArray()),
    ];

    [Theory]
    [InlineData(null)]
    [MemberData(nameof(NotADigest))]
    public void TryParse_RejectsAnythingElse(string? value)
    {
        Assert.False(Digest.TryParse(value, out var digest));
        Assert.Equal(default, digest);
    }

    [Theory]
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void TryParse_RejectsTheZeroDigest_WhichTheContractWouldRevert(string value)
    {
        Assert.False(Digest.TryParse(value, out _));
    }

    [Fact]
    public void FromBytes_RoundTripsThroughToArray()
    {
        var bytes = Convert.FromHexString(Hex);

        var digest = Digest.FromBytes(bytes);

        Assert.Equal(bytes, digest.ToArray());
        Assert.Equal(AncorhashHex, digest.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void FromBytes_RejectsWrongLengths(int length)
    {
        Assert.Throws<ArgumentException>(() => Digest.FromBytes(Enumerable.Repeat((byte)1, length).ToArray()));
    }

    [Fact]
    public void FromBytes_RejectsTheZeroDigest()
    {
        Assert.Throws<ArgumentException>(() => Digest.FromBytes(new byte[Digest.Length]));
    }

    [Fact]
    public void ToArray_ReturnsACopy()
    {
        Digest.TryParse(AncorhashHex, out var digest);

        digest.ToArray()[0] ^= 0xff;

        Assert.Equal(AncorhashHex, digest.ToString());
    }

    [Fact]
    public void Equality_DependsOnTheBytesOnly()
    {
        Digest.TryParse(AncorhashHex, out var a);
        Digest.TryParse(AncorhashBase64, out var b);
        var other = Digest.FromBytes(SHA256.HashData("ancorhash!"u8));

        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != other);
        Assert.Equal(2, new[] { a, b, other }.Distinct().Count());
    }
}
