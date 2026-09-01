namespace AudioDock.Core.Execution;

public sealed class StaleScenePreviewException(string message) : InvalidOperationException(message);
