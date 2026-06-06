using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AudioRecorder;

/// <summary>
/// Resolves the GGML model file for a given size, downloading it from
/// Hugging Face on first use. Models live in
/// %LOCALAPPDATA%\Transcribid\models\ so they survive uninstall of the .exe.
/// </summary>
public static class WhisperModelManager
{
    public static string ModelsFolder
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "Transcribid", "models");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string GetModelFileName(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Tiny    => "ggml-tiny.bin",
        WhisperModelSize.Base    => "ggml-base.bin",
        WhisperModelSize.Small   => "ggml-small.bin",
        WhisperModelSize.Medium  => "ggml-medium.bin",
        WhisperModelSize.LargeV3 => "ggml-large-v3.bin",
        _ => "ggml-small.bin",
    };

    public static string GetModelPath(WhisperModelSize size)
        => Path.Combine(ModelsFolder, GetModelFileName(size));

    public static bool IsModelDownloaded(WhisperModelSize size)
    {
        var p = GetModelPath(size);
        if (!File.Exists(p)) return false;
        // Sanity check: each model has a minimum expected size — if it's much
        // smaller, the previous download was interrupted.
        var minBytes = size switch
        {
            WhisperModelSize.Tiny    =>  60_000_000L,
            WhisperModelSize.Base    => 120_000_000L,
            WhisperModelSize.Small   => 400_000_000L,
            WhisperModelSize.Medium  => 1_400_000_000L,
            WhisperModelSize.LargeV3 => 2_800_000_000L,
            _ => 1L,
        };
        try { return new FileInfo(p).Length >= minBytes; }
        catch { return false; }
    }

    /// <summary>
    /// Downloads the model if it's missing. Reports progress in 0..1.
    /// Returns the local file path.
    /// </summary>
    public static async Task<string> EnsureModelAsync(
        WhisperModelSize size,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var target = GetModelPath(size);
        if (IsModelDownloaded(size))
        {
            progress?.Report(1.0);
            return target;
        }

        // Download to a temp file then atomically rename so partial files
        // can't be mistaken for a complete model on next launch.
        var tmp = target + ".part";
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{GetModelFileName(size)}";

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromHours(2),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Transcribid/1.0");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            int n;
            while ((n = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                if (total is long t && t > 0)
                    progress?.Report(Math.Clamp((double)received / t, 0, 1));
            }
        }

        try { if (File.Exists(target)) File.Delete(target); } catch { }
        File.Move(tmp, target);
        progress?.Report(1.0);
        return target;
    }
}
