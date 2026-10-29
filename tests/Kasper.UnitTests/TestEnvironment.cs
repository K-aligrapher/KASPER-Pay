using System.Runtime.CompilerServices;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kasper.UnitTests;

internal static class TestEnvironment
{
    public static readonly string Home = Path.Combine(Path.GetTempPath(), "kasper-tests", Guid.NewGuid().ToString("N"));
    public static readonly string SaveFolder = Path.Combine(Home, "saved");

    /// <summary>
    /// Point all KASPER data and the default save folder at a temp directory before anything
    /// reads them, so tests never touch real user data. Mock tools on, fast retries.
    /// </summary>
    [ModuleInitializer]
    public static void Init()
    {
        Environment.SetEnvironmentVariable("KASPER_HOME", Home);
        Environment.SetEnvironmentVariable("KASPER_DEFAULT_SAVE_FOLDER", SaveFolder);
        Environment.SetEnvironmentVariable("KASPER__Runtime__EnableMockTools", "true");
        Environment.SetEnvironmentVariable("KASPER__Runtime__RetryBaseDelayMs", "10");
    }
}
