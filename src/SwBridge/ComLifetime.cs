using System.Runtime.InteropServices;

namespace SwBridge;

/// <summary>COM object lifetime helpers.</summary>
public static class ComLifetime
{
    /// <summary>
    /// Releases a COM RCW if the object is one; safe to call with null or
    /// plain .NET objects. Use for short-lived interop objects obtained in
    /// loops (features, bodies) to avoid RCW buildup in long sessions.
    /// </summary>
    public static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}
