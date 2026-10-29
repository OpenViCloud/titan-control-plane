namespace Titan.Infrastructure.Auth;

/// A create refuses a request without an Idempotency-Key, so a retried create replays the first answer instead
/// of creating a second resource (titan-ops architecture/http-contract.md#idempotency-key). ApiIdempotencyKeyTests
/// pins the set.
public interface IRequiresIdempotencyKey;
