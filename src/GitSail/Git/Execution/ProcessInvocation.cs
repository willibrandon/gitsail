using System.Collections.Immutable;

namespace GitSail.Git.Execution;

/// <summary>
/// Describes one complete shell-free child-process invocation.
/// </summary>
/// <param name="Executable">The previously resolved trusted executable.</param>
/// <param name="Arguments">The ordered literal argument values.</param>
/// <param name="WorkingDirectory">The canonical child working directory.</param>
/// <param name="Environment">The complete explicitly constructed child environment.</param>
/// <param name="StandardInput">The exact standard-input bytes.</param>
/// <param name="OutputPolicy">The independent output-capture limits.</param>
internal sealed record ProcessInvocation(
    ResolvedExecutable Executable,
    ImmutableArray<ProcessArgument> Arguments,
    CanonicalDirectory WorkingDirectory,
    ChildEnvironment Environment,
    StandardInputSource StandardInput,
    OutputPolicy OutputPolicy);
