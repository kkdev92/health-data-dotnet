namespace Kkdev92.HealthData.Tests;

/// <summary>Locates the repository root from the test output directory.</summary>
internal static class RepositoryRoot
{
    /// <summary>The absolute path of the repository root.</summary>
    public static string Value { get; } = Locate();

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HealthData.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate HealthData.slnx above '{AppContext.BaseDirectory}'.");
    }
}
