param(
    [Parameter(Mandatory = $true)]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src/GitSail/GitSail.csproj'
$packageSource = Join-Path $repositoryRoot $PackageDirectory
$version = (dotnet msbuild $projectPath -getProperty:Version -nologo).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
    throw 'Could not read the GitSail package version from MSBuild.'
}

$pointerPackage = Join-Path $packageSource "GitSail.$version.nupkg"
$ridPackage = Join-Path $packageSource "GitSail.$Rid.$version.nupkg"
if (-not (Test-Path -LiteralPath $pointerPackage -PathType Leaf)) {
    throw "The pointer package is missing: $pointerPackage"
}

if (-not (Test-Path -LiteralPath $ridPackage -PathType Leaf)) {
    throw "The RID package is missing: $ridPackage"
}

$toolPath = Join-Path $repositoryRoot "artifacts/tool-install/$Rid-$([Guid]::NewGuid().ToString('N'))"
$executableName = if ($IsWindows) { 'git-tui.exe' } else { 'git-tui' }
$executable = Join-Path $toolPath $executableName
$originalPath = $env:PATH
$installed = $false

try {
    dotnet tool install GitSail `
        --tool-path $toolPath `
        --version $version `
        --add-source $packageSource `
        --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) {
        throw "Installing GitSail $version from the staged packages failed with exit code $LASTEXITCODE."
    }

    $installed = $true
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The installed tool command is missing: $executable"
    }

    & $executable --version
    if ($LASTEXITCODE -ne 0) {
        throw "The installed tool version command failed with exit code $LASTEXITCODE."
    }

    $doctor = (& $executable doctor --json | Out-String | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) {
        throw "The installed tool Doctor command failed with exit code $LASTEXITCODE."
    }

    if (-not $doctor.nativeAot -or $doctor.runtimeIdentifier -ne $Rid) {
        throw "The installed tool Doctor report does not match Native AOT RID $Rid."
    }

    $pathSeparator = [IO.Path]::PathSeparator
    $env:PATH = "$toolPath$pathSeparator$originalPath"
    & git tui --version
    if ($LASTEXITCODE -ne 0) {
        throw "Git external-command dispatch failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:PATH = $originalPath
    if ($installed) {
        dotnet tool uninstall GitSail --tool-path $toolPath
        if ($LASTEXITCODE -ne 0) {
            throw "Uninstalling GitSail from the isolated tool path failed with exit code $LASTEXITCODE."
        }

        if (Test-Path -LiteralPath $executable -PathType Leaf) {
            throw "The tool command remains after uninstall: $executable"
        }
    }
}

$manifestRoot = Join-Path $repositoryRoot "artifacts/tool-manifest/$Rid-$([Guid]::NewGuid().ToString('N'))"
$manifestPath = Join-Path $manifestRoot 'dotnet-tools.json'
$localInstalled = $false

New-Item -ItemType Directory -Path $manifestRoot | Out-Null
Push-Location $manifestRoot
try {
    dotnet new tool-manifest
}
finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Creating an isolated local tool manifest failed.'
}

try {
    dotnet tool install GitSail `
        --tool-manifest $manifestPath `
        --version $version `
        --add-source $packageSource `
        --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) {
        throw "Installing GitSail $version into the local manifest failed with exit code $LASTEXITCODE."
    }

    $localInstalled = $true
    dotnet tool restore `
        --tool-manifest $manifestPath `
        --add-source $packageSource `
        --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) {
        throw "Restoring GitSail $version from the local manifest failed with exit code $LASTEXITCODE."
    }

    Push-Location $manifestRoot
    try {
        dotnet tool run git-tui -- --version
        if ($LASTEXITCODE -ne 0) {
            throw "Running GitSail from the local manifest failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    if ($localInstalled) {
        dotnet tool uninstall GitSail --tool-manifest $manifestPath
        if ($LASTEXITCODE -ne 0) {
            throw "Uninstalling GitSail from the local manifest failed with exit code $LASTEXITCODE."
        }
    }
}
