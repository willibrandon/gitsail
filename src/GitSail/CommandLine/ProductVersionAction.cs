using System.CommandLine;
using System.CommandLine.Invocation;

namespace GitSail.CommandLine;

/// <summary>
/// Writes the stable GitSail product version for System.CommandLine's built-in version option.
/// </summary>
internal sealed class ProductVersionAction : SynchronousCommandLineAction
{
    /// <inheritdoc />
    public override int Invoke(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        parseResult.InvocationConfiguration.Output.WriteLine(BuildInformation.DisplayVersion);
        return ExitCodes.Success;
    }
}
