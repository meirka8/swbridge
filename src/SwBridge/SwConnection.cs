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
/// <remarks>
/// Per <c>ADR 0003</c>, attachment and liveness-checking run on
/// <see cref="Dispatcher"/>'s dedicated STA thread, like every other COM touch
/// in SwBridge. <see cref="GetApp()"/> is still a synchronous, direct return of
/// the live <see cref="ISldWorks"/> RCW — it is the library's intentional raw
/// escape hatch for advanced consumers (mirroring <see cref="SwDocument.Model"/>)
/// — but a caller that then uses that object from a thread other than
/// <see cref="Dispatcher"/>'s own is doing exactly the cross-apartment access
/// ADR 0003 exists to avoid; SwBridge cannot prevent this for raw consumers,
/// it can only route its own calls through the dispatcher.
/// </remarks>
public sealed class SwConnection : IDisposable
{
    private ISldWorks? _app;

    /// <summary>The dedicated STA thread every SwBridge COM call for this connection runs on.</summary>
    public SwDispatcher Dispatcher { get; } = new();

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
        var resolved = Dispatcher.Run(() =>
        {
            if (_app != null && IsAlive(_app))
            {
                return _app;
            }

            return _app = Attach();
        });

        app = resolved;
        return resolved != null;
    }

    /// <summary>Returns the live SolidWorks application object.</summary>
    /// <exception cref="SwNotRunningException">No running SolidWorks instance could be attached.</exception>
    public ISldWorks GetApp() =>
        TryGetApp(out var app) ? app : throw new SwNotRunningException();

    /// <summary>Disposes <see cref="Dispatcher"/>. Safe to call once no further calls are in flight.</summary>
    public void Dispose() => Dispatcher.Dispose();

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
