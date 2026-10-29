using System.Reflection;
using System.Text.Json;
using FastEndpoints;

namespace Titan.Tests.Api;

// titan-ops features/public-api/design.md#nullable-request-fields: a request field the endpoint binds as nullable is
// optional and nullable in the public spec, whatever validator rules sit on it. Reads the spec the running host serves.
// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class RequestFieldSpecTests(OpenApiSpecTests.ApiFactory factory) : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    [Fact]
    public async Task NullableRequestField_IsOptionalAndNullableInTheSpec()
    {
        var raw = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        var schemas = JsonDocument.Parse(raw).RootElement.GetProperty("components").GetProperty("schemas");

        var nullability = new NullabilityInfoContext();
        var problems = new List<string>();
        var checkedFields = new List<string>();
        foreach (var requestType in PublicRequestTypes(schemas))
        {
            var schema = schemas.GetProperty(requestType.Name);
            var required = schema.TryGetProperty("required", out var r)
                ? r.EnumerateArray().Select(e => e.GetString()).ToHashSet()
                : [];
            foreach (var property in requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (nullability.Create(property).WriteState != NullabilityState.Nullable)
                {
                    continue;
                }
                string field = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                // A field bound from the route or the query string is a parameter, not a body property.
                if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(field, out var spec))
                {
                    continue;
                }
                checkedFields.Add($"{requestType.Name}.{field}");
                if (required.Contains(field))
                {
                    problems.Add($"{requestType.Name}.{field} is required");
                }
                bool nullable = spec.TryGetProperty("nullable", out var n) && n.GetBoolean();
                if (!nullable)
                {
                    problems.Add($"{requestType.Name}.{field} is not nullable: {spec}");
                }
            }
        }

        Assert.Contains("CreateFirewallRuleRequest.startPort", checkedFields);
        Assert.Contains("CreateFirewallRuleRequest.endPort", checkedFields);
        Assert.Empty(problems);
    }

    // Request types of the endpoints whose body schema is in the public spec (internal and admin routes are left out of it).
    private static IEnumerable<Type> PublicRequestTypes(JsonElement schemas) =>
        typeof(Titan.Infrastructure.Endpoints.Network.CreateFirewallRuleEndpoint).Assembly.GetTypes()
            .Where(t => !t.IsAbstract)
            .Select(RequestTypeOf)
            .Where(t => t is not null && t != typeof(EmptyRequest) && schemas.TryGetProperty(t.Name, out _))
            .Select(t => t!)
            .Distinct();

    private static Type? RequestTypeOf(Type endpoint)
    {
        for (var type = endpoint.BaseType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Endpoint<,>))
            {
                return type.GetGenericArguments()[0];
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Endpoint<>))
            {
                return type.GetGenericArguments()[0];
            }
        }
        return null;
    }
}
