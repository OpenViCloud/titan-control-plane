using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Compute.SshKey;

/// A public key line as ssh-keygen writes it, read from the binary blob rather than trusted from its text
/// (titan-ops features/vm/design.md#ssh-key). Only the type and the blob survive: the comment never reaches a VM.
public sealed record SshPublicKey : ValueObject
{
    public const int MinRsaBits = 2048;
    // ssh-keygen refuses to make or use a larger RSA key.
    public const int MaxRsaBits = 16_384;
    private const int MaxTextLength = 16_384;

    private static readonly string[] AcceptedTypes =
    [
        "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "ssh-rsa",
    ];

    public string Type { get; }

    /// `SHA256:` and the unpadded base64 of SHA-256 over the blob — the string `ssh-keygen -lf` prints.
    public string Fingerprint { get; }

    /// `<type> <base64 blob>`, rebuilt from the parsed bytes: what goes into authorized_keys.
    public string Line { get; }

    private SshPublicKey(string type, byte[] blob)
    {
        Type = type;
        Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        Line = type + " " + Convert.ToBase64String(blob);
    }

    public static SshPublicKey Parse(string text)
    {
        ReadResult result = Read(text);
        if (result.Key == null)
        {
            throw new BadRequestException("INVALID_SSH_KEY", result.Problem);
        }

        return result.Key;
    }

    /// Why the text is not an accepted public key, or null when it is.
    public static string? FindProblem(string text)
    {
        ReadResult result = Read(text);
        if (result.Key == null)
        {
            return result.Problem;
        }

        return null;
    }

    private sealed record ReadResult(SshPublicKey? Key, string Problem);

    private static ReadResult Refuse(string problem) => new(null, problem);

    private static ReadResult Read(string text)
    {
        if (text.Length > MaxTextLength)
        {
            return Refuse($"The key is longer than {MaxTextLength} characters.");
        }

        string[] parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return Refuse("Paste a public key line: '<type> <base64> [comment]', for example the content of ~/.ssh/id_ed25519.pub.");
        }

        string type = parts[0];
        if (!AcceptedTypes.Contains(type))
        {
            return Refuse("Accepted key types: " + string.Join(", ", AcceptedTypes) + ".");
        }

        byte[] blob = new byte[parts[1].Length];
        bool decoded = Convert.TryFromBase64String(parts[1], blob, out int blobLength);
        if (!decoded)
        {
            return Refuse("The key data is not valid base64.");
        }

        blob = blob[..blobLength];
        var reader = new WireReader(blob);
        string? blobType = reader.ReadAscii();
        if (blobType != type)
        {
            return Refuse($"The key data is not a {type} key.");
        }

        string? problem = ReadKeyBody(type, reader);
        if (problem != null)
        {
            return Refuse(problem);
        }

        if (!reader.AtEnd)
        {
            return Refuse("The key data has bytes after the key.");
        }

        return new ReadResult(new SshPublicKey(type, blob), "");
    }

    private static string? ReadKeyBody(string type, WireReader reader)
    {
        const string truncated = "The key data is truncated.";

        if (type == "ssh-ed25519")
        {
            byte[]? point = reader.ReadBytes();
            if (point == null)
            {
                return truncated;
            }

            if (point.Length != 32)
            {
                return "An ed25519 key is 32 bytes.";
            }

            return null;
        }

        if (type.StartsWith("ecdsa-sha2-", StringComparison.Ordinal))
        {
            string expectedCurve = type["ecdsa-sha2-".Length..];
            string? curve = reader.ReadAscii();
            byte[]? point = reader.ReadBytes();
            if (curve == null || point == null)
            {
                return truncated;
            }

            if (curve != expectedCurve)
            {
                return $"The key data is a {curve} key, not {expectedCurve}.";
            }

            // An uncompressed point: 0x04, then both coordinates of the curve's size.
            int coordinateBytes = CoordinateBytes(curve);
            if (point.Length != 1 + 2 * coordinateBytes || point[0] != 0x04)
            {
                return $"The key data is not a {curve} point.";
            }

            return null;
        }

        // ssh-rsa: exponent, then modulus, both mpint.
        byte[]? exponent = reader.ReadBytes();
        byte[]? modulus = reader.ReadBytes();
        if (exponent == null || modulus == null)
        {
            return truncated;
        }

        bool negative = modulus.Length > 0 && (modulus[0] & 0x80) != 0;
        if (negative)
        {
            return "The RSA modulus is negative.";
        }

        int bits = BitLength(modulus);
        if (bits < MinRsaBits)
        {
            return $"RSA keys must be at least {MinRsaBits} bits; this one has {bits}.";
        }

        if (bits > MaxRsaBits)
        {
            return $"RSA keys must be at most {MaxRsaBits} bits; this one has {bits}.";
        }

        return null;
    }

    private static int CoordinateBytes(string curve)
    {
        if (curve == "nistp256")
        {
            return 32;
        }

        if (curve == "nistp384")
        {
            return 48;
        }

        return 66; // nistp521
    }

    // An mpint is big-endian with a leading zero byte when the top bit is set.
    private static int BitLength(byte[] mpint)
    {
        int start = 0;
        while (start < mpint.Length && mpint[start] == 0)
        {
            start++;
        }

        if (start == mpint.Length)
        {
            return 0;
        }

        int topByteBits = 8 - LeadingZeroBits(mpint[start]);
        return (mpint.Length - start - 1) * 8 + topByteBits;
    }

    private static int LeadingZeroBits(byte value) => System.Numerics.BitOperations.LeadingZeroCount((uint)value) - 24;

    /// The SSH wire format: each field is a 4-byte big-endian length, then that many bytes.
    private sealed class WireReader(byte[] data)
    {
        private int _offset;

        public bool AtEnd => _offset == data.Length;

        public byte[]? ReadBytes()
        {
            if (data.Length - _offset < 4)
            {
                return null;
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_offset, 4));
            if (length > (uint)(data.Length - _offset - 4))
            {
                return null;
            }

            byte[] field = data.AsSpan(_offset + 4, (int)length).ToArray();
            _offset += 4 + (int)length;
            return field;
        }

        public string? ReadAscii()
        {
            byte[]? field = ReadBytes();
            if (field == null)
            {
                return null;
            }

            return Encoding.ASCII.GetString(field);
        }
    }
}
