using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Network;

namespace Titan.Infrastructure.Persistence;

public class FirewallRuleConfiguration : IEntityTypeConfiguration<FirewallRule>
{
    public void Configure(EntityTypeBuilder<FirewallRule> builder)
    {
        builder.ToTable("firewall_rules");
        builder.HasKey(r => r.Id);
        // The aggregate sets the id. Left "generated on add", EF reads a rule added to a tracked network as one already
        // saved and updates it: 0 rows, DbUpdateConcurrencyException (same trap as LoadBalancerListener;
        // FirewallRuleQuotaRaceDbTests.TwoCreatesOneBelowTheCap_OneIs201_TheOtherIsQuotaExceeded_AndTheCapHolds).
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.NetworkId).HasColumnName("network_id");
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Direction).HasColumnName("direction").HasConversion<string>().IsRequired();
        builder.Property(r => r.Protocol).HasColumnName("protocol").HasConversion<string>().IsRequired();
        builder.Property(r => r.StartPort).HasColumnName("start_port");
        builder.Property(r => r.EndPort).HasColumnName("end_port");
        builder.Property(r => r.Cidr).HasColumnName("cidr").IsRequired();
        builder.Property(r => r.ExternalId).HasColumnName("external_id");
        // The Incus ACL rule state as Incus spells it (enabled · disabled · logged): the enum member names are the
        // words (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
        builder.Property(r => r.CurrentState).HasColumnName("current_state").HasConversion<string>();
        builder.Property(r => r.CurrentExistence).HasColumnName("current_existence").HasConversion<string>().IsRequired();
        builder.Property(r => r.DesiredExistence).HasColumnName("desired_existence").HasConversion<string>().IsRequired();
        // The rule's own waiting operation and last failure, six columns the entity writes through one
        // FirewallRuleOperation (titan-ops features/network/design.md#network-operation).
        builder.OwnsOne(r => r.Operation, o =>
        {
            o.Property(x => x.PendingId).HasColumnName("pending_request_id");
            o.Property(x => x.Kind).HasColumnName("pending_request_kind").HasConversion<string>();
            o.Property(x => x.AskedAt).HasColumnName("pending_request_asked_at");
            o.Property(x => x.FailureReason).HasColumnName("request_failure_reason");
            o.Property(x => x.FailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
            o.Property(x => x.FailedAt).HasColumnName("request_failed_at");
        });
        builder.Ignore(r => r.Transition);
        builder.Ignore(r => r.Ports);
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(r => r.NetworkId);
    }
}
