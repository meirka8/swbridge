namespace SwBridge;

/// <summary>
/// Describes how to read one value off a COM feature definition.
/// Verified live: SolidWorks definition objects expose some values as bare
/// properties (<c>BothDirections</c>) and others only through accessor methods
/// with arguments (<c>GetDepth(true)</c>) — a bare name is not enough.
/// </summary>
/// <param name="Name">Key under which the value is reported (e.g. <c>Depth</c>).</param>
/// <param name="Member">COM member to invoke (e.g. <c>GetDepth</c>). Case-insensitive.</param>
/// <param name="Args">Arguments for accessor methods; null or empty for bare properties.</param>
public sealed record PropertySpec(string Name, string Member, IReadOnlyList<object?>? Args = null)
{
    /// <summary>Convenience for a bare property where the report key is the member name.</summary>
    public static PropertySpec Bare(string member) => new(member, member);
}
