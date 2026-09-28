using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using ICSharpCode.SharpZipLib.BZip2;

namespace HighlightCut.Transcription.Models;

/// <summary>Why a model could not be installed, in words for the user.</summary>
public sealed class ModelDownloadException(string message, Exception? inner = null) : Exception(message, inner);

public enum InstallPhase
{
    Downloading,

    /// <summary>Unpacking an archive after the download.</summary>
    Unpacking,
}

/// <param name="Fraction">Share of the whole install done, 0..1.</param>
/// <param name="Received">Bytes downloaded so far.</param>
/// <param name="Total">Download size, once the server has said.</param>
public readonly record struct InstallProgress(InstallPhase Phase, double Fraction, long Received, long? Total);

/// <summary>
/// Downloads a model into the models folder. The download goes to "&lt;id&gt;.partial" and continues where it
/// stopped if it is interrupted (HTTP range requests); archives are unpacked there; the folder is renamed to
/// "&lt;id&gt;" only when everything is in place, so a model is either fully installed or not at all.
/// </summary>
public sealed class ModelInstaller(HttpClient http, Func<string, long?>? freeSpace = null)
{
    /// <summary>Share of the progress bar given to unpacking an archive.</summary>
    private const double UnpackShare = 0.1;

    /// <summary>A download that receives nothing for this long counts as failed.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    private readonly Func<string, long?> _freeSpace = freeSpace ?? FreeSpace;

    /// <summary>An HTTP client for model downloads: identifies HighlightCut and follows redirects (GitHub, Hugging Face).</summary>
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        string version = typeof(ModelInstaller).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HighlightCut", version));
        return client;
    }

    /// <exception cref="ModelDownloadException">The download or unpacking failed; the message says why.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; what was downloaded is kept for the next try.</exception>
    public async Task InstallAsync(TranscriptionModel model, ModelStore store, IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string staging = store.PartialOf(model);
        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModelDownloadException($"Cannot write to the models folder: {e.Message}", e);
        }

        bool archive = model.Package == ModelPackage.TarBz2;
        string download = Path.Combine(staging, archive ? "download.tar.bz2" : model.Files[0]);
        double downloadShare = archive ? 1 - UnpackShare : 1;
        await DownloadAsync(model, download, archive, p =>
            progress?.Report(new InstallProgress(InstallPhase.Downloading, p.Total is > 0 and var t ? downloadShare * p.Bytes / t : 0,
                p.Bytes, p.Total)), cancellationToken).ConfigureAwait(false);

        if (archive)
        {
            long size = new FileInfo(download).Length;
            var keep = model.Files.ToHashSet(StringComparer.Ordinal);
            await Task.Run(() => Unpack(download, staging, name => keep.Contains(name) || name.StartsWith("test_wavs/", StringComparison.Ordinal), read =>
                progress?.Report(new InstallProgress(InstallPhase.Unpacking, downloadShare + UnpackShare * read / Math.Max(1, size), size, size)),
                cancellationToken), cancellationToken).ConfigureAwait(false);
            File.Delete(download);
        }

        var missing = model.Files.Where(f => !File.Exists(Path.Combine(staging, f))).ToList();
        if (missing.Count > 0)
        {
            store.DeletePartial(model);
            throw new ModelDownloadException($"The download of {model.Id} is missing {string.Join(", ", missing)}.");
        }
        string target = store.DirectoryOf(model);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(staging, target);
        progress?.Report(new InstallProgress(InstallPhase.Unpacking, 1, 0, null));
    }

    private readonly record struct DownloadState(long Bytes, long? Total);

    private async Task DownloadAsync(TranscriptionModel model, string path, bool archive, Action<DownloadState> report,
        CancellationToken cancellationToken)
    {
        long have = File.Exists(path) ? new FileInfo(path).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ModelDownloadException($"Could not reach {model.Url.Host}: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelDownloadException($"{model.Url.Host} did not answer.", e);
        }

        using (response)
        {
            long? total;
            bool append;
            if (response.StatusCode == HttpStatusCode.PartialContent && have > 0)
            {
                total = response.Content.Headers.ContentRange?.Length;
                append = true;
            }
            else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0
                     && response.Content.Headers.ContentRange?.Length is { } length && length == have)
            {
                // Already complete.
                report(new DownloadState(have, have));
                return;
            }
            else if (response.IsSuccessStatusCode)
            {
                total = response.Content.Headers.ContentLength;
                append = false;
                have = 0;
            }
            else
            {
                throw new ModelDownloadException(
                    $"The download of {model.Id} failed: {(int)response.StatusCode} {response.ReasonPhrase} from {model.Url.Host}.");
            }

            long remaining = (total ?? model.DownloadSize) - have;
            // An archive is unpacked next to the download before the download is removed.
            long needed = remaining + (archive ? (long)((total ?? model.DownloadSize) * 1.5) : 0) + 50_000_000;
            if (_freeSpace(Path.GetDirectoryName(path)!) is { } free && free < needed)
            {
                throw new ModelDownloadException(
                    $"Not enough free space for {model.Id}: it needs about {TranscriptionModel.FormatSize(needed)}, " +
                    $"{TranscriptionModel.FormatSize(free)} is free.");
            }

            report(new DownloadState(have, total));
            await CopyAsync(response, path, append, have, total, report, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CopyAsync(HttpResponseMessage response, string path, bool append, long have, long? total,
        Action<DownloadState> report, CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        FileStream file;
        try
        {
            file = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModelDownloadException($"Cannot write to the models folder: {e.Message}", e);
        }
        await using (file)
        {
            var buffer = new byte[1 << 16];
            var lastReport = DateTime.UtcNow;
            while (true)
            {
                int read;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    stall.CancelAfter(StallTimeout);
                    try
                    {
                        read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new ModelDownloadException("The download stopped receiving data. Try again; it continues where it stopped.", e);
                    }
                    catch (IOException e)
                    {
                        throw new ModelDownloadException($"The connection was lost: {e.Message} Try again; it continues where it stopped.", e);
                    }
                }
                if (read == 0)
                    break;
                try
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    throw new ModelDownloadException($"Could not save the model: {e.Message}", e);
                }
                have += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 100)
                {
                    report(new DownloadState(have, total));
                    lastReport = DateTime.UtcNow;
                }
            }
        }
        if (total is { } expected && have != expected)
            throw new ModelDownloadException("The download ended early. Try again; it continues where it stopped.");
        report(new DownloadState(have, total ?? have));
    }

    /// <summary>
    /// Unpacks a .tar.bz2 into <paramref name="destination"/>, dropping the archive's top folder and skipping files
    /// <paramref name="wanted"/> turns down (e.g. full-precision copies of an int8 model). Entries that would land
    /// outside the destination are refused.
    /// </summary>
    internal static void Unpack(string archive, string destination, Func<string, bool> wanted, Action<long>? compressedRead,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        try
        {
            using var file = File.OpenRead(archive);
            using var bz = new BZip2InputStream(file) { IsStreamOwner = false };
            using var tar = new TarReader(bz);
            while (tar.GetNextEntry() is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    continue;
                string name = entry.Name.Replace('\\', '/');
                int slash = name.IndexOf('/', StringComparison.Ordinal);
                string relative = slash >= 0 ? name[(slash + 1)..] : name;
                if (relative.Length == 0 || !wanted(relative))
                    continue;
                string target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    throw new ModelDownloadException("The model archive is damaged (a file points outside its folder).");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                compressedRead?.Invoke(file.Position);
            }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or ICSharpCode.SharpZipLib.SharpZipBaseException or EndOfStreamException)
        {
            File.Delete(archive);
            throw new ModelDownloadException("The downloaded model archive is damaged; download it again.", e);
        }
        catch (IOException e)
        {
            throw new ModelDownloadException($"Could not unpack the model: {e.Message}", e);
        }
    }

    private static long? FreeSpace(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
