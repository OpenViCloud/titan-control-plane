using FastEndpoints;
using FluentValidation;

namespace Titan.Infrastructure.Endpoints.Network;

public class CreateFirewallRuleValidator : Validator<CreateFirewallRuleRequest>
{
    private static readonly System.Text.RegularExpressions.Regex CidrRegex =
        new(@"^(\d{1,3}\.){3}\d{1,3}/(\d|[1-2]\d|3[0-2])$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
            RuleFor(x => x.StartPort).Must(p => p.HasValue).WithMessage("'StartPort' is required for TCP/UDP.")
                .InclusiveBetween(1, 65535).WithMessage("'StartPort' must be between 1 and 65535.");
            RuleFor(x => x.EndPort).Must(p => p.HasValue).WithMessage("'EndPort' is required for TCP/UDP.")
                .InclusiveBetween(1, 65535).WithMessage("'EndPort' must be between 1 and 65535.");
            RuleFor(x => x).Must(r => r.StartPort <= r.EndPort)
                .WithMessage("'StartPort' must be less than or equal to 'EndPort'.")
                .When(r => r.StartPort.HasValue && r.EndPort.HasValue);
        });

        When(x => x.Protocol is "ICMP" or "All", () =>
        {
            RuleFor(x => x.StartPort).Null().WithMessage("'StartPort' must be null for ICMP/All protocol.");
            RuleFor(x => x.EndPort).Null().WithMessage("'EndPort' must be null for ICMP/All protocol.");
        });

        RuleFor(x => x.Cidr).NotEmpty()
            .Must(c => CidrRegex.IsMatch(c) || IsIpv6Cidr(c))
            .WithMessage("'Cidr' must be a valid IPv4 or IPv6 CIDR notation (e.g. 0.0.0.0/0 or ::/0).");
    }

    // Written as the range's first address; not ::ffff:a.b.c.d, an IPv4 range whose ICMP family the worker would guess.
    private static bool IsIpv6Cidr(string cidr)
    {
        bool parsed = System.Net.IPNetwork.TryParse(cidr, out var network);
        if (!parsed)
        {
            return false;
        }
        var address = network.BaseAddress;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6;
    }
}
