using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The fingerprints below are what `ssh-keygen -lf` printed for these keys (2026-10-13): the oracle is
// outside the code under test.
public class SshPublicKeyTests
{
    public const string Ed25519 =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJQIuvIctzZ61SoksnjJlkCRERFjIowWaKhlaoSOsHMB alice@laptop";
    public const string Ed25519Fingerprint = "SHA256:iruZOXrfD+ZK21g+LvOgxbzdggbMluG+IMSJLD4JlN0";

    public const string Ecdsa256 =
        "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBIMiFu3klbiZ+CGMJP2XmpmPwfA7gnGTqgAWypl/msVzDaBvj4PXhbcQ514NbHNMXWZqbOWMXMi5ew5ucFMvGpc= e256";

    public const string Ecdsa384 =
        "ecdsa-sha2-nistp384 AAAAE2VjZHNhLXNoYTItbmlzdHAzODQAAAAIbmlzdHAzODQAAABhBNgHC4hL3/eZrJnWlelwXuz5fov+2g7Wl/QQyqowWA76V/ehlJ1emx3RgL2/18NyKXFiS0r8Ynkz+gDtDIYG1A+aR4IFstJKKr3c5mCU4AZpWiYSkv/tgTmqrL24rM9Mmw==";

    public const string Ecdsa521 =
        "ecdsa-sha2-nistp521 AAAAE2VjZHNhLXNoYTItbmlzdHA1MjEAAAAIbmlzdHA1MjEAAACFBAE5M5GDLFHTaUL0MvEcYJV2YxZQ4XnLc/e0NfXxFQZPvIhUCrIE8WTNdXgP+LlKILNPekJ5vGvaMAUddd5ZT7n/LwBAqlzRUqtF0WcDc/kj1JkE61j2pargGEx02RCm15ZJCtrJAMpUL3yQxIyHeMKqPAR5L2w7AaFTL2Lr455XBOY3IQ==";

    public const string Rsa2048 =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCzyjeBapcz73gJNi+YDDjCUCYQobYyXZEKLrQ18JXmgTth1GMvZ/mjz8U67g5wFJU9NKCxiJk6W/p/TIL+zU5fkBNjKskyZItVPNMCNv6FW03jnr7CBJ171asLprUG9YwdSp7PUzi+3+vjq5Id+0GjxB8QHpFVIaAiadZXB6WA4xV6PEkHvkZK+xR21FX1olDH4emMQYw8o74rb6SzAuQncMgNINinR0H7B4bd2m4Q6/QCj+Z8uCwFy12yV31XIyxonpWQHQUrY2qTI4al9QdJRwC1MjhcvuFXZpkwUHnxOsVdVlNKwn13chYyQ5epG6t6dg+y+A5UoBaGWGlaTM0D rsa2048";

    public const string Rsa1024 =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAAAgQDY33MdmAGIslfV/bpc5tFTotwR7lu+jCqkdbQgiRRi5Ogh4WQpJg3Lh5V7VtOf+vHKvWBLEcZS5Mc9meSoOwlQJJYvzrFwpbXyY4mqsAe2TkJuw2O8wG95S3bebVl4QjRJm0yx5m6qjhZwktKrpqLSdjB3KnwX58efWGTdrD13/Q== rsa1024";

    [Theory]
    [InlineData(Ed25519, "ssh-ed25519", Ed25519Fingerprint)]
    [InlineData(Ecdsa256, "ecdsa-sha2-nistp256", "SHA256:YOM0v43+i6loif8GGAsaDci95B750NzJTx2F8Tpe2hE")]
    [InlineData(Ecdsa384, "ecdsa-sha2-nistp384", "SHA256:tA82HOPGxhcMic54Zhh2V2ikF9C+a/vNSJ3XdeWAwQA")]
    [InlineData(Ecdsa521, "ecdsa-sha2-nistp521", "SHA256:le+OnF6c8gkIa4FuAgEZWL++nwhJ+o5dI7+jMDa0Hgc")]
    [InlineData(Rsa2048, "ssh-rsa", "SHA256:k7BYeTgdI1tqzs8GqbBTZGu6fpqyx4vw/5PwXUdxTM0")]
    public void Parse_AcceptedKey_HasTheFingerprintSshKeygenPrints(string text, string type, string fingerprint)
    {
        SshPublicKey key = SshPublicKey.Parse(text);

        Assert.Equal(type, key.Type);
        Assert.Equal(fingerprint, key.Fingerprint);
    }

    // The line written into authorized_keys is rebuilt from the parsed parts: the comment the customer typed never
    // reaches the guest's shell script.
    [Fact]
    public void Line_IsTypeAndBlob_WithoutTheComment()
    {
        SshPublicKey key = SshPublicKey.Parse(Ed25519);

        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJQIuvIctzZ61SoksnjJlkCRERFjIowWaKhlaoSOsHMB", key.Line);
    }

    [Fact]
    public void Parse_SurroundingWhitespaceAndCommentWithSpaces_AreAccepted()
    {
        SshPublicKey key = SshPublicKey.Parse("  \n" + Ed25519 + " and more words\r\n");

        Assert.Equal(Ed25519Fingerprint, key.Fingerprint);
    }

    // Same key, different comment: one key.
    [Fact]
    public void Fingerprint_DependsOnTheBlobOnly()
    {
        string sameKeyOtherComment = Ed25519.Replace("alice@laptop", "bob");

        Assert.Equal(SshPublicKey.Parse(Ed25519).Fingerprint, SshPublicKey.Parse(sameKeyOtherComment).Fingerprint);
    }

    [Fact]
    public void Parse_Rsa1024_IsRefused()
    {
        string? problem = SshPublicKey.FindProblem(Rsa1024);

        Assert.NotNull(problem);
        Assert.Contains("2048", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 not-base64!!")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP==")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    public void Parse_Malformed_IsRefused(string text)
    {
        Assert.NotNull(SshPublicKey.FindProblem(text));
        BadRequestException refused = Assert.Throws<BadRequestException>(() => SshPublicKey.Parse(text));
        Assert.Equal("INVALID_SSH_KEY", refused.Code);
    }

    // The type at the start of the line is only a label; the blob names its own type. A label that does not match is
    // a key pasted with the wrong prefix.
    [Fact]
    public void Parse_TypeLabelThatDoesNotMatchTheBlob_IsRefused()
    {
        string relabelled = Ed25519.Replace("ssh-ed25519 ", "ssh-rsa ");

        // The reason pins which check refused it: the body would fail later checks too, and would hide this one.
        Assert.Equal("The key data is not a ssh-rsa key.", SshPublicKey.FindProblem(relabelled));
    }

    [Fact]
    public void Parse_EcdsaCurveThatDoesNotMatchTheType_IsRefused()
    {
        // A nistp384 blob whose type string was rewritten to nistp256 keeps "nistp384" as its curve.
        byte[] blob = Convert.FromBase64String(Ecdsa384.Split(' ')[1]);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes("ecdsa-sha2-nistp384");
        byte[] newTypeBytes = System.Text.Encoding.ASCII.GetBytes("ecdsa-sha2-nistp256");
        Assert.Equal(typeBytes.Length, newTypeBytes.Length);
        Array.Copy(newTypeBytes, 0, blob, 4, newTypeBytes.Length);
        string forged = "ecdsa-sha2-nistp256 " + Convert.ToBase64String(blob);

        Assert.NotNull(SshPublicKey.FindProblem(forged));
    }

    [Fact]
    public void Parse_TrailingBytesAfterTheKey_AreRefused()
    {
        byte[] blob = Convert.FromBase64String(Ed25519.Split(' ')[1]);
        byte[] longer = new byte[blob.Length + 3];
        Array.Copy(blob, longer, blob.Length);
        string padded = "ssh-ed25519 " + Convert.ToBase64String(longer);

        Assert.NotNull(SshPublicKey.FindProblem(padded));
    }

    // Wire format: each field is a 4-byte big-endian length, then the bytes.
    private static byte[] Wire(params byte[][] fields)
    {
        var bytes = new List<byte>();
        foreach (byte[] field in fields)
        {
            byte[] length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)field.Length);
            bytes.AddRange(length);
            bytes.AddRange(field);
        }

        return bytes.ToArray();
    }

    private static string RsaLine(byte[] modulus) =>
        "ssh-rsa " + Convert.ToBase64String(Wire(System.Text.Encoding.ASCII.GetBytes("ssh-rsa"), [0x01, 0x00, 0x01], modulus));

    [Fact]
    public void Parse_RsaLargerThan16384Bits_IsRefused()
    {
        byte[] modulus = new byte[2049];
        modulus[0] = 0x00;
        modulus[1] = 0xff; // 2048 bytes of value = 16384 bits: the largest accepted
        Assert.Null(SshPublicKey.FindProblem(RsaLine(modulus)));

        byte[] tooLarge = new byte[2050];
        tooLarge[1] = 0xff;
        Assert.NotNull(SshPublicKey.FindProblem(RsaLine(tooLarge)));
    }

    [Fact]
    public void Parse_Rsa2047Bits_IsRefused_And2048IsAccepted()
    {
        byte[] bits2047 = new byte[256];
        bits2047[0] = 0x7f;
        Assert.Equal("RSA keys must be at least 2048 bits; this one has 2047.", SshPublicKey.FindProblem(RsaLine(bits2047)));

        // The top bit is set, so the mpint carries a leading zero byte to stay positive.
        byte[] bits2048 = new byte[257];
        bits2048[1] = 0x80;
        Assert.Null(SshPublicKey.FindProblem(RsaLine(bits2048)));
    }

    // An mpint with its top bit set and no leading zero byte is negative: no RSA modulus is.
    [Fact]
    public void Parse_RsaNegativeModulus_IsRefused()
    {
        byte[] modulus = new byte[256];
        modulus[0] = 0x80;

        Assert.NotNull(SshPublicKey.FindProblem(RsaLine(modulus)));
    }

    [Fact]
    public void Parse_EcdsaPointThatIsNotUncompressed_IsRefused()
    {
        byte[] blob = Convert.FromBase64String(Ecdsa256.Split(' ')[1]);
        // Type (4 + 19), curve (4 + 8), then the point length (4) and its first byte, 0x04 for an uncompressed point.
        int pointFirstByte = 4 + 19 + 4 + 8 + 4;
        Assert.Equal(0x04, blob[pointFirstByte]);
        blob[pointFirstByte] = 0x02;

        Assert.NotNull(SshPublicKey.FindProblem("ecdsa-sha2-nistp256 " + Convert.ToBase64String(blob)));
    }
}
