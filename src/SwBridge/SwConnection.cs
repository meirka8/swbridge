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

    // Catches InvalidComObjectException alongside COMException/InvalidCastException:
    // a disconnected RCW (e.g. Marshal.ReleaseComObject drove its refcount to
    // zero — see ResultConverters' ownsReference remarks) throws that type,
    // which derives from SystemException, not COMException. Missing it here
    // used to mean the one mechanism that exists to recover from a dead COM
    // link (re-attaching) could not recover from this particular way of being
    // dead — the exception escaped IsAlive/TryGetApp/the dispatch entirely and
    // the connection stayed permanently broken. TryGetApp's caller (above)
    // already reassigns _app unconditionally when this returns false, so no
    // explicit null-out is needed here.
    private static bool IsAlive(ISldWorks app)
    {
        try
        {
            _ = app.Visible;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
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
