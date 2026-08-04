using System.Runtime.InteropServices;

namespace GitSail.CommandLine;

/// <summary>
/// Exposes the macOS process argument vector without converting native filename bytes.
/// </summary>
internal static unsafe partial class MacNativeCommandLine
{
    /// <summary>
    /// Gets the native address of the process argument count.
    /// </summary>
    /// <returns>A pointer to the process argument count.</returns>
    [LibraryImport("libSystem.B.dylib", EntryPoint = "_NSGetArgc")]
    internal static partial int* GetArgumentCount();

    /// <summary>
    /// Gets the native address of the process argument vector.
    /// </summary>
    /// <returns>A pointer to the process argument-vector pointer.</returns>
    [LibraryImport("libSystem.B.dylib", EntryPoint = "_NSGetArgv")]
    internal static partial byte*** GetArgumentVector();
}
