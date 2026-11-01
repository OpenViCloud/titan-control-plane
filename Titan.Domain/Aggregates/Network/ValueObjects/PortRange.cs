using System.Diagnostics.CodeAnalysis;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Network;

/// The ports a TCP or UDP firewall rule covers, both ends inclusive: 1–65535 and start ≤ end. A single port is a
/// range whose ends are equal. The rule lives here so no path builds a rule Incus refuses
/// (titan-ops architecture/code-structure.md#logic-placement); the validator asks Of for its 400s.
public sealed record PortRange : ValueObject
{
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    public int Start { get; private init; }
    public int End { get; private init; }

    private PortRange() { }

    public static PortRange Of(int start, int end)
    {
        bool bothArePorts = start >= MinPort && start <= MaxPort && end >= MinPort && end <= MaxPort;
        if (!bothArePorts || start > end)
        {
            throw new BusinessException("INVALID_PORT_RANGE",
                $"A port range is two ports {MinPort}-{MaxPort} with the start at or before the end.");
        }
        return new PortRange { Start = start, End = end };
    }

    public override string ToString() => Start == End ? $"{Start}" : $"{Start}-{End}";
}
