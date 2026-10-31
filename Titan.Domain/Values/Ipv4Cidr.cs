using System.Diagnostics.CodeAnalysis;
using System.Net;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Values;

/// An IPv4 range the customer asks a network for: four octets 0–255 and a prefix 0–32. The address need not be the
/// first of its range — Incus takes ipv4.address as the router address with its prefix, and the API accepted
/// 10.0.0.5/24 before this type existed. The rule lives here so no path builds a network with a range Incus refuses
/// (titan-ops architecture/code-structure.md#logic-placement).
public sealed record Ipv4Cidr : ValueObject
{
    /// The validator's Matches() copies this string into api/openapi-v1.json as the field's `pattern`; it is looser
    /// than Parse (it lets /33 and 999 through), and stays as it is so the /v1 spec does not change.
    public const string Pattern = @"^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$";

    // The uplink of a network with public IPv6 routes only these (titan-ops infra/incus/design.md#ipv6-address-plan).
    private static readonly IPNetwork[] PrivateRanges =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
    ];

    public string Value { get; private init; } = default!;
    public IPAddress Address { get; private init; } = default!;
    public int PrefixLength { get; private init; }

    private Ipv4Cidr() { }

    public static Ipv4Cidr Parse(string? value)
    {
        if (!TryParse(value, out Ipv4Cidr? cidr))
        {
            throw new BusinessException("INVALID_CIDR", "An IPv4 CIDR is four octets 0-255 and a prefix 0-32, like 10.0.1.0/24.");
        }
        return cidr;
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out Ipv4Cidr? cidr)
    {
        cidr = null;
        if (value == null)
        {
            return false;
        }
        string[] addressAndPrefix = value.Split('/');
        if (addressAndPrefix.Length != 2)
        {
            return false;
        }
        // Not IPAddress.TryParse: it reads "10.1" as 10.0.0.1 and "0x0a" as 10.
        string[] octets = addressAndPrefix[0].Split('.');
        if (octets.Length != 4)
        {
            return false;
        }
        foreach (string octet in octets)
        {
            if (!IsDecimal(octet, 3, out int number) || number > 255)
            {
                return false;
            }
        }
        if (!IsDecimal(addressAndPrefix[1], 2, out int prefix) || prefix > 32)
        {
            return false;
        }
        cidr = new Ipv4Cidr { Value = value, Address = IPAddress.Parse(addressAndPrefix[0]), PrefixLength = prefix };
        return true;
    }

    /// Inside one of the three private ranges, with a prefix at least as long as the range's: a supernet of a private
    /// range is not private.
    public bool IsInsideAPrivateRange
    {
        get
        {
            foreach (IPNetwork range in PrivateRanges)
            {
                if (range.Contains(Address) && PrefixLength >= range.PrefixLength)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static bool IsDecimal(string text, int maxDigits, out int number)
    {
        number = 0;
        if (text.Length == 0 || text.Length > maxDigits)
        {
            return false;
        }
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }
        number = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    public override string ToString() => Value;
}
