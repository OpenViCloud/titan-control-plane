using FastEndpoints;
using FluentValidation;
using Titan.Domain.Values;

namespace Titan.Infrastructure.Endpoints.Network;

// Same rule as CreateNetworkValidator, read from the Domain's NetworkName so the two cannot drift.
public class RenameNetworkValidator : Validator<RenameNetworkRequest>
{
    private static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(NetworkName.Pattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    public RenameNetworkValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(NetworkName.MaxLength)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, hyphens, and underscores, and must start with a letter or digit.");
    }
}
