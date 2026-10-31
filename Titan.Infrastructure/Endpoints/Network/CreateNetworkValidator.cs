using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Domain.Aggregates.Network;

namespace Titan.Infrastructure.Endpoints.Network;

// The rules are the Domain's (NetworkName, Ipv4Cidr); this translates them to 400 in the API's words. Matches() is
// kept on the Domain's Pattern because the spec copies it as the field's `pattern`; Must() adds the stricter check
// (octets 0-255, prefix 0-32) that no pattern in the spec expresses.
public class CreateNetworkValidator : Validator<CreateNetworkCommand>
{
    private static readonly System.Text.RegularExpressions.Regex CidrRegex =
        new(Ipv4Cidr.Pattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(NetworkName.Pattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    private const string CidrMessage = "'Cidr' must be a valid CIDR notation (e.g. 10.0.1.0/24).";

    public CreateNetworkValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(NetworkName.MaxLength)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, hyphens, and underscores, and must start with a letter or digit.");
        RuleFor(x => x.ClusterId).NotEmpty();
        When(x => x.Cidr is not null, () =>
            RuleFor(x => x.Cidr!).Cascade(CascadeMode.Stop)
                .Matches(CidrRegex).WithMessage(CidrMessage)
                .Must(c => Ipv4Cidr.TryParse(c, out _)).WithMessage(CidrMessage));
        // The uplink of a network with public IPv6 routes only these (titan-ops infra/incus/design.md#ipv6-address-plan).
        // A CIDR that does not parse is answered by the rule above alone.
        When(x => x.EnablePublicIpv6 && x.Cidr is not null, () =>
            RuleFor(x => x.Cidr!).Must(IsInsideAPrivateRangeOrNotACidr)
                .WithMessage("'Cidr' of a network with public IPv6 must be inside 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16."));
    }

    private static bool IsInsideAPrivateRangeOrNotACidr(string cidr)
    {
        if (!Ipv4Cidr.TryParse(cidr, out Ipv4Cidr? parsed))
        {
            return true;
        }
        return parsed.IsInsideAPrivateRange;
    }
}
