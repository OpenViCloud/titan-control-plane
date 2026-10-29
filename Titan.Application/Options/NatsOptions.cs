namespace Titan.Application.Options;

/// The NATS core notify bus is a speed layer on the durable outbox
/// (titan-ops architecture/money-and-wallet.md#single-writer-outbox): the DB is the queue, NATS only says "there is
/// work" sooner than a poll. Disabled by default, so control-plane is healthy without NATS; a missed
/// notify is a delayed apply that the sweeper drives, never lost money.
public class NatsOptions
{
    public const string SectionName = "Nats";

    /// Off = null publisher + no subscriber.
    public bool Enabled { get; set; } = false;

    /// nats://host:4222 of the cluster's NATS core. Blank is treated as disabled.
    public string Url { get; set; } = "";

    /// A per-wallet subject "<Prefix>.<walletId>" keeps ordering per wallet, and a single subscriber
    /// instance (control-plane replicas: 1) makes that one writer per wallet.
    public string SubjectPrefix { get; set; } = "wallet.mutation";

    /// One place decides it, so the DI wiring and the subscriber agree on what "on" means.
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Url);
}
