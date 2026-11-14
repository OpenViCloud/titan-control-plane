using Titan.Application.Services.Compute;

namespace Titan.Tests.Services;

// The expected strings were written by `openssl passwd -6 -salt <salt> <password>` (OpenSSL 3, macOS, 2026-11-13); the
// "saltstring" one is also the test vector of the SHA-crypt specification (Drepper, 5000 rounds, no rounds= field).
public class Sha512CryptTests
{
    [Theory]
    [InlineData("Hello world!", "saltstring",
        "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1")]
    [InlineData("Hello world!", "rgprobesalt",
        "$6$rgprobesalt$V.1tXI3VaN1kXH7WHJ5/N42r4ZReZ26CKNbqXXSfPSHEEP7Qm9l5O5mxvINbD7Sn7yk9cwx8/ciOubCuxKlpm.")]
    [InlineData("Probe-Pass-123", "rgprobesalt",
        "$6$rgprobesalt$nSxi4dKg3obBTV33X0Wnws7kIi8jT50mu.DpARo.IQtM4NQ/SAJt4uuiOqBLf8QgnyKzhezr5SvghngykEqvO0")]
    [InlineData("ünïcødé-pässwörd-12", "abcdefghijklmnop",
        "$6$abcdefghijklmnop$MIQYJ4IZ5sgivcgpqpWJ38G4DLR2bvPkuMceVC5WjCKyq.jdkC9P.GN6scRaXQGKOhDmx9WbD9WziVzTt6wQS.")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "ab./09AZaz",
        "$6$ab./09AZaz$N3Dkd2L9gB.F5jgunkmKat94P6Lnj9EIrvYwH2gBA7S8CykHOrw/5Sws.M.AfGrNazQgqG7rOW.eWI2QuqrC31")]
    public void AKnownPasswordAndSalt_GiveWhatOpensslGives(string password, string salt, string expected)
    {
        Assert.Equal(expected, Sha512Crypt.Hash(password, salt));
    }

    // A fresh salt each time: 16 characters of crypt's alphabet, so the same password never gives the same hash twice.
    [Fact]
    public void ARandomSalt_IsSixteenCryptCharacters_AndTheHashChecksAgainstIt()
    {
        string first = Sha512Crypt.Hash("Probe-Pass-123");
        string second = Sha512Crypt.Hash("Probe-Pass-123");

        Assert.NotEqual(first, second);
        string[] parts = first.Split('$');
        Assert.Equal(["", "6"], parts[..2]);
        Assert.Matches(@"\A[./0-9A-Za-z]{16}\z", parts[2]);
        Assert.Matches(@"\A[./0-9A-Za-z]{86}\z", parts[3]);
        Assert.Equal(first, Sha512Crypt.Hash("Probe-Pass-123", parts[2]));
    }

    // The hash goes into a shell script between single quotes: a salt outside crypt's alphabet is a caller's bug.
    [Theory]
    [InlineData("")]
    [InlineData("abcdefghijklmnopq")]
    [InlineData("salt'quote")]
    [InlineData("salt$dollar")]
    public void ASaltOutsideCryptsAlphabetOrLength_IsRefused(string salt)
    {
        Assert.Throws<ArgumentException>(() => Sha512Crypt.Hash("Probe-Pass-123", salt));
    }
}
