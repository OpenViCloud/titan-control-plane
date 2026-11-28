using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Infrastructure.FunctionSecrets;

namespace Titan.Tests.Infrastructure.FunctionSecrets;

// AES-256-GCM at rest (titan-ops features/ovc-function/design.md#function-secrets): a round trip, a ciphertext bound to
// its tenant, name and number, a fresh nonce each time, an old key that still decrypts, and the start-up check of the
// keys, which names the setting and never a key.
public class AesGcmFunctionSecretCipherTests
{
    private static readonly Guid Tenant = Guid.Parse("0a1b2c3d-0000-4000-8000-000000000001");
    private static readonly string KeyOne = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyTwo = Convert.ToBase64String(Enumerable.Range(101, 32).Select(i => (byte)i).ToArray());

    internal static FunctionSecretsOptions Keys(string active = "k1", params (string Id, string Key)[] keys)
    {
        var options = new FunctionSecretsOptions { ActiveKeyId = active };
        foreach ((string id, string key) in keys.Length == 0 ? [("k1", KeyOne)] : keys)
        {
            options.Keys[id] = key;
        }
        return options;
    }

    internal static AesGcmFunctionSecretCipher Cipher(FunctionSecretsOptions? options = null) => new(Options.Create(options ?? Keys()));

    [Fact]
    public void AValue_RoundTrips_UnderTheActiveKey()
    {
        var cipher = Cipher();

        FunctionSecretValue value = cipher.Encrypt(Tenant, "DB_PASSWORD", 3, "s3cr3t-é");

        Assert.Equal((3, "k1"), (value.Number, value.KeyId));
        Assert.Equal(Encoding.UTF8.GetByteCount("s3cr3t-é"), value.ValueBytes);
        Assert.Equal(FunctionSecret.NonceBytes, value.Nonce.Length);
        Assert.Equal(value.ValueBytes + FunctionSecret.TagBytes, value.Ciphertext.Length);
        Assert.DoesNotContain("s3cr3t", Encoding.UTF8.GetString(value.Ciphertext));
        Assert.Equal("s3cr3t-é", cipher.Decrypt(Tenant, "DB_PASSWORD", 3, value.KeyId, value.Nonce, value.Ciphertext));
    }

    [Fact]
    public void TheAssociatedData_IsTenantNameAndVersion()
    {
        Assert.Equal("0a1b2c3d-0000-4000-8000-000000000001|DB_PASSWORD|12",
            Encoding.UTF8.GetString(AesGcmFunctionSecretCipher.AssociatedData(Tenant, "DB_PASSWORD", 12)));
    }

    public static TheoryData<Guid, string, int> OtherBindings => new()
    {
        { Guid.NewGuid(), "DB_PASSWORD", 3 },
        { Tenant, "DB_PASSWORD2", 3 },
        { Tenant, "DB_PASSWORD", 4 },
    };

    // A row copied to another tenant, another secret or another number fails its tag.
    [Theory]
    [MemberData(nameof(OtherBindings))]
    public void ACiphertext_ReadAsAnotherTenantNameOrVersion_DoesNotDecrypt(Guid tenant, string name, int version)
    {
        var cipher = Cipher();
        FunctionSecretValue value = cipher.Encrypt(Tenant, "DB_PASSWORD", 3, "s3cr3t");

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(tenant, name, version, value.KeyId, value.Nonce, value.Ciphertext));
    }

    [Fact]
    public void ATamperedCiphertext_DoesNotDecrypt()
    {
        var cipher = Cipher();
        FunctionSecretValue value = cipher.Encrypt(Tenant, "A", 1, "s3cr3t");
        value.Ciphertext[0] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(Tenant, "A", 1, value.KeyId, value.Nonce, value.Ciphertext));
    }

    [Fact]
    public void EveryEncryption_TakesAFreshNonce()
    {
        var cipher = Cipher();

        FunctionSecretValue first = cipher.Encrypt(Tenant, "A", 1, "same");
        FunctionSecretValue second = cipher.Encrypt(Tenant, "A", 1, "same");

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public void ARowOfAnOldKey_StillDecrypts_AfterANewKeyBecomesActive()
    {
        FunctionSecretValue old = Cipher(Keys("k1", ("k1", KeyOne))).Encrypt(Tenant, "A", 1, "old value");
        var rotated = Cipher(Keys("k2", ("k1", KeyOne), ("k2", KeyTwo)));

        FunctionSecretValue fresh = rotated.Encrypt(Tenant, "A", 2, "new value");

        Assert.Equal("k2", fresh.KeyId);
        Assert.Equal("old value", rotated.Decrypt(Tenant, "A", 1, old.KeyId, old.Nonce, old.Ciphertext));
        Assert.Equal("new value", rotated.Decrypt(Tenant, "A", 2, fresh.KeyId, fresh.Nonce, fresh.Ciphertext));
    }

    [Fact]
    public void ARowOfAKeyNoLongerConfigured_NamesTheSetting_NotAValue()
    {
        FunctionSecretValue old = Cipher(Keys("k1", ("k1", KeyOne))).Encrypt(Tenant, "A", 1, "old value");
        var without = Cipher(Keys("k2", ("k2", KeyTwo)));

        var e = Assert.Throws<InvalidOperationException>(() => without.Decrypt(Tenant, "A", 1, old.KeyId, old.Nonce, old.Ciphertext));

        Assert.Contains("FunctionSecrets__Keys__k1", e.Message);
        Assert.DoesNotContain(KeyOne, e.Message);
        Assert.DoesNotContain(KeyTwo, e.Message);
    }

    // ── start-up check (titan-ops features/ovc-function/design.md#function-secrets) ──

    private static readonly FunctionClusterOptions NoCluster = new();
    private static readonly FunctionClusterOptions OneCluster = new() { Clusters = [new FunctionClusterConfig { Id = "fn-dev", Name = "fn-dev" }] };

    [Fact]
    public void WithoutAFunctionCluster_NoKeyIsRequired()
    {
        Assert.True(new FunctionSecretsValidation(NoCluster).Validate(null, new FunctionSecretsOptions()).Succeeded);
    }

    [Fact]
    public void WithAFunctionCluster_AnActiveKeyOf32Bytes_Starts()
    {
        Assert.True(new FunctionSecretsValidation(OneCluster).Validate(null, Keys("k2", ("k1", KeyOne), ("k2", KeyTwo))).Succeeded);
    }

    public static TheoryData<FunctionSecretsOptions, string> BadKeys => new()
    {
        { new FunctionSecretsOptions { ActiveKeyId = "k1" }, "FunctionSecrets__Keys__<keyId>" },
        { Keys("", ("k1", KeyOne)), "FunctionSecrets__ActiveKeyId" },
        { Keys("k9", ("k1", KeyOne)), "FunctionSecrets__ActiveKeyId" },
        { Keys("k1", ("k1", Convert.ToBase64String(new byte[16]))), "FunctionSecrets__Keys__k1" },
        { Keys("k1", ("k1", Convert.ToBase64String(new byte[33]))), "FunctionSecrets__Keys__k1" },
        { Keys("k1", ("k1", "not base64 !")), "FunctionSecrets__Keys__k1" },
        { Keys("k1", ("k1", KeyOne), ("old", "")), "FunctionSecrets__Keys__old" },
    };

    [Theory]
    [MemberData(nameof(BadKeys))]
    public void WithAFunctionCluster_ABadKeySetting_StopsTheHost_NamingTheSettingNotAKey(FunctionSecretsOptions options, string setting)
    {
        ValidateOptionsResult result = new FunctionSecretsValidation(OneCluster).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(setting, result.FailureMessage);
        Assert.DoesNotContain(KeyOne, result.FailureMessage);
    }

    // The same registration shape as Program.cs: bound from FunctionSecrets__*, validated on start.
    [Fact]
    public void AHostWithAFunctionClusterAndNoKey_DoesNotStart_AndOneWithAKey_Decrypts()
    {
        ServiceProvider Host(Dictionary<string, string?> settings)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            services.AddSingleton(OneCluster);
            services.AddOptions<FunctionSecretsOptions>().BindConfiguration(FunctionSecretsOptions.SectionName).ValidateOnStart();
            services.AddSingleton<IValidateOptions<FunctionSecretsOptions>, FunctionSecretsValidation>();
            services.AddSingleton<AesGcmFunctionSecretCipher>();
            return services.BuildServiceProvider();
        }

        using ServiceProvider without = Host(new());
        var e = Assert.Throws<OptionsValidationException>(() => without.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("FunctionSecrets__Keys__<keyId>", e.Message);

        using ServiceProvider with = Host(new() { ["FunctionSecrets:ActiveKeyId"] = "k1", ["FunctionSecrets:Keys:k1"] = KeyOne });
        with.GetRequiredService<IStartupValidator>().Validate();
        var cipher = with.GetRequiredService<AesGcmFunctionSecretCipher>();
        FunctionSecretValue value = cipher.Encrypt(Tenant, "A", 1, "v");
        Assert.Equal("v", cipher.Decrypt(Tenant, "A", 1, value.KeyId, value.Nonce, value.Ciphertext));
    }
}
