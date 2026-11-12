using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.bluetooth: PnP class "Bluetooth" (adapter + paired devices), the radio switch (Windows.Devices.Radios, read
/// only), and the Bluetooth Support Service. Read-only, no admin, never throws: failures go into ProbeErrors.
/// </summary>
public sealed class RealBluetoothProbe : IBluetoothProbe
{
    public BluetoothState Read()
    {
        var errors = new List<string>();
        var state = new BluetoothState { ProbeErrors = errors };

        try
        {
            using var sc = new ServiceController("bthserv");
            state.ServiceStatus = sc.Status.ToString();
        }
        catch (InvalidOperationException) { state.ServiceStatus = "NotFound"; }
        catch (Exception ex) { errors.Add($"Service: {ex.Message}"); }

        try { ReadDevices(state); }
        catch (Exception ex) { errors.Add($"WMI: {ex.Message}"); }

        try { state.RadioOn = Radios.BluetoothOn(); }
        catch (Exception ex) { errors.Add($"Radios: {ex.Message}"); }
        return state;
    }

    private static void ReadDevices(BluetoothState state)
    {
        var adapters = new List<BtAdapterInfo>();
        var paired = new List<BtPairedDevice>();
        using var q = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'");
        foreach (ManagementObject d in q.Get())
        {
            using (d)
            {
                var id = d["DeviceID"] as string ?? "";
                var name = d["Name"] as string ?? "(unnamed)";
                if (IsAdapter(id))
                    adapters.Add(new BtAdapterInfo(name, id, d["ConfigManagerErrorCode"] is uint code ? (int)code : null));
                else if (IsPairedDevice(id))
                {
                    var (flags, cod) = Properties(d);
                    paired.Add(new BtPairedDevice(name, id, IsAudioClass(cod),
                        flags is null ? Tri.Unknown : (flags.Value & ConnectedFlag) != 0 ? Tri.Yes : Tri.No));
                }
            }
        }
        state.Adapters = adapters;
        state.Paired = paired;
    }

    /// <summary>The radio chip itself (USB, PCI, UART ...), not the software nodes Windows adds under it (BTH*, SWD).</summary>
    internal static bool IsAdapter(string instanceId) =>
        instanceId.Length > 0 && !instanceId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
        && !instanceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase);

    /// <summary>A paired device: classic (BTHENUM\DEV_...) or Low Energy (BTHLE\DEV_...), not its service nodes.</summary>
    internal static bool IsPairedDevice(string instanceId) =>
        instanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase)
        || instanceId.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase);

    /// <summary>BDIF_CONNECTED in DEVPKEY_Bluetooth_DeviceFlags (bthdef.h).</summary>
    private const uint ConnectedFlag = 0x20;

    /// <summary>Class of Device, major class bits 8-12: 0x04 = Audio/Video (headphones, speakers, headsets).</summary>
    internal static bool IsAudioClass(uint? cod) => cod is { } c && ((c >> 8) & 0x1F) == 0x04;

    private static (uint? Flags, uint? Cod) Properties(ManagementObject d)
    {
        try
        {
            using var inParams = d.GetMethodParameters("GetDeviceProperties");
            inParams["devicePropertyKeys"] = new[] { "DEVPKEY_Bluetooth_DeviceFlags", "DEVPKEY_Bluetooth_ClassOfDevice" };
            using var result = d.InvokeMethod("GetDeviceProperties", inParams, null);
            uint? flags = null, cod = null;
            foreach (var p in result["deviceProperties"] as ManagementBaseObject[] ?? Array.Empty<ManagementBaseObject>())
            {
                if (p["Data"] is not uint v) continue;
                if (p["KeyName"] as string == "DEVPKEY_Bluetooth_DeviceFlags") flags = v;
                else if (p["KeyName"] as string == "DEVPKEY_Bluetooth_ClassOfDevice") cod = v;
            }
            return (flags, cod);
        }
        catch (Exception) { return (null, null); }
    }
}

/// <summary>
/// Windows.Devices.Radios read through the WinRT ABI directly, because this project targets plain net8.0-windows
/// (no Windows SDK projection). Reads the state only; never calls SetStateAsync.
/// </summary>
internal static class Radios
{
    private const int RadioKindBluetooth = 3;
    private const int RadioStateOn = 1;

    /// <summary>Yes if any Bluetooth radio is on, No if all are off or disabled, Unknown if Windows reports none.</summary>
    public static Tri BluetoothOn()
    {
        // A dedicated MTA thread: WinRT activation needs COM, and the caller may be the UI (STA) thread.
        Tri result = Tri.Unknown;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = Read(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(10))) throw new System.TimeoutException("radio list did not arrive");
        if (error != null) throw error;
        return result;
    }

    private static Tri Read()
    {
        var init = RoInitialize(1); // MTA; S_FALSE if already initialised
        try
        {
            var states = new List<int>();
            Check(WindowsCreateString("Windows.Devices.Radios.Radio", 28, out var className));
            IntPtr statics = IntPtr.Zero, op = IntPtr.Zero, info = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                var iid = new Guid("5FB6A12E-67CB-46AE-AAE9-65919F86EFF4"); // IRadioStatics
                Check(RoGetActivationFactory(className, ref iid, out statics));
                Check(Slot<OutPtr>(statics, 6)(statics, out op)); // GetRadiosAsync
                var asyncInfo = new Guid("00000036-0000-0000-C000-000000000046"); // IAsyncInfo
                Check(Marshal.QueryInterface(op, ref asyncInfo, out info));
                var deadline = DateTime.UtcNow.AddSeconds(8);
                int status;
                while (true)
                {
                    Check(Slot<OutInt>(info, 7)(info, out status)); // get_Status
                    if (status != 0 || DateTime.UtcNow > deadline) break;
                    Thread.Sleep(20);
                }
                if (status != 1) throw new InvalidOperationException($"radio list status {status}");
                Check(Slot<OutPtr>(op, 8)(op, out list)); // IAsyncOperation.GetResults -> IVectorView<Radio>
                Check(Slot<OutInt>(list, 7)(list, out var size)); // get_Size
                for (var i = 0; i < size; i++)
                {
                    Check(Slot<GetAt>(list, 6)(list, i, out var radio));
                    try
                    {
                        Check(Slot<OutInt>(radio, 11)(radio, out var kind)); // IRadio.get_Kind
                        if (kind != RadioKindBluetooth) continue;
                        Check(Slot<OutInt>(radio, 9)(radio, out var state)); // IRadio.get_State
                        states.Add(state);
                    }
                    finally { Marshal.Release(radio); }
                }
            }
            finally
            {
                foreach (var p in new[] { list, info, op, statics }) if (p != IntPtr.Zero) Marshal.Release(p);
                WindowsDeleteString(className);
            }
            if (states.Count == 0) return Tri.Unknown;
            return states.Contains(RadioStateOn) ? Tri.Yes : Tri.No;
        }
        finally { if (init >= 0) RoUninitialize(); }
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    private static T Slot<T>(IntPtr obj, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutPtr(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutInt(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAt(IntPtr self, int index, out IntPtr value);

    [DllImport("combase.dll")] private static extern int RoInitialize(int initType);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
}
