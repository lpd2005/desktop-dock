using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopDock.Core;

namespace DesktopDock.App;

internal enum UpdateStage { Idle, Checking, Available, Downloading, Ready, Error }

internal sealed class UpdateService : IDisposable
{
    private sealed record Cache(DateTimeOffset LastCheck, string Notified);
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly string folder, cacheFile;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private readonly ReleaseVersion current;
    private Cache cache = new(DateTimeOffset.MinValue, "");
    private string? installer;
    private bool disposed;
    public event Action? Changed;
    public event Action<string>? AutomaticUpdateFound;
    public UpdateStage Stage { get; private set; }
    public UpdateRelease? Latest { get; private set; }
    public string Status { get; private set; } = "检查 GitHub Releases 中的新版本。";
    public int Progress { get; private set; }
    public bool Busy => Stage is UpdateStage.Checking or UpdateStage.Downloading;

    public UpdateService(string data, HttpClient? client = null, string? version = null)
    {
        if (!ReleaseVersion.TryParse(version ?? AppVersion.Current, out var parsed)) throw new InvalidOperationException("Invalid build version.");
        current = parsed; folder = Path.Combine(data, "updates"); cacheFile = Path.Combine(data, "update-state.json");
        ownsClient = client is null;
        this.client = client ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
        if (ownsClient)
        {
            this.client.Timeout = Timeout.InfiniteTimeSpan;
            this.client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopDock/" + current);
        }
        try { if (File.Exists(cacheFile)) cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(cacheFile)) ?? cache; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public async Task RunAutomaticChecks(Func<bool> enabled)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (enabled() && (now - cache.LastCheck >= TimeSpan.FromDays(1) || cache.LastCheck > now.AddMinutes(5)))
                    await CheckAsync(automatic: true);
                await Task.Delay(TimeSpan.FromHours(6), lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task CheckAsync(bool automatic = false)
    {
        if (disposed || Busy || Stage == UpdateStage.Ready) return;
        if (automatic) { cache = cache with { LastCheck = DateTimeOffset.UtcNow }; SaveCache(); }
        using var cancel = Begin(UpdateStage.Checking, "正在检查更新…", TimeSpan.FromSeconds(20));
        try
        {
            using var response = await client.GetAsync(ReleaseFeed.Api, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[16384]; int read;
            while ((read = await stream.ReadAsync(buffer, cancel.Token)) != 0)
            {
                if (memory.Length + read > 4 * 1024 * 1024) throw new InvalidDataException();
                memory.Write(buffer, 0, read);
            }
            Latest = ReleaseFeed.Latest(System.Text.Encoding.UTF8.GetString(memory.ToArray()), current);
            Stage = Latest is null ? UpdateStage.Idle : UpdateStage.Available;
            Status = Latest is null ? "当前已是最新可用版本。" : "发现新版本 " + Latest.Version;
            cache = cache with { LastCheck = DateTimeOffset.UtcNow };
            if (automatic && Latest is not null && cache.Notified != Latest.Version.ToString())
            {
                cache = cache with { Notified = Latest.Version.ToString() }; AutomaticUpdateFound?.Invoke(cache.Notified);
            }
            SaveCache();
        }
        catch (OperationCanceledException) { Stage = Latest is null ? UpdateStage.Idle : UpdateStage.Available; Status = disposed ? "" : "检查已取消或超时，请重试。"; }
        catch (Exception e) when (Expected(e)) { Fail(e); }
        finally { operation = null; Changed?.Invoke(); }
    }
    public async Task DownloadAsync()
    {
        if (disposed || Busy || Latest is not { } release) return;
        Progress = 0;
        using var cancel = Begin(UpdateStage.Downloading, "正在下载安装包…", TimeSpan.FromMinutes(10));
        string partial = Path.Combine(folder, release.FileName + ".download");
        installer = null;
        try
        {
            Directory.CreateDirectory(folder);
            using var response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            response.EnsureSuccessStatusCode();
            var final = response.RequestMessage?.RequestUri;
            if (final is not null && (final.Scheme != "https" || final.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")))
                throw new InvalidDataException();
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException();
            await using var input = await response.Content.ReadAsStreamAsync(cancel.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0; byte[] buffer = new byte[65536]; int read;
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous))
            {
                while ((read = await input.ReadAsync(buffer, cancel.Token)) != 0)
                {
                    total += read;
                    if (total > release.Size || total > ReleaseFeed.MaximumBytes) throw new InvalidDataException();
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancel.Token);
                    int progress = (int)(total * 100 / release.Size);
                    if (progress != Progress) { Progress = progress; Status = $"正在下载安装包… {progress}%"; Changed?.Invoke(); }
                }
            }
            if (total != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256))) throw new InvalidDataException();
            string destination = Path.Combine(folder, release.FileName);
            File.Move(partial, destination, overwrite: true); installer = destination;
            Stage = UpdateStage.Ready; Status = "下载校验完成，可以安装更新。";
        }
        catch (OperationCanceledException) { Stage = UpdateStage.Available; Status = "下载已取消或超时，可重新下载。"; }
        catch (Exception e) when (Expected(e)) { Fail(e); }
        finally
        {
            operation = null;
            try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Changed?.Invoke();
        }
    }
    internal async Task<bool> VerifyInstallerAsync()
    {
        if (Stage != UpdateStage.Ready || installer is null || Latest is null) return false;
        try
        {
            await using var stream = File.OpenRead(installer);
            return stream.Length == Latest.Size && CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream, lifetime.Token), Convert.FromHexString(Latest.Sha256));
        }
        catch (Exception e) when (Expected(e) || e is OperationCanceledException) { return false; }
    }
    public async Task<bool> InstallAsync()
    {
        if (!await VerifyInstallerAsync()) { Stage = UpdateStage.Error; Status = "安装包校验失败，请重新下载。"; Changed?.Invoke(); return false; }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(installer!) { UseShellExecute = true, Arguments = "/SP-" });
            return process is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Stage = UpdateStage.Error; Status = "无法启动安装程序，可重新下载后重试。"; Changed?.Invoke(); return false; }
    }
    private CancellationTokenSource Begin(UpdateStage stage, string status, TimeSpan timeout)
    {
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); cancel.CancelAfter(timeout);
        operation = cancel; Stage = stage; Status = status; Changed?.Invoke(); return cancel;
    }
    private static bool Expected(Exception e) => e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException;
    private void Fail(Exception exception)
    {
        Stage = UpdateStage.Error;
        Status = exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "GitHub 暂时限制请求，请稍后重试或前往发布页面。",
            HttpRequestException => "无法连接 GitHub，请检查网络后重试。",
            InvalidDataException or JsonException => "更新文件或版本信息校验失败，请稍后重试。",
            _ => "无法写入更新文件，请检查目录权限和磁盘空间。"
        };
    }
    private void SaveCache()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!); AtomicJson.Write(cacheFile, cache); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public void Cancel() => operation?.Cancel();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); if (ownsClient) client.Dispose();
    }
}
