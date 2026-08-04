
# GitSail — MIT-Licensed Git GUI Experience for the Terminal

**Design — version 2.4**

| Field | Decision |
|---|---|
| Status | Complete design; every requirement in this document is required for 1.0 |
| Date | 2026-08-03 |
| Product | **GitSail**; .NET tool package `GitSail`, command `git-tui`, invoked directly or as `git tui` |
| License | MIT for all repository-authored source, tests, documentation, strings, translations, and assets |
| UI | Hex1b consumed as an immutable, unmodified NuGet dependency under §6 |
| Runtime | .NET 10 LTS, Native AOT only |
| Git floor | Git 2.36; newer capabilities are detected individually |
| Runtime targets | `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64` |
| Distribution | NuGet .NET tool only: one top-level `GitSail` pointer package and one Native AOT tool package for each supported RID |

## Contents

1. [Product outcome](#1-product-outcome)
2. [License and project inputs](#2-license-and-project-inputs)
3. [Baselines and support contract](#3-baselines-and-support-contract)
4. [Command-line and process modes](#4-command-line-and-process-modes)
5. [Solution and API architecture](#5-solution-and-api-architecture)
6. [Immutable UI dependency boundary](#6-immutable-ui-dependency-boundary)
7. [Git execution and byte-preserving data model](#7-git-execution-and-byte-preserving-data-model)
8. [Repository model and consistency](#8-repository-model-and-consistency)
9. [Functional feature specification](#9-functional-feature-specification)
10. [Input, layout, rendering, and accessibility](#10-input-layout-rendering-and-accessibility)
11. [Security design](#11-security-design)
12. [Internationalization](#12-internationalization)
13. [Native AOT engineering](#13-native-aot-engineering)
14. [Build, .NET tool packaging, and release](#14-build-net-tool-packaging-and-release)
15. [Diagnostics and observability](#15-diagnostics-and-observability)
16. [Verification strategy](#16-verification-strategy)
17. [Traceability, issue closure, and performance](#17-traceability-issue-closure-and-performance)
18. [Implementation sequence](#18-implementation-sequence)
19. [Final acceptance contract](#19-final-acceptance-contract)

Appendices: [Configuration](#appendix-a--configuration-registry) · [Commands and menus](#appendix-b--command-and-menu-coverage) · [Git command families](#appendix-c--git-command-families) · [State and environment](#appendix-d--state-cache-and-environment) · [Artifacts](#appendix-e--required-artifacts) · [References](#appendix-f--engineering-references)

## 1. Product outcome

**GitSail** is a complete, cross-platform terminal implementation of the workflows people use Git GUI for, with first-class keyboard and mouse interaction, plus the modern repository-state and history workflows Git users expect from a new Git client. Git performs the repository operations. The application presents, validates, sequences, and explains those operations; it does not implement an object database, revision walker, merge engine, transport, credential store, or hook runner.

### 1.1 Name

The project name is **GitSail**: short, pronounceable, and suggestive of navigating repositories without colliding with the established GitUI/Gitu TUI names. The brand and command intentionally differ: users type the descriptive and discoverable `git tui`, while project/package names, namespaces, state directories, environment variables, and new configuration use `GitSail`, `GitSail.*`, `gitsail`, `GITSAIL_*`, and `gitsail.*` consistently. The native executable and Native AOT assembly name remain `git-tui` because Git discovers `git <name>` subcommands from `git-<name>` executables.

### 1.2 Distribution boundary

GitSail is a terminal application distributed exclusively as the NuGet .NET tool package `GitSail`. It is not a desktop application and has no macOS `.app` bundle, Finder/Dock/Spotlight integration, notarization, platform installer, portable archive, Homebrew formula, WinGet manifest, Scoop manifest, `deb`, `rpm`, or Alpine package. No operating-system application launcher is part of the product.

The primary installation is `dotnet tool install --global GitSail`. The .NET 10 SDK selects the matching RID-specific Native AOT package and installs the `git-tui` command shim. When the global tools directory is on `PATH`, both `git-tui` and Git external-command dispatch through `git tui` work. Local tool manifests are supported for pinned repository use through `dotnet tool install GitSail` and `dotnet tool run git-tui`; a local manifest does not place `git-tui` on `PATH`, so the design never promises `git tui` for a local-only installation.

Version 1.0 includes all of the following:

- repository discovery and selection, initialization, cloning, and recently used repositories;
- staged and unstaged file lists, untracked files, conflicts, submodules, renames, filters, and large-repository virtualization;
- file, hunk, line, and multi-file stage, unstage, and revert operations with exact byte preservation;
- commit creation, amend, signoff, author override, signing, cleanup modes, templates, every applicable Git commit hook, hook bypass, saved commit message restore, and detached/published-commit warnings;
- branch create, checkout, rename, delete, reset, fetch-tracking behavior, detach, and worktree-aware operation;
- merge, abort, rerere, built-in per-hunk conflict resolution, side-by-side two-way diff, and three-way merge;
- remotes, fetch, prune, push, delete-remote-branch, explicit safe force leases, progress, cancellation, and credential prompts;
- stash create/list/show/apply/pop/drop;
- incremental blame, tree browsing, structured history graph, commit inspection, cherry-pick, revert, and interactive rebase planning;
- repository statistics, maintenance, verification, tools, mergetools, textconv, spell checking, SSH-key assistance, clipboard, and external editor/browser integration;
- `gui`, `citool`, `blame`, `browser`, `diff`, `merge`, `history`, `rebase`, `pick`, `version`, `help`, `completion`, and `doctor` entry modes;
- fresh MIT-licensed English UI and complete fresh translations for the 14 required locales in §12;
- all tracker enhancements and regressions represented by the locked requirement manifest in §17.

No feature in this list is postponed beyond 1.0. Milestones in §18 are sequencing boundaries, not release boundaries.

## 2. License and project inputs

All code, tests, documentation, strings, translations, and assets created for this repository use the MIT license. The root `LICENSE` file and NuGet package metadata carry the license; source files do not repeat license banners.

Use Git's documented commands, file formats, configuration, and observed behavior when implementing features. Do not copy Git GUI source code, images, manuals, UI text, or translations. Write the manual, interface text, tests, translations, and implementation for this project.

The local Git checkout in §3.2 is available for its `Documentation/` directory, technical-format documentation, command list, release notes, and Git command behavior. The application invokes the installed `git` executable and does not link Git source or internal C APIs.

The product installs as `git-tui`; it does not replace the existing `git-gui` command.

## 3. Baselines and support contract

### 3.1 Conformance baseline

The conformance target is the externally observable behavior of Git GUI at commit `5dcb97869546`, represented by this verified inventory:

- one main program of 4,030 lines;
- **40** `lib/*.tcl` modules totaling **12,604** lines;
- askpass and yes/no helpers plus legacy Windows launcher/shortcut artifacts, recorded only as reference inventory and not as GitSail distribution requirements;
- six original modes: main commit UI, citool, blame, browser, repository picker, and version;
- 14 UI locales: `bg`, `de`, `el`, `fr`, `hu`, `it`, `ja`, `nb`, `pt-BR`, `pt-PT`, `ru`, `sv`, `vi`, and `zh-CN`.

These counts only scope the behavior to cover; they do not dictate the C# project structure. Each behavior requirement has a test with a concrete expected result. “See source,” “same as upstream,” and “per spec” are not valid expected results.

### 3.2 Git reference checkout

The primary forward-looking Git reference is the clean local checkout at `/Users/brandon/src/git` with:

| Field | Reference value |
|---|---|
| Origin | `https://github.com/git/git.git` |
| Commit | `5b2471720c93ee30e5764a19f3d3b3ae9ec9712a` |
| Describe | `v2.55.0-493-g5b2471720c` |
| Commit date | `2026-08-03T09:31:20-07:00` |
| Repository license file | Git's `COPYING` (GPL version 2), SHA-256 `5b2198d1645f767585e8a88ac0499b04472164c0d2da22e75ecf97ef443ab32e` |

The absolute path is a developer-machine setting, not a build input. Reference checks are read-only and must not change that checkout. CI uses its own disposable checkout when it needs to build or execute a particular Git revision.

This checkout documents current Git behavior; the supported minimum remains Git 2.36. Each command contract provides a Git 2.36 form and capability checks for newer behavior. The application records the installed `git --version` and never assumes that it matches the reference checkout.

### 3.3 Supported operating systems

Support follows the active .NET 10 support matrix and is revalidated for every release:

| RID | Minimum release contract | Native build/test environment |
|---|---|---|
| `win-x64` | Supported .NET 10 x64 Windows, including Windows 10 1809 where still covered | Native x64 Windows runner |
| `win-arm64` | Supported .NET 10 Arm64 Windows | Native Arm64 Windows runner |
| `linux-x64` | glibc 2.27 or newer | x64 builder using a pinned glibc 2.27 sysroot; native RHEL 8 compatibility runner |
| `linux-arm64` | glibc 2.27 or newer | Native Arm64 builder and RHEL-compatible runner using the same sysroot |
| `linux-musl-x64` | musl 1.2.3 or newer | Native x64 Alpine runner |
| `linux-musl-arm64` | musl 1.2.3 or newer | Native Arm64 Alpine runner |
| `osx-x64` | macOS 14 or newer while supported by .NET 10 | Native Intel macOS runner |
| `osx-arm64` | macOS 14 or newer while supported by .NET 10 | Native Apple Silicon runner |

There are exactly eight RIDs. CI neither builds across operating-system families nor executes release gates under QEMU. Cross-architecture compilation is used only where the .NET Native AOT toolchain documents support, and never substitutes for a native execution lane.

### 3.4 System dependencies

The installed RID payload is self-contained with respect to the .NET runtime. Installing, updating, restoring, or uninstalling it uses the .NET 10 SDK's tool commands. The payload may dynamically use operating-system libraries that are part of the declared platform contract: libc, zlib, system ICU, terminal APIs, and Windows system DLLs. The manual and package metadata state the required host libraries; a .NET tool package does not pretend to install operating-system packages. CI runs `ldd`, `otool -L`, and PE import inspection and compares the result with an allowlist.

Git 2.36 or newer must be available. Optional tools are detected without searching the current directory: `aspell`, platform clipboard helpers, SSH tools, an external editor, browser helper, and user-selected merge tools. Missing optional tools disable only the associated command and produce an actionable explanation.

## 4. Command-line and process modes

The parser is generated from one typed command model. Built-in help, the embedded manual, generated shell completions, and parser tests use the same model.

```text
git tui [gui] [--working-dir <directory>] [--trace[=<file>]]
git tui citool [--amend | --nocommit] [--commitmsg]
git tui blame [--line <number>] [--range <start:end>] [--detect-moves] [--detect-copies] [<revision>] -- <path>
git tui browser [<revision>] [-- <directory>]
git tui diff [--cached] [<left> [<right>]] [-- <pathspec>...]
git tui merge [-- <path>...]
git tui history [<revision-range>] [-- <pathspec>...]
git tui rebase [--onto <revision>] [<upstream>]
git tui pick
git tui doctor [--json]
git tui help [<command>]
git tui completion <bash|zsh|fish|powershell>
git tui version
git-tui -h | --help | --version
```

Every path-bearing mode also accepts `--pathspec-from-file <file|-> --pathspec-file-nul`. The input contains native path records separated by NUL and is the automation-safe route for paths a shell or managed argv cannot represent.

Rules:

- `--` terminates option parsing. Revisions are validated with `git rev-parse --verify --end-of-options`. Before managed parsing, `NativeArgumentReader` captures lossless startup arguments: UTF-16 from Windows, `/proc/self/cmdline` bytes on Linux, and `_NSGetArgv` bytes on macOS. Path operands use those native values. Linux installations require a mounted procfs for raw path-bearing CLI modes; when it is unavailable the mode fails before mutation and accepts the same paths through a NUL-delimited `--pathspec-from-file` input. UI-selected paths always originate from raw Git output.
- Unknown options and commands print concise stderr diagnostics plus usage and return exit code 2. Repository, Git, or operation failures return 1. User cancellation returns 130. Success returns 0.
- `citool` returns success only after the requested commit flow completes.
- `--trace` never writes into the alternate-screen terminal. It writes to the selected file or the user state directory and opens the in-app log drawer on request.
- Helper modes are not public arguments. Askpass, yes/no, sequence-editor, and editor bridges are selected by authenticated private environment variables before normal argument parsing (§11.7).
- `git tui help` is the complete embedded offline manual. `git tui completion <shell>` writes a generated completion script to stdout with installation instructions; a .NET tool install does not mutate shell startup files or Git's man-page search path.

## 5. Solution and API architecture

### 5.1 Repository layout

```text
gitsail/
├── global.json                         # exact .NET/MSTest SDKs; MTP is the test runner
├── GitSail.slnx                        # application, build-tool, and test projects
├── Directory.Build.props               # warnings, analyzers, deterministic build
├── Directory.Packages.props            # centrally pinned packages
├── README.md                            # NuGet package readme and install entry point
├── LICENSE                              # MIT
├── src/GitSail/                         # one shipped application assembly
│   ├── Program.cs                       # composition root and mode dispatch
│   ├── CommandLine/
│   ├── Git/Execution/                   # process boundary and typed invocations
│   ├── Git/Parsing/                     # byte-oriented parsers
│   ├── Domain/                          # repository snapshots and services
│   ├── Features/                        # vertical feature slices
│   ├── Ui/Widgets/                      # custom Widget/Node pairs
│   ├── Ui/Dialogs/
│   ├── Ui/Theming/
│   ├── Localization/Generated/
│   └── Security/
├── tools/GitSail.BuildTools/            # non-shipped generators and validators
├── requirements/                        # locked behavior/issue/command manifests
├── locales/                             # fresh MIT Fluent/JSON source catalogs
├── docs/                                # manual and guides
└── tests/
    ├── Shared/
    │   ├── MSTestSettings.cs           # assembly-level method parallelization
    │   └── TestSeq.cs                  # sequence/single/type assertion helpers
    ├── GitSail.UnitTests/
    ├── GitSail.ServiceTests/
    ├── GitSail.UiTests/
    ├── GitSail.AotTests/
    ├── GitSail.SecurityTests/
    ├── GitSail.PerformanceTests/        # executable regression-budget gates
    ├── GitSail.CompatibilityTests/      # behavior compatibility tests
    └── GitSail.PackageTests/
```

One shipped assembly keeps application implementation types internal and avoids manufacturing an accidental public library API. Test projects receive `InternalsVisibleTo`. Build tools are isolated because they run during build and are not part of the AOT closure.

### 5.2 .NET type rules

- All application types are `internal` unless an external integration demonstrably requires public access. No public API is added merely to cross an assembly boundary.
- Domain values use types such as `ObjectId`, `RefName`, `GitPath`, `RepositoryId`, `ResolvedExecutable`, `ActionId`, and `OperationGeneration`; raw strings do not cross security boundaries.
- Async methods accept a `CancellationToken` as the last parameter. Long-lived streams use `IAsyncEnumerable<T>` with enumerator cancellation. Owned streams and processes implement `IAsyncDisposable` and expose a single completion task.
- Expected operation failures return typed results with stderr and exit information. Programmer and invariant failures throw established .NET exception types with actionable context.
- Mutable global/static state is prohibited. Process services, caches, clocks, filesystem access, and environment access are injected instances. Constants and generated immutable tables are permitted.
- One type is defined per file. Nullable annotations and analyzers are enabled. No analyzer warning is suppressed without a requirement ID and review; the Native AOT suppression target is zero.

### 5.3 Application-owned widget rules

Every custom visual follows the repository conventions:

- `*Widget` is an immutable record and `*Node` is a mutable class;
- mutable caller-visible state uses `IStatefulWidget<TWidget,TState>` and a dedicated state class;
- transient focus, hover, measurement, and animation state remains in the node;
- widget methods never start with `With`; handlers use `On*`; receiver and builder parameter names follow the consumed public API's conventions;
- default input bindings trigger stable `ActionId` values so user remapping can remove or replace them;
- every custom widget, node, adapter, state type, extension, and theme element is owned by GitSail and remains in the GitSail repository; and
- each widget has measurement, reconciliation, input, theme, accessibility, headless snapshot, resize, and disposal tests.

### 5.4 Concurrency model

The UI render loop owns all UI state. Background operations publish immutable `RepositorySnapshot` and `OperationSnapshot` values through bounded channels. Every repository refresh carries a monotonically increasing `OperationGeneration`; a result is applied only if its generation is still current and its repository identity matches the active tab.

`OperationSupervisor` owns every task and child process. It provides start, progress, cancel, graceful shutdown, escalation, exception observation, and application-exit joining. UI handlers never start unowned tasks. `Invalidate()` is the only cross-thread UI call.

`RepositoryMutationLease` replaces the underspecified lock enum. It is an exception-safe async lease with these typed purposes: `ReadIndex`, `RefreshIndex`, `UpdateIndex`, `ApplyPatch`, `Checkout`, `Commit`, `Merge`, `Abort`, `Rebase`, and `RemoteMutation`. Compatibility tests map each observed reference operation to the appropriate purpose, but widgets only consume the derived `CanExecute(ActionId)` snapshot.

## 6. Immutable UI dependency boundary

The TUI framework is an external, read-only dependency. GitSail must never modify its repository, source, projects, build targets, analyzers, native code, tests, documentation, samples, packages, assets, public API, or release process. No framework change, fork, patch, source copy, private build, feature request, or future upstream release is a prerequisite or deliverable for GitSail.

### 6.1 Binary consumption only

GitSail consumes the official `Hex1b` NuGet package at one exact centrally pinned version. The initial baseline is `0.165.0`; an upgrade may select only an already-published official package and requires ordinary dependency review. The solution contains no project reference, source submodule, vendored source, patched package, locally rebuilt package, or `InternalsVisibleTo` access to the dependency. CI verifies the package ID, version, SHA-512 content hash, license, and complete transitive graph from restore metadata and the global NuGet package cache.

The separate source checkout is read-only reference material for understanding documented public behavior. GitSail build, test, pack, and release commands never write to it, invoke its build targets, or depend on its working-tree state.

### 6.2 Public API boundary

GitSail uses only documented public types and extension points. Reflection into non-public members, copied internal code, assembly rewriting, runtime detours, source inclusion, and reliance on undocumented file layout are prohibited. A package upgrade must pass API-compatibility, UI regression, AOT, native-asset, and package-content tests before its central-version change is accepted.

All product-specific behavior is implemented in `src/GitSail`: file virtualization and selection, command/menu models, capability policy, clipboard policy, widgets/nodes, themes, presentation/workload adapters, input routing, child-process integration, and accessibility metadata. When a required behavior is not directly supplied by the public dependency, GitSail composes public primitives or implements a GitSail-owned adapter against a documented public extension point. It never resolves the gap by changing the dependency.

### 6.3 Native AOT proof

Milestone M1 publishes and executes the smallest possible GitSail Native AOT application against the exact restored package on every supported RID before feature implementation begins. `VerifyReferenceAotCompatibility=true`, trim/AOT analyzers, native import inspection, and clean-machine execution apply to the consumed binary closure exactly as published. A failure blocks that GitSail dependency selection; it does not authorize a dependency modification or warning suppression.

Static, analyzable reflection is allowed in GitSail. Runtime code generation, dynamic assembly loading, unanalyzable reflection, and unresolved trim warnings are prohibited. GitSail-authored interop uses generated marshalling or reviewed Native AOT direct calls. Dependency-owned interop remains byte-for-byte identical to the official package.

### 6.4 Runtime assets and terminal behavior

The RID-specific tool package preserves any dependency-owned native libraries or helper executables required by the selected public features; GitSail does not rebuild, relink, rename, patch, or claim ownership of them. Package tests compare those files with the restored NuGet assets and reject any unexplained addition or mutation. GitSail adds no helper executable of its own.

Universal F10 access, file-list behavior, color and Unicode policy, optional enhanced-key handling, clipboard results, and terminal capability fallbacks are application concerns implemented through existing public APIs. A capability that the current dependency cannot expose safely is not guessed. GitSail uses the conservative baseline behavior or a GitSail-owned public adapter and records that path in its own tests. When GitSail consumes a public embedded-terminal component, its application-owned integration policy covers focus ownership, resize, cancellation, completion, exit code, control-sequence filtering, and child-process lifetime.

## 7. Git execution and byte-preserving data model

### 7.1 Executable resolution

`ExecutableResolver` accepts a program kind, not an arbitrary command string. It searches only absolute entries from a sanitized `PATH`, rejects empty/relative entries, resolves symlinks or Windows reparse points, validates file type and executable permission, and never searches the current directory or worktree. Results are immutable `ResolvedExecutable` instances containing the canonical path and file identity. The identity is rechecked immediately before spawn to reduce replacement races.

Git is resolved once per repository session and its version/capabilities are cached. Optional tools are resolved only after a user invokes or enables the feature. Windows uses safe executable-search APIs and does not inherit ambiguous extension or current-directory lookup.

### 7.2 Process boundary

There is one process-spawn service and no direct `Process.Start`, `Process`, shell helper, or P/Invoke spawn call elsewhere. A banned-API analyzer enforces the boundary.

```csharp
internal sealed record ProcessInvocation(
    ResolvedExecutable Executable,
    ImmutableArray<ProcessArgument> Arguments,
    CanonicalDirectory WorkingDirectory,
    ChildEnvironment Environment,
    StandardInputSource StandardInput,
    OutputPolicy OutputPolicy,
    CancellationPolicy CancellationPolicy);

internal interface IChildProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        IProgress<ProcessEvent>? progress,
        CancellationToken cancellationToken);

    ValueTask<RunningProcess> StartAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken);
}
```

`RunningProcess` is `IAsyncDisposable`, exposes bounded byte-oriented stdout and stderr streams, a completion task, explicit stdin completion, and graceful termination. Stream pumps never block each other and spill to a permission-restricted temporary file after the configured in-memory threshold.

The .NET 10 Unix implementation keeps the exact-byte ABI in `UnixNative` and uses the Native AOT-linked `System.Native` fork/`execve` portability entry point. GitSail constructs and owns NUL-terminated `argv` and `envp` blocks, passes the canonical raw-byte working directory, receives close-on-exec redirected pipes, and reaps the exact returned PID. This avoids managed code after `fork`, adds no helper executable or native package asset, and uses the same platform implementation already carried by the self-contained runtime. The ABI is isolated to one file and guarded by framework-dependent, Linux, macOS, and Native AOT execution tests; adopting another target-framework major requires an explicit ABI review rather than runtime-major roll-forward. Windows uses only `ProcessStartInfo.ArgumentList`, a cleared explicit environment, and UTF-16 paths.

Stdout and stderr remain separate. Events carry stream identity and a monotonic receipt sequence, but the UI describes inter-stream order as arrival order rather than claiming kernel-exact ordering. Git progress is parsed from stderr; result data is parsed from stdout.

Cancellation sends the platform's graceful interrupt to the process group, waits two seconds, requests normal termination, waits another two seconds, then kills the remaining tree. The supervisor closes stdin, drains both pipes, awaits exit, cleans temporary files, releases leases, and schedules a new repository generation. Commit/ref/index mutators may override the grace interval but may not skip cleanup.

### 7.3 Raw path model

`GitPath` is lossless:

- on Unix it stores the exact non-NUL byte sequence returned by Git;
- on Windows it stores the exact UTF-16 path value;
- equality, ordering, dictionary keys, serialization, and Git round trips operate on the native representation; and
- `DisplayText` is a separate escaped, control-sanitized representation that is never passed back to Git or the filesystem.

NUL-delimited Git protocols are read and written as bytes. Commands supporting `--pathspec-from-file=- --pathspec-file-nul`, `-z --stdin`, or an equivalent stdin protocol use it. For Unix Git commands that require a path in argv and offer no byte-safe stdin form, the sole process service uses a reviewed `posix_spawn`/`execve` path that constructs native byte argv. Windows continues to use `ProcessStartInfo.ArgumentList` with UTF-16 values. Tests create legal non-UTF-8 names and prove status, diff, stage, revert, blame, rename, and delete round trips.

`ProcessArgument` and `ChildEnvironment` can carry native byte values on Unix and UTF-16 values on Windows. Compatibility tool variables therefore preserve a single raw filename; a new `GITSAIL_PATHS_FILE` points to a user-only NUL-delimited file for unambiguous multi-path tools while legacy `FILENAMES` retains its documented compatibility representation. The file is deleted when the child exits.

Unix filesystem operations on `GitPath` use reviewed `openat`/`fstatat`/`renameat`/`unlinkat` interop with raw byte names and directory handles. They never convert through `System.IO` strings. Windows uses handle-based UTF-16 APIs. The same rule covers conflict-result saves, export, revert recovery, clone cleanup, and direct interoperability files.

No operation reconstructs a native path from display text. UI copy offers both a human-readable escaped path and, when representable, the platform-native path.

### 7.4 Typed Git invocations

Git commands are constructed by internal command-specific builders. Callers cannot append arbitrary options after path arguments. `Revision`, `RefName`, `RefSpec`, `PathSpec`, `ConfigKey`, and `GitPath` occupy distinct types. Each invocation declares:

- capability/version prerequisites;
- whether repository configuration can execute code;
- stdin framing and maximum record size;
- stdout/stderr framing and decoding;
- accepted exit codes and warning treatment;
- cancellation safety and mutation lease; and
- secret-bearing arguments or output fields for logging redaction.

Command-specific builders encode exact option order, byte framing, expected exit behavior, and minimum Git version. Tests exercise their emitted invocations directly.

### 7.5 Environment

Children receive a newly built environment, not a mutation of process-global state. It starts with a documented safe inheritance allowlist, inserts the sanitized `PATH`, and applies operation-specific variables. Repository-scoped `GIT_DIR` and `GIT_WORK_TREE` are set only from discovered canonical values and are cleared before discovery or sub-repository operations.

Tests isolate `HOME`, `USERPROFILE`, XDG directories, system/global Git config, locale, credential helpers, SSH commands, editor/browser variables, hooks, and temp directories. No test reads or writes the developer's configuration.

### 7.6 Encoding

Control protocols and object IDs are ASCII or UTF-8 exactly as Git documents. Paths stay native bytes. Commit content and file content retain raw bytes plus a selected display encoding. Console output uses an incremental decoder selected from explicit command configuration, Git's encoding settings, locale, and UTF-8 fallback; invalid sequences render visibly without changing retained bytes.

The UI never feeds decoded-and-reencoded output back to Git when exact bytes are available.

### 7.7 Raw patch engine

Diff output is stored in a bounded raw-byte spool. `DiffIndex` contains offsets and lengths into that spool for headers, path fields, hunks, and lines. Decoding is a presentation operation only.

Hunk and line stage/unstage/revert operations build a new patch from original raw line slices. Only ASCII patch control fields and hunk counts are generated. Original path headers, content bytes, line endings, no-newline markers, and quoted forms are preserved. The engine never describes a patch as having the “path's encoding.”

Before mutation, the generated patch is validated with `git apply --check` using the same target flags. The actual apply uses `--whitespace=nowarn` plus the typed cached/reverse flags. On failure, no optimistic state transition survives; a fresh generation is scheduled and the complete sanitized error is shown.

Textconv and external diff output are view-only. Hunk and line mutation is disabled for those views because the transformed bytes do not represent an applicable worktree patch.

### 7.8 Large-output bounds

All parsers are incremental and impose configurable record, line, and aggregate limits. Exceeding an in-memory limit switches to spooling rather than failing. A single pathological line bypasses intraline LCS and receives a visible “intraline comparison omitted” marker. The intraline algorithm has fixed line-length and product limits and a linear-time fallback.

## 8. Repository model and consistency

### 8.1 Discovery and identity

Discovery obtains the absolute git directory, common directory, worktree, object format, bare status, prefix, and worktree identity through Git. `RepositoryId` combines canonical common-directory identity and worktree identity; path strings alone are insufficient.

Opening a repository first runs Git's ownership/safe-directory checks. The application never silently changes `safe.directory`. An unsafe repository is shown read-only with instructions; the user may invoke Git's documented trust action explicitly from a confirmation dialog.

### 8.2 Repository snapshot

One immutable `RepositorySnapshot` contains:

- generation and repository identity;
- HEAD state, branch/upstream/ahead-behind, object format, and worktree state;
- sequencer state for merge, rebase, cherry-pick, revert, bisect, and detached HEAD;
- index/worktree/untracked/conflict records keyed by lossless `GitPath`;
- rename/copy pairs with old and new paths;
- submodule state, sparse-checkout/sparse-index state, fsmonitor state, and partial-clone/promisor state;
- available actions and reasons unavailable; and
- diagnostics produced during the scan.

The scan uses raw/NUL Git output and does not walk the worktree to infer Git status. This preserves sparse checkouts, filters, LFS, submodules, ignored files, and unusual filesystems.

### 8.3 Rescan pipeline

Each rescan is a cancellable DAG with a unique generation. Independent read-only Git operations execute concurrently under bounded concurrency. Any mutation invalidates earlier generations. Results publish atomically only when all mandatory nodes complete and the HEAD object, exact symbolic HEAD target or detached state, and index-content identity still match the scan start; otherwise the scan retries once and then reports concurrent repository change.

Optional automatic refresh uses filesystem notifications only as a debounce signal. Overflow, rename ambiguity, watcher failure, network filesystems, and the program's own writes all collapse to a full Git rescan. A periodic low-frequency validation prevents permanent staleness. The default remains manual refresh plus refresh after every mutation; users may enable automatic refresh.

### 8.4 Configuration

Configuration is loaded with NUL-delimited `git config` output that includes each value's file and scope: system, global, local, worktree, or command. Typed readers distinguish absent, invalid, empty, inherited, and explicit values. Writes use `git config`, never edit config files directly, and preserve scope semantics.

Existing `gui.*`, `color.diff.*`, core, branch, remote, merge, and tool keys needed for compatibility are honored. New product settings consistently use `gitsail.*`. No `gittui.*` or `guitui.*` spelling is ever written.

Repository configuration capable of executing code is governed by §11.4 rather than treated as ordinary data.

### 8.5 State and direct file access

Git commands are preferred for repository mutations. Direct access is limited to commit-message interoperability files that Git GUI users expect and files required by documented Git protocols. Every path comes from `git rev-parse --git-path`, is opened without following links, is verified beneath the canonical Git/common directory by file identity, and is written through a permission-restricted same-directory temporary file plus atomic replacement.

Merge, cherry-pick, revert, rebase, rerere, and ref state are changed through Git commands. The application does not delete their sentinel files itself.

Index-lock recovery is a deliberately dangerous manual action. The dialog displays the lock path and metadata, checks for known live child and Git processes, explains that age is not proof, defaults to Cancel, and requires typed confirmation before unlinking the exact no-follow file. A rescan follows immediately.

Undo-revert patches and crash reports are never stored inside `.git`. Undo data is held in memory and in a mode-0600 file under the user cache directory, removed after successful undo, repository close, or 24-hour cleanup. Crash reports go to the user state directory and exclude repository content by default.

## 9. Functional feature specification

### 9.1 Main commit workspace

The main screen has four responsive regions: unstaged files, staged files, diff/conflict content, and commit message/actions. At wide sizes it uses a two-column layout; at medium sizes it stacks file lists above content; at the supported minimum of 60×18 it uses tabs without hiding actions. Below 60×18 it shows a non-destructive resize screen with Help, Doctor, and Quit available.

File lists are virtualized, filterable by path/status, and preserve selection by raw path across generations. They support keyboard range selection, Ctrl/Shift mouse selection, status badges that never rely on color alone, rename old/new names, conflict stages, submodule summaries, and an escaped-path detail view. A configured untracked display cap affects rendering only, not correctness or stage-all behavior.

The commit action is visually primary. Push is not placed beside Commit by default; it remains in Remote, F2, and its optional shortcut. Users who want a persistent push action may enable `gitsail.showPushAction`, which places it in a separately labeled remote-action region so an accidental adjacent click cannot commit and push.

The diff view uses the published `EditorWidget`, `EditorState`, document, gutter, decoration, and view-renderer APIs as a read-only presentation surface rather than rebuilding editor behavior. GitSail supplies a read-only input profile that retains keyboard navigation, line and word selection, click positioning, drag selection, Ctrl-click, double-click, triple-click, vertical and horizontal wheel scrolling, scrollbars, wrapping, copy, and search while removing every text mutation, undo, redo, completion, and language-server action. Unified views use one editor state; side-by-side views use independent states over aligned presentation documents with generation-checked synchronized scrolling. The published `GitDiffDecorationProvider` supplies baseline unified-diff syntax treatment; GitSail-owned gutters and semantic decoration providers add old/new line numbers, hunk actions, intraline spans, whitespace, and conflict state through the public extension points.

The editor document contains decoded and sanitized display text only. The raw patch spool and `DiffIndex` identify each file, hunk, and line; generation-stamped line metadata maps cursor offsets and selected display text back to the original raw slices. Editor text is never encoded back into a patch, and typing into a diff view cannot mutate either the presentation document or repository. The diff view also supports unified and side-by-side layouts, context adjustment, search, goto line, bidi isolation, encoding selection, copy, raw-byte metadata, and a context menu whose enablement is derived from typed diff capabilities.

### 9.2 Stage, unstage, and revert

The following actions are required for tracked, untracked, renamed, deleted, type-changed, conflicted, and submodule entries where Git permits them:

- stage/unstage focused file and selection;
- stage all and unstage all;
- stage/unstage a hunk;
- stage/unstage selected lines, including discontiguous selections;
- stage an untracked hunk through a typed intent-to-add sequence;
- revert file, hunk, or selected lines with a destructive confirmation;
- undo the most recent revert while its repository/index preconditions still match; and
- choose ours, theirs, or base-derived conflict content per hunk before staging the resolution.

Every operation captures precondition OIDs/stat information, acquires the correct lease, uses byte-safe paths, validates patches, treats non-zero exit as failure, surfaces stderr warnings separately, and performs a generation-checked rescan.

Rename-aware status and diffs use explicit rename detection (`-M`/configured threshold) with raw old/new path metadata. Commands that would hide the old path by filtering only the new path are not used.

### 9.3 Commit editor and commit pipeline

The commit message editor uses lifted `EditorState`, preserves drafts across reconciliation and failed hooks, supports undo/redo, optional hard wrap, spelling, signoff, templates, author override, and amend. Initial content has one exact precedence order: a present `GITGUI_EDITMSG`, `GITGUI_MSG`, or `GITGUI_BCK` saved commit message file; a pending merge message; a pending squash message; the exact selected HEAD body for an amend session; the effective `commit.template`; then an empty document. A present empty saved message file is intentional and still wins. Lower-precedence sources never overwrite a higher-precedence message.

GitSail asks Git for the effective template with `git config --null --type=path --get commit.template`, so scope, includes, conditional includes, last-value precedence, and `~` expansion remain Git-owned. A relative result is resolved from the canonical repository working directory used for `git commit`. Unix values and reads retain exact native path bytes; Windows uses the native UTF-16 path. The read is limited to a 16 MiB regular file, follows ordinary file-link semantics so shared linked templates work, and requires valid UTF-8 editor content. A configured path that is missing, not a regular file, too large, or invalid UTF-8 produces an actionable open failure instead of silently falling back to an empty message.

An exact unchanged template disables Commit and explains that the template must be edited. Editing and then restoring the exact initial content disables Commit again. This deliberately preserves Git's editor-template safeguard even though the final porcelain transaction uses `--file`, a mode for which Git itself documents that templates otherwise have no effect. Recovery, merge, squash, and amend messages are not subject to the unchanged-template rule. GitSail obtains external-editor precedence from `git var GIT_EDITOR`; external editing occurs in an embedded PTY and reloads only after an atomic file-change check.

The commit transaction delegates the repository transaction to Git porcelain instead of reimplementing `git commit` with `write-tree`/`commit-tree`/`update-ref`:

1. acquire `Commit` lease and capture the HEAD object, exact symbolic HEAD target or detached state, and index-content preconditions;
2. validate committer identity through Git;
3. resolve amend, merge, detached, and sequencer state;
4. warn when amending any commit contained by remote-tracking refs, listing all matching refs and explaining that the check is a local heuristic;
5. prepare the `GITGUI_EDITMSG` draft atomically and retain the user's cleanup selection as either Git-owned `default` or one explicit documented mode;
6. invoke one typed `git commit --file=<draft>` transaction with the resolved amend, signoff, author, signing, cleanup, merge/sequencer, and bypass options; for `--cleanup=default`, Git itself resolves `commit.cleanup` plus the effective `core.commentChar`/`core.commentString`, while an explicit mode overrides the configured default; Git owns index/ref locking, reflog, parent selection, `core.hooksPath`, linked-worktree paths, signing, and hook order;
7. let Git run every applicable hook, including `pre-commit`, `prepare-commit-msg`, `commit-msg`, `post-commit`, and `post-rewrite` for amend, with the stdin/arguments Git defines;
8. stream sanitized output and classify hook/signing/ref failures without pretending success; cancellation follows the mutating-operation policy and never manually edits refs or sequencer files; and
9. verify the resulting HEAD attachment, object, and index, save or clear drafts according to outcome, and publish a new generation.

The symbolic HEAD target is a mutation precondition independently of its resolved object ID. Detaching at the same commit or switching from one branch to another branch at the same commit invalidates the prepared transaction, because otherwise an external checkout could redirect the user's reviewed commit without changing either the OID or staged bytes. Status capture brackets the index with both the resolved object and `git symbolic-ref --quiet HEAD`; commit validation repeats the complete precondition after acquiring the lease and immediately before invoking porcelain.

When HEAD is detached, GitSail asks Git for the effective boolean `gui.warndetachedcommit`; an absent value defaults to enabled. An enabled warning is bound to the exact detached HEAD object ID, defaults focus to Cancel, displays that ID, explains that the new commit will not belong to a branch and may become unreachable, and directs the user to create or switch to a branch when detachment is not intentional. The view passes the exact warning snapshot it displayed; the transaction service resolves the effective setting again under the commit lease and accepts only confirmation for the exact current detached object. Detached-HEAD, published-amend, and hook-bypass warnings are composed into one cancel-first dialog when they overlap, rather than training the user through a sequence of independent prompts.

`git stripspace` is used only for a non-mutating preview when the UI shows what cleanup will remove. The bytes committed are produced by `git commit`, preventing drift in cleanup, hooks, signing, reflog, merge parents, or future Git semantics.

“Commit without hooks” is a separate, clearly labeled action with confirmation and audit entry. It skips only the hooks Git documents as bypassable and never bypasses message validation silently.

### 9.4 Branch, checkout, and reset

Branch create, checkout, detach, rename, delete, and reset use Git validation and transactional preconditions. Dialogs expose tracking configuration, fetch-before-checkout, fast-forward-only versus reset behavior, deletion mergedness checks, and exact commits affected. Creating a local branch from a remote branch proposes the complete remote branch tail, preserves namespace prefixes, sets the upstream explicitly, and previews the source/upstream pair. Destructive choices default to Cancel. Worktree occupancy, sparse checkout, submodules, filters, and concurrent ref changes are handled as first-class errors rather than overwritten. Branch reset uses typed revisions and has a regression fixture for malformed/special ref names; UI text can never become command syntax.

### 9.5 Merge, conflict resolution, diff, and merge modes

Merge provides revision selection, strategy/options validated against an allowlist, fetch, progress, abort, rerere, and conflict navigation. Abort calls Git and never removes state files directly.

Selecting Merge from the branch/worktree workspace first binds the complete nonsymbolic source ref and exact source OID to the displayed HEAD attachment/OID, complete index fingerprint, and action-relevant worktree fingerprint. GitSail brackets that fingerprint with stable complete branch-catalog captures, rejects unborn HEAD and self-merge, and computes current-only/incoming-only counts from the two exact OIDs. The cancel-first confirmation shows both full OIDs and classifies already-integrated, fast-forward, or diverged history before any mutation.

Merge options are typed values rather than command fragments: Git-configured/fast-forward-only/create-merge-commit policy; Git default, `ort`, `resolve`, `ours`, or `subtree` strategy; normal/ours/theirs ort conflict preference; squash; stop-before-commit; and configured/on/off overrides for autostash, rerere index update, and incoming-tip signature verification. Invalid combinations cannot be constructed. The dialog explicitly distinguishes the `ours` strategy, which discards the incoming tree, from the `-Xours` conflict preference, which retains nonconflicting incoming changes. It also explains that fast-forward cannot honor stop-before-commit unless no-fast-forward is selected and that autostash reapplication can itself conflict.

Immediately before execution, the service reacquires stable branch catalogs around a new exact worktree fingerprint and requires every confirmed byte and the selected ref target to match. It then invokes noninteractive Git porcelain with forced progress, allowlisted literal options, and the exact confirmed source OID, so a later ref movement cannot redirect the merge. Git owns hooks, strategy execution, rerere, autostash, index/ref locks, merge messages, commits, conflict stages, and rollback behavior. Exit status plus bounded `ls-files --unmerged -z` and `MERGE_HEAD` queries classify completed, stopped-before-commit, squash-prepared, and conflict outcomes without parsing localized prose. A conflict is a successful transition into the ordinary editable conflict workspace, not false command success; other nonzero exits remain failures and trigger a full reconciliation scan.

When execution leaves a merge, squash, or conflict transaction for review, the already-open workspace reloads Git's generated message through the same saved-message-first precedence used at startup. It replaces only an untouched lower-precedence empty, template, or amend message; an edited or restored saved message is never overwritten. The pending transaction disables amend, carries exact `MERGE_HEAD` presence as typed state, and moves directly into the ordinary commit or conflict workflow without reopening the application.

An active merge-abort warning is bound to the displayed HEAD object, exact symbolic attachment, complete index fingerprint, SHA-256 of Git's complete binary worktree diff, every ordered object ID in the verified `MERGE_HEAD` path, and the optional exact `MERGE_AUTOSTASH` object that Git reports and will apply. The cancel-first dialog shows the full current, incoming, and autostash object IDs and passes that exact snapshot to the transaction boundary. After acquiring the `Abort` mutation lease, GitSail brackets bounded `MERGE_HEAD` reads, `MERGE_AUTOSTASH` ref queries, and worktree-diff captures with live precondition captures, rejects any staged or unstaged stale confirmation, and invokes only `git merge --abort`; it never reconstructs the pre-merge tree, applies a stash itself, or deletes merge-state files. A generation-checked rescan then restores the ordinary workspace or reports Git's actionable failure without pretending the abort succeeded.

The comparison workspace fulfills side-by-side diff and chunk merge requirements:

- `diff` mode accepts worktree/index/commit pairs and pathspecs, shows synchronized two-pane scrolling, optional unified view, file tree, hunk navigation, intraline highlighting, and copy/export;
- `merge` mode discovers unmerged paths through the index, loads stages 1/2/3 as raw buffers, presents base/ours/theirs/result, and supports accept-left/right/base/both per conflict or hunk;
- the result buffer is user-owned state with undo/redo and encoding/line-ending preservation; saving uses a no-follow atomic replacement followed by explicit staging; and
- binary, too-large, filter-produced, or undecodable files present metadata and route to a user-approved external mergetool rather than corrupting data.

### 9.6 Remotes and transport

Remote add/remove, fetch, fetch-all, prune, push, tag push, branch deletion, and remote initialization are fully asynchronous, cancellable, and backed by console panels with separate stdout/stderr rendering. Push always previews the exact source OID/ref, destination remote URL with secrets removed, destination ref, upstream relationship, expected remote OID, and commit count; ambiguous same-tail remote branches are highlighted and never auto-selected.

Force push defaults to an explicit lease, `--force-with-lease=<destination-ref>:<expected-remote-oid>`, captured immediately before confirmation. A background fetch cannot silently change that expected value. Plain `--force` is available only through a second destructive confirmation.

Remote initialization over SSH uses a fixed POSIX `sh -s` program sent on stdin and a separately framed base64url path payload containing only validated alphabet characters. User data is never interpolated into shell syntax. A capability probe verifies POSIX shell and decoder support; unsupported servers get an actionable manual command rather than an unsafe fallback.

A local-path remote is initialized by launching a new Git operation with its own canonical `--git-dir` and no inherited worktree variables. Windows drive/UNC paths and `file://` URLs use typed platform parsing. The local and SSH code paths are distinct and have environment-isolation regression tests.

### 9.7 Stash

Stash list uses `git log -g -z --format=%H%x00%gD%x00%gs%x00%ct%x00 refs/stash`. Every record therefore has four NUL-terminated fields followed by the `-z` NUL record boundary. The bounded parser requires the full canonical `refs/stash@{N}` selector, contiguous selector order, a SHA-1 or SHA-256 object ID, exact reflog-subject bytes, a valid Unix timestamp, and the second NUL; it never depends on locale-rendered `git log` text. A missing `refs/stash` is an empty catalog, not an error. Each reflog read is bracketed by exact `refs/stash` reads and proves that the first entry equals the ref. Full catalog capture compares complete exact entries both before and after worktree fingerprinting, brackets the entire operation with HEAD/index preconditions, and retries a concurrent ref disappearance instead of surfacing Git's transient log failure.

The searchable, resizable stash workspace is available from the header and F3, remains complete at 80×24, and supports pointer focus, activation, selection, scrolling, editor selection, and window resizing. Each row shows the generated selector, local time, full-identity prefix, and control-safe subject. Details show the full selected OID, selector, time, and subject. The lower pane uses the built-in read-only editor behavior with line numbers, diff decoration, horizontal/vertical scrolling, mouse selection, and a bounded presentation prefix while retaining exact spooled patch bytes. Incremental filtering covers selector, complete OID, time, and subject. Focus follows the same OID when a new entry shifts reflog positions, but mutation identity always includes both the currently displayed position and OID.

Create is a noninteractive typed `stash push` transaction with an optional message and mutually valid tracked-only, include-untracked, include-ignored, keep-index, and staged-only choices. The dialog explains the selected semantics and current staged, unstaged, and untracked counts. No UI text becomes command syntax, and a leading-hyphen message remains an option value. Git owns snapshot construction, worktree restoration, object creation, ref updates, filters, and failure behavior.

Show addresses the exact selected OID and requests a binary, full-index, no-color patch including saved untracked content with external diff and text conversion disabled. Apply also addresses the exact OID. Pop and drop use only the generated canonical selector after proving that the complete ordered reflog still matches and that the selector still names the displayed OID. Preview and drop perform this reflog-only revalidation without scanning an unrelated worktree. Immediately before apply or pop, GitSail revalidates the selected catalog, symbolic HEAD attachment, HEAD OID, complete index stream, SHA-256 of Git's complete binary tracked-worktree diff, and byte-identical porcelain-v2 path occupancy with all untracked paths and matching ignored entries. It deliberately does not read or hash unrelated untracked/ignored file contents or recursively enumerate ignored cache contents: Git does not overwrite an occupied untracked path during stash application and remains the final authority for an ignored-path collision, while hashing or enumerating ignored build caches would impose unbounded irrelevant I/O. Create consumes the live bytes at the instant the user invokes it because no earlier destructive confirmation claims an older worktree snapshot.

Apply offers explicit index restoration and retains the stash. Pop is cancel-first, offers the same index choice, and explains that Git retains the entry when application conflicts or otherwise fails. Drop is cancel-first, shows the complete OID and unrecoverability warning, and never applies worktree content. No failure path claims that a pop removed the entry; status is rescanned so conflicts remain available for the ordinary resolution workspace. Create, show, apply, pop, and drop preserve Git-owned working state and surface exact stdout/stderr without home-grown cleanup.

### 9.8 Blame and tree browser

Blame consumes `git blame --incremental` as raw records, supports worktree contents, ranges, copy/move detection, parent navigation, history context, search, goto line, commit details, path copy, and encoding-aware display. File content bytes and Git metadata remain separate.

The tree browser uses NUL-delimited `ls-tree`, supports arbitrary raw names, lazy expansion, blobs/submodules/symlinks/trees, revision switching, search, open, blame, history, and export through Git. It does not materialize hostile paths beneath an arbitrary destination without containment checks.

### 9.9 History and sequencer workflows

History is native to the TUI rather than delegated to gitk. It obtains structured commit records and parent OIDs from Git, builds a bounded lane graph, virtualizes long histories, shows refs and signatures, filters by path/author/text, and opens commit diffs. Cherry-pick and revert use Git and expose Continue, Skip, and Abort when the sequencer stops.

Interactive rebase uses Git itself. Git launches the current executable as `GIT_SEQUENCE_EDITOR`; an authenticated helper transfers the todo file to the running app. The user edits typed todo commands with validation, reorders commits, chooses pick/reword/edit/squash/fixup/drop/exec, then returns the exact requested file to Git. Rebase progress exposes Continue, Skip, Edit Todo where Git permits, and Abort. The helper also works without a parent UI by opening a minimal terminal editor.

### 9.10 Repository management

The chooser supports open, recent, clone, initialize, initialize bare, and open existing worktree. Clone modes cover standard local optimization, full copy, shared clone with a prominent corruption warning, and recursive submodules. Target paths are canonicalized before creation; partial failures offer cleanup only for the exact newly created directory after identity verification.

The linked-worktree window lists Git's complete worktree records and supports create, open, move, lock, unlock, remove, repair, and prune from the keyboard or mouse. Creation covers existing branches, new branches, detached HEAD, direct remote tracking, and atomic creation with an optional lock reason. Before removal, the confirmation shows tracked, untracked, ignored, and submodule state and explains that forcing the operation deletes the selected worktree directory. Prune first shows Git's dry-run result and runs the dry-run again immediately before the confirmed prune.

Maintenance uses Git's maintenance/gc/count/fsck commands with streamed progress and no home-grown object parsing. The repository-management menu links to an embedded Installation and Invocation page that can copy the global install, update, uninstall, and completion commands and can run the same shim/PATH checks as `doctor`. This is the explicit terminal-medium equivalent of desktop-shortcut behavior. GitSail neither installs nor generates desktop shortcuts, application bundles, or operating-system launchers.

### 9.11 Tools, textconv, mergetools, SSH, spellcheck, and openers

User-defined tools support add, edit, remove, selection/path/revision variables, confirmation, console/no-console, and rescan semantics. Tool, textconv, and mergetool execution is governed by the capability grants in §11.4.

The spell checker resolves only a trusted executable and uses a bounded, cancellable pipe protocol. Protocol/version failure disables spelling with an explanation. SSH-key creation defaults to Ed25519, exposes deliberate alternatives, never overwrites an existing key without confirmation, and treats private-key material as secret.

Browser behavior uses `git web--browse` and Git's browser configuration. Editor behavior uses `git var GIT_EDITOR`. Platform openers are resolved through a fixed allowlist and never through repository configuration.

### 9.12 Options and configuration UI

Options distinguish repository, worktree, global, and inherited values. They cover identity, merge behavior, diff context/options/colors, textconv policy, trust-mtime, fetch pruning, tracking defaults, untracked display/staging, recent repositories, encoding, commit width/wrap/cleanup/template/signing, spelling, detached-commit warnings, themes, accessibility, keymap, clipboard, automatic refresh, rename detection, and safe-force policy.

Invalid values display their source and do not get silently normalized. Saving shows the exact scope and keys changed. Reset removes only the selected explicit value, revealing inheritance. Configuration writes are serialized with repository operations and use Git.

### 9.13 Citool

Citool retains single-commit behavior, amend/no-commit/message options, success/failure exit semantics, and restricted actions. It shares the same commit transaction, security model, draft handling, and tests as the main workspace. Closing before success returns failure and leaves a recoverable draft when content changed.

### 9.14 Help and command palette

F1 opens context-sensitive help with live bindings, terminal capability status, and links to offline topics. F2 opens a searchable command palette containing every visible and hidden action, its availability reason, and current binding. This makes every action keyboard-accessible even on a baseline terminal. Offline-document links are constructed with `Uri`/platform path APIs as valid `file://` URLs and are tested with spaces, Unicode, UNC paths, and reserved URI characters.

The palette uses stable action identifiers and one live registry for labels, categories, descriptions, bindings, predicates, reasons, and executors. Filtering covers every presented field, retains unavailable commands instead of hiding them, and supports typed-submit, list Enter, and pointer activation. Help and the palette remain available while a repository operation is busy and in the below-minimum resize view. F8 opens branches/worktrees and F9 opens stashes as optional direct baseline shortcuts; neither displaces F1 or F2. The in-TUI Doctor view reports the same build, Native AOT, runtime, Git, and repository facts needed during an interactive session, while `git tui doctor --json` remains the stable automation surface.

Help documents destructive operations, configuration precedence, repository trust, executable configuration, Native AOT diagnostics, symbol collection, raw-path display, and terminal limitations. The Help menu also provides About, Doctor, logs, SSH-key assistance, and online documentation.

## 10. Input, layout, rendering, and accessibility

### 10.1 Baseline keymap

The baseline profile assumes only ordinary VT key sequences. It never assigns distinct actions to byte sequences a baseline terminal cannot distinguish. Bindings are contextual so printable file-list shortcuts do not steal text from an editor.

| Context | Action | Baseline binding |
|---|---|---|
| Global | Help | F1 |
| Global | Command palette | F2 |
| Global | Branches and linked worktrees | F8; command palette |
| Global | Stashes and exact patches | F9; command palette |
| Global | Commit current transaction | F4 |
| Global | Rescan | F5; Ctrl+R alternate |
| Global | Cycle panes | F6 |
| Global | Find | F7 |
| Global | Open menu | F10 |
| Global | Close tab/window | Ctrl+W |
| Global | Quit | Ctrl+Q; command palette |
| File/diff | Stage focused/selected item or hunk | `s`; Space on a file row |
| File/diff | Unstage focused/selected item or hunk | `u` |
| File/diff | Revert focused item or hunk | `x` followed by confirmation |
| File list | Stage all | `a` |
| File list | Unstage all | `U` |
| File/diff | Less/more context | `[` / `]` |
| File/diff/history | Next/previous match | `n` / `N` |
| Diff/history | Next/previous hunk or commit | `j` / `k`; arrows |
| Menu/dialog | Activate/cancel | Enter / Escape |

Branch, merge, push, rebase, stash, and uncommon actions are always available through F10 and F2. Enhanced terminals may add `Ctrl+Enter`, modified function keys, and familiar compatibility chords only after negotiated input reporting proves they are distinguishable.

`Ctrl+I`, `Ctrl+Shift+I`, `Ctrl+M`, `Ctrl+[`, and unnegotiated `Ctrl+Enter` are never distinct baseline bindings. A generated byte-sequence test proves that every active binding maps to at most one action. User remapping rejects collisions in the current context and explains terminal-equivalent sequences.

### 10.2 Menus and pointer input

The complete menu model is data-driven and feeds MenuBar, command palette, F1 help, and tests. Each entry has one `ActionId`, availability predicate, destructive classification, and context. F10 is universal; Alt-letter accelerators and the Menu key are optional additions when reported. A menu may be pinned as a non-modal TUI window; pinned menu identity/position is versioned and restored across sessions, and `gitsail.restorePinnedMenus=false` disables restoration. This provides the useful persistent-menu behavior without copying Tk tear-off implementation.

Keyboard and mouse are equally supported first-class input methods; neither is a partial compatibility mode. Every visible action that can sensibly be pointed at has an accurate hit target, hover and focus feedback, and an honest busy or unavailable state. Mouse support includes click, double-click, wheel, horizontal wheel where reported, scrollbar interaction, splitter drag, text selection, context menus, Ctrl-click toggle, Shift-click range, and modifier-preserving drag. Every pointer action also has a keyboard equivalent. Pointer actions, hit-target boundaries, drag cancellation, capture loss, double-click timing, wheel routing, and modifiers are tested in headless and real-terminal adapters on every supported operating-system family.

### 10.3 Responsive layout

Layout breakpoints are behavioral requirements, not screenshots:

- 120×30 and wider: dual file columns and content/editor split;
- 80×24 through 119×29: stacked file lists with resizable content/editor;
- 60×18 through 79×23: tabbed panes, compact status, full command palette;
- below 60×18: resize screen without starting or abandoning mutations.

Every pane supports focus indication independent of color, horizontal navigation for unbreakable content, safe truncation by grapheme/cell width, and a detail view for omitted text. Resize during an operation preserves selection, editor state, scroll anchors, dialogs, and progress.

### 10.4 Color, Unicode, and hostile text

Themes have monochrome, 16-color, 256-color, and truecolor variants. `NO_COLOR` selects monochrome unless the user explicitly overrides it. High-contrast and color-vision-deficiency presets are included. Status is always communicated by text/glyph as well as color.

Unicode rendering uses the TUI framework's public cell-width behavior and isolates untrusted bidirectional text. Controls, ESC, OSC, C1 characters, invalid sequences, and bidi controls are displayed as visible tokens or escaped glyphs in all repository-derived content, including paths, refs, authors, messages, tool output, hook output, remote output, prompts, and logs. Sanitization happens at render sinks; retained raw data is unchanged.

ASCII mode has complete replacements for borders, badges, ellipses, progress, conflict markers, and key descriptions. CJK, combining marks, emoji variation selectors, RTL text, and malformed input appear in snapshot and terminal tests.

### 10.5 Accessibility

Required accessibility behavior includes:

- full keyboard operation and command palette coverage;
- focus and selection indicators that survive monochrome output;
- no animation required to understand state and a reduced-motion preference;
- configurable high contrast and color-blind-safe palettes;
- copyable dialog/error bodies and stable text labels for automation;
- no information conveyed solely through cursor shape, color, hover, or sound;
- predictable Tab/Shift+Tab focus order and Escape hierarchy; and
- accessible names/descriptions in the public semantic snapshot model for future presentation adapters.

The console TUI makes no unsupported claim of compatibility with every screen reader. The manual documents tested terminal/screen-reader combinations and the line-oriented `--trace`, `doctor --json`, and non-interactive help alternatives.

### 10.6 Clipboard

Clipboard preference is `off`, `auto`, `osc52`, or an explicit platform helper. Sensitive values—private keys, askpass responses, credential-bearing URLs, and redacted log fields—are never copied through a generic action. Large copies require confirmation and have a configurable byte limit. Failure always leaves a selectable popup so the user can copy manually.

## 11. Security design

### 11.1 Trust zones

The design distinguishes:

1. application-authored constants and assets verified by the NuGet package content manifest and recorded package hash;
2. user-global configuration;
3. repository/worktree content and repository-local configuration;
4. Git/SSH/hook/tool subprocess output;
5. executable configuration (`textconv`, external diff, filters, hooks, guitools, mergetools, editor, browser, SSH commands); and
6. secrets handled by credential/askpass paths.

Repository data and child output are hostile. User-global executable configuration is not assumed safe merely because it is global; it is shown in capability review and may be revoked.

### 11.2 Enforced invariants

| ID | Invariant |
|---|---|
| SEC-01 | Only `IChildProcessRunner` may spawn a process. A banned-API analyzer and binary call-graph test enforce this. |
| SEC-02 | Ordinary invocations never use a shell and never concatenate a command string. |
| SEC-03 | Native paths, revisions, refs, refspecs, options, and display text are distinct types. A display value cannot become an execution value. |
| SEC-04 | Unix paths retain raw bytes from Git through the filesystem/process boundary. |
| SEC-05 | Every shell-capable feature goes through `ExecutableConfigurationBroker`; there is no unnamed shell exception. |
| SEC-06 | Repository trust and executable-capability grants are independent decisions. |
| SEC-07 | Untrusted terminal text is sanitized at every render/log sink, including embedded child terminals. |
| SEC-08 | Direct repository file access is no-follow, identity-checked, allowlisted, permission-restricted, and atomic. |
| SEC-09 | IPC endpoints authenticate the parent/session, restrict access to the current user, frame messages, limit size, time out, and fail closed. |
| SEC-10 | Secrets never enter command traces, terminal recordings, crash reports, clipboard history, telemetry, or exception messages. |
| SEC-11 | Destructive actions capture expected OIDs/file identities and default to Cancel. |
| SEC-12 | The .NET tool package graph is reproducible, hash-verified, dependency-inventoried, and scanned before publication. |

### 11.3 Filesystem containment

Canonicalization alone is insufficient. `SecureFileSystem` uses handle-relative/no-follow operations where the OS provides them, rejects symlink/junction/reparse traversal at every component for protected writes, validates device/file identity after opening, uses same-directory atomic replacement, and applies user-only permissions to secret or recovery files. Windows tests cover UNC, extended-length paths, alternate data streams, junctions, and reparse points. Unix tests cover symlink swaps and rename races.

Deletion accepts an opened file identity plus expected parent identity, never an unresolved environment variable, glob, or display string. Recursive deletion is used only for a newly created clone target recorded by identity, after a separate confirmation.

### 11.4 Executable configuration broker

`ExecutableConfigurationBroker` covers all of these cases: hooks, textconv, external diff, clean/smudge/process filters, guitools, mergetools, editor, browser, credential helper, `core.sshCommand`, remote commands, and interactive-rebase `exec` lines.

Capabilities are granted per canonical repository identity and command hash. Before the first automatic execution, the UI shows source scope, exact command, executable resolution, working directory, data exposed, and whether a shell is involved. Choices are Deny, Allow Once, or Allow for This Repository. Grants live in user configuration, never repository configuration, and are invalidated when the command changes.

Passive repository opening never executes textconv, external diff, filters beyond those Git necessarily uses for a user-requested mutation, or tools without a grant. Diff defaults to `--no-ext-diff --no-textconv`; the user can enable a granted driver for a selected file. Hunk staging stays disabled for transformed output.

Hooks are expected executable code for a commit/checkout/merge action. The first mutating action in an untrusted repository presents a consolidated hook capability review. Bypass behavior is explicit and does not redefine Git's semantics.

For arbitrary configured shell commands, the broker invokes one fixed platform shell with the command as a single opaque script argument and supplies Git-defined variables through the environment. It never attempts unsafe placeholder substitution into a fabricated argv array.

### 11.5 Remote shell

SSH itself is an executable-capability boundary. The only application-generated remote program is the fixed initialization script described in §9.6. Remote host and user are parsed as structured SSH destination values; options are allowlisted; repository data cannot become SSH options. Host-key and credential prompts flow through the authenticated prompt system.

### 11.6 Terminal and log safety

The ordinary console panel parses carriage-return progress but renders all other child control bytes visibly unless the user explicitly opens an isolated embedded terminal for an approved interactive tool. The embedded terminal has a policy denying clipboard OSC, window-title OSC, file transfer, graphics, hyperlinks with unsafe schemes, and host queries by default.

Structured logs use named fields and classification (`Public`, `RepositoryData`, `Path`, `Secret`). Redaction occurs before serialization. Remote URLs remove userinfo and credential query values. Environment dumps are allowlist-only. `--record` pauses and clears prompt regions during secret entry and emits a redacted marker.

### 11.7 Askpass, yes/no, editor, and sequence helpers

Git and OpenSSH treat `GIT_ASKPASS` and `SSH_ASKPASS` as literal executable paths. GitSail therefore supplies the trusted canonical `Environment.ProcessPath` directly, without quoting, command parsing, a mode argument, or repository/user data. Private variables select helper kind, endpoint, session ID, one-time nonce, protocol version, and parent process identity. Tests install GitSail under legal platform-specific paths containing spaces, Unicode, shell metacharacters, and quote characters where the platform permits them, then prove literal helper launch without injection.

The helper validates all fields, connects to a user-only named pipe/Unix socket, performs nonce challenge-response, sends a length-prefixed request under strict limits, and waits with timeout/cancellation. Multiple prompts are queued and labeled by operation. Responses are written only to the required stdout/file and immediately cleared from managed buffers where practical.

If no authenticated parent is available, askpass opens `/dev/tty` or `CONIN$` directly, disables echo for secrets, restores terminal state in `finally`, and returns failure when no controlling terminal exists. It never falls back to ordinary stdin. The yes/no helper uses the same path. Editor helpers open a minimal full-screen editor only when a terminal exists; otherwise they return failure without modifying the file.

### 11.8 Supply chain and runtime hardening

- Dependency restore uses centrally pinned versions and validates NuGet content hashes; GitSail's own packages do not require a signing certificate.
- Native toolchains, sysroots, actions, .NET/NuGet tooling, and build containers are digest-pinned.
- Dependencies have license and vulnerability review; vendored code is prohibited without a recorded exception.
- Windows enables Control Flow Guard and CET where supported. All targets retain NX/DEP, ASLR/PIE, stack protection, and hardened linker defaults.
- Native executables use the compiler and linker hardening available for their RID, but receive no platform application signing, entitlements, or notarization. Installed tool files are user-writable only according to the .NET tool store's normal permissions.
- GitSail never self-updates or self-replaces. The UI reports that a newer version exists only after an opt-in, privacy-disclosed metadata check; users update or select an older version with `dotnet tool update` and an explicit package version.

## 12. Internationalization

### 12.1 Catalog format and generation

English source messages use stable semantic IDs, not English strings as keys. The source format supports named arguments, plural/select forms, translator comments, accelerator annotations, and terminal-width notes. A build-time source generator validates and emits strongly typed C# methods; runtime lookup uses generated tables with no reflection or dynamic resource probing.

Example:

```text
operation.files.completed = { $operation }: { $completed } of { $total } files ({ $percent }%)
```

Named arguments eliminate positional/dynamic-width printf conversion entirely. Alignment is a rendering concern calculated after localization; catalogs never contain `%*i`, `%N$s`, or .NET composite-format indices.

The generator rejects:

- missing or extra arguments;
- type mismatches;
- invalid plural categories;
- duplicate IDs;
- control/escape characters;
- accelerator collisions within a menu;
- text that exceeds a declared hard terminal constraint without a wrapping policy; and
- catalogs not explicitly licensed MIT by their contributor metadata.

### 12.2 Locale coverage

Version 1.0 ships translations for English plus `bg`, `de`, `el`, `fr`, `hu`, `it`, `ja`, `nb`, `pt-BR`, `pt-PT`, `ru`, `sv`, `vi`, and `zh-CN`. Culture normalization maps POSIX names such as `pt_BR` and `zh_CN` to BCP 47, then falls back through specific culture, neutral culture, and English per message.

Translation work is complete only when every required ID is translated, reviewed by a second contributor or professional reviewer, licensed under MIT, and passes pseudo-localization plus real-layout tests. “Fallback to English” is a runtime resilience mechanism, not a release-completeness substitute.

### 12.3 Content rules

All UI prose is newly written. Git command names, config keys, ref names, paths, and verbatim Git output remain technically exact and are visually distinguished from translated explanation. Error messages lead with the failed user action, then Git's sanitized diagnostic, then recovery choices.

RTL catalogs and hostile bidi repository text are isolated independently. Translators do not place terminal control codes in messages. Width, wrapping, mnemonic, and plural behavior is tested at 60×18, 80×24, and 120×30.

## 13. Native AOT engineering

### 13.1 Project settings

The shipped project uses these release defaults:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <OutputType>Exe</OutputType>
  <AssemblyName>git-tui</AssemblyName>
  <RootNamespace>GitSail</RootNamespace>
  <PackageId>GitSail</PackageId>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <PackageReadmeFile>README.md</PackageReadmeFile>
  <PackageTags>git;tui;terminal;native-aot</PackageTags>
  <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>git-tui</ToolCommandName>
  <RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;linux-musl-x64;linux-musl-arm64;osx-x64;osx-arm64</RuntimeIdentifiers>
  <ToolPackageRuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;linux-musl-x64;linux-musl-arm64;osx-x64;osx-arm64</ToolPackageRuntimeIdentifiers>
  <CreateRidSpecificToolPackages>true</CreateRidSpecificToolPackages>
  <PublishAot>true</PublishAot>
  <SelfContained>true</SelfContained>
  <IsAotCompatible>true</IsAotCompatible>
  <VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
  <EnableAotAnalyzer>true</EnableAotAnalyzer>
  <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
  <InvariantGlobalization>false</InvariantGlobalization>
  <OptimizationPreference>Size</OptimizationPreference>
  <StackTraceSupport>true</StackTraceSupport>
  <StackTraceLineNumberSupport>true</StackTraceLineNumberSupport>
  <EventSourceSupport>true</EventSourceSupport>
  <MetricsSupport>true</MetricsSupport>
  <UseSystemResourceKeys>false</UseSystemResourceKeys>
  <StripSymbols>true</StripSymbols>
  <DebugType>portable</DebugType>
  <DebugSymbols>true</DebugSymbols>
  <Deterministic>true</Deterministic>
  <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
</PropertyGroup>

<PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
  <ControlFlowGuard>Guard</ControlFlowGuard>
  <CETCompat>true</CETCompat>
</PropertyGroup>

<ItemGroup>
  <None Include="../../README.md" Pack="true" PackagePath="/" />
</ItemGroup>
```

`OptimizationPreference=Size` is the release choice because reduced mapped code improves distribution and startup for this interactive client. CI still records the default and Speed variants as benchmark evidence, but releases do not leave the setting undecided.

### 13.2 Analyzer and feature policy

The build fails on IL2026, IL2057–IL2099, IL3050, IL3053, IL3058, single-file warnings, and ordinary compiler/analyzer warnings. No `UnconditionalSuppressMessage` is allowed in 1.0. If a dependency cannot satisfy the metadata and warning contract, it is upgraded, isolated outside the shipped closure, replaced, or removed.

Generated JSON contexts cover every serialized type. Generated regex is used only where bounded non-backtracking parsing is appropriate. Parsers for Git data use spans/state machines rather than regex when record structure is byte-oriented. Any COM required by a Windows terminal or clipboard implementation uses source-generated interfaces; there is no shortcut adapter.

Runtime startup for `--version`, `--help`, helper modes, and `doctor --json` does not initialize the TUI stack, repository services, localization catalogs beyond the selected message, EventPipe providers beyond runtime defaults, or ICU-dependent formatting unless required.

### 13.3 Globalization

Invariant globalization is prohibited because culture-aware messages, casing, and formatting are product requirements. Windows and macOS use supported system globalization facilities. Linux tool-package metadata and installation documentation declare the required system ICU library; no app-local ICU files are shipped. Clean-machine tool tests run every locale on every libc family and require an actionable startup error if ICU is absent.

### 13.4 Native interop

GitSail-authored interop is explicit per RID. Dynamic imports are inspected in CI, and loader search paths never include the working directory. Native handles use `SafeHandle`; callbacks have rooted lifetimes; errno/Win32 errors are captured immediately; struct layouts have architecture tests. Dependency-owned native assets are consumed unchanged and validated under §6.4, not relinked into GitSail.

### 13.5 Symbols and crash diagnosis

Each stripped executable has a corresponding PDB, `.dbg`, or dSYM artifact, build ID/UUID, source revision, compiler version, and source-link record. Symbols are retained for the complete .NET 10 support lifetime plus one year. Release documentation gives exact WinDbg, LLDB, GDB, and `addr2line` commands. `dotnet-symbol` is not described as a native stack decoder.

EventPipe remains enabled so `dotnet-trace`, `dotnet-counters`, and compatible diagnostic clients can attach where Native AOT supports them. The size cost is included in the fixed budget rather than removed from production diagnostics.

### 13.6 Runtime servicing

Because Native AOT embeds runtime code, every .NET security or critical servicing release triggers dependency review and republish of all eight RIDs within seven calendar days; actively exploited issues trigger the emergency release process within 48 hours. Release metadata records the exact runtime commit/package versions so affected artifacts can be queried.

## 14. Build, .NET tool packaging, and release

### 14.1 Reproducible inputs

- `global.json` pins the exact SDK and disables roll-forward.
- NuGet versions are centrally pinned to exact versions.
- the official TUI-framework NuGet package and every direct package dependency use exact centrally pinned versions; project references, source submodules, vendored packages, and floating versions are prohibited;
- native compilers, linkers, SDKs, sysroots, container images, and build actions are digest-pinned;
- `SOURCE_DATE_EPOCH`, deterministic paths, repository commit, and locale/timezone are fixed; and
- build tools run from a locked local tool manifest.

Two separate builders produce each native payload and NuGet package. CI normalizes documented build-ID and NuGet-container fields, compares payload bytes and package content manifests, and requires byte-for-byte equality wherever the pinned toolchain supports it. Any unexplained difference blocks release.

### 14.2 Native build matrix

Each RID has native build, execution, `dotnet pack -r <RID>`, and clean-install smoke-test lanes. Linux glibc uses a pinned 2.27 sysroot and verifies on RHEL 8 plus a current distribution. Musl uses native Alpine x64/Arm64. macOS uses native Intel and Apple Silicon runners with deployment target 14. Windows uses native x64 and Arm64 runners. No cross-OS or emulator result satisfies an execution gate.

### 14.3 Tool package graph and composition

Every version consists of exactly nine NuGet packages with one version number:

1. `GitSail.<version>.nupkg`, the SDK-generated top-level package of type `DotnetTool`, containing the supported-RID map; and
2. `GitSail.<rid>.<version>.nupkg` for each of the eight RIDs, produced by `dotnet pack -r <RID>` on an OS matching that RID's operating-system family.

There is no `any` package and no framework-dependent fallback: an unsupported RID fails installation with the supported RID list rather than silently running a non-AOT build. Each RID package contains the Native AOT `git-tui` entry point, NuGet metadata, the root MIT license, and only the immutable dependency-owned runtime assets proven necessary for that RID. Offline help and completion generation are embedded in the entry point. Debug symbols, SBOMs, test reports, and native-import reports are retained with the package version, not offered as alternate application downloads.

### 14.4 Installation, update, and publication

NuGet.org is the sole public distribution feed. Supported user flows are:

```text
dotnet tool install --global GitSail
git tui
dotnet tool update --global GitSail
dotnet tool uninstall --global GitSail

# Repository-pinned local tool
dotnet tool install GitSail
dotnet tool run git-tui
```

The .NET 10 CLI infers the host RID and selects the matching package; users never choose or download an executable manually. A custom `--tool-path` install is supported when that directory is placed on `PATH`. The application detects a missing global-tools PATH entry and prints exact platform-appropriate instructions without editing the user's environment.

All eight RID packages are published and installed from a private staging feed first. CI then publishes the eight identically versioned RID packages to NuGet.org, verifies that each is queryable, and publishes the top-level pointer package last. A partial publication is never repaired by overwriting immutable packages; it is unlisted where possible and replaced by a new version. Clean machines test global install, direct `git-tui`, `git tui`, embedded help, generated completions, exact-version update/downgrade, uninstall, local-manifest install/restore/run, permissions, and preservation of documented user state.

No other package channel or application artifact is produced. In particular, the release has no macOS app, code signing, notarization, Homebrew package, WinGet package, Scoop package, Linux system package, portable zip/tar archive, or standalone executable download.

### 14.5 Package integrity

GitSail submits all nine `.nupkg` files without an author signature. The project has no code-signing certificate, paid signing service, signing identity, or signing-key ceremony. It also applies no Authenticode, Apple code signing, entitlements, notarization, or other platform signature to `git-tui`.

CI computes SHA-256 and SHA-512 hashes before upload, verifies them against the staged packages and package-content manifests, and records the NuGet.org package identity and version after publication. NuGet.org's normal upload validation and malware scanning remain feed services rather than project signing requirements. CI also emits CycloneDX and SPDX SBOMs, a dependency license report, vulnerability scan, compiler/linker invocation record, and package-content manifest. These reports are not packaged with the application.

## 15. Diagnostics and observability

### 15.1 User diagnostics

`git tui doctor` reports, in human-readable form or stable JSON:

- `GitSail` package/application/build/runtime version, selected tool-package RID, Native AOT status, resolved command path, and detectable global/local/tool-path scope;
- availability and version of the .NET SDK needed for tool update/uninstall operations, without treating it as a runtime dependency of the installed payload;
- Git resolution, version, and capability checks;
- terminal dimensions, color/input/mouse/Unicode/clipboard capabilities;
- locale/ICU availability;
- repository discovery and trust state without repository content;
- optional tool resolution;
- state/cache/log paths and permissions;
- loaded configuration sources with secret fields redacted; and
- symbol lookup instructions for the current build ID.

Doctor performs no mutation and no executable repository configuration.

### 15.2 Logging and tracing

Logs are structured JSON Lines under the platform user-state directory with rotation, size limits, user-only permissions, and a stable event schema. The in-app drawer presents a sanitized view. `--trace=<path>` writes the same schema to an explicit no-follow file; bare `--trace` chooses a timestamped state-directory path and prints it after the TUI exits.

Operation IDs connect UI actions, Git invocations, prompts, progress, cancellation, and results. Commands are logged as typed fields with secret/path classification, never reconstructed shell text. Raw repository content, patches, messages, environment blocks, and prompt responses are excluded by default.

### 15.3 Crashes and hangs

Unhandled exceptions restore terminal modes first, then write a minimal report under the user state directory containing build ID, sanitized exception, active operation IDs, terminal capability summary, and recent public log events. Repository paths are hashed unless the user explicitly opts in. Reports are never automatically uploaded.

The app has a watchdog command that can dump owned task/process state without repository content. Native dump collection is documented per OS and is opt-in because dumps may contain secrets.

## 16. Verification strategy

### 16.1 Test layers

| Layer | Required coverage |
|---|---|
| Unit | Value types, byte parsers, patch transforms, state transitions, key decoding, localization generator, config precedence, redaction, path containment |
| Service | Real isolated Git repositories across object formats and Git versions; every mutating workflow and failure path |
| UI | Full-stack TUI semantic and visual snapshots at all breakpoints, color tiers, locales, capability profiles, and focus states |
| Compatibility | Black-box reference interactions compared by semantic Git argv/env/stdin, exit class, and repository OIDs/state—not copied prose or screenshots |
| Security | Malicious paths/refs/messages/config/output, executable grants, IPC attacks, traversal/races, prompt secrecy, dependency/artifact inspection |
| AOT | Publish and execute the actual stripped Native AOT artifact; analyzer closure and metadata checks |
| Tool package | Clean global/local install, restore, update, exact-version downgrade, uninstall, RID selection, content-hash/package-manifest, dependency, embedded-manual, and completion-generation checks |
| Performance | Fixed corpus, hardware classes, cold/warm methodology, memory/CPU/I/O profiles, regressions |

All managed suites use MSTest on Microsoft.Testing.Platform under the mandatory contract in §16.2.

### 16.2 MSTest and Microsoft.Testing.Platform contract

MSTest 4.2.3 through `MSTest.Sdk` is the only managed test framework, and Microsoft.Testing.Platform (MTP) is the only test runner. VSTest, `Microsoft.NET.Test.Sdk`, separately referenced `MSTest.TestAdapter`/`MSTest.TestFramework`, xUnit, NUnit, and mixed-runner projects are prohibited. The SDK version advances only through the pinned-dependency update process.

The repository-level `global.json` fixes both the .NET SDK and runner selection:

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "disable",
    "allowPrerelease": false
  },
  "msbuild-sdks": {
    "MSTest.Sdk": "4.2.3"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

Every runnable managed test project uses this shape; `OutputType=Exe` is explicit even though `MSTest.Sdk` supplies the MTP entry point and test-application defaults:

```xml
<Project Sdk="MSTest.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TestingExtensionsProfile>Default</TestingExtensionsProfile>
    <UseVSTest>false</UseVSTest>
  </PropertyGroup>
</Project>
```

`MSTest.Sdk` owns compatible MSTest, MTP, analyzer, TRX, and code-coverage dependencies; test projects do not duplicate them. The `Default` extension profile is intentional and pinned with the SDK. Test utility projects that contain no tests set `IsTestApplication=false` instead of pretending to be runnable suites.

The normal local and CI entry point is `dotnet test`, which is MTP—not VSTest—because of `global.json`. Direct executable-style invocation remains supported for one-suite debugging:

```text
dotnet restore GitSail.slnx
dotnet build GitSail.slnx --configuration Release --no-restore
dotnet test --solution GitSail.slnx --configuration Release --no-build --no-restore --results-directory artifacts/test-results -- --report-trx --minimum-expected-tests 1

dotnet run --project tests/GitSail.UnitTests --configuration Release --no-build -- --filter "FullyQualifiedName~RawPath"
```

The coverage lane adds `--coverage --coverage-output-format cobertura`; it does not use VSTest-only `--collect`, `--logger`, or adapter arguments. CI archives deterministic TRX, coverage, standard output, failure attachments, and TUI visual captures. It runs each test executable with `--help`/`--info` in a configuration audit and fails if MTP, the expected extensions, or the resolved pinned versions are absent. A zero-test run is always failure.

Tests use `[TestClass]`, `[TestMethod]`, `[DataRow]`, `[TestInitialize]`, and `[TestCleanup]` as applicable and follow `MethodName_Scenario_ExpectedBehavior`. Async tests return `Task` and propagate `TestContext.Current.CancellationToken`. While MSTest 4.2.3 marks that API experimental, test projects centrally acknowledge only `MSTESTEXP` under requirement `TEST-MTP-01`; the suppression does not apply to product projects and is removed when the API becomes stable. `tests/Shared/MSTestSettings.cs` declares `[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`; `[DoNotParallelize]` is allowed only for a documented process-global or exclusive native-resource constraint. Collection equality, single-item, and checked-type assertions use `TestSeq` rather than reference equality or unchecked casts.

GitSail UI behavior is tested through the full public headless terminal stack with fixed dimensions, semantic/cell/color assertions, and `WaitUntil` after every state-changing input and immediately before capture. Tests never use `Task.Delay` as synchronization; they wait on observable state or `TaskCompletionSource`. The MTP test applications run on CoreCLR for diagnostics and coverage. `GitSail.AotTests` remains an MTP executable test project that launches and verifies the actual stripped Native AOT GitSail payload; its name does not mean the test runner itself is AOT-published.

### 16.3 Git matrix and isolation

Service tests run Git 2.36, the newest maintenance releases from each still-supported major compatibility band, and current stable. They cover SHA-1/SHA-256, bare/non-bare, linked worktrees, submodules, sparse checkout/index, fsmonitor, partial clone/promisor objects, Git LFS filters, alternates, long paths, case-sensitive/case-insensitive filesystems, symlinks, and network-like watcher behavior.

Each test receives fresh home/XDG/temp/config/credential/SSH/editor/browser/hook directories and a deterministic locale/timezone. Tests never invoke developer-global configuration and never share writable repositories.

### 16.4 Raw-path and parser corpus

Real filesystem tests use maximum legal component and path sizes for each platform rather than impossible 64 KB filenames. Synthetic parser tests cover records from zero bytes through configured multi-megabyte limits. Unix lanes create invalid UTF-8 and control-byte filenames. Windows lanes cover reserved-looking names where legal, Unicode normalization, UNC, long-path prefixes, junctions, and reparse points.

Coverage includes NUL framing splits at every byte, CR/LF boundaries, invalid encodings, huge lines, missing terminators, truncated records, duplicate fields, unknown status codes, SHA-1/SHA-256 lengths, and bounded failure behavior.

### 16.5 Behavior comparison tests

The test harness creates the same repository fixture twice, drives the reference application and `git-tui` through the same user actions, records their Git commands in an isolated test environment, and compares:

- action availability and resulting repository state;
- index/tree/HEAD/ref/object IDs;
- message and interoperability file bytes where compatibility requires them;
- Git command intent, inputs, environment classes, and exit handling; and
- user-visible outcome category, focus target, and recoverability.

Tests record intentional terminal-specific differences by name so an unexpected behavior change cannot pass unnoticed.

### 16.6 Fuzzing

Continuous fuzz targets cover status/config/refs/tree/blame/diff/progress parsers, raw patch selection, URL/refspec parsing, localization messages, terminal sanitizer, key decoder, helper IPC framing, log redaction, and secure-path resolution. Every crash, hang, excessive allocation, or nondeterministic result becomes a minimized regression input. Fuzzers have memory/time limits and run both managed and sanitizer-enabled native interop builds.

### 16.7 Failure and cancellation testing

Fault injection covers child hangs, ignored interrupts, output floods, partial writes, broken pipes, crash-before-exit, parent death, nested process trees, prompt timeout, disk full, read-only directories, permission changes, disappearing worktrees, index/ref races, watcher overflow, terminal disconnect, resize storms, absent ICU, missing tools, corrupt config, and unsupported Git capabilities.

After every injected failure, tests assert terminal restoration, no unobserved task, no live owned child, no leaked secret/temp file, released lease, accurate exit code, preserved repository invariants, and a usable recovery action.

### 16.8 Keyboard and terminal testing

A byte-level baseline test feeds every ASCII control and supported escape sequence and proves the active keymap is injective per context. Enhanced profiles run only after simulated successful negotiation. Tests explicitly prove Tab versus `Ctrl+I`, Enter versus `Ctrl+M`, Escape versus `Ctrl+[`, modified Enter, Caps Lock, Alt timing, bracketed paste, and mouse modifiers behave as documented.

The actual Native AOT executable is driven through real PTYs on all eight native RID lanes. Text snapshots and visual captures verify focus, color, borders, wide characters, dialogs, and resize behavior.

### 16.9 Localization and accessibility testing

CI requires 100% reviewed translation coverage for all required locales, named-argument compatibility, pseudo-localization expansion, RTL isolation, accelerator uniqueness, and layout snapshots at every breakpoint. Accessibility tests verify command-palette reachability for every action, logical focus order, monochrome distinguishability, reduced motion, copyable errors, and non-color status labels.

## 17. Traceability, issue closure, and performance

### 17.1 Traceability

This design and the automated tests describe the required behavior. CI checks behavior IDs, command builders, action reachability, configuration access, allowed state paths, locale coverage, and issue closure directly from code and tests. Appendices A–E summarize those checks without adding duplicate metadata files.

### 17.2 j6t issue set: 15 of 15

| Issues | Required resolution | Primary requirement/test area |
|---|---|---|
| 2 | Commit without hooks, explicit confirmation and audit | §9.3, executable-capability tests |
| 3 | File-list path/status filtering | §9.1, `UI-FILTER-*` |
| 4 | Validated responsive layout state; corrupt state cannot hide the UI | §10.3, `UI-LAYOUT-*` |
| 5 | Configurable diff colors across all capability tiers | §10.4, theme snapshots |
| 8 | Push is absent from the primary commit action region by default and can be shown only through an explicit preference | §9.1, accidental-activation tests |
| 10 | Comment character/string including `auto` | §9.3, Git differential tests |
| 13, 18 | Native light/dark/high-contrast themes with contrast gates | §10.4–10.5 |
| 21 | Revert errors propagate; no false-success state | §9.2, fault injection |
| 22 | Published-commit amend warning without repository deadlock | §9.3, remote-ref matrix |
| 24 | No subtree synchronization dependency; repository uses the MIT license | §2, license checks |
| 25 | Graceful interrupt and owned-child shutdown | §7.2, cancellation suite |
| 26 | Argument/operator injection impossible at process boundary | §7 and §11, malicious corpus |
| 27 | Spellcheck failure degrades visibly without disabling commit | §9.11 |
| 28 | Terminal-responsive scaling, width, and minimum-size behavior | §10.3–10.5 |

### 17.3 prati issue set: 72 of 72

Every listed issue is a 1.0 gate. “Support,” “Tk-only,” “wrong tracker,” or “historical” reports still receive a product documentation, robustness, or medium-equivalence test rather than disappearing.

| Issues | Resolution |
|---|---|
| 5 | Untracked-file hunk and line staging with typed intent-to-add |
| 6 | Add/edit/remove user tools |
| 7 | Optional hard wrap in the commit editor |
| 8, 92 | F1 live keyboard reference and F2 command discovery |
| 9, 17, 27, 42, 46 | Maintenance toggle, keyboard-dismissable/copyable dialogs, correct post-operation focus, and hook-failure focus/state |
| 10 | Per-hunk built-in conflict resolution |
| 11 | Caps-Lock-independent bindings and byte-level key tests |
| 12 | Clear internal API naming (`FilePaneFocusService`, staged/unstaged targets) and no ambiguous general-purpose focus helper |
| 13 | Pinnable menu windows with optional persisted restoration |
| 14 | Stage all and unstage all |
| 15 | Configurable, collision-validated action keymap |
| **16** | **Side-by-side diff and built-in chunk-by-chunk two/three-way merge; no deferral and no tkdiff code** |
| 18, 19 | Native history/commit inspection replaces dependency on a separate history GUI |
| 20 | Path/ref/revision completion in applicable fields |
| 23 | Clipboard persistence with honest OSC 52 result and utility fallback |
| 24 | Commit templates and draft preservation |
| 26, 41, 78 | Native AOT startup/install/crash robustness replaces Tcl/Tk-specific failure paths |
| 28 | Typed branch-reset process boundary and malformed/special-ref regression tests |
| 29 | Terminal cell scaling and responsive layout |
| 30, 44, 64, 86 | Theme and diff-color configuration |
| 31 | Bounded intraline diff highlighting |
| 32 | Git editor precedence and embedded editor/PTY workflow |
| 33 | Rename-aware status and diff with old/new raw paths |
| 34 | Full terminal restoration on PowerShell/Windows Terminal and every other supported shell |
| 35 | Amend preserves or explicitly discards a changed draft |
| 36 | Stderr warnings are not operation failure when Git exits zero |
| 37 | Complete fresh MIT translations and locale diagnostics |
| 39, 47, 48, 72 | Theme/display behavior, including a visible editor cursor and selection in every palette |
| 40 | Project manual, offline help, and completions |
| 43 | Commit author override with scoped environment |
| 45 | Published release cadence, support lifetime, changelog, servicing SLA, and visible roadmap |
| 50 | Remote branch namespace preservation plus explicit push destination/upstream/OID preview |
| 51, 65 | Git-compatible template stripping and commit cleanup |
| 52 | Local-path remote initialization with isolated `GIT_DIR`/`GIT_WORK_TREE` environment |
| 53 | Responsive commit editor receives remaining space |
| 55 | Hook stdin and lifecycle contract |
| 56 | Discoverable create-branch actions in menu, palette, and context |
| 57 | Generation-checked refresh after partial unstage |
| 58 | Config-source discovery in Doctor/options, including Explorer-launched Windows sessions and global-config overrides |
| 59 | Removed or empty reports remain test inputs; malformed empty inputs produce deterministic diagnostics and no mutation |
| 60 | Complete stash workflow |
| 61 | Correct RFC-compliant local `file://` documentation URLs through typed URI/path conversion |
| 62, 71 | Hooks and state paths work in linked worktrees |
| 63 | Complete `push.default` behavior and explicit destination preview |
| 68 | Incremental locale-correct console decoding with retained bytes |
| 70 | Bidi and invisible-character isolation/visibility |
| 73 | Push is not adjacent to Commit by default; an optional persistent push action is separately labeled |
| 75 | Child environment hygiene for submodule operations |
| 76 | Explicit terminal-medium equivalence: global .NET tool installation, `git tui` command discovery, and Installation/Invocation diagnostics; no desktop application or launcher |
| 80 | Repository maintenance hint is based on exact Git output |
| 81 | Chooser and clone paths use a safe current-directory prefill |
| 84 | No X11/XWayland dependency; native import and terminal-only integration tests |
| 93 | Prepare-commit-message file lifecycle matches Git hook expectations |
| 94 | Optional tools are found only through trusted executable resolution |
| 104 | External/textconv diff output cannot be staged as if it were raw content |
| 111 | About and help clearly identify GitSail, its package, and its commands |

The issue tables and their corresponding named tests are the reviewed mapping; no separate issue metadata file is required.

### 17.4 Performance budgets

Performance is measured on pinned x64 and Arm64 reference machines, with release Native AOT artifacts, a fixed repository corpus, CPU governor controls, and no debugger. Warm-cache and true cold-boot/page-cache runs are reported separately; a warm filesystem run is never labeled cold.

| Metric | 1.0 gate |
|---|---|
| `--version` warm P95 | ≤ 25 ms |
| First interactive frame, warm P95 | ≤ 100 ms |
| First interactive frame, controlled cold P95 | ≤ 250 ms |
| Stripped executable, x64 | ≤ 40 MiB |
| Idle working set after first frame | ≤ 70 MiB |
| 100,000-path repository working set | ≤ 140 MiB |
| 1,000,000-path repository working set | ≤ 250 MiB with spooling/virtualization |
| Idle input-to-frame P95 | ≤ 16 ms |
| Input-to-frame under active Git output P95 | ≤ 50 ms |
| Rescan orchestration overhead | ≤ 15% above the underlying Git commands' wall time |
| Cancel acknowledgement | ≤ 100 ms; graceful interrupt sent immediately |

A regression above 5% requires an approved benchmark record even when still below the absolute gate. Parsers, lists, history, and diffs have allocation profiles. No test constructs one million retained rich line objects; large data stays in compact records and spools.

## 18. Implementation sequence

Every milestone is required before 1.0:

| Milestone | Complete output |
|---|---|
| M0 — Build foundation | GitSail naming, MIT license, pinned SDK/dependencies/toolchains, and a reproducible nine-package tool skeleton with eight Native AOT payloads |
| M1 — Immutable dependency proof | locked official binary restore, public-API-only integration, eight-RID Native AOT smoke applications, exact native-asset inventory, package-content hashes, and zero writes or changes to the dependency |
| M2 — Secure Git substrate | typed process boundary, raw paths, bounded streams, environment, parsers, raw patch spool, secure filesystem, helper IPC, trust broker |
| M3 — Repository/commit core | discovery, snapshots/generations, status, diff, stage/unstage/revert, editor, commit/hooks/amend/citool, options |
| M4 — Branch and conflict | branch/checkout/reset, merge/abort/rerere, two-way diff, three-way chunk merge, mergetools |
| M5 — Transport and repository management | remotes/fetch/push/prune, askpass, stash, chooser/init/clone, maintenance, SSH, and tool-install/invocation diagnostics |
| M6 — Exploration and modern workflows | blame, browser, history graph, cherry-pick, revert, interactive rebase, sequencer recovery |
| M7 — Complete presentation | all dialogs/menus/actions, responsive layouts, themes, accessibility, fresh 14-locale translations, help/manual/completions |
| M8 — Release proof | MSTest/MTP configuration audit plus behavior/security/fuzz/AOT/performance/tool-package matrices green on all eight RIDs; reproducibility, package hashes/manifests, SBOM, and servicing drill |

A milestone closes only when its manifests and tests are green. Partial milestone builds are development artifacts and are not branded 1.0.

## 19. Final acceptance contract

GitSail 1.0 is releasable only when all of these are simultaneously true:

1. The repository and all of its code, prose, strings, translations, tests, and assets use the MIT license and do not contain copied Git GUI material.
2. Exactly 15 j6t and 72 prati issue records have implemented/tested or tested medium-equivalence dispositions, with no deferred/rejected-to-later state.
3. Every listed reference behavior has a passing compatibility test or a named terminal-specific expectation.
4. Unix non-UTF-8 paths round-trip through every supported path operation, and patch mutation retains exact content bytes and line endings.
5. The baseline keymap is collision-free at the byte level; enhanced bindings activate only after successful negotiation; every action appears in F2 and is keyboard reachable.
6. All process, shell, executable-config, filesystem, IPC, terminal, secret, and release invariants in §11 pass analyzers and adversarial tests.
7. GitSail targets .NET 10 with `VerifyReferenceAotCompatibility=true`; its exact unmodified binary dependency closure has zero unresolved AOT/trim/single-file warnings or suppressions.
8. Every RID-specific .NET tool package contains the Native AOT GitSail entry point and only the immutable dependency-owned runtime assets required by that RID, is selected automatically, starts on its declared minimum platform, passes native terminal/tool-install tests, and has retained symbols.
9. All 14 non-English locales are freshly translated, MIT-licensed, reviewed, complete, and layout-tested; English and pseudo-locales also pass.
10. EventPipe diagnostics, line-number stack metadata, Doctor, redacted logs, crash restoration, symbolication instructions, and the runtime servicing drill work from release artifacts.
11. The reproducible, hash-verified nine-package tool graph, SBOMs, global/local install, update, exact-version downgrade, restore, and uninstall tests pass.
12. Every absolute performance and regression budget in §17.4 passes on both reference architectures.
13. The generated action, command, config, state-file, issue, locale, and behavior appendices exactly match their locked manifests.
14. `git-tui`, `git tui` from a global installation, `dotnet tool run git-tui` from a local manifest, every documented mode, offline help, generated shell completions, and exit codes pass end-to-end from clean installations.
15. Every managed test project resolves the pinned `MSTest.Sdk` 4.2.3 as an executable MTP test application; `global.json` selects MTP, no VSTest or alternate-framework dependency is present, direct and `dotnet test` execution agree, and required TRX/coverage/zero-test gates pass.
16. Every Git command and configuration claim traces to the Git 2.36 baseline, a documented capability check, or the §3.2 reference revision. Tests do not modify the reference checkout.

There is no “known incomplete” waiver for a 1.0 acceptance item. A failed gate keeps the version pre-1.0 until corrected.

## Appendix A — Configuration registry

The generated registry contains exact types, defaults, scopes, and tests. Required compatibility reads include:

- identity and commit: `user.name`, `user.email`, `user.signingkey`, `commit.gpgsign`, `commit.template`, `commit.cleanup`, `core.commentChar`, `core.commentString`, `i18n.commitEncoding`;
- status/diff: `gui.trustmtime`, `gui.textconv`, `gui.diffcontext`, `gui.diffopts`, `gui.displayuntracked`, `gui.stageuntracked`, `gui.maxfilesdisplayed`, `gui.tabsize`, `diff.renames`, `diff.renameLimit`, `color.diff.*`;
- branch/merge: `branch.autoSetupMerge`, `gui.matchtrackingbranch`, `merge.summary`, `merge.verbosity`, `merge.diffstat`, `merge.tool`, `mergetool.*`, `rerere.enabled`;
- remote/transport: `remote.*`, `push.default`, `push.autoSetupRemote`, `gui.pruneDuringFetch`;
- UI compatibility: `gui.recentrepo`, `gui.maxrecentrepo`, `gui.encoding`, `gui.commitmsgwidth`, `gui.newbranchtemplate`, `gui.warndetachedcommit`, `gui.spellingdictionary`, `gui.search.case`, `gui.search.regexp`, `gui.gcwarning`, `gui.autoexplore`, `gui.fastcopyblame`, `gui.copyblamethreshold`, and `gui.blamehistoryctx`; and
- tools: the documented `guitool.<name>.*` keys.

Terminal-inapplicable compatibility keys—`gui.usettk`, `gui.fontui`, `gui.fontdiff`, `gui.geometry`, and `gui.wmstate`—are tolerated and shown as inapplicable in Doctor/options, but are never changed. This is an explicit medium equivalence, not silent removal.

New keys use only `gitsail.*`:

| Key | Type/default | Purpose |
|---|---|---|
| `gitsail.theme` | enum `auto` | light/dark/high-contrast/color-blind preset |
| `gitsail.colorDepth` | enum `auto` | capability override |
| `gitsail.unicode` | enum `auto` | Unicode/ASCII policy |
| `gitsail.ambiguousWidth` | 1 or 2, platform default | terminal cell-width override |
| `gitsail.keymap.<ActionId>` | chord list | collision-validated remapping |
| `gitsail.layout` | versioned validated record | splitter/tab layout |
| `gitsail.restorePinnedMenus` | bool `true` | restore pinned menu windows |
| `gitsail.showPushAction` | bool `false` | show separately labeled persistent push action |
| `gitsail.autoRescan` | bool `false` | watcher-assisted refresh |
| `gitsail.wrapCommitMessage` | bool `false` | visual hard-wrap mode |
| `gitsail.clipboard` | enum `auto` | off/auto/OSC52/helper |
| `gitsail.renameThreshold` | integer `50` | rename-detection percentage |
| `gitsail.safeForcePolicy` | enum `explicit-lease` | push safety policy |
| `gitsail.trustedRepository.<id>` | capability record | user-owned executable grants; stored globally only |
| `gitsail.logLevel` | enum `information` | structured log verbosity |

`gui.diffopts` is parsed into a typed allowlist. Context/whitespace/algorithm/stat options are permitted where compatible. Options that enable external commands, redirect output, introduce color/control codes, alter paths, or conflict with structured parsing are rejected with a specific explanation.

## Appendix B — Command and menu coverage

The top-level menus are Repository, Edit, View, Branch, Commit, Merge, Remote, Stash, History, Tools, and Help. Every entry is also present in the command palette and action manifest.

- **Repository:** open, recent, initialize, clone, worktree, browse, embedded shell, statistics, maintenance, verify, installation/invocation help, close, quit.
- **Edit:** undo, redo, cut, copy, paste, delete, select all, find, goto, options.
- **View:** unified/side-by-side, context, whitespace/invisibles/bidi, filters, encoding, panes, logs, refresh.
- **Branch:** create, checkout/detach, rename, delete, reset, upstream.
- **Commit:** amend, author, signoff, stage/unstage/revert variants, commit, commit without hooks, done in citool.
- **Merge:** merge, abort, conflict navigation, accept ours/theirs/base/both, rerere, mergetool.
- **Remote:** add/remove, fetch/all, prune, push, delete branch, remote initialization.
- **Stash:** save, list/show, apply, pop, drop.
- **History:** graph, inspect, compare, blame, cherry-pick, revert, interactive rebase, sequencer continue/skip/abort.
- **Tools:** configured tools, add, edit, remove, editor, browser, SSH keys, spellcheck status.
- **Help:** context help, keyboard reference, command palette, Doctor, logs, manual, online documentation, About.

Context menus exist for file lists, all diff/conflict modes, commit editor/spelling, blame, history, tree browser, consoles, and copyable dialogs. They reference the same actions and predicates rather than duplicating handlers.

## Appendix C — Git command families

Typed command builders and their tests are the executable contracts. The complete families are:

- discovery/capabilities: `--version`, `rev-parse`, `config`, `var`, `version --build-options`, `help --config`;
- status/index: `status --porcelain=v2 -z` where its data is sufficient, plus raw `diff-index`, `diff-files`, `ls-files`, `update-index`, `checkout-index`, and `apply --check/apply` protocols required for exact behavior;
- content/diff: `diff`, `diff-tree`, `cat-file`, `check-attr`, `hash-object`, with explicit `--no-color` and executable-driver policy;
- commit/hooks/refs: `commit`, `stripspace` for preview, `update-ref`/`symbolic-ref` only for non-commit ref operations, `show`, and `for-each-ref`;
- branch/worktree/merge/sequencer: `branch`, `switch`/plumbing equivalents as capability permits, `worktree`, `merge`, `merge-base`, `read-tree`, `rerere`, `cherry-pick`, `revert`, `rebase`;
- remotes: `remote`, `fetch`, `push`, `ls-remote`, explicit refspecs and leases;
- exploration: `log`, `show`, `blame --incremental`, `ls-tree -z`, `rev-list`;
- stash: `stash push/show/apply/pop/drop/list` with an explicit NUL format;
- repository management: `init`, `clone`, `count-objects`, `maintenance`, `gc`, `pack-refs`, `reflog`, `repack`, `fsck`; and
- integration: `web--browse`, `var GIT_EDITOR`, `mergetool`, credential/askpass, SSH, optional aspell, clipboard, and .NET tool shim/PATH diagnostics.

Command builders choose the documented command available at the Git floor and capability-detect newer improvements. User data always follows `--` or a byte-safe stdin protocol and never occupies an option position.

## Appendix D — State, cache, and environment

### Direct repository files

The complete direct-repository allowlist is:

| Path obtained through `git rev-parse --git-path` | Operations | Reason |
|---|---|---|
| `GITGUI_MSG` | read, atomic write, delete after successful commit/explicit discard | compatible recoverable draft |
| `GITGUI_BCK` | read, atomic write, delete after successful restore/discard | compatible saved commit message backup |
| `GITGUI_EDITMSG` | read, atomic write, delete after completed commit flow | commit message and hook input |
| `PREPARE_COMMIT_MSG` | create empty/atomic write, read, delete after completed flow | prepare-commit-msg hook lifecycle |
| `COMMIT_EDITMSG` | read after a `git commit` attempt only | recover the exact message after prepare/commit hooks modify Git's transaction file |
| `MERGE_MSG`, `SQUASH_MSG` | read only while Git reports the matching repository state | prefill the commit editor for Git-created merge/squash state |
| `MERGE_HEAD` | bounded read only while capturing or revalidating merge-abort state | bind confirmation to every incoming merge parent; the optional autostash ref is queried through Git |
| `index.lock` | metadata read and separately confirmed no-follow delete only | manual stale-lock recovery |

No other Git-directory path is opened directly. Merge, rebase, cherry-pick, revert, rerere, reflog, ref, object, `FETCH_HEAD`, hook, common-directory, and worktree state is otherwise queried or changed through Git commands. Tests fail any direct repository-state access not represented in this table.

The effective `commit.template` is a separately classified configured input, not an inferred Git-directory path. GitSail reads only the exact pathname returned by Git, only during ordinary commit-message initialization after every higher-precedence message source has been ruled out. The bounded reader accepts a regular file or a link resolving to a regular file and never writes, replaces, deletes, or derives another pathname from its display text.

### User directories

- configuration: platform config directory under `gitsail`;
- state/log/crash: platform state directory under `gitsail`;
- cache/spools/undo: platform cache directory under `gitsail`;
- IPC: user runtime directory with user-only access, falling back to a secured per-user temp directory.

Every file class has size, retention, permissions, cleanup, and redaction policy. Repository IDs use keyed hashes in filenames so paths do not leak.

### Environment

Startup reads are explicitly classified:

- platform/location: `PATH`, `HOME`, `USERPROFILE`, `XDG_CONFIG_HOME`, `XDG_CACHE_HOME`, `XDG_STATE_HOME`, `XDG_RUNTIME_DIR`, `LOCALAPPDATA`, `APPDATA`, `TMPDIR`, `TEMP`, `TMP`, `SHELL`, `COMSPEC`;
- locale/terminal: `LANG`, `LC_ALL`, `LC_MESSAGES`, `TERM`, `COLORTERM`, `NO_COLOR`, `WT_SESSION`, `TERM_PROGRAM`;
- Git repository/config: `GIT_DIR`, `GIT_WORK_TREE`, `GIT_COMMON_DIR`, `GIT_CEILING_DIRECTORIES`, `GIT_DISCOVERY_ACROSS_FILESYSTEM`, `GIT_CONFIG_NOSYSTEM`, `GIT_CONFIG_SYSTEM`, `GIT_CONFIG_GLOBAL`, `GIT_CONFIG_COUNT`, and the corresponding numbered config-key/value variables;
- Git identity/integration: `GIT_AUTHOR_*`, `GIT_COMMITTER_*`, `GIT_EDITOR`, `GIT_SEQUENCE_EDITOR`, `VISUAL`, `EDITOR`, `GIT_PAGER`, `PAGER`, `GIT_SSH`, `GIT_SSH_COMMAND`, `SSH_AUTH_SOCK`, `BROWSER`; and
- Git tracing variables only when `doctor` reports their effect; GitSail's own logging does not copy their values.

Explicit startup `GIT_DIR`/`GIT_WORK_TREE` and config overrides are honored for initial discovery as Git users expect, then converted to canonical operation-scoped values. They are removed or replaced when entering a submodule, worktree, or different repository so they cannot bleed across contexts.

Children receive only the classified values needed by that command plus operation-scoped repository, author, tool, askpass, editor, sequence-editor, locale, and transport variables. Tool variables include the documented `GIT_GUITOOL`, selected filename(s), current branch, arguments, and revision with raw-path-safe transport. Private helper variables use `GITSAIL_HELPER_*` and are nonce-authenticated. Environment values are never logged wholesale.

Machine-readable Git invocations set `GIT_PAGER=cat` and use `--no-pager`; read-only commands set `GIT_OPTIONAL_LOCKS=0` when doing so preserves documented behavior. Commands that may prompt install the authenticated askpass/yes-no bridge and an explicit prompt policy. No command accidentally inherits an interactive pager on a redirected pipe.

## Appendix E — Required artifacts

Each release publishes:

1. one unsigned top-level `GitSail.<version>.nupkg` pointer package;
2. eight unsigned `GitSail.<rid>.<version>.nupkg` Native AOT tool packages, published before the pointer package;
3. eight symbol artifacts retained as diagnostic evidence rather than application downloads;
4. root MIT license and embedded manual/completion content;
5. SPDX and CycloneDX SBOMs, dependency-license report, and vulnerability report;
6. reproducibility comparison, package-content, and native-import reports; and
7. MSTest/MTP configuration, compatibility, security, accessibility, localization, AOT, performance, and clean .NET tool installation test summaries.

## Appendix F — Engineering references

Native AOT decisions are pinned to the .NET 10 SDK/runtime used by the release and validated against:

- [Native AOT deployment overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [Optimize Native AOT deployments](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/optimizing)
- [Native AOT diagnostics and instrumentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics)
- [Native code interop with Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)
- [Native AOT libraries](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)
- [Native AOT cross-compilation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)
- [Native AOT security](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/security)
- [Resolve trim and Native AOT warnings](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/fixing-warnings)
- [APIs that intrinsically require dynamic code](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/intrinsic-requiresdynamiccode-apis)
- [Create RID-specific, self-contained, and AOT .NET tools](https://learn.microsoft.com/en-us/dotnet/core/tools/rid-specific-tools)
- [.NET 10 RID-specific tool packaging change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-tool-pack-publish)
- [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)

Test-platform decisions are pinned to the SDK versions in `global.json` and validated against:

- [Run tests with MSTest](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-running-tests)
- [Microsoft.Testing.Platform overview](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-intro)
- [MSTest SDK configuration](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-sdk)

The source-level runtime baseline is `dotnet/runtime@88956c7f4433c29c87df4d1ae9c7859b7e29c0ec`, especially `src/coreclr/nativeaot/BuildIntegration`. The TUI framework baseline is the official locked NuGet package identified in §6.1 and is consumed without modification. The Git source/documentation reference is the exact official checkout recorded in §3.2; it remains external and read-only.

Git command contracts link to the corresponding versioned pages under [git-scm.com/docs](https://git-scm.com/docs). Release manifests record the exact documentation revision and Git binaries used by each compatibility lane.

*End of design. Implementation may add detail, but it may not weaken or defer a requirement without producing a new reviewed design version.*
