using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Localization.Generated;
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
    private WindowManager? _popupWindowManager;
    private readonly List<WindowHandle> _popupWindows = [];
    private long _credentialPromptId;
    private readonly PopupViewport _popupViewport = new();

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
        _popupWindowManager = null;
        _popupWindows.Clear();
        _credentialPromptId = 0;
    }

    /// <summary>
    /// Builds the complete responsive chooser and bounded dialog host.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The controlled repository chooser tree.</returns>
    internal Hex1bWidget Build(RootContext context)
        => context.Responsive(responsive =>
        [
            responsive.When(
                _popupViewport.Capture,
                builder => BuildWindowPanel(builder)),
        ]);

    private WindowPanelWidget BuildWindowPanel<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.WindowPanel()
            .Background(background => background.ZStack(layers =>
            [
                BuildChooser(layers),
                _popupWindows.Count > 0
                    ? layers.Backdrop()
                        .Transparent()
                        .OnClickAway(CloseActivePopup)
                    : null,
            ]).Fill())
            .Fill();

    private VStackWidget BuildChooser<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            builder.Responsive(responsive =>
            [
                responsive.When(
                    static (width, height) => width < 60 || height < 18,
                    compact => BuildMinimumChooser(compact)),
                responsive.Otherwise(ready => BuildStandardChooser(ready)),
            ]).Fill(),
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

    private VStackWidget BuildStandardChooser<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            builder.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section(AppMessages.ChooserHeaderTitle),
                info.Spacer(),
                info.Section($"Git {_session.Installation.Version}"),
            ]).Divider(" | "),
            builder.VStack(content =>
            [
                BuildNavigation(content),
                BuildPage(content),
            ]).Fill(),
            BuildStatusBar(builder),
            BuildChooserShortcutBar(builder),
        ]).Fill();

    private VStackWidget BuildMinimumChooser<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            BuildResizeView(builder),
            builder.HStack(actions =>
            [
                actions.Button(AppMessages.WorkspaceActionHelp).OnClick(
                    eventArgs => ShowHelp(eventArgs.Windows)),
                actions.Text(" "),
                actions.Button(AppMessages.ChooserActionRecent).OnClick(
                    _ => _session.ReloadRecentAsync()),
                actions.Text(" "),
                actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                    eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth(),
            builder.InfoBar(info =>
            [
                info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
                info.Section($"F5 {AppMessages.ChooserActionRecent}"),
                info.Spacer(),
                info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
            ]).Divider(" | "),
        ]).Fill();

    private static InfoBarWidget BuildChooserShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section($"Tab {AppMessages.ChooserActionFocus}"),
            info.Section($"Enter {AppMessages.ChooserActionActivate}"),
            info.Section(AppMessages.WorkspaceActionMouse),
            info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
            info.Section($"F5 {AppMessages.ChooserActionRecent}"),
            info.Spacer(),
            info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
        ]).Divider(" | ");

    private void OpenPopup(WindowManager windows, WindowHandle popup)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(popup);
        _popupWindowManager = windows;
        _popupWindows.Add(popup);
        popup.OnClose(() =>
        {
            _popupWindows.Remove(popup);
            if (_popupWindows.Count == 0)
            {
                _popupWindowManager = null;
            }
        });
        popup.Open(windows);
    }

    private void CloseActivePopup()
    {
        if (_popupWindowManager is { } windows)
        {
            ClosePopupOnBackgroundClick(windows);
        }
    }

    private void ClosePopupOnBackgroundClick(WindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var activeWindow = windows.ActiveWindow;
        if (activeWindow is null)
        {
            return;
        }

        for (var index = _popupWindows.Count - 1; index >= 0; index--)
        {
            var popup = _popupWindows[index];
            if (ReferenceEquals(windows.Get(popup), activeWindow))
            {
                windows.Close(popup);
                return;
            }
        }
    }

    private static TWidget DismissOnEscape<TWidget>(TWidget widget, WindowHandle window)
        where TWidget : Hex1bWidget
        => widget.InputBindings(bindings =>
        {
            bindings.Remove(Hex1bKey.Escape);
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Cancel(),
                "Close the active window");
        });

    private BorderWidget BuildNavigation<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.WrapPanel(actions =>
        [
            NavigationButton(actions, RepositoryChooserPage.Open, AppMessages.ChooserActionOpen),
            NavigationButton(actions, RepositoryChooserPage.Recent, AppMessages.ChooserActionRecent),
            NavigationButton(actions, RepositoryChooserPage.Clone, AppMessages.ChooserActionClone),
            NavigationButton(actions, RepositoryChooserPage.Initialize, AppMessages.ChooserActionInitialize),
            NavigationButton(
                actions,
                RepositoryChooserPage.InitializeBare,
                AppMessages.ChooserActionInitializeBare),
            NavigationButton(
                actions,
                RepositoryChooserPage.OpenWorktree,
                AppMessages.ChooserActionOpenWorktree),
        ]).FillWidth()).Title(AppMessages.ChooserSectionRepositoryActions);

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
                ? AppMessages.ChooserOpenGuidanceWorktree
                : AppMessages.ChooserOpenGuidanceRepository).Wrap(),
            builder.HStack(path =>
            [
                path.Text($"{AppMessages.ChooserLabelDirectory} "),
                path.TextBox()
                    .State(_session.OpenPath)
                    .OnSubmit(_ => CompleteSelectionAsync(_session.SelectOpenPathAsync))
                    .FillWidth(),
            ]).FillWidth(),
            builder.HStack(actions =>
            [
                _session.IsBusy
                    ? actions.Text(AppMessages.ChooserStatusOpenBusy)
                    : actions.Button(worktree
                            ? AppMessages.ChooserActionOpenExistingWorktree
                            : AppMessages.ChooserActionOpenRepository)
                        .OnClick(_ => CompleteSelectionAsync(_session.SelectOpenPathAsync)),
                actions.Text(" "),
                actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                    eventArgs => eventArgs.Context.RequestStop()),
            ]),
            builder.Text(AppMessages.ChooserOpenDiscoveryExplanation).Wrap(),
        ]).Fill()).Title(worktree
            ? AppMessages.ChooserActionOpenExistingWorktree
            : AppMessages.ChooserActionOpenRepository).Fill();

    private BorderWidget BuildRecentPage<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.List(_session.RecentRepositories)
                .ItemKey(static path => path)
                .FocusedIndex(_session.RecentFocusedIndex)
                .OnFocusChanged(eventArgs => _session.FocusRecent(eventArgs.FocusedIndex))
                .Empty(empty => empty.Text(AppMessages.ChooserStatusNoRecent))
                .InputBindings(bindings => bindings.Key(Hex1bKey.Enter).Action(
                    _ => CompleteSelectionAsync(_session.SelectRecentAsync),
                    "Open the focused recent repository"))
                .Fill(),
            builder.WrapPanel(actions =>
            [
                _session.RecentRepositories.IsEmpty || _session.IsBusy
                    ? actions.Text(AppMessages.ChooserStatusOpenUnavailable)
                    : actions.Button(AppMessages.ChooserActionOpenSelected).OnClick(
                        _ => CompleteSelectionAsync(_session.SelectRecentAsync)),
                actions.Text(" "),
                _session.RecentRepositories.IsEmpty || _session.IsBusy
                    ? actions.Text(AppMessages.ChooserStatusRemoveUnavailable)
                    : actions.Button(AppMessages.ChooserActionRemoveRecent).OnClick(
                        _ => _session.RemoveFocusedRecentAsync()),
                actions.Text(" "),
                _session.IsBusy
                    ? actions.Text(AppMessages.ChooserStatusRefreshUnavailable)
                    : actions.Button(AppMessages.WorkspaceActionRefresh).OnClick(
                        _ => _session.ReloadRecentAsync()),
            ]).FillWidth(),
        ]).Fill()).Title($"{AppMessages.ChooserSectionRecentRepositories} " +
            $"({_session.RecentRepositories.Length})").Fill();

    private BorderWidget BuildClonePage<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.HStack(source =>
            [
                source.Text($"{AppMessages.ChooserLabelSource} "),
                source.TextBox().State(_session.CloneSource).FillWidth(),
            ]).FillWidth(),
            builder.HStack(target =>
            [
                target.Text($"{AppMessages.ChooserLabelTarget} "),
                target.TextBox().State(_session.CloneTarget).FillWidth(),
            ]).FillWidth(),
            builder.WrapPanel(options =>
            [
                options.Button(GetCloneModeLabel()).OnClick(_ => _session.CycleCloneMode()),
                options.Text(" "),
                options.Button($"[{(_session.RecurseSubmodules ? 'x' : ' ')}] " +
                        AppMessages.ChooserOptionRecursiveSubmodules)
                    .OnClick(_ => _session.ToggleRecursiveSubmodules()),
            ]).FillWidth(),
            BuildCloneModeExplanation(builder),
            builder.WrapPanel(actions =>
            [
                _session.IsBusy
                    ? actions.Button(AppMessages.ChooserActionCancelClone).OnClick(
                        _ => _session.CancelOperation())
                    : actions.Button(AppMessages.ChooserActionCloneAndOpen).OnClick(
                        eventArgs => RunCloneAsync(eventArgs.Windows)),
                actions.Text(" "),
                actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                    eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth(),
            builder.Text(AppMessages.ChooserCloneOperandExplanation).Wrap(),
        ]).Fill()).Title(AppMessages.ChooserSectionCloneRepository).Fill();

    private Hex1bWidget BuildCloneModeExplanation<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.CloneMode == RepositoryCloneMode.Shared
            ? context.Border(context.Text(
                AppMessages.ChooserCloneSharedWarning).Wrap())
                .Title(AppMessages.ChooserCloneSharedWarningTitle)
            : context.Text(_session.CloneMode == RepositoryCloneMode.FullCopy
                ? AppMessages.ChooserCloneFullCopyExplanation
                : AppMessages.ChooserCloneStandardExplanation).Wrap();

    private BorderWidget BuildInitializePage<TParent>(
        WidgetContext<TParent> context,
        bool bare)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.Text(bare
                ? AppMessages.ChooserInitializeGuidanceBare
                : AppMessages.ChooserInitializeGuidanceWorktree).Wrap(),
            builder.HStack(target =>
            [
                target.Text($"{AppMessages.ChooserLabelTarget} "),
                target.TextBox()
                    .State(_session.InitializePath)
                    .OnSubmit(_ => CompleteSelectionAsync(() => _session.InitializeAsync(bare)))
                    .FillWidth(),
            ]).FillWidth(),
            builder.WrapPanel(actions =>
            [
                _session.IsBusy
                    ? actions.Button(AppMessages.ChooserActionCancelInitialization).OnClick(
                        _ => _session.CancelOperation())
                    : actions.Button(bare
                            ? AppMessages.ChooserActionInitializeBareAndOpen
                            : AppMessages.ChooserActionInitializeAndOpen)
                        .OnClick(_ => CompleteSelectionAsync(() => _session.InitializeAsync(bare))),
                actions.Text(" "),
                actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                    eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth(),
            builder.Text(AppMessages.ChooserInitializeExplanation).Wrap(),
        ]).Fill()).Title(bare
            ? AppMessages.ChooserSectionInitializeBareRepository
            : AppMessages.ChooserSectionInitializeRepository).Fill();

    private VStackWidget BuildStatusBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            builder.Border(builder.Text(_session.Status).Wrap())
                .Title(_session.IsBusy
                    ? AppMessages.ChooserLabelWorking
                    : AppMessages.ChooserLabelStatus),
            _session.Cleanup is null
                ? builder.Text(string.Empty)
                : builder.HStack(actions =>
                [
                    actions.Text($"{AppMessages.ChooserLabelPartialTarget}: " +
                        $"{_session.Cleanup.DisplayPath}  "),
                    actions.Button(AppMessages.ChooserActionRemovePartialTarget).OnClick(
                        _ => _session.CleanupAsync()),
                ]).FillWidth(),
        ]);

    private static BorderWidget BuildResizeView<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.Text(AppMessages.WorkspaceResizeRequirement).Wrap(),
            builder.Text(AppMessages.ChooserResizeInstruction).Wrap(),
            builder.Text(AppMessages.ChooserResizeBindings).Wrap(),
        ])).Title(AppMessages.WorkspaceResizeTitle).Fill();

    private string GetCloneModeLabel()
        => _session.CloneMode switch
        {
            RepositoryCloneMode.Standard =>
                $"{AppMessages.ChooserLabelMode}: {AppMessages.ChooserModeStandard}",
            RepositoryCloneMode.FullCopy =>
                $"{AppMessages.ChooserLabelMode}: {AppMessages.ChooserModeFullCopy}",
            RepositoryCloneMode.Shared =>
                $"{AppMessages.ChooserLabelMode}: {AppMessages.ChooserModeSharedObjects}",
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
                input = DismissOnEscape(
                    builder.TextBox()
                        .State(visibleResponse)
                        .OnSubmit(_ => SubmitText()),
                    window.Window)
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
                        var text = await eventArgs.Paste
                            .ReadToEndAsync(16 * 1024, _cancellationToken)
                            .ConfigureAwait(false);
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
                    CloseWithResultIfOpen(windows, window.Window, "response");
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
                    CloseWithResultIfOpen(windows, window.Window, "response");
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
                    CloseWithResultIfOpen(windows, window.Window, accepted ? "yes" : "no");
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
        .Size(
            _popupViewport.FitWidth(78),
            _popupViewport.FitHeight(request.Kind == CredentialPromptKind.Confirmation ? 12 : 14))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
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

    private static void CloseWithResultIfOpen<T>(
        WindowManager windows,
        WindowHandle window,
        T result)
        => windows.Get(window)?.CloseWithResult(result);

    private void ShowHelp(WindowManager windows)
        => OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.VScrollPanel(content =>
            [
                content.Text(AppMessages.ChooserHelpOpen).Wrap(),
                content.Text(AppMessages.ChooserHelpRecent).Wrap(),
                content.Text(AppMessages.ChooserHelpClone).Wrap(),
                content.Text(AppMessages.ChooserHelpShared).Wrap(),
                content.Text(AppMessages.ChooserHelpSubmodules).Wrap(),
                content.Text(AppMessages.ChooserHelpCleanup).Wrap(),
                content.Text(AppMessages.ChooserHelpNavigation).Wrap(),
            ], showScrollbar: true).Fill(),
            builder.Button(AppMessages.CommonActionClose).OnClick(_ => window.Window.Cancel()),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            AppMessages.ChooserHelpBindingClose)))
        .Title(AppMessages.ChooserHelpTitle)
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(18))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 14, 120, 28));
}
