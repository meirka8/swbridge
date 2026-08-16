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

    // DISPATCH_PROPERTYGET only — deliberately does not fall back to invoking a
    // same-named method, unlike ReadFlags above. This exists specifically for
    // ComPath: a dotted path segment must never be able to execute anything
    // (a segment happening to name a zero-argument method like "ExitApp" or
    // "EditDelete" must fail, not run). ComPropertyReader.TryGetMember's combined
    // flags stay as they are for the feature-definition read path, which
    // legitimately needs accessor methods (e.g. GetDepth(true)) — this is a
    // separate, stricter reader for a job that must never be able to write or invoke.
    private const BindingFlags StrictGetFlags =
        BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

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

    /// <summary>
    /// Reads a property with <c>DISPATCH_PROPERTYGET</c> only — never falls back
    /// to invoking a method of the same name, unlike <see cref="TryGetMember"/>/
    /// <see cref="TryGetProperty"/>. Used by <see cref="ComPath"/>, whose segments
    /// must not be able to execute anything: with the combined flags, a path
    /// segment that happened to name a zero-argument COM method (e.g.
    /// <c>ExitApp</c>, <c>EditDelete</c>) would silently invoke it while merely
    /// being *resolved*. A segment that genuinely is a method now fails
    /// explicitly here instead — strictly more debuggable than executing it.
    /// </summary>
    public static bool TryGetPropertyStrict(object? comObject, string propertyName, out object? value)
    {
        value = null;
        if (comObject == null)
        {
            return false;
        }

        try
        {
            value = comObject.GetType().InvokeMember(
                propertyName, StrictGetFlags, binder: null, target: comObject, args: null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
