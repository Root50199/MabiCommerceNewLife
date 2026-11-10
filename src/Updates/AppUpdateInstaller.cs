using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiCommerceNewLife;

public static class AppUpdateInstaller
{
    public const string ExecutableName = "MabiCommerceNewLife.exe";
    private static readonly string[] RequiredFiles =
        [ExecutableName, @"Data\commerce-catalog.json", @"x64\tesseract55.dll", @"x64\leptonica-1.85.0.dll", "LICENSE", "THIRD-PARTY-NOTICES.txt"];

    public static IReadOnlyList<string> ExtractPackage(string archivePath, string destination)
    {
        if (Directory.Exists(destination)) throw new IOException("Update staging destination already exists.");
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count is 0 or > 4096) throw new InvalidDataException("Invalid update package file count.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedSize = 0;
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.Replace('/', '\\');
            const string prefix = @"MabiCommerceNewLife\";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Update package has an unexpected root directory.");
            var relative = path[prefix.Length..];
            if (relative.Length == 0 || relative.EndsWith('\\')) continue;
            ValidatePackageFile(relative);
            // Unix symlinks are not regular package files.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !files.Add(relative))
                throw new InvalidDataException("Update package contains a duplicate path or symbolic link.");
            expandedSize = checked(expandedSize + entry.Length);
            if (expandedSize > 1024L * 1024 * 1024) throw new InvalidDataException("Expanded update package is too large.");
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("Update file exceeds its declared size.");
                output.Write(buffer, 0, count);
            }
            if (written != entry.Length) throw new InvalidDataException("Update file is truncated.");
        }
        if (RequiredFiles.Any(required => !files.Contains(required)))
            throw new InvalidDataException("Update package is incomplete. Nothing was installed.");
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void ValidatePackageFile(string path)
    {
        if (path.Length == 0 || path.Contains('/') || path.Split('\\').Any(part =>
                part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Unsafe path in update package.");
        if (path != ExecutableName && path != "LICENSE" && path != "THIRD-PARTY-NOTICES.txt" &&
            !path.StartsWith(@"Data\", StringComparison.Ordinal) && !path.StartsWith(@"x64\", StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected update file: {path}. Local settings/history cannot be overwritten.");
    }

    public static void ValidateTarget(string directory, IReadOnlyList<string> files)
    {
        var fullPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (fullPath.Length <= 3 || !File.Exists(Path.Combine(fullPath, ExecutableName)) ||
            File.Exists(Path.Combine(fullPath, "MabiCommerceNewLife.csproj")))
            throw new IOException("Run updates from a writable app output/extracted release folder, not the source root.");
        foreach (var file in files)
        {
            ValidatePackageFile(file);
            for (var node = new DirectoryInfo(Path.GetDirectoryName(Path.Combine(fullPath, file))!); node is not null; node = node.Parent)
                if (node.Exists && node.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Updates cannot install through linked directories.");
            var target = Path.Combine(fullPath, file);
            if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Updates cannot replace linked files.");
        }
        var probe = Path.Combine(fullPath, $".update-write-test-{Guid.NewGuid():N}");
        using (File.Create(probe)) { }
        File.Delete(probe);
    }

    public static async Task StartHelperAsync(string workspace, string target, IReadOnlyList<string> files)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "MabiCommerceUpdates");
        if (!Path.GetDirectoryName(Path.GetFullPath(workspace))!.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(workspace), "N", out _))
            throw new IOException("Update staging must use a unique updater-owned temporary directory.");
        ValidateTarget(target, files);
        var scriptPath = Path.Combine(workspace, "ApplyUpdate.ps1");
        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("MabiCommerceNewLife.ApplyUpdate.ps1")
            ?? throw new InvalidOperationException("The update helper is missing."))
        using (var output = File.Create(scriptPath))
            resource.CopyTo(output);
        var planPath = Path.Combine(workspace, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(new
        {
            Target = Path.GetFullPath(target), ParentId = Environment.ProcessId, Files = files
        }));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-PlanPath", planPath })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("The update helper could not start.");
        var ready = Path.Combine(workspace, "ready");
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(ready))
        {
            if (helper.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(15))
                throw new IOException("The update helper did not become ready. No installation was authorized.");
            await Task.Delay(100);
        }
        File.WriteAllText(Path.Combine(workspace, "install"), string.Empty);
    }
}
