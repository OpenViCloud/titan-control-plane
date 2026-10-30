using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.Legal;
using Titan.Application.Commands.Platform.Legal;
using Titan.Application.Commands.Platform.Legal.Contracts;
using Titan.Application.Queries.Platform.Legal;
using Titan.Application.Queries.Platform.Legal.Contracts;
using Titan.Domain.Aggregates.Acceptance;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

// Click-wrap consent gate bound to the document version. A fake catalog makes the "current version"
// controllable for the version-binding negative case.
public class LegalServiceTests
{
    private const string Subject = "kc-user-1";

    private readonly Mock<IAcceptanceRepository> _repo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();

    public LegalServiceTests() =>
        _currentUser.SetupGet(u => u.KeycloakUserId).Returns(Subject);

    private sealed class FakeCatalog(string version) : ILegalDocumentCatalog
    {
        public LegalDocument Current { get; } =
            new("terms", version, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                English: new LegalText("Terms of Service", "english body"),
                Vietnamese: new LegalText("Điều khoản dịch vụ", "vietnamese body"));
    }

    private LegalFacade CreateService(string currentVersion = "v0-placeholder") =>
        new(new FakeCatalog(currentVersion), _repo.Object, _currentUser.Object);

    // Facade over the two legal query handlers and the RecordAcceptance command handler: it forwards
    // and adds nothing.
    private sealed class LegalFacade(
        ILegalDocumentCatalog catalog, IAcceptanceRepository acceptances, ICurrentUser currentUser)
    {
        public LegalDocumentQueryResult GetCurrentDocument(string? lang = null)
            => new GetLegalDocumentQueryHandler(catalog).HandleAsync(new GetLegalDocumentQuery(lang)).GetAwaiter().GetResult();

        public Task<ConsentStatusQueryResult> GetConsentStatusAsync(CancellationToken ct = default)
            => new GetConsentStatusQueryHandler(catalog, acceptances, currentUser).HandleAsync(new GetConsentStatusQuery(), ct);

        public Task RecordAcceptanceAsync(string? sourceIp, CancellationToken ct = default)
            => new RecordAcceptanceCommandHandler(catalog, acceptances, currentUser).HandleAsync(new RecordAcceptanceCommand(sourceIp), ct);
    }

    // ── GetConsentStatus ────────────────────────────────────────────────────

    [Fact]
    public async Task GetConsentStatus_NoAcceptance_MustAcceptTrue()
    {
        _repo.Setup(r => r.ExistsAsync(Subject, "terms", "v0-placeholder", It.IsAny<CancellationToken>()))
             .ReturnsAsync(false);

        var status = await CreateService().GetConsentStatusAsync();

        Assert.True(status.MustAcceptTerms);
        Assert.Equal("terms", status.DocumentKey);
        Assert.Equal("v0-placeholder", status.DocumentVersion);
    }

    [Fact]
    public async Task GetConsentStatus_AcceptedCurrentVersion_MustAcceptFalse()
    {
        _repo.Setup(r => r.ExistsAsync(Subject, "terms", "v0-placeholder", It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var status = await CreateService().GetConsentStatusAsync();

        Assert.False(status.MustAcceptTerms);
    }

    // The gate is bound to the CURRENT version: "already ticked" ≠ "accepted the current text". A check
    // on "any acceptance exists" would falsely pass this.
    [Fact]
    public async Task GetConsentStatus_AcceptedOnlyOldVersion_MustAcceptTrue()
    {
        // Old version accepted…
        _repo.Setup(r => r.ExistsAsync(Subject, "terms", "v0-placeholder", It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);
        // …but the current version (v1) is NOT.
        _repo.Setup(r => r.ExistsAsync(Subject, "terms", "v1", It.IsAny<CancellationToken>()))
             .ReturnsAsync(false);

        var status = await CreateService(currentVersion: "v1").GetConsentStatusAsync();

        Assert.True(status.MustAcceptTerms);
        Assert.Equal("v1", status.DocumentVersion);
    }

    // ── RecordAcceptance ─────────────────────────────────────────────────────

    // Exactly one proof, bound to the caller, the current version, and the IP.
    [Fact]
    public async Task RecordAcceptance_NotYetAccepted_AddsProofWithCurrentVersionAndIp()
    {
        _repo.Setup(r => r.ExistsAsync(Subject, "terms", "v0-placeholder", It.IsAny<CancellationToken>()))
             .ReturnsAsync(false);
        Acceptance? captured = null;
        _repo.Setup(r => r.AddAsync(It.IsAny<Acceptance>(), It.IsAny<CancellationToken>()))
             .Callback<Acceptance, CancellationToken>((a, _) => captured = a)
             .Returns(Task.CompletedTask);

        await CreateService().RecordAcceptanceAsync("203.0.113.7");

        Assert.NotNull(captured);
        Assert.Equal(Subject, captured!.SubjectId);
        Assert.Equal("terms", captured.DocumentKey);
        Assert.Equal("v0-placeholder", captured.DocumentVersion);
        Assert.Equal("203.0.113.7", captured.SourceIp);
        _repo.Verify(r => r.AddAsync(It.IsAny<Acceptance>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Idempotent — re-accepting a version already accepted records nothing new.
    [Fact]
    public async Task RecordAcceptance_AlreadyAccepted_NoOp()
    {
        _repo.Setup(r => r.ExistsAsync(Subject, "terms", "v0-placeholder", It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        await CreateService().RecordAcceptanceAsync("203.0.113.7");

        _repo.Verify(r => r.AddAsync(It.IsAny<Acceptance>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── GetCurrentDocument ───────────────────────────────────────────────────

    // Real catalog, so the placeholder shape is exercised too.
    [Fact]
    public void GetCurrentDocument_FromRealCatalog_HasKeyVersionEffectiveAt()
    {
        var service = new LegalFacade(new LegalDocumentCatalog(), _repo.Object, _currentUser.Object);

        var doc = service.GetCurrentDocument();

        Assert.Equal("terms", doc.Key);
        Assert.Equal("v0-placeholder", doc.Version);
        Assert.False(string.IsNullOrWhiteSpace(doc.Title));
        Assert.False(string.IsNullOrWhiteSpace(doc.Content));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), doc.EffectiveAt);
    }

    // titan-ops features/legal/design.md#legal-document-in-code: one version, one text per language.
    [Fact]
    public void GetCurrentDocument_Vietnamese_ReturnsTheVietnameseTextWithTheSameVersion()
    {
        var doc = CreateService("v7").GetCurrentDocument("vi");

        Assert.Equal("vi", doc.Lang);
        Assert.Equal("Điều khoản dịch vụ", doc.Title);
        Assert.Equal("vietnamese body", doc.Content);
        Assert.Equal("v7", doc.Version);
    }

    [Theory]
    [InlineData("en")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    public void GetCurrentDocument_EnglishMissingOrUnknownLanguage_ReturnsTheEnglishText(string? lang)
    {
        var doc = CreateService("v7").GetCurrentDocument(lang);

        Assert.Equal("en", doc.Lang);
        Assert.Equal("Terms of Service", doc.Title);
        Assert.Equal("english body", doc.Content);
        Assert.Equal("v7", doc.Version);
    }

    [Fact]
    public void GetCurrentDocument_LanguageIsNotCaseSensitive()
    {
        Assert.Equal("vi", CreateService().GetCurrentDocument("VI").Lang);
    }

    [Fact]
    public void RealCatalog_HasAnEnglishAndAVietnameseText()
    {
        var doc = new LegalDocumentCatalog().Current;

        Assert.Equal("Terms of Service", doc.English.Title);
        Assert.Equal("Điều khoản dịch vụ", doc.Vietnamese.Title);
        Assert.False(string.IsNullOrWhiteSpace(doc.English.Content));
        Assert.False(string.IsNullOrWhiteSpace(doc.Vietnamese.Content));
        Assert.NotEqual(doc.English.Content, doc.Vietnamese.Content);
    }
}
