namespace NugetManager.Tests;

public class StartupDirectoryTests
{
    [Fact]
    public void NoArgumentUsesCurrentWorkingDirectory() =>
        Assert.Equal(Environment.CurrentDirectory, Program.ResolveStartupDirectory([]));

    [Fact]
    public void ExplicitDirectoryOverridesCurrentWorkingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NugetManager startup workspace");
        Assert.Equal(directory, Program.ResolveStartupDirectory([directory]));
    }

    [Theory]
    [InlineData(".")]
    [InlineData(@"..\NugetManager startup workspace")]
    public void RelativeArgumentIsResolvedAgainstCurrentWorkingDirectory(string argument) =>
        Assert.Equal(Path.GetFullPath(argument, Environment.CurrentDirectory),
            Program.ResolveStartupDirectory([argument]));
}
