namespace AudioDock.Windows;

/// <summary>
/// Marks the future Windows Core Audio adapter assembly. No native audio API is invoked in milestone 1.
/// </summary>
public static class WindowsAdapterBoundary
{
    public const bool NativeInventoryImplemented = false;
}
