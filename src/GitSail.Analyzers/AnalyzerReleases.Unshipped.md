; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
GITSAIL0001 | GitSail.SourcePolicy | Error | Enforces one type per source file
GITSAIL0002 | GitSail.SourcePolicy | Error | Requires XML documentation on visible declarations
GITSAIL0003 | GitSail.SourcePolicy | Error | Requires physical three-line XML summaries
