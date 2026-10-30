using System.Text.Json;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmSnapshot;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.VolumeSnapshot;
using Titan.Domain.Aggregates.FirewallRule;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Values;

namespace Titan.Tests.Api;

// titan-ops features/public-api/design.md#state-enums: each state field of the public spec is an enum whose words are the
// Domain enum's member names, a nullable one is `nullable` + `allOf` (oapi-codegen turns `oneOf` into a wrapper
// type), and label fields stay plain strings. Reads the spec the running host serves.
// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class StateEnumSpecTests(OpenApiSpecTests.ApiFactory factory) : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    public static TheoryData<string, string, Type, bool> StateFields => new()
    {
        { "VmQueryResult", "currentState", typeof(VmState), true },
        { "VmQueryResult", "desiredState", typeof(VmDesiredState), false },
        { "VmQueryResult", "currentExistence", typeof(CurrentExistence), false },
        { "VmQueryResult", "desiredExistence", typeof(DesiredExistence), false },
        { "VmQueryResult", "transition", typeof(VmTransition), true },
        { "NetworkQueryResult", "currentState", typeof(NetworkStatus), true },
        { "NetworkQueryResult", "currentExistence", typeof(CurrentExistence), false },
        { "NetworkQueryResult", "desiredExistence", typeof(DesiredExistence), false },
        { "NetworkQueryResult", "transition", typeof(NetworkTransition), true },
        { "FirewallRuleQueryResult", "currentState", typeof(FirewallRuleState), true },
        { "FirewallRuleQueryResult", "currentExistence", typeof(CurrentExistence), false },
        { "FirewallRuleQueryResult", "desiredExistence", typeof(DesiredExistence), false },
        { "FirewallRuleQueryResult", "transition", typeof(FirewallRuleTransition), true },
        { "VolumeQueryResult", "currentExistence", typeof(CurrentExistence), false },
        { "VolumeQueryResult", "desiredExistence", typeof(DesiredExistence), false },
        { "VolumeQueryResult", "transition", typeof(VolumeTransition), true },
        { "VolumeSnapshotQueryResult", "currentExistence", typeof(CurrentExistence), false },
        { "VolumeSnapshotQueryResult", "desiredExistence", typeof(DesiredExistence), false },
        { "VolumeSnapshotQueryResult", "transition", typeof(VolumeSnapshotTransition), true },
        { "VmSnapshotQueryResult", "currentExistence", typeof(CurrentExistence), false },
        { "VmSnapshotQueryResult", "desiredExistence", typeof(DesiredExistence), false },
        { "VmSnapshotQueryResult", "transition", typeof(VmSnapshotTransition), true },
        { "LoadBalancerQueryResult", "currentExistence", typeof(CurrentExistence), false },
        { "LoadBalancerQueryResult", "desiredExistence", typeof(DesiredExistence), false },
        { "LoadBalancerQueryResult", "transition", typeof(LoadBalancerTransition), true },
        { "TenantQueryResult", "status", typeof(TenantStatus), false },
        { "InvoiceQueryResult", "status", typeof(InvoiceDisplayStatus), false },
        { "InvoiceDetailQueryResult", "status", typeof(InvoiceDisplayStatus), false },
        { "TopUpQueryResult", "status", typeof(TopUpStatus), false },
    };

    // Labels grow with features; an enum in the spec would make every new one a breaking change.
    public static TheoryData<string, string> LabelFields => new()
    {
        { "ActivityEventQueryResult", "action" },
        { "ActivityEventQueryResult", "resourceType" },
        { "FirewallRuleQueryResult", "protocol" },
        { "FirewallRuleQueryResult", "direction" },
        { "SshKeyQueryResult", "type" },
    };

    private async Task<JsonElement> SchemasAsync()
    {
        var raw = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        return JsonDocument.Parse(raw).RootElement.GetProperty("components").GetProperty("schemas").Clone();
    }

    private static string RefName(JsonElement reference) =>
        reference.GetProperty("$ref").GetString()!.Split('/').Last();

    [Theory]
    [MemberData(nameof(StateFields))]
    public async Task StateField_IsTheDomainEnum(string schema, string field, Type domainEnum, bool nullable)
    {
        var schemas = await SchemasAsync();
        var property = schemas.GetProperty(schema).GetProperty("properties").GetProperty(field);

        Assert.False(property.TryGetProperty("oneOf", out _), $"{schema}.{field} uses oneOf: {property}");
        bool isNullable = property.TryGetProperty("nullable", out var n) && n.GetBoolean();
        Assert.Equal(nullable, isNullable);

        string enumSchema;
        if (property.TryGetProperty("allOf", out var allOf))
        {
            enumSchema = RefName(Assert.Single(allOf.EnumerateArray()));
        }
        else
        {
            Assert.True(property.TryGetProperty("$ref", out _), $"{schema}.{field} is not an enum reference: {property}");
            enumSchema = RefName(property);
        }

        var target = schemas.GetProperty(enumSchema);
        Assert.Equal("string", target.GetProperty("type").GetString());
        var words = target.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(Enum.GetNames(domainEnum), words);
    }

    [Theory]
    [MemberData(nameof(LabelFields))]
    public async Task LabelField_StaysAPlainString(string schema, string field)
    {
        var schemas = await SchemasAsync();
        var property = schemas.GetProperty(schema).GetProperty("properties").GetProperty(field);

        Assert.Equal("string", property.GetProperty("type").GetString());
        Assert.False(property.TryGetProperty("enum", out _));
    }
}
