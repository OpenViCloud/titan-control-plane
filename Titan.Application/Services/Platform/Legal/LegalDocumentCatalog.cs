namespace Titan.Application.Services.Platform.Legal;

// Content is git-tracked, not a CMS table (titan-ops features/legal/design.md#legal-document-in-code): bumping
// content = bumping Version here. Go-live is blocked until legal sign-off replaces the placeholder
// (BLOCK-LEGAL-1).

public interface ILegalDocumentCatalog
{
    /// The one gated document. Its Version is what an Acceptance binds to.
    LegalDocument Current { get; }
}

public class LegalDocumentCatalog : ILegalDocumentCatalog
{
    public LegalDocument Current { get; } = new(
        Key: "terms",
        Version: "v0-placeholder",
        EffectiveAt: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        English: new LegalText("Terms of Service", EnglishPlaceholder),
        Vietnamese: new LegalText("Điều khoản dịch vụ", VietnamesePlaceholder));

    // PLACEHOLDER — NOT legally reviewed; blocks production go-live (BLOCK-LEGAL-1). A lawyer replaces both texts and the
    // Version bumps to v1. One document with four clearly separated sections: a merged file, not merged content.
    // The two languages say the same thing; one Version covers both, so accepting either is the same acceptance.
    private const string EnglishPlaceholder = """
        # Terms of Service (DRAFT — waiting for a lawyer)

        > ⚠️ This is placeholder text and has NOT had a legal review. Not for production.

        ## 1. Terms of Service
        (TODO — written by a lawyer)

        ## 2. Privacy policy
        (TODO — written by a lawyer. Keep this section clear and separate — Decree 13/2023.)

        ## 3. Acceptable use policy
        (TODO — written by a lawyer. No using VMs or networks for spam, mining or attacks.)

        ## 4. Payment, refunds and SLA
        (TODO — written by a lawyer. Closed-loop credit, NOT withdrawable or transferable as cash — docs/architecture/money-and-wallet.md#tax-legal-posture.)
        """;

    private const string VietnamesePlaceholder = """
        # Điều khoản dịch vụ (BẢN NHÁP — chờ luật sư)

        > ⚠️ Đây là nội dung placeholder, CHƯA được rà soát pháp lý. Không dùng cho production.

        ## 1. Điều khoản dịch vụ (Terms of Service)
        (TODO — luật sư soạn)

        ## 2. Chính sách bảo mật (Privacy)
        (TODO — luật sư soạn. Giữ mục này rõ ràng, tách bạch — Nghị định 13/2023.)

        ## 3. Chính sách sử dụng hợp lệ (Acceptable Use)
        (TODO — luật sư soạn. Cấm dùng VM/network để spam, mining, tấn công.)

        ## 4. Thanh toán, hoàn tiền & SLA (Payment / Refund / SLA)
        (TODO — luật sư soạn. Credit closed-loop, KHÔNG rút/chuyển ra tiền mặt — docs/architecture/money-and-wallet.md#tax-legal-posture.)
        """;
}

// The catalog's own record — not a query contract: RecordAcceptance (a command) reads catalog.Current
// too, so it cannot sit on either side's Contracts/ (titan-ops architecture/code-structure.md#no-contract-reuse).
public record LegalDocument(string Key, string Version, DateTimeOffset EffectiveAt, LegalText English, LegalText Vietnamese);

public record LegalText(string Title, string Content);
