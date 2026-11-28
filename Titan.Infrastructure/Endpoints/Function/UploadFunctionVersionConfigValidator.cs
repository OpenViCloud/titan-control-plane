using System.Text.Json;
using FastEndpoints;
using FluentValidation;
using FluentValidation.Results;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;
using FunctionSecretAggregate = Titan.Domain.Aggregates.FunctionSecret.FunctionSecret;

namespace Titan.Infrastructure.Endpoints.Function;

// The limits are the Domain's (titan-ops features/ovc-function/design.md#function-limits). The upload endpoint binds no
// request, so it runs this on the config part itself.
public class UploadFunctionVersionConfigValidator : Validator<UploadFunctionVersionConfig>
{
    public const int HandlerMaxLength = 128;

    // "module.function": the path of the file (dots for directories), then the exported function; the same shape the zip
    // inspector holds the zip to, so a handler that passes here is not refused there.
    private static readonly System.Text.RegularExpressions.Regex HandlerShape =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public UploadFunctionVersionConfigValidator()
    {
        RuleFor(x => x.Runtime).NotEmpty().WithMessage("'runtime' is required.");
        RuleFor(x => x.Handler!)
            .MaximumLength(HandlerMaxLength)
            .Matches(HandlerShape).WithMessage("'handler' is \"module.function\".")
            .When(x => !string.IsNullOrEmpty(x.Handler));
        RuleFor(x => x.MemoryMiB!.Value)
            .Must(FunctionAggregate.IsAMemorySize)
            .WithMessage($"'memoryMiB' is {FunctionAggregate.MinMemoryMiB} to {FunctionAggregate.MaxMemoryMiB} in steps of {FunctionAggregate.MemoryStepMiB}.")
            .When(x => x.MemoryMiB != null);
        RuleFor(x => x.TimeoutSeconds!.Value)
            .Must(FunctionAggregate.IsATimeout)
            .WithMessage($"'timeoutSeconds' is {FunctionAggregate.MinTimeoutSeconds} to {FunctionAggregate.MaxTimeoutSeconds}.")
            .When(x => x.TimeoutSeconds != null);
        RuleFor(x => x.Schedule!)
            .Must(FunctionAggregate.IsASchedule)
            .WithMessage("'schedule' is five cron fields in UTC (minute hour day-of-month month day-of-week), each `*` or numbers, ranges and steps; no names or macros.")
            .When(x => x.Schedule != null);
        // titan-ops features/ovc-function/design.md#function-env: names, reserved names, one name in one place, and the
        // size of what is known here; the secrets' own values are weighed by the handler, which reads their lengths.
        RuleFor(x => x).Custom((config, context) =>
        {
            Dictionary<string, string?> env = config.Env ?? [];
            Dictionary<string, UploadFunctionVersionSecretConfig?> secrets = config.Secrets ?? [];
            foreach ((string name, string? value) in env)
            {
                CheckName(context, "env", name);
                if (value == null)
                {
                    context.AddFailure("env", $"'env.{name}' is a string.");
                }
            }
            foreach ((string name, UploadFunctionVersionSecretConfig? secret) in secrets)
            {
                CheckName(context, "secrets", name);
                CheckSecret(context, name, secret);
                if (env.ContainsKey(name))
                {
                    context.AddFailure("secrets", $"'{name}' is in both 'env' and 'secrets'.");
                }
            }
            Dictionary<string, string> values = env.ToDictionary(p => p.Key, p => p.Value ?? "", StringComparer.Ordinal);
            int known = FunctionAggregate.ConfigBytes(values, secrets.Keys.Select(k => (k, 0)));
            if (!FunctionAggregate.IsAConfigSize(known))
            {
                context.AddFailure("env", $"'env' and 'secrets' are at most {FunctionAggregate.MaxConfigBytes} bytes together.");
            }
        });
    }

    private static void CheckName(FluentValidation.ValidationContext<UploadFunctionVersionConfig> context, string part, string name)
    {
        if (!FunctionAggregate.IsAnEnvName(name))
        {
            context.AddFailure(new ValidationFailure(part,
                $"'{part}' names are 1 to {FunctionAggregate.EnvNameMaxLength} letters, digits or underscores, not starting with a digit."));
        }
        else if (FunctionAggregate.IsAReservedEnvName(name))
        {
            context.AddFailure(new ValidationFailure(part,
                $"'{name}' is set by the platform: names starting {FunctionAggregate.ReservedEnvPrefix}, and {string.Join(", ", FunctionAggregate.ReservedEnvNames)}, are reserved."));
        }
    }

    private static void CheckSecret(FluentValidation.ValidationContext<UploadFunctionVersionConfig> context, string name, UploadFunctionVersionSecretConfig? secret)
    {
        if (secret == null || !FunctionSecretAggregate.IsAName(secret.Secret))
        {
            context.AddFailure("secrets", $"'secrets.{name}.secret' is the name of one of the tenant's function secrets.");
        }
        if (secret == null || !UploadFunctionVersionSecretConfig.VersionOf(secret.Version).IsValid)
        {
            context.AddFailure("secrets", $"'secrets.{name}.version' is \"latest\" or a version number, 1 or more.");
        }
    }
}
