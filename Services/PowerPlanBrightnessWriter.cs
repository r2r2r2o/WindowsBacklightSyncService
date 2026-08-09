using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WindowsBacklightSyncService.Services;

/// <summary>
/// Writes the display-brightness power setting ("Display brightness level",
/// powercfg alias VIDEONORMALLEVEL) of every power scheme via the native power profile API
/// (powrprof.dll). This is the same operation powercfg /setacvalueindex|/setdcvalueindex performs.
/// </summary>
public interface IPowerPlanBrightnessWriter
{
    /// <summary>All power scheme GUIDs currently registered on the system.</summary>
    IReadOnlyList<Guid> EnumeratePowerSchemes();

    /// <summary>Writes the brightness value (0-100) for a scheme's AC or DC value index.</summary>
    void WriteBrightnessValue(Guid schemeGuid, int brightnessPercent, bool ac);

    /// <summary>Reads the brightness value (0-100) currently stored for a scheme, or null if unreadable.</summary>
    int? ReadBrightnessValue(Guid schemeGuid, bool ac);

    /// <summary>The GUID of the currently active power scheme, or null.</summary>
    Guid? GetActiveScheme();

    /// <summary>Activates a scheme (used to re-apply the active scheme after writes so the value takes effect).</summary>
    void SetActiveScheme(Guid schemeGuid);

    /// <summary>Friendly name of a power scheme (e.g. "Balanced"), or null if unavailable.</summary>
    string? GetSchemeName(Guid schemeGuid);
}

public sealed class PowerPlanBrightnessWriter : IPowerPlanBrightnessWriter
{
    /// <summary>Display sub-group GUID (powercfg alias SUB_VIDEO).</summary>
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");

    /// <summary>
    /// "Display brightness level" power setting GUID (powercfg alias VIDEONORMALLEVEL).
    /// Hidden in powercfg output by default, but fully readable/writable.
    /// Range: 0-100 (%).
    /// </summary>
    private static readonly Guid VideoBrightnessSetting = new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    public IReadOnlyList<Guid> EnumeratePowerSchemes()
    {
        var schemes = new List<Guid>();
        for (uint index = 0; ; index++)
        {
            // Size query: PowerEnumerate reports ERROR_MORE_DATA (234) and returns the required
            // buffer size (it may also return ERROR_SUCCESS for a trivial one-GUID answer).
            uint bufferSize = 0;
            uint status = Native.PowerEnumerate(
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                Native.PowerDataAccessor.AccessScheme, index, IntPtr.Zero, ref bufferSize);
            if (status == Native.ErrorNoMoreItems)
                break;
            if (status != Native.ErrorSuccess && status != Native.ErrorMoreData)
                throw new Win32Exception(unchecked((int)status));
            if (bufferSize == 0)
                break;

            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                // The required buffer size can change between the size query and the fill
                // (the scheme set is dynamic); retry a few times on ERROR_MORE_DATA.
                for (int attempt = 0; ; attempt++)
                {
                    status = Native.PowerEnumerate(
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        Native.PowerDataAccessor.AccessScheme, index, buffer, ref bufferSize);
                    if (status == Native.ErrorSuccess)
                    {
                        schemes.Add(Marshal.PtrToStructure<Guid>(buffer));
                        break;
                    }
                    if (status == Native.ErrorNoMoreItems)
                        break;
                    if (status == Native.ErrorMoreData && attempt < 2 && bufferSize > 0 && bufferSize <= (1 << 16))
                    {
                        // Retry with the (possibly larger) reported size.
                        Marshal.FreeHGlobal(buffer);
                        buffer = Marshal.AllocHGlobal((int)bufferSize);
                        continue;
                    }
                    throw new Win32Exception(unchecked((int)status));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return schemes;
    }

    public void WriteBrightnessValue(Guid schemeGuid, int brightnessPercent, bool ac)
    {
        // Locals are required: static readonly fields cannot be passed by ref.
        Guid subgroup = VideoSubgroup;
        Guid setting = VideoBrightnessSetting;

        uint value = (uint)Math.Clamp(brightnessPercent, 0, 100);
        uint status = ac
            ? Native.PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroup, ref setting, value)
            : Native.PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroup, ref setting, value);
        if (status != Native.ErrorSuccess)
            throw new Win32Exception(unchecked((int)status));
    }

    public int? ReadBrightnessValue(Guid schemeGuid, bool ac)
    {
        // Locals are required: static readonly fields cannot be passed by ref.
        Guid subgroup = VideoSubgroup;
        Guid setting = VideoBrightnessSetting;

        uint value;
        uint status = ac
            ? Native.PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroup, ref setting, out value)
            : Native.PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroup, ref setting, out value);
        if (status != Native.ErrorSuccess)
            return null;
        return (int)value;
    }

    public Guid? GetActiveScheme()
    {
        uint status = Native.PowerGetActiveScheme(IntPtr.Zero, out IntPtr activePolicyGuid);
        if (status != Native.ErrorSuccess)
            return null;
        try
        {
            return Marshal.PtrToStructure<Guid>(activePolicyGuid);
        }
        finally
        {
            Native.LocalFree(activePolicyGuid);
        }
    }

    public void SetActiveScheme(Guid schemeGuid)
    {
        uint status = Native.PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
        if (status != Native.ErrorSuccess)
            throw new Win32Exception(unchecked((int)status));
    }

    public string? GetSchemeName(Guid schemeGuid)
    {
        // Scheme names are stored in the registry:
        //   user-created schemes: HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{GUID}
        //   built-in schemes:     HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSchemes\{GUID}
        // The friendly name may be in the (Default) value or in a named value.
        string[] roots =
        {
            $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{schemeGuid:D}",
            $@"SYSTEM\CurrentControlSet\Control\Power\PowerSchemes\{schemeGuid:D}",
        };
        string?[] valueNames = { null, "Name", "FriendlyName", "Description" }; // null = (Default)
        foreach (string root in roots)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null)
                    continue;
                foreach (string? valueName in valueNames)
                {
                    if (key.GetValue(valueName) is string name && !string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
            catch
            {
                // try the next root
            }
        }

        // Fallback 2: WMI Win32_PowerPlan (root\cimv2\power) — ElementName is the friendly name.
        try
        {
            string instanceId = "Microsoft:PowerPlan\\{" + schemeGuid.ToString("B").ToUpperInvariant() + "}";
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2\power",
                "SELECT ElementName, InstanceID FROM Win32_PowerPlan");
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                if (string.Equals(item["InstanceID"]?.ToString(), instanceId, StringComparison.OrdinalIgnoreCase))
                    return item["ElementName"]?.ToString();
            }
        }
        catch
        {
            // fall through
        }

        // Fallback 3: the native API.
        // First call with a null buffer returns the required size (ERROR_MORE_DATA).
        uint size = 0;
        uint status = Native.PowerReadFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (status != Native.ErrorMoreData || size == 0)
            return null;

        var buffer = new StringBuilder((int)size);
        status = Native.PowerReadFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
        if (status != Native.ErrorSuccess)
            return null;
        return buffer.ToString();
    }

    private static class Native
    {
        internal const uint ErrorSuccess = 0;
        internal const uint ErrorMoreData = 234;   // ERROR_MORE_DATA
        internal const uint ErrorNoMoreItems = 259; // ERROR_NO_MORE_ITEMS

        internal enum PowerDataAccessor : uint
        {
            AccessScheme = 16,
            AccessSubgroup = 17,
            AccessIndividualSetting = 18,
        }

        [DllImport("powrprof.dll")]
        internal static extern uint PowerEnumerate(
            IntPtr rootPowerKey,
            IntPtr schemeGuid,
            IntPtr subgroupOfPowerSettingsGuid,
            PowerDataAccessor accessFlags,
            uint index,
            IntPtr buffer,
            ref uint bufferSize);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerWriteACValueIndex(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            uint acValueIndex);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerWriteDCValueIndex(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            uint dcValueIndex);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerReadACValueIndex(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            out uint acValueIndex);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerReadDCValueIndex(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            out uint dcValueIndex);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        internal static extern uint PowerReadFriendlyName(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            IntPtr subgroupOfPowerSettingsGuid,
            IntPtr powerSettingGuid,
            StringBuilder? buffer,
            ref uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LocalFree(IntPtr hMem);
    }
}
