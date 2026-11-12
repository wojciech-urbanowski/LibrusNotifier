namespace LibrusNotifier.Tests;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void DefaultsToScheduledRunInExecutableDirectory()
    {
        var options = CommandLineOptions.Parse([]);
        Assert.False(options.RunOnce);
        Assert.Null(options.Directory);
    }

    [Fact]
    public void ParsesRunOnceWithDirectoryInAnyOrder()
    {
        Assert.Equal(new CommandLineOptions(true, "/opt/librus", false), CommandLineOptions.Parse(["--katalog", "/opt/librus", "--run-once"]));
        Assert.Equal(new CommandLineOptions(true, "C:\\Librus", false), CommandLineOptions.Parse(["--run-once", "--katalog", "C:\\Librus"]));
    }

    [Theory]
    [InlineData("--katalog")]
    [InlineData("--katalog", "--run-once")]
    [InlineData("--run-once", "--run-once")]
    [InlineData("--katalog", "a", "--katalog", "b")]
    [InlineData("--nieznany")]
    public void RejectsInvalidArguments(params string[] args) =>
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(args));

    [Fact]
    public void AppPathsUseApplicationDirectory()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "librus-app"));
        Assert.EndsWith(Path.Combine("librus-app", "librus_config.txt"), paths.ConfigFile);
        Assert.EndsWith(Path.Combine("librus-app", "data", "librus.db"), paths.DatabaseFile);
        Assert.EndsWith(Path.Combine("librus-app", "data", "logs"), paths.LogDirectory);
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(AppPaths.DefaultAppDirectory));
    }
}
