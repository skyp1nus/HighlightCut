using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;
using HighlightCut.Transcription.Models;

namespace HighlightCut.Transcription.Tests;

/// <summary>Serves one file over fake HTTP, honouring range requests unless told not to.</summary>
internal sealed class FakeServer(byte[] content) : HttpMessageHandler
{
    public List<RangeHeaderValue?> Ranges { get; } = [];
    public bool IgnoreRanges { get; set; }
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>Stops sending after this many bytes of a response, as a dropped connection does.</summary>
    public int? CutAfter { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Ranges.Add(request.Headers.Range);
        if (FailWith is { } status)
            return Task.FromResult(new HttpResponseMessage(status) { ReasonPhrase = "Nope" });
        long from = !IgnoreRanges && request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } f ? f : 0;
        if (from >= content.Length && from > 0)
        {
            var full = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
            full.Content.Headers.ContentRange = new ContentRangeHeaderValue(content.Length);
            return Task.FromResult(full);
        }
        byte[] part = content[(int)from..];
        if (CutAfter is { } cut && cut < part.Length)
            part = part[..cut];
        var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(part),
        };
        response.Content.Headers.ContentLength = part.Length;
        if (from > 0)
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, content.Length - 1, content.Length);
        else if (CutAfter is not null)
            response.Content.Headers.ContentLength = content.Length; // Promised more than it sends.
        return Task.FromResult(response);
    }
}

public sealed class ModelInstallerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-models").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TranscriptionModel SingleFile(int size = 300_000) => new("whisper-test", TranscriptionEngine.Whisper, "English", size,
        new Uri("https://models.example/ggml-test.bin"), ModelPackage.SingleFile, ["ggml-test.bin"]);

    private static byte[] Bytes(int n) => [.. Enumerable.Range(0, n).Select(i => (byte)(i * 31 % 251))];

    private sealed class Reports : IProgress<InstallProgress>
    {
        public List<InstallProgress> All { get; } = [];
        public void Report(InstallProgress value) => All.Add(value);
    }

    [Fact]
    public async Task A_single_file_model_is_downloaded_and_installed()
    {
        byte[] content = Bytes(300_000);
        var store = new ModelStore(_dir);
        var model = SingleFile();
        var reports = new Reports();

        await new ModelInstaller(new HttpClient(new FakeServer(content))).InstallAsync(model, store, reports, Ct);

        Assert.True(store.IsInstalled(model));
        Assert.False(store.HasPartial(model));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(store.DirectoryOf(model), "ggml-test.bin"), Ct));
        Assert.Equal(1, reports.All[^1].Fraction);
        Assert.Equal(300_000, store.SizeOnDisk(model));
        Assert.True(reports.All.Select(r => r.Fraction).SequenceEqual(reports.All.Select(r => r.Fraction).Order()));
    }

    [Fact]
    public async Task A_dropped_download_continues_where_it_stopped()
    {
        byte[] content = Bytes(300_000);
        var store = new ModelStore(_dir);
        var model = SingleFile();
        var server = new FakeServer(content) { CutAfter = 120_000 };
        var installer = new ModelInstaller(new HttpClient(server));

        var dropped = await Assert.ThrowsAsync<ModelDownloadException>(() => installer.InstallAsync(model, store, cancellationToken: Ct));
        Assert.Contains("continues where it stopped", dropped.Message, StringComparison.Ordinal);
        Assert.True(store.HasPartial(model));
        Assert.False(store.IsInstalled(model));

        server.CutAfter = null;
        await installer.InstallAsync(model, store, cancellationToken: Ct);

        Assert.Equal(120_000, server.Ranges[^1]?.Ranges.Single().From);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(store.DirectoryOf(model), "ggml-test.bin"), Ct));
    }

    [Fact]
    public async Task A_server_that_ignores_ranges_starts_over()
    {
        byte[] content = Bytes(50_000);
        var store = new ModelStore(_dir);
        var model = SingleFile(50_000);
        Directory.CreateDirectory(store.PartialOf(model));
        await File.WriteAllBytesAsync(Path.Combine(store.PartialOf(model), "ggml-test.bin"), Bytes(10_000), Ct);

        await new ModelInstaller(new HttpClient(new FakeServer(content) { IgnoreRanges = true })).InstallAsync(model, store, cancellationToken: Ct);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(store.DirectoryOf(model), "ggml-test.bin"), Ct));
    }

    [Fact]
    public async Task Http_errors_and_a_full_disk_are_explained()
    {
        var store = new ModelStore(_dir);
        var model = SingleFile();

        var notFound = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            new ModelInstaller(new HttpClient(new FakeServer([]) { FailWith = HttpStatusCode.NotFound })).InstallAsync(model, store, cancellationToken: Ct));
        Assert.Contains("404", notFound.Message, StringComparison.Ordinal);

        var full = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            new ModelInstaller(new HttpClient(new FakeServer(Bytes(300_000))), _ => 1_000_000).InstallAsync(model, store, cancellationToken: Ct));
        Assert.Contains("Not enough free space", full.Message, StringComparison.Ordinal);
        Assert.False(store.IsInstalled(model));
    }

    [Fact]
    public async Task Cancelling_keeps_the_partial_download()
    {
        var store = new ModelStore(_dir);
        var model = SingleFile();
        using var cts = new CancellationTokenSource();
        var progress = new Reports();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelInstaller(new HttpClient(new FakeServer(Bytes(300_000)))).InstallAsync(model, store, progress, cts.Token));

        Assert.False(store.IsInstalled(model));
        store.DeletePartial(model);
        Assert.False(store.HasPartial(model));
    }

    [Fact]
    public async Task An_archive_is_unpacked_without_its_top_folder()
    {
        byte[] archive = TarBz2(("model-v3/", null), ("model-v3/encoder.onnx", "enc"), ("model-v3/tokens.txt", "a 0\nb 1"),
            ("model-v3/test_wavs/en.wav", "RIFF"), ("model-v3/encoder.fp32.onnx", "big"));
        var model = new TranscriptionModel("parakeet-test", TranscriptionEngine.Parakeet, "English", archive.Length,
            new Uri("https://github.example/model.tar.bz2"), ModelPackage.TarBz2, ["encoder.onnx", "tokens.txt"]);
        var store = new ModelStore(_dir);
        var reports = new Reports();

        await new ModelInstaller(new HttpClient(new FakeServer(archive))).InstallAsync(model, store, reports, Ct);

        Assert.True(store.IsInstalled(model));
        string dir = store.DirectoryOf(model);
        Assert.Equal("a 0\nb 1", await File.ReadAllTextAsync(Path.Combine(dir, "tokens.txt"), Ct));
        Assert.True(File.Exists(Path.Combine(dir, "test_wavs", "en.wav")));
        Assert.False(File.Exists(Path.Combine(dir, "encoder.fp32.onnx")));
        Assert.False(File.Exists(Path.Combine(dir, "download.tar.bz2")));
        Assert.Contains(reports.All, r => r.Phase == InstallPhase.Unpacking && r.Fraction is > 0.9 and < 1);
    }

    [Fact]
    public async Task An_archive_without_the_expected_files_or_with_escaping_paths_is_refused()
    {
        var store = new ModelStore(_dir);
        var incomplete = new TranscriptionModel("m1", TranscriptionEngine.Parakeet, "", 0, new Uri("https://x.example/a.tar.bz2"),
            ModelPackage.TarBz2, ["encoder.onnx", "tokens.txt"]);
        var missing = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            new ModelInstaller(new HttpClient(new FakeServer(TarBz2(("m/encoder.onnx", "x"))))).InstallAsync(incomplete, store, cancellationToken: Ct));
        Assert.Contains("tokens.txt", missing.Message, StringComparison.Ordinal);
        Assert.False(store.HasPartial(incomplete));

        var escaping = incomplete with { Id = "m2" };
        await Assert.ThrowsAsync<ModelDownloadException>(() =>
            new ModelInstaller(new HttpClient(new FakeServer(TarBz2(("m/../../evil.txt", "x"))))).InstallAsync(escaping, store, cancellationToken: Ct));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));

        var damaged = incomplete with { Id = "m3" };
        var bad = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            new ModelInstaller(new HttpClient(new FakeServer(Encoding.ASCII.GetBytes("not an archive")))).InstallAsync(damaged, store, cancellationToken: Ct));
        Assert.Contains("damaged", bad.Message, StringComparison.Ordinal);
    }

    /// <summary>A .tar.bz2 with the given entries (a null content makes a directory).</summary>
    private static byte[] TarBz2(params (string Name, string? Content)[] entries)
    {
        using var tarBytes = new MemoryStream();
        using (var writer = new TarWriter(tarBytes, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = new PaxTarEntry(content is null ? TarEntryType.Directory : TarEntryType.RegularFile, name);
                if (content is not null)
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                writer.WriteEntry(entry);
            }
        }
        using var output = new MemoryStream();
        tarBytes.Position = 0;
        BZip2.Compress(tarBytes, output, isStreamOwner: false, level: 9);
        return output.ToArray();
    }
}

public class ModelCatalogTests
{
    [Fact]
    public void The_catalog_lists_parakeet_first_and_whisper_as_single_files()
    {
        Assert.Equal(["parakeet-tdt-0.6b-v3", "whisper-large-v3-turbo", "whisper-small", "whisper-base.en"], ModelCatalog.All.Select(m => m.Id));
        Assert.All(ModelCatalog.All, m => Assert.Equal("https", m.Url.Scheme));
        Assert.All(ModelCatalog.All, m => Assert.Equal(ModelPackage.TarBz2, m.Package));
        Assert.Equal(["turbo-encoder.int8.onnx", "turbo-decoder.int8.onnx", "turbo-tokens.txt"], ModelCatalog.All[1].Files);
        Assert.Equal(["487 MB", "564 MB", "639 MB", "209 MB"], ModelCatalog.All.Select(m => m.SizeText));
        Assert.Equal("en", ModelCatalog.OnlyLanguage(ModelCatalog.All[3]));
        Assert.Null(ModelCatalog.OnlyLanguage(ModelCatalog.All[1]));
        Assert.Same(ModelCatalog.Parakeet, ModelCatalog.Find("parakeet-tdt-0.6b-v3"));
    }

    [Fact]
    public void A_model_counts_as_installed_only_with_all_its_files()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut-store").FullName;
        try
        {
            var store = new ModelStore(dir);
            var model = ModelCatalog.Parakeet;
            Directory.CreateDirectory(store.DirectoryOf(model));
            foreach (string f in model.Files.Skip(1))
                File.WriteAllText(Path.Combine(store.DirectoryOf(model), f), "x");
            Assert.False(store.IsInstalled(model));
            File.WriteAllText(Path.Combine(store.DirectoryOf(model), model.Files[0]), "x");
            Assert.True(store.IsInstalled(model));
            store.Delete(model);
            Assert.False(store.IsInstalled(model));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// Downloads a real model. Runs only with HIGHLIGHTCUT_NETWORK_TESTS=1 (the Parakeet archive is ~490 MB); the model is
/// installed into HIGHLIGHTCUT_MODELS_DIR when set, so later runs and the engine tests can reuse it.
/// </summary>
public class RealDownloadTests
{
    [Theory]
    [InlineData("parakeet-tdt-0.6b-v3")]
    [InlineData("whisper-base.en")]
    public async Task A_model_downloads_and_unpacks_from_github(string id)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("HIGHLIGHTCUT_NETWORK_TESTS") == "1", "Set HIGHLIGHTCUT_NETWORK_TESTS=1 to download models.");
        string folder = Environment.GetEnvironmentVariable("HIGHLIGHTCUT_MODELS_DIR") is { Length: > 0 } dir
            ? dir
            : Directory.CreateTempSubdirectory("highlightcut-real-models").FullName;
        var store = new ModelStore(folder);
        var model = ModelCatalog.Find(id)!;
        if (!store.IsInstalled(model))
        {
            using var http = ModelInstaller.CreateHttpClient();
            await new ModelInstaller(http).InstallAsync(model, store, cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.True(store.IsInstalled(model));
        // Only the int8 files are kept: well under the full-precision size.
        Assert.InRange(store.SizeOnDisk(model), 100_000_000, 800_000_000);
    }
}
