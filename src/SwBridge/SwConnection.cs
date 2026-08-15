using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace SwBridge;

/// <summary>
/// Manages attachment to a running SolidWorks instance. Attachment is lazy —
/// nothing is resolved until the first call — and a dead COM link (SolidWorks
/// closed or restarted since the last call) is detected and re-attached
/// transparently. Never launches SolidWorks itself.
/// </summary>
public sealed class SwConnection
{
    private ISldWorks? _app;

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    /// <summary>Whether a live SolidWorks instance is currently reachable. Triggers an attach attempt.</summary>
    public bool IsConnected => TryGetApp(out _);

    /// <summary>
    /// Returns the live SolidWorks application object, attaching or re-attaching if needed.
    /// </summary>
    public bool TryGetApp([NotNullWhen(true)] out ISldWorks? app)
    {
        if (_app != null && IsAlive(_app))
        {
            app = _app;
            return true;
        }

        _app = Attach();
        app = _app;
        return app != null;
    }

    /// <summary>Returns the live SolidWorks application object.</summary>
    /// <exception cref="SwNotRunningException">No running SolidWorks instance could be attached.</exception>
    public ISldWorks GetApp() =>
        TryGetApp(out var app) ? app : throw new SwNotRunningException();

    private static bool IsAlive(ISldWorks app)
    {
        try
        {
            _ = app.Visible;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    private static ISldWorks? Attach()
    {
        try
        {
            var progIdType = Type.GetTypeFromProgID("SldWorks.Application");
            if (progIdType == null)
            {
                return null;
            }

            var guid = progIdType.GUID;
            GetActiveObject(ref guid, IntPtr.Zero, out var instance);
            return instance as ISldWorks;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
