using System.Text.Json;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Application.Queries.Networking.FirewallRule.Contracts;
using Titan.Application.Queries.Platform.Tenant.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;

namespace Titan.Tests.Api;

// titan-ops features/public-api/design.md#state-enums: the state fields are typed enums, and the words on the wire stay the
// ones the API has always sent — the member name, never a number. Options are FastEndpoints' default (web defaults).
public class StateFieldJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Json<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, Web)).RootElement;

    [Fact]
    public void Vm_StatusIsSentAsItsName_AndTheOldFieldsAreGone()
    {
        var vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);

        var waiting = Json(VmQueryResult.From(vm));
        Assert.Equal("Creating", waiting.GetProperty("status").GetString());
        foreach (string gone in new[] { "currentState", "desiredState", "currentExistence", "desiredExistence", "transition" })
        {
            Assert.False(waiting.TryGetProperty(gone, out _), $"{gone} is still on the VM result");
        }

        vm.MarkRunning("cs-vm-id", "10.0.0.2");
        var running = Json(VmQueryResult.From(vm));
        Assert.Equal("Running", running.GetProperty("status").GetString());
    }

    [Fact]
    public void FirewallRule_StateIsIncusLowercaseWord()
    {
        var rule = FirewallRule.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), FirewallDirection.Ingress,
            FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");
        rule.MarkObserved(FirewallRuleState.enabled);

        var json = Json(FirewallRuleQueryResult.From(rule));
        Assert.Equal("enabled", json.GetProperty("currentState").GetString());
        Assert.Equal("Exists", json.GetProperty("currentExistence").GetString());
    }

    [Fact]
    public void Tenant_StatusIsSentAsItsName()
    {
        var tenant = Tenant.CreatePending("bob", "user-1", "bob@openvicloud.com");
        tenant.Approve();

        Assert.Equal("Active", Json(TenantQueryResult.From(tenant)).GetProperty("status").GetString());
    }
}
