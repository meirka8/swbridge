using System.Runtime.CompilerServices;

// Lets SwBridge.Tests unit-test ComTypeInspector.DescribeInterfaceMembers (pure
// reflection over an interop Type, no COM object needed) without making it
// part of the public API surface.
[assembly: InternalsVisibleTo("SwBridge.Tests")]
