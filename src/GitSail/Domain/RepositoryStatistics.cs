namespace GitSail.Domain;

/// <summary>
/// Contains one bounded snapshot of Git object-database storage statistics.
/// </summary>
/// <param name="LooseObjectCount">The number of loose objects.</param>
/// <param name="LooseObjectSizeKiB">The loose-object size in kibibytes.</param>
/// <param name="PackedObjectCount">The number of objects stored in pack files.</param>
/// <param name="PackCount">The number of pack files.</param>
/// <param name="PackSizeKiB">The pack-file size in kibibytes.</param>
/// <param name="PrunePackableObjectCount">The number of loose objects duplicated in pack files.</param>
/// <param name="GarbageFileCount">The number of unrecognized object-database files.</param>
/// <param name="GarbageSizeKiB">The unrecognized-file size in kibibytes.</param>
/// <param name="AlternateObjectDatabaseCount">The number of configured alternate object databases.</param>
internal sealed record RepositoryStatistics(
    long LooseObjectCount,
    long LooseObjectSizeKiB,
    long PackedObjectCount,
    long PackCount,
    long PackSizeKiB,
    long PrunePackableObjectCount,
    long GarbageFileCount,
    long GarbageSizeKiB,
    int AlternateObjectDatabaseCount);
