using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// What the status and the operation of a network and of a firewall rule do to their row, on Postgres (titan-ops
/// features/network/design.md#network-operation): each move writes the status and the columns the NetworkOperation and the
/// FirewallRuleOperation keep (the pending id and time, the last failure, and observed_state, the Incus status seen while an
/// operation waits), and a row whose operation columns are all NULL still reads with an operation. InMemory cannot show the
/// column spelling, the replacement of the owned object on a tracked aggregate, or an operation read from a hand-written row.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class NetworkOperationDbTests(PostgresFixture pg)
{
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;

    private static Network NewNetwork() =>
        Network.Create(Tenant, NetworkName.Parse($"op-{Guid.NewGuid():N}"[..14]), "cluster-a", null, wantsPublicIpv6: false);

    // status | an operation is pending | its time is set | reason | detail | a failure time is set | the Incus status seen
    private const string Columns =
        "status || '|' || (pending_request_id IS NOT NULL) || '|' || (pending_request_asked_at IS NOT NULL) || '|' || "
        + "coalesce(request_failure_reason, '-') || '|' || coalesce(request_failure_detail, '-') || '|' || "
        + "(request_failed_at IS NOT NULL) || '|' || coalesce(observed_state, '-')";

    private const string NetworkRow = "SELECT " + Columns + " AS \"Value\" FROM core.networks WHERE id = {0}";
    private const string RuleRow = "SELECT " + Columns + " AS \"Value\" FROM core.firewall_rules WHERE id = {0}";

    private static async Task<string> RowOf(TitanDbContext db, string table, Guid id) =>
        (await db.Database.SqlQueryRaw<string>(table == "networks" ? NetworkRow : RuleRow, id).ToListAsync())[0];

    private async Task<string> RowOf(string table, Guid id)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await RowOf(db, table, id);
    }

    [Fact]
    public async Task EveryMove_OfANetwork_WritesItsColumns_AlsoWhenTheNetworkIsTrackedAcrossThem()
    {
        Network network = NewNetwork();
        await using TitanDbContext db = pg.NewDbContext();
        db.Networks.Add(network);
        await db.SaveChangesAsync();
        Assert.Equal("Creating|true|true|-|-|false|-", await RowOf("networks", network.Id));

        network.ReportDone(network.Operation.PendingId!.Value, "net42", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        await db.SaveChangesAsync();
        Assert.Equal("Created|false|false|-|-|false|-", await RowOf("networks", network.Id));

        network.Delete();
        await db.SaveChangesAsync();
        Assert.Equal("Deleting|true|true|-|-|false|Created", await RowOf("networks", network.Id));

        network.ReportFailed(network.Operation.PendingId!.Value, null, null, null, null, "NO_ROOM", "raw detail");
        await db.SaveChangesAsync();
        Assert.Equal("Created|false|false|NO_ROOM|raw detail|true|-", await RowOf("networks", network.Id));

        network.Delete();
        await db.SaveChangesAsync();
        Assert.Equal("Deleting|true|true|NO_ROOM|raw detail|true|Created", await RowOf("networks", network.Id));

        network.ReportDone(network.Operation.PendingId!.Value, null, null, null, null);
        await db.SaveChangesAsync();
        Assert.Equal("Deleted|false|false|-|-|false|-", await RowOf("networks", network.Id));
    }

    [Fact]
    public async Task EveryMove_OfAFirewallRule_WritesItsColumns_AlsoWhenTheNetworkIsTrackedAcrossThem()
    {
        Network network = NewNetwork();
        network.ReportDone(network.Operation.PendingId!.Value, "net42", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        await using TitanDbContext db = pg.NewDbContext();
        db.Networks.Add(network);
        await db.SaveChangesAsync();

        FirewallRule rule = network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, null, FirewallCidr.Parse("10.0.0.0/8"));
        await db.SaveChangesAsync();
        Assert.Equal("Creating|true|true|-|-|false|-", await RowOf("firewall_rules", rule.Id));

        network.ReportRuleDone(rule.Id, rule.Operation.PendingId!.Value, "acl/1", FirewallRuleStatus.enabled);
        await db.SaveChangesAsync();
        Assert.Equal("enabled|false|false|-|-|false|-", await RowOf("firewall_rules", rule.Id));

        network.DeleteFirewallRule(rule.Id);
        await db.SaveChangesAsync();
        Assert.Equal("Deleting|true|true|-|-|false|enabled", await RowOf("firewall_rules", rule.Id));

        network.ReportRuleFailed(rule.Id, rule.Operation.PendingId!.Value, null, null, "NO_ROOM", "raw detail");
        await db.SaveChangesAsync();
        Assert.Equal("enabled|false|false|NO_ROOM|raw detail|true|-", await RowOf("firewall_rules", rule.Id));

        network.DeleteFirewallRule(rule.Id);
        network.ReportRuleDone(rule.Id, rule.Operation.PendingId!.Value, null, null);
        await db.SaveChangesAsync();
        Assert.Equal("Deleted|false|false|-|-|false|-", await RowOf("firewall_rules", rule.Id));
    }

    [Fact]
    public async Task AReloadedNetworkAndRule_ReadBackWhatEachMoveWrote()
    {
        Network network = NewNetwork();
        network.ReportDone(network.Operation.PendingId!.Value, "net42", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        FirewallRule rule = network.WithFirewallRule(FirewallDirection.Egress, FirewallProtocol.All, null, FirewallCidr.Parse("10.0.0.0/8"));
        network.ReportRuleFailed(rule.Id, rule.Operation.PendingId!.Value, null, null, "RULE_REFUSED", "rule raw");
        network.Delete();
        Guid deleteId = network.Operation.PendingId!.Value;
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext reading = pg.NewDbContext();
        Network reloaded = await reading.Networks.Include(n => n.FirewallRules).SingleAsync(n => n.Id == network.Id);

        Assert.Equal(deleteId, reloaded.Operation.PendingId);
        Assert.Equal(NetworkStatus.Deleting, reloaded.Status);
        Assert.Equal(NetworkStatus.Created, reloaded.Operation.LastSeenStatus);
        Assert.Equal(network.Operation.AskedAt!.Value, reloaded.Operation.AskedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null(reloaded.Operation.FailureReason);
        FirewallRule reloadedRule = reloaded.FirewallRules.Single();
        Assert.Null(reloadedRule.Operation.PendingId);
        Assert.Equal(FirewallRuleStatus.Failed, reloadedRule.Status);
        Assert.Equal("RULE_REFUSED", reloadedRule.Operation.FailureReason);
        Assert.Equal("rule raw", reloadedRule.Operation.FailureDetail);
        Assert.NotNull(reloadedRule.Operation.FailedAt);
    }

    [Fact]
    public async Task ANetworkAndARuleThatNeverHadAnOperation_HaveOne_WhenTheirOperationColumnsAreAllNull()
    {
        Network network = NewNetwork();
        network.ReportDone(network.Operation.PendingId!.Value, "net42", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        FirewallRule rule = network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, null, FirewallCidr.Parse("10.0.0.0/8"));
        network.ReportRuleDone(rule.Id, rule.Operation.PendingId!.Value, "acl/1", FirewallRuleStatus.enabled);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext reading = pg.NewDbContext();
        Network reloaded = await reading.Networks.Include(n => n.FirewallRules).SingleAsync(n => n.Id == network.Id);

        Assert.Equal("Created|false|false|-|-|false|-", await RowOf(reading, "networks", network.Id));
        Assert.Equal("enabled|false|false|-|-|false|-", await RowOf(reading, "firewall_rules", rule.Id));
        Assert.NotNull(reloaded.Operation);
        Assert.NotNull(reading.Entry(reloaded).Reference(n => n.Operation).TargetEntry);
        Assert.Null(reloaded.Operation.PendingId);
        Assert.Null(reloaded.Operation.LastSeenStatus);
        Assert.NotNull(reloaded.FirewallRules.Single().Operation);
        Assert.Null(reloaded.FirewallRules.Single().Operation.PendingId);
    }

    // The columns the aggregates write: a hand-written row of a network whose delete waits, with an old failure.
    [Fact]
    public async Task ARowWrittenByHandWithAWaitingDeleteAndAnOldFailure_ReadsAsAnOperation()
    {
        Network network = NewNetwork();
        network.ReportDone(network.Operation.PendingId!.Value, "net42", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        Guid operationId = Guid.NewGuid();
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE core.networks SET status = 'Deleting', pending_request_id = {0}, "
                + "pending_request_asked_at = now(), observed_state = 'Errored', request_failure_reason = 'OLD', "
                + "request_failure_detail = 'old detail', request_failed_at = now() WHERE id = {1}", operationId, network.Id);
        }

        await using TitanDbContext reading = pg.NewDbContext();
        Network reloaded = await reading.Networks.SingleAsync(n => n.Id == network.Id);

        Assert.Equal(operationId, reloaded.Operation.PendingId);
        Assert.Equal(NetworkStatus.Deleting, reloaded.Status);
        Assert.Equal(NetworkStatus.Errored, reloaded.Operation.LastSeenStatus);
        Assert.Equal("OLD", reloaded.Operation.FailureReason);
        Assert.Equal("old detail", reloaded.Operation.FailureDetail);
        Assert.NotNull(reloaded.Operation.FailedAt);
        Assert.Equal(NetworkStatus.Errored, reloaded.InstanceStatus);
    }
}
