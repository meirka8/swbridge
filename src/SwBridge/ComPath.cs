namespace SwBridge;

/// <summary>
/// Result of resolving a <see cref="ComPath"/>. Never thrown for a bad path —
/// <see cref="Success"/> is false and <see cref="FailedSegment"/>/<see cref="FailureDetail"/>
/// say exactly where resolution stopped, which is what turns a typo'd path into
/// a reportable runtime error rather than a silent null.
/// </summary>
/// <param name="Success">Whether every segment of the path resolved.</param>
/// <param name="Value">The object the full path resolved to, on success.</param>
/// <param name="FailedSegment">The dotted prefix up to and including the segment that failed, on failure.</param>
/// <param name="FailureDetail">Human-readable failure detail, on failure.</param>
public sealed record ComPathResult(bool Success, object? Value, string? FailedSegment, string? FailureDetail)
{
    /// <summary>Builds a successful result.</summary>
    public static ComPathResult Ok(object? value) => new(true, value, null, null);

    /// <summary>Builds a failed result naming the segment resolution stopped at.</summary>
    public static ComPathResult Fail(string failedSegment, string detail) => new(false, null, failedSegment, detail);
}

/// <summary>
/// Resolves a dotted, read-only path from a root object (an <c>ISldWorks</c>
/// application, or a document's <c>ModelDoc2</c>) — e.g. <c>"FeatureManager"</c>,
/// <c>"SketchManager"</c>, <c>"Extension.SelectionManager"</c>. Per <c>ADR
/// 0001</c>, this is deliberately an open path grammar rather than a closed
/// enum of known targets: a bad path is a runtime error (a failed
/// <see cref="ComPathResult"/>), not a schema error, so discovering a new
/// manager object on a live SolidWorks object (e.g. via
/// <see cref="ComTypeInspector"/>) never requires a SwBridge release before it
/// can be addressed by path.
/// </summary>
/// <remarks>
/// Every hop is a <em>strict</em> property get — <c>DISPATCH_PROPERTYGET</c> only,
/// via <see cref="ComPropertyReader.TryGetPropertyStrict"/> — never the combined
/// <c>GetProperty | InvokeMethod</c> flags <see cref="ComPropertyReader.TryGetMember"/>
/// uses for the feature-definition read path, where accessor methods are
/// legitimately needed. A path segment that happens to name a zero-argument
/// method (e.g. <c>ExitApp</c>, <c>EditDelete</c>) fails to resolve rather than
/// being invoked while merely being walked — <see cref="ComPath"/> genuinely
/// cannot execute anything; that happens once, on the resolved terminal object,
/// through <see cref="ComInvoker"/>. Intermediate hops (e.g. the
/// <c>FeatureManager</c> object reached on the way to a deeper path) are not
/// individually released, matching the rest of this library's existing
/// convention for property-get results (see <see cref="ModelInspector"/>);
/// releasing whatever the full path resolves to is the caller's responsibility,
/// typically via a result converter.
/// </remarks>
public static class ComPath
{
    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="root"/>, one
    /// property-get per dot-separated segment.
    /// </summary>
    /// <param name="root">
    /// The object to start from. Null yields a failed result. An empty or
    /// whitespace-only <paramref name="path"/> resolves to <paramref name="root"/>
    /// itself — the target <em>is</em> the root (e.g. invoking directly on the
    /// application or document object).
    /// </param>
    /// <param name="path">Dot-separated property path, e.g. <c>"Extension.SelectionManager"</c>.</param>
    public static ComPathResult Resolve(object? root, string path)
    {
        if (root == null)
        {
            return ComPathResult.Fail("", "Root object is null.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return ComPathResult.Ok(root);
        }

        object current = root;
        var walked = new List<string>();

        foreach (var rawSegment in path.Split('.'))
        {
            var segment = rawSegment.Trim();
            walked.Add(segment);
            var walkedSoFar = string.Join(".", walked);

            if (segment.Length == 0)
            {
                return ComPathResult.Fail(walkedSoFar, "Empty path segment.");
            }

            if (!ComPropertyReader.TryGetPropertyStrict(current, segment, out var next))
            {
                return ComPathResult.Fail(walkedSoFar,
                    $"'{segment}' is not a readable property on the current object. " +
                    "Path segments must be properties; methods are only invocable as the terminal member, via ComInvoker.");
            }

            if (next == null)
            {
                return ComPathResult.Fail(walkedSoFar, $"'{segment}' resolved to null.");
            }

            current = next;
        }

        return ComPathResult.Ok(current);
    }
}
