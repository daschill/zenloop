using ZenLoop.Core;

static int Usage()
{
    Console.Error.WriteLine(
        """
        ZenLoop UV bake-off host (offline report; no GPU required)

          build --stock <bench.json> --zenloop <bench.json> [--adrenalin <bench.json>]
                [--zenloop-profile <gpu.json>] [--adrenalin-profile <path>]
                [--goal balanced] [--out <dir>]

          import-adrenalin <path>   Print imported GpuProfile JSON or fail

        See docs/UV-BAKEOFF.md
        """);
    return 2;
}

if (args.Length == 0) return Usage();

var cmd = args[0].ToLowerInvariant();
if (cmd is "help" or "-h" or "--help") return Usage();

if (cmd == "import-adrenalin")
{
    if (args.Length < 2) return Usage();
    var profile = AdrenalinUvBakeOff.TryImportAdrenalinProfile(args[1]);
    if (profile is null)
    {
        Console.Error.WriteLine("Could not import Adrenalin/ZenLoop GPU profile (unsupported or missing fields).");
        return 1;
    }
    Console.WriteLine(profile.ToJson());
    return 0;
}

if (cmd != "build") return Usage();

string? GetOpt(string name)
{
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

var stock = GetOpt("--stock");
var zen = GetOpt("--zenloop");
if (string.IsNullOrWhiteSpace(stock) || string.IsNullOrWhiteSpace(zen))
{
    Console.Error.WriteLine("build requires --stock and --zenloop bench JSON paths.");
    return Usage();
}

var report = AdrenalinUvBakeOff.BuildFromBenchFiles(
    stockBenchPath: stock,
    zenLoopBenchPath: zen,
    adrenalinBenchPath: GetOpt("--adrenalin"),
    zenLoopProfilePath: GetOpt("--zenloop-profile"),
    adrenalinProfilePath: GetOpt("--adrenalin-profile"),
    goal: GetOpt("--goal"),
    notes: GetOpt("--notes"));

var outDir = GetOpt("--out") ?? AdrenalinUvBakeOff.DefaultDirectory;
var path = AdrenalinUvBakeOff.Write(report, outDir);
Console.WriteLine(path);
Console.WriteLine(Path.Combine(outDir, AdrenalinUvBakeOff.MarkdownFileName));
if (report.ZenLoopVsStock is not null)
    Console.WriteLine("ZenLoop vs stock: " + report.ZenLoopVsStock.Summary);
if (report.WinnerByScore is not null)
    Console.WriteLine("Winner (score): " + report.WinnerByScore);
return 0;
