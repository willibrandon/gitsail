using GitSail.Git.Execution;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies fixed-locale transport prompts receive safe visible, secret, and confirmation treatment.
/// </summary>
[TestClass]
public sealed class CredentialPromptClassifierTests
{
    /// <summary>
    /// Verifies common Git and SSH prompt shapes receive the expected response treatment.
    /// </summary>
    /// <param name="prompt">The fixed-locale prompt text.</param>
    /// <param name="expectedValue">The numeric expected response treatment.</param>
    [TestMethod]
    [DataRow("Username for 'https://example.invalid':", (int)CredentialPromptKind.Text)]
    [DataRow("Password for 'https://example.invalid':", (int)CredentialPromptKind.Secret)]
    [DataRow("Are you sure you want to continue connecting (yes/no/[fingerprint])?", (int)CredentialPromptKind.Confirmation)]
    public void Classify_WithFixedLocalePrompt_ReturnsExpectedKind(
        string prompt,
        int expectedValue)
    {
        var expected = (CredentialPromptKind)expectedValue;
        Assert.AreEqual(expected, CredentialPromptClassifier.Classify(prompt));
    }

    /// <summary>
    /// Verifies terminal controls and bidirectional overrides become visible inert text.
    /// </summary>
    [TestMethod]
    public void Sanitize_WithTerminalAndBidirectionalControls_ReturnsVisibleTokens()
    {
        var result = CredentialPromptTextSanitizer.Sanitize("Password\u001B[2J\u202Esecret");

        Assert.AreEqual("Password<U+001B>[2J<U+202E>secret", result);
    }
}
