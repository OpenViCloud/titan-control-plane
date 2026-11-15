using System.Security.Cryptography;
using System.Text;

namespace Titan.Application.Services.Compute;

/// SHA-512 crypt (`$6$`), the hash `/etc/shadow` and `chpasswd -e` take, with the default 5000 rounds and no `rounds=`
/// field (titan-ops features/vm/design.md#console-password). .NET has no crypt(3); this follows the SHA-crypt
/// specification (Drepper) step by step. The password is UTF-8.
public static class Sha512Crypt
{
    private const int Rounds = 5000;
    private const int SaltLength = 16;
    private const string Alphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // The specification's order of the 64 digest bytes in the output, three at a time; the last byte goes alone.
    private static readonly int[][] Triples =
    [
        [0, 21, 42], [22, 43, 1], [44, 2, 23], [3, 24, 45], [25, 46, 4], [47, 5, 26], [6, 27, 48], [28, 49, 7],
        [50, 8, 29], [9, 30, 51], [31, 52, 10], [53, 11, 32], [12, 33, 54], [34, 55, 13], [56, 14, 35], [15, 36, 57],
        [37, 58, 16], [59, 17, 38], [18, 39, 60], [40, 61, 19], [62, 20, 41],
    ];

    /// A hash with a fresh random salt of 16 characters.
    public static string Hash(string password)
    {
        var salt = new StringBuilder(SaltLength);
        for (int i = 0; i < SaltLength; i++)
        {
            salt.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return Hash(password, salt.ToString());
    }

    public static string Hash(string password, string salt)
    {
        if (salt.Length == 0 || salt.Length > SaltLength || salt.Any(c => !Alphabet.Contains(c)))
        {
            throw new ArgumentException("A salt is 1 to 16 characters of [./0-9A-Za-z].", nameof(salt));
        }

        byte[] p = Encoding.UTF8.GetBytes(password);
        byte[] s = Encoding.ASCII.GetBytes(salt);

        byte[] b = Digest(p, s, p);

        var a = new List<byte>();
        a.AddRange(p);
        a.AddRange(s);
        int left = p.Length;
        while (left > 64)
        {
            a.AddRange(b);
            left -= 64;
        }
        a.AddRange(b.Take(left));
        for (int bits = p.Length; bits > 0; bits >>= 1)
        {
            if ((bits & 1) == 1)
            {
                a.AddRange(b);
            }
            else
            {
                a.AddRange(p);
            }
        }
        byte[] digest = SHA512.HashData(a.ToArray());

        byte[] dp = SHA512.HashData(Repeat(p, p.Length));
        byte[] pSequence = Stretch(dp, p.Length);
        byte[] ds = SHA512.HashData(Repeat(s, 16 + digest[0]));
        byte[] sSequence = Stretch(ds, s.Length);

        for (int round = 0; round < Rounds; round++)
        {
            var c = new List<byte>();
            if ((round & 1) == 1)
            {
                c.AddRange(pSequence);
            }
            else
            {
                c.AddRange(digest);
            }
            if (round % 3 != 0)
            {
                c.AddRange(sSequence);
            }
            if (round % 7 != 0)
            {
                c.AddRange(pSequence);
            }
            if ((round & 1) == 1)
            {
                c.AddRange(digest);
            }
            else
            {
                c.AddRange(pSequence);
            }
            digest = SHA512.HashData(c.ToArray());
        }

        var output = new StringBuilder("$6$").Append(salt).Append('$');
        foreach (int[] triple in Triples)
        {
            Encode(output, digest[triple[0]], digest[triple[1]], digest[triple[2]], 4);
        }
        Encode(output, 0, 0, digest[63], 2);
        return output.ToString();
    }

    private static byte[] Digest(params byte[][] parts)
    {
        return SHA512.HashData(parts.SelectMany(part => part).ToArray());
    }

    private static byte[] Repeat(byte[] bytes, int times)
    {
        var all = new List<byte>(bytes.Length * times);
        for (int i = 0; i < times; i++)
        {
            all.AddRange(bytes);
        }
        return all.ToArray();
    }

    // `length` bytes made of `digest` repeated: the specification's P and S sequences.
    private static byte[] Stretch(byte[] digest, int length)
    {
        var sequence = new byte[length];
        for (int i = 0; i < length; i++)
        {
            sequence[i] = digest[i % digest.Length];
        }
        return sequence;
    }

    private static void Encode(StringBuilder output, byte high, byte middle, byte low, int characters)
    {
        int word = (high << 16) | (middle << 8) | low;
        for (int i = 0; i < characters; i++)
        {
            output.Append(Alphabet[word & 0x3f]);
            word >>= 6;
        }
    }
}
