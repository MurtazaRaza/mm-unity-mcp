using System.Runtime.CompilerServices;

// NOTE: These tools live in their own assembly on purpose. Tools compiled into
// MCPForUnity.Editor are treated as "built-in" by ToolDiscoveryService
// (StringCaseUtility.IsBuiltInMcpType) and are then skipped by the server's
// custom-tool sync — they would never appear in tools/list.
[assembly: InternalsVisibleTo("MCPForUnityTests.EditMode.UITK")]
