# GitSail

GitSail is a cross-platform Git client for the terminal with first-class keyboard
and mouse interaction. It is an independently implemented, MIT-licensed
application that delegates repository semantics to an installed Git executable.

The project is under active pre-1.0 development. The complete required behavior
and release gates are defined by [the normative design](docs/design.md).

## Install

Release builds are distributed only as a Native AOT .NET tool:

```console
dotnet tool install --global GitSail
git tui
```

Git 2.36 or newer is required. The .NET SDK is required to install, update, or
remove the tool, but the installed Native AOT application does not require a
separate .NET runtime.

## Build and test

```console
dotnet restore GitSail.slnx
dotnet build GitSail.slnx --configuration Release --no-restore
dotnet test --solution GitSail.slnx --configuration Release --no-build --no-restore
dotnet publish src/GitSail/GitSail.csproj --configuration Release --runtime osx-arm64 --no-restore
```

All managed tests use MSTest.Sdk and Microsoft.Testing.Platform.

## License

GitSail is licensed under the MIT License.
