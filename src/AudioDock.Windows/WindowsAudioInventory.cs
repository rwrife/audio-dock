using System.Runtime.CompilerServices;
using AudioDock.Core.Abstractions;
using AudioDock.Core.Models;

namespace AudioDock.Windows;

public sealed class WindowsAudioInventory : IAudioInventory, IDisposable
{
    private readonly IWindowsAudioInventoryBackend backend;
    private readonly CancellationTokenSource disposalCancellation = new();
    private bool disposed;

    public WindowsAudioInventory()
        : this(new CoreAudioInventoryBackend())
    {
    }

    public WindowsAudioInventory(IWindowsAudioInventoryBackend backend)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public AdapterCapabilities Capabilities { get; } = new(
        CanInventoryEndpoints: true,
        CanInventorySessions: true,
        CanObserveChanges: true,
        Limitation: "Read-only snapshot polling; endpoint-role mutation is not implemented.");

    public ValueTask<AudioSnapshot> CaptureAsync(
        AudioInventoryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            NativeInventorySnapshot native = backend.Capture(
                options?.IncludeExecutablePaths == true,
                cancellationToken);
            EndpointDescriptor[] endpoints = native.Endpoints
                .OrderBy(endpoint => endpoint.StableId, StringComparer.Ordinal)
                .ToArray();
            SessionDescriptor[] sessions = native.Sessions
                .Select(session => options?.IncludeExecutablePaths == true
                    ? session
                    : session with { ExecutablePath = null })
                .OrderBy(session => session.SessionId, StringComparer.Ordinal)
                .ToArray();
            InventoryDiagnostic[] diagnostics = native.Diagnostics
                .OrderBy(diagnostic => diagnostic.Scope, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.StableId, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.NativeErrorCode)
                .ToArray();
            return ValueTask.FromResult(new AudioSnapshot(
                DateTimeOffset.UtcNow,
                endpoints,
                sessions,
                diagnostics));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not AudioInventoryException)
        {
            throw new AudioInventoryException("Core Audio inventory failed before a usable snapshot could be produced.", exception);
        }
    }

    public async IAsyncEnumerable<AudioSnapshot> ObserveAsync(
        AudioInventoryOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        TimeSpan interval = options?.PollInterval ?? TimeSpan.FromSeconds(2);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Poll interval must be positive.");
        }

        using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            disposalCancellation.Token);
        CancellationToken observationToken = observationCancellation.Token;
        string? previousFingerprint = null;
        while (true)
        {
            observationToken.ThrowIfCancellationRequested();
            AudioSnapshot snapshot = await CaptureAsync(options, observationToken).ConfigureAwait(false);
            string fingerprint = SnapshotFingerprint.Create(snapshot);
            if (!StringComparer.Ordinal.Equals(previousFingerprint, fingerprint))
            {
                previousFingerprint = fingerprint;
                yield return snapshot;
            }

            await Task.Delay(interval, observationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        disposalCancellation.Cancel();
        backend.Dispose();
        disposalCancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}

public interface IWindowsAudioInventoryBackend : IDisposable
{
    NativeInventorySnapshot Capture(bool includeExecutablePaths, CancellationToken cancellationToken);
}

public sealed record NativeInventorySnapshot(
    IReadOnlyList<EndpointDescriptor> Endpoints,
    IReadOnlyList<SessionDescriptor> Sessions,
    IReadOnlyList<InventoryDiagnostic> Diagnostics);

public sealed class AudioInventoryException : Exception
{
    public AudioInventoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class SnapshotFingerprint
{
    internal static string Create(AudioSnapshot snapshot) => string.Join('\n',
        snapshot.Endpoints.OrderBy(x => x.StableId, StringComparer.Ordinal).Select(x =>
            $"E|{x.StableId}|{x.FriendlyName}|{x.Direction}|{x.State}|{string.Join(',', x.DefaultRoles.Order())}|{x.Volume}|{x.IsMuted}")
        .Concat(snapshot.Sessions.OrderBy(x => x.SessionId, StringComparer.Ordinal).Select(x =>
            $"S|{x.SessionId}|{x.ProcessName}|{x.State}|{x.Volume}|{x.IsMuted}|{x.PackageFamilyName}|{x.ExecutablePath}"))
        .Concat((snapshot.Diagnostics ?? [])
            .OrderBy(x => x.Scope, StringComparer.Ordinal)
            .ThenBy(x => x.StableId, StringComparer.Ordinal)
            .ThenBy(x => x.Message, StringComparer.Ordinal)
            .ThenBy(x => x.NativeErrorCode)
            .Select(x => $"D|{x.Scope}|{x.StableId}|{x.Message}|{x.NativeErrorCode}")));
}
