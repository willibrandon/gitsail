namespace GitSail.Testing;

/// <summary>
/// Provides checked collection and type assertions shared by MSTest suites.
/// </summary>
internal static class TestSeq
{
    /// <summary>
    /// Asserts deep sequence equality.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="expected">The expected sequence.</param>
    /// <param name="actual">The actual sequence.</param>
    internal static void AreEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        => CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());

    /// <summary>
    /// Asserts that a sequence contains exactly one item and returns it.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="values">The sequence to inspect.</param>
    /// <returns>The only item.</returns>
    internal static T Single<T>(IEnumerable<T> values)
    {
        var materialized = values.Take(2).ToArray();
        Assert.HasCount(1, materialized);
        return materialized[0];
    }

    /// <summary>
    /// Asserts the runtime type and returns the checked value.
    /// </summary>
    /// <typeparam name="T">The required runtime type.</typeparam>
    /// <param name="value">The value to inspect.</param>
    /// <returns>The checked value.</returns>
    internal static T IsType<T>(object? value)
    {
        Assert.IsInstanceOfType<T>(value);
        return (T)value;
    }
}
