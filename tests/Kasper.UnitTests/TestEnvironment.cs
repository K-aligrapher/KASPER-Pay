using System.Runtime.CompilerServices;

namespace Kasper.UnitTests;

internal static class TestEnvironment
{
    public static readonly string Home = Path.Combine(Path.GetTempPath(), "kasper-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Point KasperPaths at a temp folder before anything reads it, so tests never touch real user data.</summary>
    [ModuleInitializer]
    public static void Init() => Environment.SetEnvironmentVariable("KASPER_HOME", Home);
}
