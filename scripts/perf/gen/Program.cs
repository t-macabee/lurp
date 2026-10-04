using System.Globalization;
using System.Text;

// lurp-perf-gen: deterministic generator for the synthetic C# solutions the Lurp
// performance harness measures (audit B3, Phase 8 step 2). Every size writes
// exactly 50 .cs files per project and the same per-document shape, so time and
// memory growth between sizes reflects Lurp, not the fixture. The same
// (size, seed) pair always writes byte-identical files. Output must live outside
// the Lurp repository; the generator never deletes anything.

const int FilesPerProject = 50;
const int UnitFileCount = FilesPerProject - 1;
const int NamespaceCount = 4;
const int TypesPerFile = 3;
const int MethodsPerType = 1;
const int FanOut = 2;
const string GeneratorName = "lurp-perf-gen";
const string GeneratorVersion = "1.1.0";
const string MarkerFileName = ".lurp-perf-gen";
const string MarkerContent = "lurp-perf-gen 1.1.0\n";

const string OutputDirectoryBuildProps =
    "<Project>\n" +
    "  <PropertyGroup>\n" +
    "    <EnableNETAnalyzers>false</EnableNETAnalyzers>\n" +
    "    <AnalysisLevel>none</AnalysisLevel>\n" +
    "    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>\n" +
    "    <Nullable>disable</Nullable>\n" +
    "    <ImplicitUsings>enable</ImplicitUsings>\n" +
    "    <NuGetAudit>false</NuGetAudit>\n" +
    "  </PropertyGroup>\n" +
    "</Project>\n";

string? sizeRaw = null;
string? outRaw = null;
var seed = 1;

foreach (var arg in args)
{
    if (arg.StartsWith("--size=", StringComparison.Ordinal))
        sizeRaw = arg["--size=".Length..];
    else if (arg.StartsWith("--out=", StringComparison.Ordinal))
        outRaw = arg["--out=".Length..];
    else if (arg.StartsWith("--seed=", StringComparison.Ordinal))
    {
        var raw = arg["--seed=".Length..];
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
            return Fail($"--seed must be an integer, got '{raw}'.");
    }
    else
    {
        return Fail($"unknown argument '{arg}'.");
    }
}

if (sizeRaw is null)
    return Fail("--size=<1|4|16> is required.");

if (string.IsNullOrEmpty(outRaw))
    return Fail("--out=<dir> is required.");

var projectCount = sizeRaw switch { "1" => 5, "4" => 20, "16" => 80, _ => 0 };
if (projectCount == 0)
    return Fail($"--size must be 1, 4 or 16, got '{sizeRaw}'.");

string outDir;
try
{
    outDir = Path.GetFullPath(outRaw);
}
catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
{
    return Fail($"--out is not a valid path: '{outRaw}'.");
}

var repoRoot = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Directory.GetCurrentDirectory());
if (repoRoot is not null && IsInside(outDir, repoRoot))
    return Fail($"--out '{outDir}' is inside the Lurp repo at '{repoRoot}'. Generated output must stay outside the repo tree.");

var outRepoRoot = FindRepoRoot(outDir);
if (outRepoRoot is not null)
    return Fail($"--out '{outDir}' is inside the Lurp repo at '{outRepoRoot}'. Generated output must stay outside the repo tree.");

var markerPath = Path.Combine(outDir, MarkerFileName);
if (Directory.Exists(outDir))
{
    var hasEntries = Directory.EnumerateFileSystemEntries(outDir).Any();
    if (hasEntries)
    {
        if (!File.Exists(markerPath))
            return Fail($"--out '{outDir}' is not empty and has no {MarkerFileName} marker. Remove that directory yourself; the generator never deletes.");

        var markerText = File.ReadAllText(markerPath);
        if (!markerText.StartsWith(GeneratorName, StringComparison.Ordinal))
            return Fail($"{markerPath} was not written by {GeneratorName}. Remove the directory yourself; the generator never deletes.");
    }
}

Directory.CreateDirectory(outDir);
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
File.WriteAllText(markerPath, MarkerContent, utf8);
File.WriteAllText(Path.Combine(outDir, "Directory.Build.props"), OutputDirectoryBuildProps, utf8);
File.WriteAllText(Path.Combine(outDir, "Perf.slnx"), BuildSolutionFile(projectCount), utf8);

for (var project = 0; project < projectCount; project++)
{
    var projectDir = Path.Combine(outDir, $"Perf.P{project}");
    Directory.CreateDirectory(projectDir);
    File.WriteAllText(Path.Combine(projectDir, $"Perf.P{project}.csproj"), BuildProjectFile(project), utf8);

    for (var file = 0; file < UnitFileCount; file++)
        File.WriteAllText(Path.Combine(projectDir, $"Unit{file:D2}.cs"), BuildUnitFile(project, file, seed), utf8);

    File.WriteAllText(Path.Combine(projectDir, "Composition.cs"), BuildCompositionFile(project), utf8);
}

File.WriteAllText(Path.Combine(outDir, "perf-manifest.json"), BuildManifest(projectCount, sizeRaw, seed), utf8);

Console.WriteLine($"Generated {projectCount} projects and {projectCount * FilesPerProject} documents into {outDir} (size={sizeRaw}, seed={seed}).");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine("ERROR: " + message);
    return 1;
}

static string? FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Lurp.slnx")))
            return dir.FullName;

        dir = dir.Parent;
    }

    return null;
}

static bool IsInside(string candidate, string root)
{
    var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
    var rootNormalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    if (string.Equals(normalized, rootNormalized, StringComparison.OrdinalIgnoreCase))
        return true;

    return normalized.StartsWith(rootNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

static string BuildSolutionFile(int projectCount)
{
    var sb = new StringBuilder();
    sb.Append("<Solution>\n");
    for (var project = 0; project < projectCount; project++)
        sb.Append("  <Project Path=\"Perf.P").Append(project).Append("/Perf.P").Append(project).Append(".csproj\" />\n");
    sb.Append("</Solution>\n");
    return sb.ToString();
}

static string BuildProjectFile(int project)
{
    var sb = new StringBuilder();
    sb.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
    sb.Append("  <PropertyGroup>\n");
    sb.Append("    <TargetFramework>net10.0</TargetFramework>\n");
    sb.Append("  </PropertyGroup>\n");

    if (project > 0)
    {
        sb.Append("  <ItemGroup>\n");
        for (var reference = Math.Max(0, project - 2); reference < project; reference++)
            sb.Append("    <ProjectReference Include=\"..\\Perf.P").Append(reference).Append("\\Perf.P").Append(reference).Append(".csproj\" />\n");
        sb.Append("  </ItemGroup>\n");
    }

    sb.Append("</Project>\n");
    return sb.ToString();
}

static string TypeName((int Project, int File) target)
    => $"Perf.P{target.Project}.N{target.File % NamespaceCount}.A{target.Project}_{target.File}";

static int PickPreviousProject(int project, int file)
{
    if (project == 0)
        return 0;

    if (project == 1)
        return 0;

    return file % 2 == 0 ? project - 1 : project - 2;
}

static int PickCrossTargetFile(int file) => (file * 7 + 2) % UnitFileCount;

static string BuildUnitFile(int project, int file, int seed)
{
    var rng = new Random(unchecked(seed * 1_000_003 + project * 7_919 + file));
    var interfaceName = $"IContract{project}_{file}";
    var aName = $"A{project}_{file}";
    var bName = $"B{project}_{file}";
    var crossTarget = (Project: PickPreviousProject(project, file), File: PickCrossTargetFile(file));

    var sb = new StringBuilder();
    sb.Append("// Generated by ").Append(GeneratorName).Append(' ').Append(GeneratorVersion).Append(".\n");
    sb.Append("using System;\n\n");
    sb.Append("namespace Perf.P").Append(project).Append(".N").Append(file % NamespaceCount).Append(";\n\n");

    sb.Append("public interface ").Append(interfaceName).Append('\n');
    sb.Append("{\n");
    for (var method = 0; method < MethodsPerType; method++)
        sb.Append("    int Op").Append(method).Append("(int value);\n");
    sb.Append("}\n\n");

    sb.Append("public class ").Append(aName).Append(" : ").Append(interfaceName).Append('\n');
    sb.Append("{\n");
    sb.Append("    private int _state;\n");
    sb.Append("    private readonly ").Append(interfaceName).Append(" _peer = new ").Append(bName).Append("();\n");
    sb.Append("    private readonly ").Append(TypeName(crossTarget)).Append(" _remote = new ").Append(TypeName(crossTarget)).Append("();\n\n");
    sb.Append("    public int Depth { get; set; }\n\n");
    sb.Append("    public event Func<int, int> Updated;\n\n");
    sb.Append("    public ").Append(aName).Append("()\n");
    sb.Append("    {\n");
    sb.Append("        _state = ").Append(rng.Next(1, 1000)).Append(";\n");
    sb.Append("        Updated += Shift;\n");
    sb.Append("        Func<int, int> adjust = Shift;\n");
    sb.Append("        Depth = adjust(_state);\n");
    sb.Append("    }\n\n");
    sb.Append("    public static int Shift(int value)\n");
    sb.Append("    {\n");
    sb.Append("        return value + ").Append(rng.Next(1, 100)).Append(";\n");
    sb.Append("    }\n\n");

    for (var method = 0; method < MethodsPerType; method++)
    {
        var next = (method + 1) % MethodsPerType;
        sb.Append("    public virtual int Op").Append(method).Append("(int value)\n");
        sb.Append("    {\n");
        sb.Append("        int a = _peer.Op").Append(method).Append("(value);\n");
        sb.Append("        int b = _remote.Op").Append(next).Append("(value);\n");
        sb.Append("        return a + b;\n");
        sb.Append("    }\n\n");
    }

    sb.Append("}\n\n");

    sb.Append("public class ").Append(bName).Append(" : ").Append(aName).Append(", ").Append(interfaceName).Append('\n');
    sb.Append("{\n");
    sb.Append("    private int _state;\n");
    sb.Append("    private readonly ").Append(interfaceName).Append(" _peer = new ").Append(aName).Append("();\n");
    sb.Append("    private readonly ").Append(TypeName(crossTarget)).Append(" _remote = new ").Append(TypeName(crossTarget)).Append("();\n\n");
    sb.Append("    public int Level { get; set; }\n\n");
    sb.Append("    public event Action<int> Changed;\n\n");
    sb.Append("    public ").Append(bName).Append("()\n");
    sb.Append("    {\n");
    sb.Append("        _state = ").Append(rng.Next(1, 1000)).Append(";\n");
    sb.Append("        Changed += HandleChanged;\n");
    sb.Append("    }\n\n");
    sb.Append("    public void HandleChanged(int value)\n");
    sb.Append("    {\n");
    sb.Append("        _state = _state + value;\n");
    sb.Append("    }\n\n");

    for (var method = 0; method < MethodsPerType; method++)
    {
        var next = (method + 1) % MethodsPerType;
        sb.Append("    public override int Op").Append(method).Append("(int value)\n");
        sb.Append("    {\n");
        sb.Append("        int a = _peer.Op").Append(next).Append("(value);\n");
        sb.Append("        int b = _remote.Op").Append(method).Append("(value);\n");
        sb.Append("        return a + b;\n");
        sb.Append("    }\n\n");
    }

    sb.Append("}\n");
    return sb.ToString();
}

static string BuildCompositionFile(int project)
{
    var sb = new StringBuilder();
    sb.Append("// Generated by ").Append(GeneratorName).Append(' ').Append(GeneratorVersion).Append(".\n");
    sb.Append("using Microsoft.Extensions.DependencyInjection;\n");
    for (var ns = 0; ns < NamespaceCount; ns++)
        sb.Append("using Perf.P").Append(project).Append(".N").Append(ns).Append(";\n");
    sb.Append("using System;\n\n");

    if (project == 0)
    {
        sb.Append("namespace Microsoft.Extensions.DependencyInjection\n");
        sb.Append("{\n");
        sb.Append("    public interface IServiceCollection\n");
        sb.Append("    {\n");
        sb.Append("    }\n\n");
        sb.Append("    public static class ServiceCollectionServiceExtensions\n");
        sb.Append("    {\n");
        sb.Append("        public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services)\n");
        sb.Append("            where TService : class\n");
        sb.Append("            where TImplementation : class, TService\n");
        sb.Append("        {\n");
        sb.Append("            return services;\n");
        sb.Append("        }\n");
        sb.Append("    }\n");
        sb.Append("}\n\n");
    }

    sb.Append("namespace Perf.P").Append(project).Append('\n');
    sb.Append("{\n");
    sb.Append("    public static class Composition").Append(project).Append('\n');
    sb.Append("    {\n");
    sb.Append("        public static IServiceCollection Register(IServiceCollection services)\n");
    sb.Append("        {\n");

    for (var i = 0; i < 4; i++)
    {
        var file = i * 3 + project % 3;
        var impl = i % 2 == 0 ? $"A{project}_{file}" : $"B{project}_{file}";
        sb.Append("            services.AddTransient<IContract").Append(project).Append('_').Append(file).Append(", ").Append(impl).Append(">();\n");
    }

    sb.Append("            return services;\n");
    sb.Append("        }\n");
    sb.Append("    }\n");
    sb.Append("}\n");
    return sb.ToString();
}

static string BuildManifest(int projectCount, string sizeRaw, int seed)
{
    var (upstreamWide, downstreamWide, middle, editDocument) = BuildAnchors(projectCount);
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"generator\": \"").Append(GeneratorName).Append("\",\n");
    sb.Append("  \"generator_version\": \"").Append(GeneratorVersion).Append("\",\n");
    sb.Append("  \"size\": ").Append(sizeRaw).Append(",\n");
    sb.Append("  \"seed\": ").Append(seed).Append(",\n");
    sb.Append("  \"projects\": ").Append(projectCount).Append(",\n");
    sb.Append("  \"documents\": ").Append(projectCount * FilesPerProject).Append(",\n");
    sb.Append("  \"files_per_project\": ").Append(FilesPerProject).Append(",\n");
    sb.Append("  \"types_per_file\": ").Append(TypesPerFile).Append(",\n");
    sb.Append("  \"methods_per_type\": ").Append(MethodsPerType).Append(",\n");
    sb.Append("  \"fan_out\": ").Append(FanOut).Append(",\n");
    sb.Append("  \"anchors\": {\n");
    sb.Append("    \"upstream_wide\": \"").Append(upstreamWide).Append("\",\n");
    sb.Append("    \"downstream_wide\": \"").Append(downstreamWide).Append("\",\n");
    sb.Append("    \"middle\": \"").Append(middle).Append("\",\n");
    sb.Append("    \"edit_document\": \"").Append(editDocument).Append("\"\n");
    sb.Append("  }\n");
    sb.Append("}\n");
    return sb.ToString();
}

static (string UpstreamWide, string DownstreamWide, string Middle, string EditDocument) BuildAnchors(int projectCount)
{
    var upstreamFile = 0;
    var upstreamProjects = -1;
    for (var file0 = 0; file0 < UnitFileCount; file0++)
    {
        var callerProjects = 0;
        for (var project = 0; project < projectCount; project++)
        {
            var called = false;
            for (var file = 0; file < UnitFileCount && !called; file++)
                called = PickPreviousProject(project, file) == 0 && PickCrossTargetFile(file) == file0;

            if (called)
                callerProjects++;
        }

        if (callerProjects > upstreamProjects)
        {
            upstreamProjects = callerProjects;
            upstreamFile = file0;
        }
    }

    var lastProject = projectCount - 1;
    var middleProject = projectCount / 2;
    return (
        $"Perf.P0.N{upstreamFile % NamespaceCount}.A0_{upstreamFile}.Op0",
        $"Perf.P{lastProject}.N0.A{lastProject}_0.Op0",
        $"Perf.P{middleProject}.N0.A{middleProject}_0.Op0",
        $"Perf.P{lastProject}/Unit00.cs");
}
