namespace GitSail.BuildTools;

/// <summary>
/// Provides repository generators and validators that stay outside the shipped application.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.Out.WriteLine("GitSail.BuildTools 0.1.0");
            return 0;
        }

        Console.Out.WriteLine("Usage: GitSail.BuildTools --version");
        return args.Length == 0 ? 0 : 2;
    }
}
