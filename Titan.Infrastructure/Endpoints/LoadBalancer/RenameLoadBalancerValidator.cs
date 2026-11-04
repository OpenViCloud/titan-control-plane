using FastEndpoints;
using FluentValidation;

using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;

namespace Titan.Infrastructure.Endpoints.LoadBalancer;

// The rule is the Domain's (titan-ops architecture/code-structure.md#logic-placement), the same as create; this answers 400
// in the API's words.
public class RenameLoadBalancerValidator : Validator<RenameLoadBalancerBody>
{
    private static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(LoadBalancerAggregate.NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    public RenameLoadBalancerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(LoadBalancerAggregate.NameMaxLength)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, hyphens, and underscores, and must start with a letter or digit.");
    }
}
