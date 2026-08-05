using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies the complete native Windows input mode selected for interactive GitSail sessions.
/// </summary>
[TestClass]
public sealed class WindowsConsoleInputModeTests
{
    /// <summary>
    /// Verifies native mouse and resize records replace virtual-terminal input without losing unrelated flags.
    /// </summary>
    [TestMethod]
    public void GetRequiredMode_WithVirtualTerminalInput_ReturnsNativeEventMode()
    {
        const uint processedInput = 0x0001;
        const uint virtualTerminalInput = 0x0200;

        var result = WindowsConsoleInputMode.GetRequiredMode(processedInput | virtualTerminalInput);

        Assert.AreEqual(0x0099u, result);
    }

    /// <summary>
    /// Verifies applying the required input transformation repeatedly leaves the mode unchanged.
    /// </summary>
    [TestMethod]
    public void GetRequiredMode_WithRequiredMode_ReturnsSameMode()
    {
        const uint requiredMode = 0x0099;

        Assert.AreEqual(requiredMode, WindowsConsoleInputMode.GetRequiredMode(requiredMode));
    }
}
