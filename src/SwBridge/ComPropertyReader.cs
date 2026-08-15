using System.Reflection;

namespace SwBridge;

/// <summary>
/// Reads properties off late-bound COM objects (e.g. SolidWorks feature
/// definitions) by name, case-insensitively. This is the mechanism that lets
/// callers describe feature types as data instead of compiling a class per type.
/// </summary>
public static class ComPropertyReader
{
    // GetProperty | InvokeMethod maps to DISPATCH_PROPERTYGET | DISPATCH_METHOD —
    // the read-only dispatch combination. Never add SetProperty here: invoking a
    // bare COM property with an argument would otherwise hit its setter.
    private const BindingFlags ReadFlags =
        BindingFlags.GetProperty | BindingFlags.InvokeMethod |
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    /// <summary>
    /// Attempts to read <paramref name="memberName"/> from <paramref name="comObject"/> —
    /// either a bare property (<paramref name="args"/> null/empty, e.g. <c>BothDirections</c>)
    /// or an accessor method with arguments (e.g. <c>GetDepth(true)</c>).
    /// Returns false when the object is null, the member does not exist, the argument
    /// count is wrong, or the COM call fails; the library deliberately does not
    /// distinguish these — callers treat an unreadable member as absent.
    /// </summary>
    public static bool TryGetMember(object? comObject, string memberName, object?[]? args, out object? value)
    {
        value = null;
        if (comObject == null)
        {
            return false;
        }

        try
        {
            value = comObject.GetType().InvokeMember(
                memberName,
                ReadFlags,
                binder: null,
                target: comObject,
                args: args is { Length: > 0 } ? args : null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Attempts to read a bare (argument-less) property.</summary>
    public static bool TryGetProperty(object? comObject, string propertyName, out object? value) =>
        TryGetMember(comObject, propertyName, null, out value);

    /// <summary>Reads a bare property, returning null when it cannot be read.</summary>
    public static object? GetProperty(object? comObject, string propertyName) =>
        TryGetMember(comObject, propertyName, null, out var value) ? value : null;
}
