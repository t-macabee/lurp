using System.Text.Json.Nodes;

namespace Lurp.Tests;

public sealed class PerfGeneratorTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("lurp-perf-gen-").FullName;

    public void Dispose()
    {
        TestDatabase.DeleteDirectory(_temp);
        GC.SuppressFinalize(this);
    }

    internal static (int Code, string Stdout, string Stderr) Generate(string path, int size = 1, int seed = 1)
        => TestProcess.Run("dotnet", [Path.Combine(AppContext.BaseDirectory, "gen.dll"),
            $"--out={path}", $"--size={size}", $"--seed={seed}"], TimeSpan.FromMinutes(2));

    [Theory]
    [InlineData(1, 5, 250)]
    [InlineData(4, 20, 1000)]
    [InlineData(16, 80, 4000)]
    public void FixtureCountsMatchContract(int size, int projects, int documents)
    {
        var result = Generate(_temp, size);
        Assert.True(result.Code == 0, result.Stderr);

        var directories = Directory.GetDirectories(_temp, "Perf.P*");
        Assert.Equal(projects, directories.Length);
        Assert.All(directories, path => Assert.Equal(50, Directory.GetFiles(path, "*.cs").Length));
        Assert.Equal(documents, Directory.GetFiles(_temp, "*.cs", SearchOption.AllDirectories).Length);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_temp, "perf-manifest.json")))!;
        Assert.Equal(size, (int?)manifest["size"]);
        Assert.Equal(1, (int?)manifest["seed"]);
        Assert.Equal(projects, (int?)manifest["projects"]);
        Assert.Equal(documents, (int?)manifest["documents"]);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(1, 2)]
    public void OtherSizeOrSeed_IsRefused(int size, int seed)
    {
        Assert.Equal(0, Generate(_temp).Code);

        var result = Generate(_temp, size, seed);

        Assert.Equal(1, result.Code);
        Assert.Contains("Remove the directory", result.Stderr);
        Assert.Contains($"size={size}, seed={seed}", result.Stderr);
        Assert.Equal(5, Directory.GetDirectories(_temp, "Perf.P*").Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrUnreadableManifest_IsRefused(bool corrupt)
    {
        File.WriteAllText(Path.Combine(_temp, ".lurp-perf-gen"), "lurp-perf-gen 1.1.0\n");
        if (corrupt)
            File.WriteAllText(Path.Combine(_temp, "perf-manifest.json"), "{bad JSON");

        var result = Generate(_temp);

        Assert.Equal(1, result.Code);
        Assert.Contains("perf-manifest.json", result.Stderr);
        Assert.Contains("Remove the directory", result.Stderr);
    }

    [Fact]
    public void SameSizeAndSeed_RewritesIdenticalFiles()
    {
        Assert.Equal(0, Generate(_temp).Code);
        var initial = ReadFiles(_temp);

        Assert.Equal(0, Generate(_temp).Code);
        AssertSameFiles(initial, ReadFiles(_temp));
    }

    [Fact]
    public void SeedControlsGeneratedContent()
    {
        var first = Path.Combine(_temp, "first");
        var same = Path.Combine(_temp, "same");
        var different = Path.Combine(_temp, "different");
        Assert.Equal(0, Generate(first).Code);
        Assert.Equal(0, Generate(same).Code);
        Assert.Equal(0, Generate(different, seed: 2).Code);

        var initial = ReadFiles(first);
        AssertSameFiles(initial, ReadFiles(same));
        var changed = ReadFiles(different);
        Assert.Contains(initial, entry =>
            Path.GetFileName(entry.Key).StartsWith("Unit", StringComparison.Ordinal) &&
            !entry.Value.AsSpan().SequenceEqual(changed[entry.Key]));
    }

    [Fact]
    public void GenOutUnderAnotherCheckout_IsRefused()
    {
        File.WriteAllText(Path.Combine(_temp, "Lurp.slnx"), "");

        var result = Generate(Path.Combine(_temp, "sub", "out"));

        Assert.Equal(1, result.Code);
        Assert.Contains($"inside the Lurp repo at '{_temp}'", result.Stderr);
    }

    private static Dictionary<string, byte[]> ReadFiles(string path)
        => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(path, file), File.ReadAllBytes);

    private static void AssertSameFiles(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected)
            Assert.Equal(bytes, actual[path]);
    }
}
