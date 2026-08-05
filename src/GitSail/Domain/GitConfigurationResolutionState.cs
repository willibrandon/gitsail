namespace GitSail.Domain;

/// <summary>
/// Identifies how a registered value is supplied at one selected configuration scope.
/// </summary>
internal enum GitConfigurationResolutionState
{
    /// <summary>
    /// Identifies an absent value for which only the registered default may apply.
    /// </summary>
    Absent,

    /// <summary>
    /// Identifies a valid nonempty value inherited from another scope.
    /// </summary>
    Inherited,

    /// <summary>
    /// Identifies an empty value inherited from another scope.
    /// </summary>
    InheritedEmpty,

    /// <summary>
    /// Identifies an invalid value inherited from another scope.
    /// </summary>
    InheritedInvalid,

    /// <summary>
    /// Identifies a valid nonempty value explicitly set at the selected scope.
    /// </summary>
    Explicit,

    /// <summary>
    /// Identifies an empty value explicitly set at the selected scope.
    /// </summary>
    ExplicitEmpty,

    /// <summary>
    /// Identifies an invalid value explicitly set at the selected scope.
    /// </summary>
    ExplicitInvalid,
}
