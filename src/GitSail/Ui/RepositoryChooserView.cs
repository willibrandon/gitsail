using GitSail.Domain;
using GitSail.Git.Execution;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Composes the responsive keyboard- and mouse-complete repository chooser.
/// </summary>
internal sealed class RepositoryChooserView
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly RepositoryChooserSession _session;
    private readonly CancellationToken _cancellationToken;
    private readonly Lock _credentialPromptLock = new();
    private Hex1bApp? _application;
    private WindowManager? _credentialWindowManager;
    private WindowHandle? _credentialPromptWindow;
    private long _credentialPromptId;

    /// <summary>
    /// Initializes one chooser view over controlled session state.
    /// </summary>
    /// <param name="session">The Git-backed chooser session.</param>
    /// <param name="cancellationToken">Signals terminal and repository-operation shutdown.</param>
    internal RepositoryChooserView(
        RepositoryChooserSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Connects chooser invalidation and credential prompt synchronization to its application.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    internal void Attach(Hex1bApp application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The repository chooser view is already attached.");
        }

        _application = application;
        _session.Changed += HandleSessionChanged;
    }

    /// <summary>
    /// Disconnects chooser invalidation and cancels any displayed credential prompt.
    /// </summary>
    internal void Detach()
    {
        if (_application is null)
        {
            return;
        }

        _session.Changed -= HandleSessionChanged;
        if (_session.CredentialPrompts.Current is { } request)
        {
            _session.CredentialPrompts.Cancel(request.Id);
        }

        _application = null;
        _credentialWindowManager = null;
        _credentialPromptWindow = null;
        _credentialPromptId = 0;
    }

    /// <summary>
    /// Builds the complete responsive chooser and bounded dialog host.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The controlled repository chooser tree.</returns>
    internal WindowPanelWidget Build(RootContext context)
        => context.WindowPanel()
            .Background(background => BuildChooser(background))
            .Fill();

    private VStackWidget BuildChooser<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            builder.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("repository chooser"),
                info.Spacer(),
                info.Section($"Git {_session.Installation.Version}"),
            ]).Divider(" | "),
            builder.Responsive(responsive =>
            [
                responsive.When(
                    static (width, height) => width < 60 || height < 18,
                    compact => BuildResizeView(compact)),
                responsive.Otherwise(ready => ready.VStack(content =>
                [
                    BuildNavigation(content),
                    BuildPage(content),
                ]).Fill()),
            ]).Fill(),
            BuildStatusBar(builder),
            builder.InfoBar(info =>
            [
                info.Section("Tab Focus"),
                info.Section("Enter Activate"),
                info.Section("Mouse"),
                info.Section("F1 Help"),
                info.Section("F5 Recent"),
                info.Spacer(),
                info.Section("Ctrl+Q Quit"),
            ]).Divider(" | "),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.F1).Action(
                actionContext => ShowHelp(actionContext.Windows),
                "Open repository chooser help");
            bindings.Key(Hex1bKey.F5).Action(
                _ => _session.ReloadRecentAsync(),
                "Refresh recent repositories");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }).Fill();

    private BorderWidget BuildNavigation<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.WrapPanel(actions =>
        [
            NavigationButton(actions, RepositoryChooserPage.Open, "Open"),
            NavigationButton(actions, RepositoryChooserPage.Recent, "Recent"),
            NavigationButton(actions, RepositoryChooserPage.Clone, "Clone"),
            NavigationButton(actions, RepositoryChooserPage.Initialize, "Initialize"),
            NavigationButton(actions, RepositoryChooserPage.InitializeBare, "Initialize bare"),
            NavigationButton(actions, RepositoryChooserPage.OpenWorktree, "Open worktree"),
        ]).FillWidth()).Title("Repository actions");

    private ButtonWidget NavigationButton<TParent>(
        WidgetContext<TParent> context,
        RepositoryChooserPage page,
        string label)
        where TParent : Hex1bWidget
        => context.Button(_session.Page == page ? $"[{label}]" : label)
            .OnClick(_ => _session.SetPage(page));

    private BorderWidget BuildPage<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.Page switch
        {
            RepositoryChooserPage.Open => BuildOpenPage(context, worktree: false),
            RepositoryChooserPage.Recent => BuildRecentPage(context),
            RepositoryChooserPage.Clone => BuildClonePage(context),
            RepositoryChooserPage.Initialize => BuildInitializePage(context, bare: false),
            RepositoryChooserPage.InitializeBare => BuildInitializePage(context, bare: true),
            RepositoryChooserPage.OpenWorktree => BuildOpenPage(context, worktree: true),
            _ => throw new InvalidOperationException("The repository chooser page is invalid."),
        };

    private BorderWidget BuildOpenPage<TParent>(
        WidgetContext<TParent> context,
        bool worktree)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.Text(worktree
                ? "Enter the root or any existing directory inside a main or linked worktree."
                : "Enter a repository root or any existing directory beneath its worktree."),
            builder.HStack(path =>
            [
                path.Text("Directory: "),
                path.TextBox()
                    .State(_session.OpenPath)
                    .OnSubmit(_ => CompleteSelectionAsync(_session.SelectOpenPathAsync))
                    .FillWidth(),
            ]).FillWidth(),
            builder.HStack(actions =>
            [
                _session.IsBusy
                    ? actions.Text("Open unavailable while an operation is active")
                    : actions.Button(worktree ? "Open existing worktree" : "Open repository")
                        .OnClick(_ => CompleteSelectionAsync(_session.SelectOpenPathAsync)),
                actions.Text(" "),
                actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]),
            builder.Text("Git performs repository discovery and honors explicit startup repository overrides only for this initial open."),
        ]).Fill()).Title(worktree ? "Open existing worktree" : "Open repository").Fill();

    private BorderWidget BuildRecentPage<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.List(_session.RecentRepositories)
                .ItemKey(static path => path)
                .FocusedIndex(_session.RecentFocusedIndex)
                .OnFocusChanged(eventArgs => _session.FocusRecent(eventArgs.FocusedIndex))
                .Empty(empty => empty.Text("No recent repositories are recorded."))
                .InputBindings(bindings => bindings.Key(Hex1bKey.Enter).Action(
                    _ => CompleteSelectionAsync(_session.SelectRecentAsync),
                    "Open the focused recent repository"))
                .Fill(),
            builder.WrapPanel(actions =>
            [
                _session.RecentRepositories.IsEmpty || _session.IsBusy
                    ? actions.Text("Open unavailable")
                    : actions.Button("Open selected").OnClick(
                        _ => CompleteSelectionAsync(_session.SelectRecentAsync)),
                actions.Text(" "),
                _session.RecentRepositories.IsEmpty || _session.IsBusy
                    ? actions.Text("Remove unavailable")
                    : actions.Button("Remove from recent").OnClick(
                        _ => _session.RemoveFocusedRecentAsync()),
                actions.Text(" "),
                _session.IsBusy
                    ? actions.Text("Refresh unavailable")
                    : actions.Button("Refresh").OnClick(_ => _session.ReloadRecentAsync()),
            ]).FillWidth(),
        ]).Fill()).Title($"Recent repositories ({_session.RecentRepositories.Length})").Fill();

    private BorderWidget BuildClonePage<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.HStack(source =>
            [
                source.Text("Source: "),
                source.TextBox().State(_session.CloneSource).FillWidth(),
            ]).FillWidth(),
            builder.HStack(target =>
            [
                target.Text("Target: "),
                target.TextBox().State(_session.CloneTarget).FillWidth(),
            ]).FillWidth(),
            builder.WrapPanel(options =>
            [
                options.Button(GetCloneModeLabel()).OnClick(_ => _session.CycleCloneMode()),
                options.Text(" "),
                options.Button(_session.RecurseSubmodules
                        ? "[x] Recursive submodules"
                        : "[ ] Recursive submodules")
                    .OnClick(_ => _session.ToggleRecursiveSubmodules()),
            ]).FillWidth(),
            BuildCloneModeExplanation(builder),
            builder.WrapPanel(actions =>
            [
                _session.IsBusy
                    ? actions.Button("Cancel clone").OnClick(_ => _session.CancelOperation())
                    : actions.Button("Clone and open").OnClick(
                        eventArgs => RunCloneAsync(eventArgs.Windows)),
                actions.Text(" "),
                actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth(),
            builder.Text("Source and target are passed to Git as literal operands after --. Git owns transport, checkout, filters, and submodule behavior."),
        ]).Fill()).Title("Clone repository").Fill();

    private Hex1bWidget BuildCloneModeExplanation<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.CloneMode == RepositoryCloneMode.Shared
            ? context.Border(context.Text(
                "Warning: the clone will borrow objects from the source. Removing source objects can corrupt this clone.").Wrap())
                .Title("Shared clone can become corrupt")
            : context.Text(_session.CloneMode == RepositoryCloneMode.FullCopy
                ? "Full copy disables local hardlinks so the clone owns separate object files."
                : "Standard lets Git use safe local hardlinks when applicable and normal transport otherwise.").Wrap();

    private BorderWidget BuildInitializePage<TParent>(
        WidgetContext<TParent> context,
        bool bare)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.Text(bare
                ? "Create a repository without a worktree for server, backup, or remote use."
                : "Create or safely reinitialize a repository with a worktree."),
            builder.HStack(target =>
            [
                target.Text("Target: "),
                target.TextBox()
                    .State(_session.InitializePath)
                    .OnSubmit(_ => CompleteSelectionAsync(() => _session.InitializeAsync(bare)))
                    .FillWidth(),
            ]).FillWidth(),
            builder.WrapPanel(actions =>
            [
                _session.IsBusy
                    ? actions.Button("Cancel initialization").OnClick(_ => _session.CancelOperation())
                    : actions.Button(bare ? "Initialize bare and open" : "Initialize and open")
                        .OnClick(_ => CompleteSelectionAsync(() => _session.InitializeAsync(bare))),
                actions.Text(" "),
                actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth(),
            builder.Text("Git owns templates and the configured default initial branch. Existing repositories are reinitialized using Git's documented behavior."),
        ]).Fill()).Title(bare ? "Initialize bare repository" : "Initialize repository").Fill();

    private VStackWidget BuildStatusBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            builder.Border(builder.Text(_session.Status).Wrap())
                .Title(_session.IsBusy ? "Working" : "Status"),
            _session.Cleanup is null
                ? builder.Text(string.Empty)
                : builder.HStack(actions =>
                [
                    actions.Text($"Partial target: {_session.Cleanup.DisplayPath}  "),
                    actions.Button("Remove unchanged partial target").OnClick(
                        _ => _session.CleanupAsync()),
                ]).FillWidth(),
        ]);

    private static BorderWidget BuildResizeView<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.Text("GitSail needs a terminal at least 60 columns wide and 18 rows high."),
            builder.Text("Resize the terminal to return to the repository chooser."),
            builder.Text("F1 Help, F5 recent refresh, and Ctrl+Q Quit remain available."),
        ])).Title("Terminal too small").Fill();

    private string GetCloneModeLabel()
        => _session.CloneMode switch
        {
            RepositoryCloneMode.Standard => "Mode: Standard",
            RepositoryCloneMode.FullCopy => "Mode: Full copy",
            RepositoryCloneMode.Shared => "Mode: Shared objects",
            _ => throw new ArgumentOutOfRangeException(nameof(_session.CloneMode)),
        };

    private async Task CompleteSelectionAsync(Func<Task> selectAsync)
    {
        ArgumentNullException.ThrowIfNull(selectAsync);
        await selectAsync().ConfigureAwait(false);
        if (_session.SelectedDirectory is not null)
        {
            _application?.RequestStop();
        }
    }

    private async Task RunCloneAsync(WindowManager windows)
    {
        _credentialWindowManager = windows;
        await CompleteSelectionAsync(_session.CloneAsync).ConfigureAwait(false);
        if (_session.CredentialPrompts.Current is null)
        {
            _credentialWindowManager = null;
        }
    }

    private void HandleSessionChanged(object? sender, EventArgs eventArgs)
    {
        SynchronizeCredentialPromptWindow();
        _application?.Invalidate();
    }

    private void SynchronizeCredentialPromptWindow()
    {
        lock (_credentialPromptLock)
        {
            var request = _session.CredentialPrompts.Current;
            var windows = _credentialWindowManager;
            if (request is null)
            {
                if (_credentialPromptWindow is { } completedWindow &&
                    windows?.IsOpen(completedWindow) == true)
                {
                    windows.Close(completedWindow);
                }

                _credentialPromptWindow = null;
                _credentialPromptId = 0;
                return;
            }

            if (windows is null ||
                (_credentialPromptId == request.Id &&
                    _credentialPromptWindow is { } currentWindow &&
                    windows.IsOpen(currentWindow)))
            {
                return;
            }

            if (_credentialPromptWindow is { } previousWindow && windows.IsOpen(previousWindow))
            {
                windows.Close(previousWindow);
            }

            _credentialPromptWindow = OpenCredentialPromptWindow(windows, request);
            _credentialPromptId = request.Id;
        }
    }

    private WindowHandle OpenCredentialPromptWindow(
        WindowManager windows,
        CredentialPromptRequest request)
    {
        var visibleResponse = new TextBoxState();
        var secretResponse = new List<byte[]>();
        var secretCharacterCount = 0;
        var secretByteCount = 0;
        var submitted = false;
        var handle = windows.Window(window => window.VStack(builder =>
        {
            if (request.Kind == CredentialPromptKind.Confirmation)
            {
                return
                [
                    builder.Text($"Operation: {request.Operation}"),
                    builder.Text(request.Prompt).Wrap(),
                    builder.HStack(actions =>
                    [
                        actions.Button("No").OnClick(_ => SubmitConfirmation(accepted: false)),
                        actions.Text(" "),
                        actions.Button("Yes").OnClick(_ => SubmitConfirmation(accepted: true)),
                    ]),
                    builder.Text("No is focused first | Enter or mouse selects | Esc cancels"),
                ];
            }

            Hex1bWidget input;
            if (request.Kind == CredentialPromptKind.Text)
            {
                input = builder.TextBox()
                    .State(visibleResponse)
                    .OnSubmit(_ => SubmitText())
                    .FillWidth();
            }
            else
            {
                var secretButton = builder.Button(secretCharacterCount == 0
                        ? "Secret input: <empty>"
                        : $"Secret input: {new string('•', secretCharacterCount)}")
                    .OnClick(_ => SubmitSecret())
                    .InputBindings(bindings =>
                    {
                        bindings.AnyCharacter().Action(text =>
                        {
                            var bytes = s_strictUtf8.GetBytes(text);
                            if (secretCharacterCount + text.Length <= 16 * 1024 &&
                                secretByteCount + bytes.Length <= 64 * 1024)
                            {
                                secretResponse.Add(bytes);
                                secretCharacterCount += text.Length;
                                secretByteCount += bytes.Length;
                                _application?.Invalidate();
                            }
                            else
                            {
                                CryptographicOperations.ZeroMemory(bytes);
                            }
                        }, "Enter secret text without rendering it");
                        bindings.Key(Hex1bKey.Backspace).Action(() =>
                        {
                            if (secretResponse.Count > 0)
                            {
                                var removed = secretResponse[^1];
                                secretResponse.RemoveAt(secretResponse.Count - 1);
                                secretCharacterCount -= s_strictUtf8.GetCharCount(removed);
                                secretByteCount -= removed.Length;
                                CryptographicOperations.ZeroMemory(removed);
                                _application?.Invalidate();
                            }
                        }, "Remove the last secret character");
                    });
                input = builder.Pastable(secretButton)
                    .MaxSize(16 * 1024)
                    .Timeout(TimeSpan.FromSeconds(30))
                    .OnPaste(async eventArgs =>
                    {
                        var text = await eventArgs.Paste.ReadToEndAsync(16 * 1024).ConfigureAwait(false);
                        var bytes = s_strictUtf8.GetBytes(text);
                        if (secretCharacterCount + text.Length <= 16 * 1024 &&
                            secretByteCount + bytes.Length <= 64 * 1024)
                        {
                            secretResponse.Add(bytes);
                            secretCharacterCount += text.Length;
                            secretByteCount += bytes.Length;
                            eventArgs.Paste.Invalidate();
                        }
                        else
                        {
                            CryptographicOperations.ZeroMemory(bytes);
                        }
                    });
            }

            return
            [
                builder.Text($"Operation: {request.Operation}"),
                builder.Text(request.Prompt).Wrap(),
                input,
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    actions.Button("Submit response").OnClick(_ =>
                    {
                        if (request.Kind == CredentialPromptKind.Text)
                        {
                            SubmitText();
                        }
                        else
                        {
                            SubmitSecret();
                        }
                    }),
                ]),
                builder.Text(request.Kind == CredentialPromptKind.Secret
                    ? "Characters are masked and never saved | Enter or mouse submits | Esc cancels"
                    : "Enter or mouse submits | Esc cancels"),
            ];

            void SubmitText()
            {
                submitted = _session.CredentialPrompts.Submit(request.Id, visibleResponse.Text);
                if (submitted)
                {
                    window.Window.CloseWithResult("response");
                }
            }

            void SubmitSecret()
            {
                var response = new byte[secretByteCount];
                var offset = 0;
                foreach (var segment in secretResponse)
                {
                    segment.CopyTo(response, offset);
                    offset += segment.Length;
                }

                ClearSecretResponse();
                submitted = _session.CredentialPrompts.SubmitOwned(request.Id, response);
                if (submitted)
                {
                    window.Window.CloseWithResult("response");
                }
            }

            void ClearSecretResponse()
            {
                foreach (var segment in secretResponse)
                {
                    CryptographicOperations.ZeroMemory(segment);
                }

                secretResponse.Clear();
                secretCharacterCount = 0;
                secretByteCount = 0;
            }

            void SubmitConfirmation(bool accepted)
            {
                submitted = _session.CredentialPrompts.Confirm(request.Id, accepted);
                if (submitted)
                {
                    window.Window.CloseWithResult(accepted ? "yes" : "no");
                }
            }
        }))
        .Title(request.Kind switch
        {
            CredentialPromptKind.Text => "Credential text required",
            CredentialPromptKind.Secret => "Credential secret required",
            CredentialPromptKind.Confirmation => "Transport confirmation required",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        })
        .Size(88, request.Kind == CredentialPromptKind.Confirmation ? 12 : 14)
        .Resizable(58, 10, 118, 24)
        .Modal()
        .OnClose(() =>
        {
            if (!submitted)
            {
                _session.CredentialPrompts.Cancel(request.Id);
            }

            foreach (var segment in secretResponse)
            {
                CryptographicOperations.ZeroMemory(segment);
            }

            secretResponse.Clear();
            secretCharacterCount = 0;
            secretByteCount = 0;
        });
        handle.Open(windows);
        return handle;
    }

    private static void ShowHelp(WindowManager windows)
    {
        windows.Window(window => window.VStack(builder =>
        [
            builder.Text("Open accepts a repository root or any directory beneath a worktree."),
            builder.Text("Recent paths are stored through global Git configuration and retain exact native identity."),
            builder.Text("Standard clone uses Git's normal local optimization. Full copy disables hardlinks."),
            builder.Text("Shared clone borrows source objects and can become corrupt if the source loses them."),
            builder.Text("Recursive submodules delegates initialization and recursion entirely to Git."),
            builder.Text("Failed new targets can be removed only while their target and parent identities still match."),
            builder.Text("Tab and Shift+Tab move focus. Enter and mouse activate controls. Ctrl+Q quits."),
            builder.Button("Close").OnClick(_ => window.Window.Cancel()),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close chooser help")))
        .Title("Repository chooser help")
        .Size(92, 18)
        .Resizable(60, 14, 120, 28)
        .Modal()
        .Open(windows);
    }
}
