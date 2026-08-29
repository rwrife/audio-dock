namespace AudioDock.Core.Models;

public enum AudioDirection
{
    Render,
    Capture,
}

public enum AudioRole
{
    Console,
    Multimedia,
    Communications,
}

public enum EndpointState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged,
}

public enum SessionState
{
    Active,
    Inactive,
    Expired,
}

public enum MatchStatus
{
    Matched,
    Unmatched,
    Ambiguous,
}

public enum ChangeKind
{
    DefaultRole,
    EndpointVolume,
    EndpointMute,
    SessionVolume,
    SessionMute,
}

public enum PlanDisposition
{
    Apply,
    NoOp,
    Skipped,
}

public enum OperationResultState
{
    Applied,
    NoOp,
    Skipped,
    Failed,
    VerificationMismatch,
    RolledBack,
    RollbackFailed,
    Deferred,
}

public enum ApplyOverallState
{
    Applied,
    Partial,
    Failed,
    Canceled,
    RolledBack,
}

public enum RollbackState
{
    NotRequired,
    Available,
    Attempted,
    Succeeded,
    Failed,
    Cleared,
}
