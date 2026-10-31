using FastEndpoints;
using FluentValidation;
using Titan.Domain.Aggregates.Network;

namespace Titan.Infrastructure.Endpoints.Network;

// The port and CIDR rules are the Domain's (PortRange, FirewallCidr); this translates them to 400 in the API's words
// and pairs the ports with the protocol, which stays here (titan-ops features/network/design.md#firewall-on-incus).
public class CreateFirewallRuleValidator : Validator<CreateFirewallRuleRequest>
{
    private static readonly string[] ValidDirections = ["Ingress", "Egress"];
    private static readonly string[] ValidProtocols = ["TCP", "UDP", "ICMP", "All"];

    public CreateFirewallRuleValidator()
    {
        RuleFor(x => x.Direction).NotEmpty()
            .Must(d => ValidDirections.Contains(d)).WithMessage("'Direction' must be 'Ingress' or 'Egress'.");

        RuleFor(x => x.Protocol).NotEmpty()
            .Must(p => ValidProtocols.Contains(p)).WithMessage("'Protocol' must be 'TCP', 'UDP', 'ICMP', or 'All'.");

        When(x => x.Protocol is "TCP" or "UDP", () =>
        {
            // A predicate, not NotNull(): the spec copies NotNull() as `required` without the When, and ICMP/All
            // must send no port (titan-ops features/public-api/design.md#nullable-request-fields).
            // InclusiveBetween is what the spec copies as the field's minimum and maximum.
            RuleFor(x => x.StartPort).Must(p => p.HasValue).WithMessage("'StartPort' is required for TCP/UDP.")
                .InclusiveBetween(PortRange.MinPort, PortRange.MaxPort).WithMessage("'StartPort' must be between 1 and 65535.");
            RuleFor(x => x.EndPort).Must(p => p.HasValue).WithMessage("'EndPort' is required for TCP/UDP.")
                .InclusiveBetween(PortRange.MinPort, PortRange.MaxPort).WithMessage("'EndPort' must be between 1 and 65535.");
            RuleFor(x => x).Must(StartIsAtOrBeforeEnd)
                .WithMessage("'StartPort' must be less than or equal to 'EndPort'.")
                .When(r => r.StartPort.HasValue && r.EndPort.HasValue
                           && PortRange.IsPort(r.StartPort.Value) && PortRange.IsPort(r.EndPort.Value));
        });

        When(x => x.Protocol is "ICMP" or "All", () =>
        {
            RuleFor(x => x.StartPort).Null().WithMessage("'StartPort' must be null for ICMP/All protocol.");
            RuleFor(x => x.EndPort).Null().WithMessage("'EndPort' must be null for ICMP/All protocol.");
        });

        RuleFor(x => x.Cidr).NotEmpty()
            .Must(c => FirewallCidr.TryParse(c, out _))
            .WithMessage("'Cidr' must be a valid IPv4 or IPv6 CIDR notation (e.g. 0.0.0.0/0 or ::/0).");
    }

    // Reached only when both ports are present and in range (the When above); the null checks are for the compiler.
    private static bool StartIsAtOrBeforeEnd(CreateFirewallRuleRequest r)
    {
        if (r.StartPort == null || r.EndPort == null)
        {
            return true;
        }
        return PortRange.TryOf(r.StartPort.Value, r.EndPort.Value, out _);
    }
}
