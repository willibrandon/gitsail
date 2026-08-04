# Third-party notices

GitSail invokes an independently installed Git executable. Git is not included
in GitSail packages.

The application consumes the official `Hex1b` 0.165.0 NuGet package under the
MIT License. Its package graph includes Microsoft.Extensions.Logging.Abstractions
8.0.3 and QRCoder 1.7.0. Release builds generate the authoritative dependency
license report and SBOM from the locked restore graph.

Command-line parsing uses `System.CommandLine` 2.0.10 under the MIT License.

Native AOT packages include portions of the .NET runtime under the licenses
distributed with the .NET SDK/runtime. Linux hosts provide their own ICU and
system libraries as described in the installation requirements.
