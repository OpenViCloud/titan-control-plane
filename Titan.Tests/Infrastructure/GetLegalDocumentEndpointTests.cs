using FastEndpoints;
using Titan.Application.Queries.Platform.Legal;
using Titan.Application.Queries.Platform.Legal.Contracts;
using Titan.Application.Services.Platform.Legal;
using Titan.Infrastructure.Endpoints.Legal;

namespace Titan.Tests.Infrastructure;

// GetLegalDocumentEndpoint carries its own logic: it reads ?lang= off the query string
// (titan-ops features/legal/design.md#legal-endpoints). The handler is concrete, so it runs for real on the real catalog.
public class GetLegalDocumentEndpointTests
{
    private static async Task<LegalDocumentQueryResult> Get(string queryString)
    {
        var ep = Factory.Create<GetLegalDocumentEndpoint>(
            ctx => ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(queryString),
            new GetLegalDocumentQueryHandler(new LegalDocumentCatalog()));

        await ep.HandleAsync(CancellationToken.None);

        return ep.Response;
    }

    [Fact]
    public async Task HandleAsync_LangVi_SendsTheVietnameseText()
    {
        var doc = await Get("?lang=vi");

        Assert.Equal("vi", doc.Lang);
        Assert.Equal("Điều khoản dịch vụ", doc.Title);
    }

    [Fact]
    public async Task HandleAsync_NoLang_SendsTheEnglishText()
    {
        var doc = await Get("");

        Assert.Equal("en", doc.Lang);
        Assert.Equal("Terms of Service", doc.Title);
    }
}
