using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace AudioRecorder;

public sealed class TranscriptionProgressEventArgs : EventArgs
{
    public required string FilePath { get; init; }
    public required TranscriptionStatus Status { get; init; }
    public double Progress { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Background queue that runs one Whisper.net job at a time.
/// </summary>
public sealed class TranscriptionService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly HashSet<string> _enqueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _enqueueLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Thread _worker;

    private WhisperFactory? _factory;
    private WhisperModelSize? _factoryFor;

    public event EventHandler<TranscriptionProgressEventArgs>? StatusChanged;

    public TranscriptionService(AppSettings settings)
    {
        _settings = settings;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Transcription-Worker",
        };
        _worker.Start();
    }

    public bool Enqueue(string mp3Path)
    {
        lock (_enqueueLock)
        {
            if (!_enqueued.Add(mp3Path)) return false;
        }
        _queue.Add(mp3Path);
        StatusChanged?.Invoke(this, new TranscriptionProgressEventArgs
        {
            FilePath = mp3Path,
            Status = TranscriptionStatus.Queued,
        });
        return true;
    }

    public bool IsQueued(string mp3Path)
    {
        lock (_enqueueLock) return _enqueued.Contains(mp3Path);
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var path in _queue.GetConsumingEnumerable(_shutdownCts.Token))
            {
                try
                {
                    ProcessOneAsync(path, _shutdownCts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke(this, new TranscriptionProgressEventArgs
                    {
                        FilePath = path,
                        Status = TranscriptionStatus.Failed,
                        Message = ex.Message,
                    });
                }
                finally
                {
                    lock (_enqueueLock) _enqueued.Remove(path);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (InvalidOperationException) { /* queue completed during shutdown */ }
    }

    private async Task ProcessOneAsync(string mp3Path, CancellationToken ct)
    {
        if (!File.Exists(mp3Path))
        {
            StatusChanged?.Invoke(this, new TranscriptionProgressEventArgs
            {
                FilePath = mp3Path,
                Status = TranscriptionStatus.Failed,
                Message = "Arquivo não encontrado.",
            });
            return;
        }

        StatusChanged?.Invoke(this, new TranscriptionProgressEventArgs
        {
            FilePath = mp3Path,
            Status = TranscriptionStatus.Running,
            Progress = 0,
            Message = "Preparando modelo…",
        });

        // 1) Make sure the model is on disk.
        var modelProgress = new Progress<double>(p =>
        {
            // Reserve 0..30% of UI progress for model download.
            StatusChanged?.Invoke(this, new TranscriptionProgressEventArgs
            {
                FilePath = mp3Path,
                Status = TranscriptionStatus.Running,
                Progress = p * 0.30,
                Message = "Baixando modelo Whisper…",
            });
        });
        var modelPath = await WhisperModelManager
            .EnsureModelAsync(_settings.ModelSize, modelProgress, ct)
            .ConfigureAwait(false);

        // 2) Make/refresh the WhisperFactory if the model size changed.
        if (_factory == null || _factoryFor != _settings.ModelSize)
        {
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(modelPath);
            _factoryFor = _settings.ModelSize;
        }

        // 3) Decode MP3 → 16 kHz mono 16-bit WAV in a memory stream.
        StatusChanged?.Invoke(this, new TranscriptionProgressEventArgs
        {
            FilePath = mp3Path,
            Status = TranscriptionStatus.Running,
            Progress = 0.32,
            Message = "Convertendo áudio…",
        });

        using var wavStream = DecodeMp3ToWav16kMono(mp3Path);

        // 4) Build processor and stream segments.
        var lang = string.IsNullOrWhiteSpace(_settings.Language) ? "auto" : _settings.Language;
        var builder = _factory.CreateBuilder().WithLanguage(lang);
        await using var processor = builder.Build();

        var segments = new List<(TimeSpan Start, TimeSpan End, string Text)>();
        var text = new StringBuilder();

        // Whisper doesn't expose progress directly; approximate via segment End vs total duration.
        TimeSpan totalDuration;
        try
        {
            using var reader = new Mp3FileReader(mp3Path);
            totalDuration = reader.TotalTime;
        }
        catch { totalDuration = TimeSpan.Zero; }

        await foreach (var segment in processor.ProcessAsync(wavStream, ct).ConfigureAwait(false))
        {
            segments.Add((segment.Start, segment.End, segment.Text));
            if (text.Length > 0) text.Append(' ');
            text.Append(segment.Text.Trim());

            double pct = 0.5;
            if (totalDuration.TotalSeconds > 0)
                pct = 0.35 + 0.60 * Math.Clamp(
                    segment.End.TotalSeconds / totalDuration.TotalSeconds, 0, 1);

            StatusChanged?.Invoke(this, new TranscriptionProgressEventArgs
            {
                FilePath = mp3Path,
                Status = TranscriptionStatus.Running,
                Progress = pct,
                Message = "Transcrevendo…",
            });
        }

        // 5) Save .txt and (optionally) .srt next to the MP3.
        var txtPath = mp3Path + ".txt";
        await File.WriteAllTextAsync(txtPath, text.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);

        if (_settings.WriteSrt)
        {
            var srtPath = mp3Path + ".srt";
            var srt = BuildSrt(segments);
            await File.WriteAllTextAsync(srtPath, srt, Encoding.UTF8, ct).ConfigureAwait(false);
        }

        StatusChanged?.Invoke(this, new TranscriptionProgressEventArgs
        {
            FilePath = mp3Path,
            Status = TranscriptionStatus.Done,
            Progress = 1.0,
        });
    }

    /// <summary>
    /// Decodes an MP3 into a fresh MemoryStream containing a valid WAV file
    /// at 16 kHz, mono, 16-bit PCM — the format Whisper.net expects.
    /// </summary>
    private static MemoryStream DecodeMp3ToWav16kMono(string mp3Path)
    {
        using var mp3 = new Mp3FileReader(mp3Path);
        var targetFormat = new WaveFormat(16000, 16, 1);

        // Stereo → mono via downmix (avoids needing Media Foundation).
        ISampleProvider source = mp3.ToSampleProvider();
        if (source.WaveFormat.Channels == 2)
        {
            source = new StereoToMonoSampleProvider(source)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f,
            };
        }
        else if (source.WaveFormat.Channels > 2)
        {
            // Take channel 0 only.
            var mux = new MultiplexingSampleProvider(new[] { source }, 1);
            mux.ConnectInputToOutput(0, 0);
            source = mux;
        }

        // Resample to 16 kHz.
        if (source.WaveFormat.SampleRate != 16000)
            source = new WdlResamplingSampleProvider(source, 16000);

        // Float → 16-bit PCM.
        var pcm16 = source.ToWaveProvider16();

        var ms = new MemoryStream();
        // Write WAV header + body. WaveFileWriter.WriteWavFileToStream closes the
        // stream when done — we don't want that, so write manually.
        WriteWavStream(ms, pcm16, targetFormat);
        ms.Position = 0;
        return ms;
    }

    private static void WriteWavStream(Stream dest, IWaveProvider source, WaveFormat fmt)
    {
        // Use NAudio's WaveFileWriter but write to a non-closing wrapper.
        using var nonClosing = new NonClosingStream(dest);
        using var writer = new WaveFileWriter(nonClosing, fmt);
        var buf = new byte[16 * 1024];
        int n;
        while ((n = source.Read(buf, 0, buf.Length)) > 0)
        {
            writer.Write(buf, 0, n);
        }
        writer.Flush();
    }

    private static string BuildSrt(List<(TimeSpan Start, TimeSpan End, string Text)> segments)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            var (start, end, txt) = segments[i];
            sb.Append(i + 1).Append('\n');
            sb.Append(FormatSrtTime(start)).Append(" --> ").Append(FormatSrtTime(end)).Append('\n');
            sb.Append(txt.Trim()).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string FormatSrtTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return string.Format(CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2},{3:D3}",
            (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
    }

    public void Dispose()
    {
        try { _shutdownCts.Cancel(); } catch { }
        try { _queue.CompleteAdding(); } catch { }
        try { _worker.Join(TimeSpan.FromSeconds(3)); } catch { }
        try { _factory?.Dispose(); } catch { }
        try { _queue.Dispose(); } catch { }
        try { _shutdownCts.Dispose(); } catch { }
    }

    /// <summary>Wrapper that suppresses Dispose so we can wrap a long-lived MemoryStream.</summary>
    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;
        public NonClosingStream(Stream inner) { _inner = inner; }
        public override bool CanRead  => _inner.CanRead;
        public override bool CanSeek  => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length   => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int  Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* intentional no-op */ }
    }
}
