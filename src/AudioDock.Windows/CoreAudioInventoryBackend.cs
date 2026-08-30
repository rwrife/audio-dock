using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AudioDock.Core.Models;
using AudioDock.Windows.Interop;

namespace AudioDock.Windows;

internal sealed class CoreAudioInventoryBackend : IWindowsAudioInventoryBackend
{
    private bool disposed;

    public NativeInventorySnapshot Capture(bool includeExecutablePaths, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Core Audio inventory requires Windows 10 22H2 or Windows 11.");
        }

        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            var diagnostics = new List<InventoryDiagnostic>();
            Dictionary<(AudioDirection, AudioRole), string> defaults = ReadDefaults(enumerator, diagnostics);
            List<EndpointDescriptor> endpoints = [];
            List<SessionDescriptor> sessions = [];
            EnumerateDirection(enumerator, EDataFlow.Render, AudioDirection.Render, defaults, includeExecutablePaths,
                endpoints, sessions, diagnostics, cancellationToken);
            EnumerateDirection(enumerator, EDataFlow.Capture, AudioDirection.Capture, defaults, includeExecutablePaths,
                endpoints, sessions, diagnostics, cancellationToken);
            return new(endpoints, sessions, diagnostics);
        }
        finally
        {
            ComRelease.Final(enumerator);
        }
    }

    public void Dispose() => disposed = true;

    private static Dictionary<(AudioDirection, AudioRole), string> ReadDefaults(
        IMMDeviceEnumerator enumerator,
        List<InventoryDiagnostic> diagnostics)
    {
        var result = new Dictionary<(AudioDirection, AudioRole), string>();
        foreach (EDataFlow flow in new[] { EDataFlow.Render, EDataFlow.Capture })
        {
            foreach (ERole role in Enum.GetValues<ERole>())
            {
                IMMDevice? device = null;
                try
                {
                    int hr = enumerator.GetDefaultAudioEndpoint(flow, role, out device);
                    if (hr < 0 || device is null) continue;
                    Marshal.ThrowExceptionForHR(device.GetId(out string id));
                    result[(
                        flow == EDataFlow.Render ? AudioDirection.Render : AudioDirection.Capture,
                        NativeAudioMapping.MapRole(role))] = id;
                }
                catch (COMException exception)
                {
                    diagnostics.Add(new("default-role", null, exception.Message, exception.HResult));
                }
                finally
                {
                    ComRelease.Final(device);
                }
            }
        }

        return result;
    }

    private static void EnumerateDirection(
        IMMDeviceEnumerator enumerator,
        EDataFlow flow,
        AudioDirection direction,
        Dictionary<(AudioDirection, AudioRole), string> defaults,
        bool includeExecutablePaths,
        List<EndpointDescriptor> endpoints,
        List<SessionDescriptor> sessions,
        List<InventoryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        IMMDeviceCollection? collection = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, DeviceState.All, out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMMDevice? device = null;
                string? stableId = null;
                try
                {
                    Marshal.ThrowExceptionForHR(collection.Item(index, out device));
                    Marshal.ThrowExceptionForHR(device.GetId(out stableId));
                    Marshal.ThrowExceptionForHR(device.GetState(out DeviceState state));
                    string name = ReadFriendlyName(device) ?? stableId;
                    AudioRole[] roles = Enum.GetValues<AudioRole>()
                        .Where(role => defaults.TryGetValue((direction, role), out string? id) &&
                            StringComparer.Ordinal.Equals(id, stableId))
                        .ToArray();
                    (VolumeLevel? volume, bool? muted) = ReadEndpointVolume(device, stableId, diagnostics);
                    endpoints.Add(new(stableId, name, direction, Map(state), roles,
                        EndpointCapabilities.ReadOnly, volume, muted));
                    if (state == DeviceState.Active)
                    {
                        ReadSessions(device, stableId, includeExecutablePaths, sessions, diagnostics, cancellationToken);
                    }
                }
                catch (Exception exception) when (exception is COMException or Win32Exception)
                {
                    diagnostics.Add(new("endpoint", stableId, exception.Message, exception.HResult));
                }
                finally
                {
                    ComRelease.Final(device);
                }
            }
        }
        finally
        {
            ComRelease.Final(collection);
        }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        PropVariant value = default;
        try
        {
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccessMode.Read, out store));
            PropertyKey key = PropertyKeys.DeviceFriendlyName;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
            return value.GetString();
        }
        finally
        {
            value.Clear();
            ComRelease.Final(store);
        }
    }

    private static (VolumeLevel?, bool?) ReadEndpointVolume(
        IMMDevice device,
        string stableId,
        List<InventoryDiagnostic> diagnostics)
    {
        object? activated = null;
        try
        {
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out activated));
            var volume = (IAudioEndpointVolume)activated;
            Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out float scalar));
            Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted));
            return (new VolumeLevel(scalar), muted);
        }
        catch (COMException exception)
        {
            diagnostics.Add(new("endpoint-volume", stableId, exception.Message, exception.HResult));
            return (null, null);
        }
        finally
        {
            ComRelease.Final(activated);
        }
    }

    private static void ReadSessions(
        IMMDevice device,
        string endpointId,
        bool includeExecutablePaths,
        List<SessionDescriptor> sessions,
        List<InventoryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        object? activated = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        try
        {
            Guid iid = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out activated));
            var manager = (IAudioSessionManager2)activated;
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessionEnumerator));
            Marshal.ThrowExceptionForHR(sessionEnumerator.GetCount(out int count));
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IAudioSessionControl? control = null;
                string? identifier = null;
                try
                {
                    Marshal.ThrowExceptionForHR(sessionEnumerator.GetSession(index, out control));
                    var control2 = (IAudioSessionControl2)control;
                    Marshal.ThrowExceptionForHR(control.GetState(out AudioSessionState state));
                    if (state != AudioSessionState.Active) continue;
                    Marshal.ThrowExceptionForHR(control2.GetSessionInstanceIdentifier(out identifier));
                    Marshal.ThrowExceptionForHR(control2.GetProcessId(out uint processId));
                    ProcessIdentity identity = ProcessIdentity.Read(processId, includeExecutablePaths);
                    var simpleVolume = (ISimpleAudioVolume)control;
                    Marshal.ThrowExceptionForHR(simpleVolume.GetMasterVolume(out float scalar));
                    Marshal.ThrowExceptionForHR(simpleVolume.GetMute(out bool muted));
                    string stableSessionId = NativeAudioMapping.ComposeSessionId(endpointId, identifier);
                    sessions.Add(new(stableSessionId, identity.Name, NativeAudioMapping.MapSessionState(state),
                        SessionCapabilities.ReadOnly, new VolumeLevel(scalar), muted,
                        identity.PackageFamilyName, ExecutablePath: identity.ExecutablePath,
                        ProcessId: checked((int)processId)));
                    if (identity.Diagnostic is not null)
                    {
                        diagnostics.Add(identity.Diagnostic with { StableId = stableSessionId });
                    }
                }
                catch (Exception exception) when (exception is COMException or Win32Exception or ArgumentException)
                {
                    string stableId = identifier is null
                        ? endpointId
                        : NativeAudioMapping.ComposeSessionId(endpointId, identifier);
                    diagnostics.Add(new("session", stableId, exception.Message, exception.HResult));
                }
                finally
                {
                    ComRelease.Final(control);
                }
            }
        }
        catch (COMException exception)
        {
            diagnostics.Add(new("sessions", endpointId, exception.Message, exception.HResult));
        }
        finally
        {
            ComRelease.Final(sessionEnumerator);
            ComRelease.Final(activated);
        }
    }

    private static EndpointState Map(DeviceState state) => state switch
    {
        DeviceState.Active => EndpointState.Active,
        DeviceState.Disabled => EndpointState.Disabled,
        DeviceState.NotPresent => EndpointState.NotPresent,
        DeviceState.Unplugged => EndpointState.Unplugged,
        _ => EndpointState.NotPresent,
    };
}

internal sealed record ProcessIdentity(
    string Name,
    string? PackageFamilyName,
    string? ExecutablePath,
    InventoryDiagnostic? Diagnostic)
{
    internal static ProcessIdentity Read(uint processId, bool includePath)
    {
        if (processId == 0) return new("System Sounds", null, null, null);
        IntPtr process = NativeMethods.OpenProcess(ProcessAccess.QueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            return new($"Process {processId}", null, null,
                new("process", null, new Win32Exception(error).Message, error));
        }

        try
        {
            string? package = NativeMethods.TryGetPackageFamilyName(process);
            string name;
            InventoryDiagnostic? diagnostic = null;
            try
            {
                using Process managed = Process.GetProcessById(checked((int)processId));
                name = managed.ProcessName;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or Win32Exception)
            {
                name = $"Process {processId}";
                diagnostic = new("process", null, exception.Message, exception.HResult);
            }

            string? path = includePath ? NativeMethods.TryGetProcessPath(process) : null;
            return new(name, package, path, diagnostic);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
