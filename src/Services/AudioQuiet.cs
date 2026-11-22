using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PulsePC.Services;

/// <summary>
/// Тихо ли сейчас в звуковом устройстве: по индикатору уровня Windows (Core Audio, IAudioMeterInformation) — то же значение, что рисует
/// полоска громкости в «Параметрах звука». Только чтение: звук не перехватывается и не записывается, настройки не меняются.
/// Нужно, чтобы опрос заряда, на время которого гарнитура глохнет (Logitech G435), шёл в паузах, а не посреди музыки или разговора.
/// </summary>
internal static class AudioQuiet
{
    /// <summary>Уровень ниже этого — тишина (шум и «пустой» поток дают 0).</summary>
    private const float SilentPeak = 0.002f;

    /// <summary>
    /// true — все включённые устройства воспроизведения и записи, в названии которых есть nameMark, молчат (проверка ~0,3 с);
    /// false — где-то идёт звук; null — таких устройств нет или Windows не ответила.
    /// </summary>
    public static bool? IsQuiet(string nameMark)
    {
        var meters = new List<IAudioMeterInformation>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(2 /* eAll */, 1 /* ACTIVE */, out var devices) != 0 || devices == null) return null;
            devices.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0 || device == null) continue;
                string name = FriendlyName(device);
                if (name == null || name.IndexOf(nameMark, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var iid = typeof(IAudioMeterInformation).GUID;
                if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out object meter) == 0 && meter is IAudioMeterInformation m) meters.Add(m);
            }
            if (meters.Count == 0) return null;
            for (int sample = 0; sample < 6; sample++)
            {
                foreach (var m in meters)
                    if (m.GetPeakValue(out float peak) == 0 && peak > SilentPeak) return false;
                Thread.Sleep(50);
            }
            return true;
        }
        catch { return null; }
    }

    private static string FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0 || store == null) return null;
        var key = new PropertyKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };
        var pv = new PropVariant();
        try
        {
            if (store.GetValue(ref key, out pv) != 0) return null;
            return pv.Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.Pointer) : null;
        }
        finally { PropVariantClear(ref pv); }
    }

    // ───────────────────────── Core Audio ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Fmtid; public int Pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
