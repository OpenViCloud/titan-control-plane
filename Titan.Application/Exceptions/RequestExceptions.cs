namespace Titan.Application.Exceptions;

// What the Application or the edge says about a request, not what the Domain says about a rule: the caller is not
// allowed (403), the request is malformed (400), its body is over the route's limit (413), or a system the request needs
// is down or misconfigured (503).
// ErrorStatus maps them.

public class ForbiddenException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public class BadRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public class ServiceUnavailableException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public class PayloadTooLargeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
