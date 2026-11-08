using System.Runtime.InteropServices;

namespace NexusAssistant.Services;

internal static class AudioOutput
{
    internal sealed record Device(string Id, string Name);

    public static (Device[] Devices, string? Default) List()
    {
        var enumerator = Create<IMMDeviceEnumerator>("bcde0395-e52f-467c-8e3d-c4579291692e");
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out var collection));
            try
            {
                Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                var devices = new List<Device>();
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) < 0) continue;
                    try
                    {
                        if (device.GetId(out var id) >= 0) devices.Add(new(id, FriendlyName(device) ?? $"Устройство {i + 1}"));
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
                return (devices.ToArray(), DefaultId(enumerator));
            }
            finally { Marshal.ReleaseComObject(collection); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    public static void SetDefault(string id)
    {
        var policy = Create<IPolicyConfig>("870af99c-171d-4f9e-af0d-e63df40c2bc9");
        try
        {
            // Console and multimedia defaults. Leave the communications device selection alone.
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, 1));
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, 0));
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    private static T Create<T>(string clsid) where T : class =>
        (T)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid), throwOnError: true)!)!;

    private static string? DefaultId(IMMDeviceEnumerator enumerator)
    {
        if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) < 0) return null;
        try { return device.GetId(out var id) >= 0 ? id : null; }
        finally { Marshal.ReleaseComObject(device); }
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) < 0) return null;
        try
        {
            var key = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
            var status = store.GetValue(ref key, out var value);
            try { return status >= 0 && value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CountedPointer { public uint Count; public IntPtr Pointer; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] public CountedPointer Array;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(IntPtr iid, int context, IntPtr activation, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Preserve the native vtable slots preceding SetDefaultEndpoint.
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}
