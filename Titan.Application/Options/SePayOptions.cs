namespace Titan.Application.Options;

/// Config for the inbound SePay webhook. ApiKey verifies the `Authorization: Apikey <key>`
/// header; it is the SePay account key, separate from the worker→CP X-Internal-Token. Delivered via
/// sealed-secret/env (SePay__ApiKey), never committed (titan-ops architecture/money-and-wallet.md#sepay-writer / titan-ops features/billing/topup/design.md#sepay-flow).
public class SePayOptions
{
    public const string SectionName = "SePay";

    public string ApiKey { get; set; } = "";
}
