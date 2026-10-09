using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocaUltrator.Core;

public sealed class GameEngineDetection
{
    public string Engine { get; set; } = "Ismeretlen";
    public double Confidence { get; set; }
    public List<string> Evidence { get; set; } = [];
    public List<string> LooseLocalizationFiles { get; set; } = [];
    public List<string> ArchiveFiles { get; set; } = [];
    public string Recommendation { get; set; } = string.Empty;
}

public sealed class GameToolManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public List<string> Engines { get; set; } = [];
    public string Homepage { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public List<GameToolOperation> Operations { get; set; } = [];
}

public sealed class GameToolOperation
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 900;
    public string ExpectedOutputRelativePath { get; set; } = string.Empty;
}

public sealed class GameToolRunResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public string ExpectedOutputPath { get; set; } = string.Empty;
    public string ReportPath { get; set; } = string.Empty;
}

public sealed class GameIntegrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public GameEngineDetection Detect(string gameRoot)
    {
        string root = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("A játék mappája nem található: " + root);
        var detection = new GameEngineDetection();
        string[] topFiles = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).ToArray();
        string[] topDirectories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray();

        if (topFiles.Any(path => Path.GetFileName(path).Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)) ||
            topDirectories.Any(path => Path.GetFileName(path).EndsWith("_Data", StringComparison.OrdinalIgnoreCase)))
        {
            detection.Engine = "Unity"; detection.Confidence = 0.95;
            detection.Evidence.Add("UnityPlayer.dll vagy *_Data mappa található.");
        }
        else if (Directory.Exists(Path.Combine(root, "Engine")) || FindFiles(root, "*.pak", 2, 30).Count > 0)
        {
            detection.Engine = "Unreal Engine"; detection.Confidence = 0.88;
            detection.Evidence.Add("Engine mappa vagy .pak archívum található.");
        }
        else if (Directory.Exists(Path.Combine(root, "renpy")) || topFiles.Any(path => path.EndsWith(".rpa", StringComparison.OrdinalIgnoreCase)))
        {
            detection.Engine = "Ren'Py"; detection.Confidence = 0.95;
            detection.Evidence.Add("renpy mappa vagy .rpa archívum található.");
        }
        else if (topFiles.Any(path => path.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)))
        {
            detection.Engine = "Godot"; detection.Confidence = 0.9;
            detection.Evidence.Add("Godot .pck csomag található.");
        }

        detection.LooseLocalizationFiles.AddRange(FindLooseLocalizationFiles(root, 5, 5_000));
        detection.ArchiveFiles.AddRange(FindArchiveFiles(root, 4, 1_000));
        detection.Recommendation = detection.LooseLocalizationFiles.Count > 0
            ? "Először a laza szövegfájlokat importáld. Az archívumhoz csak ellenőrzött adaptert használj."
            : detection.ArchiveFiles.Count > 0
                ? "A szöveg archívumban lehet. Válassz a motorhoz illő, manifesttel rendelkező adaptert."
                : "Nem találtam biztonságosan, közvetlenül importálható szövegfájlt. Kicsomagolás vagy külön adapter szükséges.";
        return detection;
    }

    public IReadOnlyList<GameToolManifest> LoadManifests()
    {
        LocaPaths.EnsureCreated();
        var result = new List<GameToolManifest>();
        foreach (string path in Directory.EnumerateFiles(LocaPaths.Tools, "*.locatool.json", SearchOption.AllDirectories))
        {
            try
            {
                GameToolManifest? manifest = JsonSerializer.Deserialize<GameToolManifest>(File.ReadAllText(path), JsonOptions);
                if (manifest is not null && manifest.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(manifest.Id))
                {
                    if (!Path.IsPathRooted(manifest.ExecutablePath))
                        manifest.ExecutablePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, manifest.ExecutablePath));
                    result.Add(manifest);
                }
            }
            catch (JsonException) { }
        }
        return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<GameToolRunResult> RunAdapterAsync(
        GameToolManifest manifest,
        GameToolOperation operation,
        string gameRoot,
        string? translatedInputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifest.ExecutablePath)) throw new FileNotFoundException("Az adapter futtatható fájlja nem található.", manifest.ExecutablePath);
        string root = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        string workspace = Path.Combine(LocaPaths.Root, "Adapter-munkaterületek", LocaPaths.SafeName(manifest.Id), runId);
        Directory.CreateDirectory(workspace);
        string input = string.IsNullOrWhiteSpace(translatedInputPath) ? string.Empty : Path.GetFullPath(translatedInputPath);

        var start = new ProcessStartInfo
        {
            FileName = manifest.ExecutablePath,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in operation.Arguments)
            start.ArgumentList.Add(argument
                .Replace("{gameRoot}", root, StringComparison.Ordinal)
                .Replace("{workspace}", workspace, StringComparison.Ordinal)
                .Replace("{input}", input, StringComparison.Ordinal));

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Az adapter nem indítható.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(operation.TimeoutSeconds, 10, 7_200)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }

        string expected = string.IsNullOrWhiteSpace(operation.ExpectedOutputRelativePath) ? string.Empty :
            ResolveInside(workspace, operation.ExpectedOutputRelativePath);
        var result = new GameToolRunResult
        {
            Success = process.ExitCode == 0 && (expected.Length == 0 || File.Exists(expected) || Directory.Exists(expected)),
            ExitCode = process.ExitCode,
            StandardOutput = await stdout,
            StandardError = await stderr,
            WorkspacePath = workspace,
            ExpectedOutputPath = expected
        };
        result.ReportPath = Path.Combine(workspace, "run-report.json");
        await File.WriteAllTextAsync(result.ReportPath, JsonSerializer.Serialize(new
        {
            runId,
            utc = DateTime.UtcNow,
            manifest = new { manifest.Id, manifest.Name, manifest.Version, manifest.ExecutablePath },
            operation = operation.Id,
            gameRoot = root,
            input,
            result.Success,
            result.ExitCode,
            result.ExpectedOutputPath,
            executableSha256 = await HashFileAsync(manifest.ExecutablePath, cancellationToken),
            result.StandardOutput,
            result.StandardError
        }, JsonOptions), cancellationToken);
        return result;
    }

    private static List<string> FindLooseLocalizationFiles(string root, int maxDepth, int maxCount)
    {
        string[] extensions = [".txt", ".csv", ".tsv", ".jsonl", ".lutext", ".po", ".xml", ".json", ".ini", ".lang", ".yml", ".yaml", ".resx", ".locres"];
        return EnumerateBounded(root, maxDepth, maxCount)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<string> FindArchiveFiles(string root, int maxDepth, int maxCount)
    {
        string[] extensions = [".pak", ".pck", ".rpa", ".bundle", ".assets", ".resource", ".cpk", ".arc", ".big"];
        return EnumerateBounded(root, maxDepth, maxCount)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<string> FindFiles(string root, string pattern, int maxDepth, int maxCount) =>
        EnumerateBounded(root, maxDepth, maxCount).Where(path => Path.GetFileName(path).EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase)).ToList();

    private static IEnumerable<string> EnumerateBounded(string root, int maxDepth, int maxCount)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        int yielded = 0;
        while (queue.Count > 0 && yielded < maxCount)
        {
            (string folder, int depth) = queue.Dequeue();
            string[] files;
            try { files = Directory.GetFiles(folder); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (string file in files)
            {
                yield return file;
                if (++yielded >= maxCount) yield break;
            }
            if (depth >= maxDepth) continue;
            try
            {
                foreach (string child in Directory.GetDirectories(folder)) queue.Enqueue((child, depth + 1));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    private static string ResolveInside(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Az adapter kimeneti útvonala kilépne a munkaterületből.");
        return full;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
