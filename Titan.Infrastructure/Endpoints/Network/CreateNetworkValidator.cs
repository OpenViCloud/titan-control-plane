using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Networking.Network.Contracts;

namespace Titan.Infrastructure.Endpoints.Network;

public class CreateNetworkValidator : Validator<CreateNetworkCommand>
{
    private static readonly System.Text.RegularExpressions.Regex CidrRegex =
        new(@"^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(@"^[a-zA-Z0-9][a-zA-Z0-9_-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public CreateNetworkValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, hyphens, and underscores, and must start with a letter or digit.");
        RuleFor(x => x.ClusterId).NotEmpty();
        When(x => x.Cidr is not null, () =>
            RuleFor(x => x.Cidr!).Matches(CidrRegex).WithMessage("'Cidr' must be a valid CIDR notation (e.g. 10.0.1.0/24)."));
        // The uplink of a network with public IPv6 routes only these (https://github.com/OpenViCloud/titan-ops/blob/main/docs/infra/incus/design.md#ipv6-address-plan).
        When(x => x.EnablePublicIpv6 && x.Cidr is not null, () =>
            RuleFor(x => x.Cidr!).Must(IsInsideAPrivateRange)
                .WithMessage("'Cidr' of a network with public IPv6 must be inside 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16."));
    }

    private static readonly System.Net.IPNetwork[] PrivateRanges =
    [
        System.Net.IPNetwork.Parse("10.0.0.0/8"),
        System.Net.IPNetwork.Parse("172.16.0.0/12"),
        System.Net.IPNetwork.Parse("192.168.0.0/16"),
    ];

    private static bool IsInsideAPrivateRange(string cidr)
    {
        string[] parts = cidr.Split('/');
        bool parsed = parts.Length == 2
            && System.Net.IPAddress.TryParse(parts[0], out _)
            && int.TryParse(parts[1], out _);
        if (!parsed)
        {
            return false;
        }
        var address = System.Net.IPAddress.Parse(parts[0]);
        int prefix = int.Parse(parts[1]);
        if (prefix > 32)
        {
            return false;
        }
        foreach (var range in PrivateRanges)
        {
            if (range.Contains(address) && prefix >= range.PrefixLength)
            {
                return true;
            }
        }
        return false;
    }
}
