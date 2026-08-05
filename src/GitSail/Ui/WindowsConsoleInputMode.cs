using GitSail.Git.Execution;
using Microsoft.Win32.SafeHandles;

namespace GitSail.Ui;

/// <summary>
/// Selects complete native Windows console key, mouse, and resize records for GitSail.
/// </summary>
internal static class WindowsConsoleInputMode
{
    private const int StandardInputHandle = -10;
    private const uint EnableWindowInput = 0x0008;
    private const uint EnableMouseInput = 0x0010;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint EnableVirtualTerminalInput = 0x0200;

    /// <summary>
    /// Prevents fragmented virtual-terminal mouse reports from becoming ordinary text input.
    /// </summary>
    internal static void Apply()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var nativeHandle = WindowsNative.GetStdHandle(StandardInputHandle);
        if (nativeHandle == 0 || nativeHandle == new nint(-1))
        {
            return;
        }

        using var input = new SafeFileHandle(nativeHandle, ownsHandle: false);
        if (WindowsNative.GetConsoleMode(input, out var currentMode) == 0)
        {
            return;
        }

        var requiredMode = GetRequiredMode(currentMode);
        if (requiredMode != currentMode)
        {
            _ = WindowsNative.SetConsoleMode(input, requiredMode);
        }
    }

    /// <summary>
    /// Produces the Windows input mode that retains keys, mouse events, and resize events.
    /// </summary>
    /// <param name="currentMode">The input mode installed by the terminal host.</param>
    /// <returns>The complete mode GitSail requires for native console input records.</returns>
    internal static uint GetRequiredMode(uint currentMode)
        => (currentMode | EnableWindowInput | EnableMouseInput | EnableExtendedFlags) &
            ~EnableVirtualTerminalInput;
}
