using System.Text;
using FluentValidation;
using FunctionSecretAggregate = Titan.Domain.Aggregates.FunctionSecret.FunctionSecret;

namespace Titan.Infrastructure.Endpoints.FunctionSecret;

// The value rule both secret routes share (titan-ops features/ovc-function/design.md#function-secrets): present, and 1
// to FunctionSecret.MaxValueBytes bytes of UTF-8. The message never repeats the value.
internal static class FunctionSecretValueRule
{
    internal static IRuleBuilderOptions<T, string?> IsASecretValue<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(v => v != null && FunctionSecretAggregate.IsAValueSize(Encoding.UTF8.GetByteCount(v)))
            .WithMessage($"'value' is required, 1 to {FunctionSecretAggregate.MaxValueBytes} bytes of UTF-8.");
}
