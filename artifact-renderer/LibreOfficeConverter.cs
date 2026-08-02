using System.Diagnostics;

namespace AgentHub.ArtifactRenderer;

public sealed class ConversionException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class RendererOptions
{
    public string WorkRoot { get; init; } = "/tmp/render";
    public long MaxInputBytes { get; init; } = 50L * 1024 * 1024;
    public long MaxOutputBytes { get; init; } = 50L * 1024 * 1024;
    public int TimeoutSeconds { get; init; } = 90;
}

public sealed class LibreOfficeConverter(RendererOptions options)
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".docx", ".pptx", ".xlsx" };

    public static bool IsSupported(string extension) => Extensions.Contains(extension);

    public static ProcessStartInfo BuildCommand(
        string inputPath, string outputDirectory, string profileDirectory)
    {
        var command = new ProcessStartInfo("soffice")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var normalizedProfile = profileDirectory.Replace('\\', '/');
        var profileUri = normalizedProfile.StartsWith('/') ? new Uri("file://" + normalizedProfile).AbsoluteUri : new Uri(Path.GetFullPath(profileDirectory)).AbsoluteUri;
        command.ArgumentList.Add("--headless");
        command.ArgumentList.Add($"-env:UserInstallation={profileUri}");
        command.ArgumentList.Add("--convert-to");
        command.ArgumentList.Add("pdf");
        command.ArgumentList.Add("--outdir");
        command.ArgumentList.Add(outputDirectory);
        command.ArgumentList.Add(inputPath);
        return command;
    }

    public async Task<byte[]> ConvertAsync(
        Stream source, string extension, CancellationToken ct = default)
    {
        extension = extension.ToLowerInvariant();
        if (!IsSupported(extension)) throw new ConversionException("unsupported_file_type");

        var root = Path.GetFullPath(options.WorkRoot);
        Directory.CreateDirectory(root);
        var requestDirectory = Path.Combine(root, Guid.NewGuid().ToString("n"));
        var resolvedRequest = Path.GetFullPath(requestDirectory);
        if (!resolvedRequest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ConversionException("invalid_work_directory");

        Directory.CreateDirectory(resolvedRequest);
        var inputDirectory = Directory.CreateDirectory(Path.Combine(resolvedRequest, "in")).FullName;
        var outputDirectory = Directory.CreateDirectory(Path.Combine(resolvedRequest, "out")).FullName;
        var profileDirectory = Directory.CreateDirectory(Path.Combine(resolvedRequest, "profile")).FullName;
        var inputPath = Path.Combine(inputDirectory, "source" + extension);

        try
        {
            await CopyBoundedAsync(source, inputPath, options.MaxInputBytes, ct);
            using var process = new Process { StartInfo = BuildCommand(inputPath, outputDirectory, profileDirectory) };
            if (!process.Start()) throw new ConversionException("converter_start_failed");
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                throw new ConversionException("conversion_timeout");
            }

            var stderr = await stderrTask;
            _ = await stdoutTask;
            if (process.ExitCode != 0)
                throw new ConversionException($"conversion_failed:{Clip(stderr)}");

            var outputs = Directory.GetFiles(outputDirectory, "*.pdf", SearchOption.TopDirectoryOnly);
            if (outputs.Length != 1) throw new ConversionException("conversion_output_missing");
            var info = new FileInfo(outputs[0]);
            if (info.Length <= 0 || info.Length > options.MaxOutputBytes)
                throw new ConversionException("conversion_output_too_large");
            return await File.ReadAllBytesAsync(outputs[0], ct);
        }
        catch (ConversionException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ConversionException("conversion_failed", exception);
        }
        finally
        {
            if (Directory.Exists(resolvedRequest)) Directory.Delete(resolvedRequest, recursive: true);
        }
    }

    private static async Task CopyBoundedAsync(Stream source, string path, long maximum, CancellationToken ct)
    {
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            total += read;
            if (total > maximum) throw new ConversionException("file_too_large");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static string Clip(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim()[..Math.Min(200, value.Trim().Length)];
}
