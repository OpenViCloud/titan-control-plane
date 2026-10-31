namespace Titan.Domain.Aggregates.Vm;

/// What the caller must do with a request the VM accepted (titan-ops architecture/intent-and-observation.md#intent-has-identity). A repeat or an already-done
/// request is never accepted — the aggregate refuses it (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat) — so there is no "nothing to do".
public enum VmIntentOutcome
{
    Dispatch,

    /// Delete of a VM that never reached the provider. The caller calls Destroy() after its own
    /// preconditions, which is why RequestDelete() does not destroy it.
    FinishLocally
}
