using System.Reflection;

namespace SwBridge;

/// <summary>
/// Result of a <see cref="ComInvoker"/> call. Never thrown for a member-level
/// failure (a missing member, a wrong argument count, a COM error) — those are
/// reported here so a caller composing several invocations (e.g. an operation
/// recipe runner) can react to a specific step failing without an exception
/// unwinding the whole plan.
/// </summary>
/// <param name="Success">Whether the invocation completed without error.</param>
/// <param name="Value">The returned value on success; null on failure or for a void member.</param>
/// <param name="FailureDetail">Human-readable failure detail; null on success.</param>
public sealed record InvokeOutcome(bool Success, object? Value, string? FailureDetail)
{
    /// <summary>Builds a successful outcome.</summary>
    public static InvokeOutcome Ok(object? value) => new(true, value, null);

    /// <summary>Builds a failed outcome with a human-readable detail.</summary>
    public static InvokeOutcome Fail(string detail) => new(false, null, detail);
}

/// <summary>
/// The only write-dispatch door in SwBridge: explicit, single-purpose COM
/// invocation against a late-bound target. Every call maps to exactly <b>one</b>
/// COM dispatch flag — the deliberate contrast with <see cref="ComPropertyReader"/>,
/// whose combined <c>GetProperty | InvokeMethod</c> flags exist specifically to
/// stay read-only. Combining flags here would reopen the exact hazard
/// <see cref="ComPropertyReader"/>'s own comment warns about: invoking a bare
/// property with an argument silently hits its setter. <see cref="InvokeMethod"/>,
/// <see cref="SetProperty"/> and <see cref="GetProperty"/> never combine flags,
/// so which COM operation runs is always explicit at the call site.
/// </summary>
public static class ComInvoker
{
    private const BindingFlags MethodFlags =
        BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance;

    private const BindingFlags GetPropertyFlags =
        BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance;

    private const BindingFlags SetPropertyFlags =
        BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// Invokes a method (<c>DISPATCH_METHOD</c> only) — e.g. <c>FeatureExtrusion3(...)</c>
    /// or a bare, argument-less method call.
    /// </summary>
    /// <param name="target">The COM object to invoke on. Null yields a failed outcome.</param>
    /// <param name="memberName">Method name, case-insensitive.</param>
    /// <param name="args">Positional arguments, in declaration order. Null/empty for a parameterless method.</param>
    public static InvokeOutcome InvokeMethod(object? target, string memberName, object?[]? args = null) =>
        Invoke(target, memberName, MethodFlags, args is { Length: > 0 } ? args : null);

    /// <summary>
    /// Reads a property (<c>DISPATCH_PROPERTYGET</c> only) — never falls back to
    /// invoking a method of the same name, unlike <see cref="ComPropertyReader"/>.
    /// </summary>
    /// <param name="target">The COM object to read from. Null yields a failed outcome.</param>
    /// <param name="propertyName">Property name, case-insensitive.</param>
    public static InvokeOutcome GetProperty(object? target, string propertyName) =>
        Invoke(target, propertyName, GetPropertyFlags, null);

    /// <summary>
    /// Writes a property (<c>DISPATCH_PROPERTYPUT</c> only) — the write half
    /// <see cref="ComPropertyReader"/> deliberately never performs.
    /// </summary>
    /// <param name="target">The COM object to write to. Null yields a failed outcome.</param>
    /// <param name="propertyName">Property name, case-insensitive.</param>
    /// <param name="value">The value to assign.</param>
    public static InvokeOutcome SetProperty(object? target, string propertyName, object? value) =>
        Invoke(target, propertyName, SetPropertyFlags, new[] { value });

    private static InvokeOutcome Invoke(object? target, string memberName, BindingFlags flags, object?[]? args)
    {
        if (target == null)
        {
            return InvokeOutcome.Fail($"Cannot invoke '{memberName}': target is null.");
        }

        if (string.IsNullOrWhiteSpace(memberName))
        {
            return InvokeOutcome.Fail("Member name must not be empty.");
        }

        try
        {
            var value = target.GetType().InvokeMember(memberName, flags, binder: null, target: target, args: args);
            return InvokeOutcome.Ok(value);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            var inner = ex.InnerException;
            return InvokeOutcome.Fail($"{inner.GetType().Name}: {inner.Message}");
        }
        catch (Exception ex)
        {
            return InvokeOutcome.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
