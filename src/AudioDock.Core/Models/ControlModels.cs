namespace AudioDock.Core.Models;

public sealed record AudioControlCommand(
    ChangeKind Kind,
    string TargetId,
    AudioDirection? Direction = null,
    AudioRole? Role = null,
    VolumeLevel? Volume = null,
    bool? IsMuted = null);

public sealed record ControlWriteResult(bool Succeeded, string Detail, int? NativeErrorCode = null)
{
    public static ControlWriteResult Success(string detail = "The control write completed.") =>
        new(true, detail);

    public static ControlWriteResult Failure(string detail, int? nativeErrorCode = null) =>
        new(false, detail, nativeErrorCode);
}
