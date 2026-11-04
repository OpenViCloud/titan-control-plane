using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;

using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;

namespace Titan.Infrastructure.Endpoints.LoadBalancer;

// The rule is the Domain's (titan-ops architecture/code-structure.md#logic-placement); this answers 400 in the API's words.
public class CreateLoadBalancerValidator : Validator<CreateLoadBalancerCommand>
{
    private static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(LoadBalancerAggregate.NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    public CreateLoadBalancerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(LoadBalancerAggregate.NameMaxLength)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, hyphens, and underscores, and must start with a letter or digit.");
        RuleFor(x => x.NetworkId).NotEmpty();
        RuleFor(x => x.Listeners).Must(LoadBalancerSpecRules.ListenersAreValid).WithMessage(LoadBalancerSpecRules.ListenersMessage);
        RuleFor(x => x.Backends).Must(LoadBalancerSpecRules.BackendsAreValid).WithMessage(LoadBalancerSpecRules.BackendsMessage);
    }
}
