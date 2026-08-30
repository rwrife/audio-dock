using System.Runtime.InteropServices;
using System.Text;

namespace AudioDock.Windows.Interop;

internal enum EDataFlow { Render, Capture, All }
internal enum ERole { Console, Multimedia, Communications }
internal enum AudioSessionState { Inactive, Active, Expired }

internal static class NativeAudioMapping
{
    internal static string ComposeSessionId(string endpointId, string sessionInstanceId) =>
        $"{endpointId}|{sessionInstanceId}";

    internal static AudioDock.Core.Models.AudioRole MapRole(ERole role) => role switch
    {
        ERole.Console => AudioDock.Core.Models.AudioRole.Console,
        ERole.Multimedia => AudioDock.Core.Models.AudioRole.Multimedia,
        ERole.Communications => AudioDock.Core.Models.AudioRole.Communications,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown Core Audio role."),
    };

    internal static AudioDock.Core.Models.SessionState MapSessionState(AudioSessionState state) => state switch
    {
        AudioSessionState.Active => AudioDock.Core.Models.SessionState.Active,
        AudioSessionState.Inactive => AudioDock.Core.Models.SessionState.Inactive,
        AudioSessionState.Expired => AudioDock.Core.Models.SessionState.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Core Audio session state."),
    };
}

[Flags]
internal enum DeviceState : uint
{
    Active = 1,
    Disabled = 2,
    NotPresent = 4,
    Unplugged = 8,
    All = 15,
}

[Flags]
internal enum ClsCtx : uint { All = 23 }
internal enum StorageAccessMode : uint { Read = 0 }
[Flags]
internal enum ProcessAccess : uint { QueryLimitedInformation = 0x1000 }

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    internal Guid FormatId;
    internal uint PropertyId;
}

internal static class PropertyKeys
{
    internal static PropertyKey DeviceFriendlyName => new()
    {
        FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        PropertyId = 14,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    private ushort type;
    private ushort reserved1;
    private ushort reserved2;
    private ushort reserved3;
    private IntPtr pointerValue;
    private int value2;

    internal readonly string? GetString() => type == 31 && pointerValue != IntPtr.Zero
        ? Marshal.PtrToStringUni(pointerValue)
        : null;

    internal void Clear()
    {
        if (type != 0) _ = NativeMethods.PropVariantClear(ref this);
        this = default;
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-C0A3D6D7F247")]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, ClsCtx context, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(StorageAccessMode access, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out DeviceState state);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, IntPtr eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, IntPtr eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IAudioSessionControl control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out ISimpleAudioVolume volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl control);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out AudioSessionState state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingId);
    [PreserveSig] int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out AudioSessionState state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingId);
    [PreserveSig] int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
    [PreserveSig] int GetProcessId(out uint processId);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

#pragma warning disable CA1838 // StringBuilder is appropriate for these variable-length Win32 buffers.
#pragma warning disable SYSLIB1054 // StringBuilder enables safe two-call package-name marshaling.
internal static class NativeMethods
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant variant);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(ProcessAccess access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? familyName);

    internal static string? TryGetProcessPath(IntPtr process)
    {
        var path = new StringBuilder(32768);
        uint size = (uint)path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
    }

    internal static string? TryGetPackageFamilyName(IntPtr process)
    {
        uint length = 0;
        int result = GetPackageFamilyName(process, ref length, null);
        if (result == AppModelErrorNoPackage || length == 0) return null;
        if (result != 122) return null;
        var familyName = new StringBuilder(checked((int)length));
        return GetPackageFamilyName(process, ref length, familyName) == 0 ? familyName.ToString() : null;
    }
}
#pragma warning restore SYSLIB1054
#pragma warning restore CA1838

internal static class ComRelease
{
    internal static void Final(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
