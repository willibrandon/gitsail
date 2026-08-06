using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns the current version-matched commit spelling result and its visible status.
/// </summary>
internal sealed class SpellingState
{
    private readonly Lock _gate = new();
    private ImmutableArray<SpellingIssue> _issues = [];
    private long _documentVersion = -1;
    private bool _isAvailable = true;
    private bool _isChecking;
    private string _statusText = "Spelling has not run yet.";

    /// <summary>
    /// Initializes spelling state and its stable editor decoration provider.
    /// </summary>
    internal SpellingState()
    {
        DecorationProvider = new SpellingDecorationProvider(this);
    }

    /// <summary>
    /// Gets the stable provider that underlines issues from the current editor version.
    /// </summary>
    internal SpellingDecorationProvider DecorationProvider { get; }

    /// <summary>
    /// Gets whether the optional checker is ready to run or has a current result.
    /// </summary>
    internal bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _isAvailable;
            }
        }
    }

    /// <summary>
    /// Gets whether a debounce or checker invocation is active for the current message.
    /// </summary>
    internal bool IsChecking
    {
        get
        {
            lock (_gate)
            {
                return _isChecking;
            }
        }
    }

    /// <summary>
    /// Gets the control-safe explanation presented by spelling commands and dialogs.
    /// </summary>
    internal string StatusText
    {
        get
        {
            lock (_gate)
            {
                return _statusText;
            }
        }
    }

    /// <summary>
    /// Gets the immutable issues for the most recently accepted editor version.
    /// </summary>
    internal ImmutableArray<SpellingIssue> Issues
    {
        get
        {
            lock (_gate)
            {
                return _issues;
            }
        }
    }

    /// <summary>
    /// Clears stale issues and records a pending check for one exact editor version.
    /// </summary>
    /// <param name="documentVersion">The editor version captured for the pending check.</param>
    internal void BeginCheck(long documentVersion)
    {
        lock (_gate)
        {
            _documentVersion = documentVersion;
            _issues = [];
            _isAvailable = true;
            _isChecking = true;
            _statusText = "Checking spelling...";
        }
    }

    /// <summary>
    /// Accepts a checker result only when it still represents the pending editor version.
    /// </summary>
    /// <param name="result">The validated bounded checker result.</param>
    /// <returns><see langword="true"/> when the result became current.</returns>
    internal bool TryComplete(SpellCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (result.DocumentVersion != _documentVersion)
            {
                return false;
            }

            _issues = result.Issues;
            _isAvailable = true;
            _isChecking = false;
            _statusText = result.Issues.Length switch
            {
                0 => $"Spelling checked with {result.CheckerVersion}: no possible misspellings.",
                1 => $"Spelling checked with {result.CheckerVersion}: 1 possible misspelling.",
                _ => $"Spelling checked with {result.CheckerVersion}: {result.Issues.Length} possible misspellings.",
            };
            return true;
        }
    }

    /// <summary>
    /// Disables checking with an actionable explanation for one still-current version.
    /// </summary>
    /// <param name="documentVersion">The editor version whose check failed.</param>
    /// <param name="reason">The control-safe failure or availability explanation.</param>
    /// <returns><see langword="true"/> when the failure became current.</returns>
    internal bool TryDisable(long documentVersion, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (documentVersion != _documentVersion)
            {
                return false;
            }

            _issues = [];
            _isAvailable = false;
            _isChecking = false;
            _statusText = $"Spelling is off: {reason}";
            return true;
        }
    }

    /// <summary>
    /// Clears accepted issues after the editor replaces its complete document.
    /// </summary>
    /// <param name="documentVersion">The replacement document version.</param>
    internal void Clear(long documentVersion)
    {
        lock (_gate)
        {
            _documentVersion = documentVersion;
            _issues = [];
            _isChecking = false;
            if (_isAvailable)
            {
                _statusText = "Spelling has not run yet.";
            }
        }
    }

    /// <summary>
    /// Gets issues only when they represent the supplied current document version.
    /// </summary>
    /// <param name="documentVersion">The document version being rendered.</param>
    /// <returns>The matching immutable issue set, or an empty set for stale results.</returns>
    internal ImmutableArray<SpellingIssue> GetIssues(long documentVersion)
    {
        lock (_gate)
        {
            return documentVersion == _documentVersion ? _issues : [];
        }
    }
}
