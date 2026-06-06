using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioRecorder;

/// <summary>
/// Captures EVERY active audio endpoint of the machine at once:
///   - every render device via WASAPI loopback (everything you HEAR, on any output)
///   - every capture device via WASAPI capture (every mic / line-in)
/// mixes them all sample-for-sample and writes a single CBR-192 MP3 in real time.
///
/// Pacing is driven by a wall clock, not by buffered data. Each input is a
/// BufferedWaveProvider with ReadFully=true, so when a device is silent it
/// simply contributes zeros — the MP3 timeline always equals real elapsed
/// time, with no stalls and no fragile "silence keep-alive" needed.
/// </summary>
public sealed class RecorderEngine : IDisposable
{
    private const int TargetSampleRate = 48000;
    private const int Mp3BitRateKbps   = 192;

    private sealed class Source
    {
        public required IWaveIn Capture { get; init; }
        public required BufferedWaveProvider Buffer { get; init; }
        public required WaveFormat Format { get; init; }
        public required bool IsRender { get; init; }
        public required string DeviceName { get; init; }
        public ManualResetEventSlim Stopped { get; } = new(false);
    }

    private readonly List<Source> _sources = new();
    private LameMP3FileWriter? _mp3;
    private IWaveProvider?     _mixOutput;
    private int                _mixBlockAlign;
    private int                _mixBytesPerSec;

    private Thread?                  _writerThread;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch       _stopwatch = new();
    private readonly object          _gate = new();
    private bool _isStopping;
    private volatile bool _fatalError;

    public bool IsRecording { get; private set; }
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public string?  CurrentFilePath { get; private set; }
    public bool HadFatalError => _fatalError;

    public event EventHandler<float>? MicLevel;
    public event EventHandler<float>? SystemLevel;
    public event EventHandler<Exception>? Error;
    public event EventHandler? Stopped;

    public void Start(string outputFilePath)
    {
        lock (_gate)
        {
            if (IsRecording || _isStopping) return;

            CurrentFilePath = outputFilePath;
            _fatalError = false;
            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)
                ?? throw new ArgumentException("Caminho inválido", nameof(outputFilePath)));

            try
            {
                BuildSources();

                if (_sources.Count == 0)
                    throw new InvalidOperationException(
                        "Nenhum dispositivo de áudio ativo encontrado (nem saída nem microfone).");

                // Normalize every source to the SAME 48k stereo float format so
                // MixingSampleProvider's strict .Equals() check passes.
                var normalized = _sources
                    .Select(s =>
                    {
                        ISampleProvider sp = NormalizeToStereo48k(s.Buffer);
                        float vol = s.IsRender ? 0.8f : 0.9f;
                        return (ISampleProvider)new VolumeSampleProvider(sp) { Volume = vol };
                    })
                    .ToList();

                var first = normalized[0].WaveFormat;
                foreach (var sp in normalized)
                {
                    if (!sp.WaveFormat.Equals(first))
                        throw new InvalidOperationException(
                            "Falha ao normalizar formatos de áudio dos dispositivos.");
                }

                var mixer = new MixingSampleProvider(normalized) { ReadFully = true };
                _mixOutput      = mixer.ToWaveProvider16();
                _mixBlockAlign  = _mixOutput.WaveFormat.BlockAlign;
                _mixBytesPerSec = _mixOutput.WaveFormat.AverageBytesPerSecond;

                _mp3 = new LameMP3FileWriter(outputFilePath, _mixOutput.WaveFormat, Mp3BitRateKbps);

                foreach (var s in _sources)
                {
                    try { s.Capture.StartRecording(); }
                    catch (Exception ex)
                    {
                        SafeFireError(new InvalidOperationException(
                            $"Não foi possível iniciar '{s.DeviceName}': {ex.Message}", ex));
                    }
                }

                _cts = new CancellationTokenSource();
                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name         = "MP3-Writer",
                    Priority     = ThreadPriority.AboveNormal,
                };
                _stopwatch.Restart();
                _writerThread.Start(_cts.Token);

                IsRecording = true;
            }
            catch
            {
                CleanupInternal();
                throw;
            }
        }
    }

    private void BuildSources()
    {
        using var en = new MMDeviceEnumerator();

        // ---- All active render endpoints (system audio, any output) ----
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var cap = new WasapiLoopbackCapture(dev);
                AddSource(cap, cap.WaveFormat, isRender: true, dev.FriendlyName);
            }
            catch (Exception ex)
            {
                SafeFireError(new InvalidOperationException(
                    $"Saída ignorada '{Safe(() => dev.FriendlyName)}': {ex.Message}", ex));
            }
            finally { try { dev.Dispose(); } catch { } }
        }

        // ---- All active capture endpoints (every mic / line-in) ----
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                // Skip loopback-style capture endpoints (Stereo Mix / "What U Hear"
                // / Mixagem Estéreo): render loopback already captures system audio,
                // so including these would record everything you hear TWICE.
                if (IsLoopbackStyleCapture(dev.FriendlyName))
                {
                    try { dev.Dispose(); } catch { }
                    continue;
                }
                var cap = new WasapiCapture(dev) { ShareMode = AudioClientShareMode.Shared };
                AddSource(cap, cap.WaveFormat, isRender: false, dev.FriendlyName);
            }
            catch (Exception ex)
            {
                SafeFireError(new InvalidOperationException(
                    $"Entrada ignorada '{Safe(() => dev.FriendlyName)}': {ex.Message}", ex));
            }
            finally { try { dev.Dispose(); } catch { } }
        }
    }

    private static readonly string[] LoopbackCaptureHints =
    {
        "stereo mix", "stereomix", "what u hear", "what you hear",
        "wave out", "mixagem", "mezcla est", "o que voc",
    };

    private static bool IsLoopbackStyleCapture(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        foreach (var h in LoopbackCaptureHints)
            if (n.Contains(h)) return true;
        return false;
    }

    private void AddSource(IWaveIn cap, WaveFormat fmt, bool isRender, string name)
    {
        var buf = new BufferedWaveProvider(fmt)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration          = TimeSpan.FromSeconds(5),
            ReadFully               = true,
        };
        var src = new Source
        {
            Capture    = cap,
            Buffer     = buf,
            Format     = fmt,
            IsRender   = isRender,
            DeviceName = name,
        };

        cap.DataAvailable += (_, e) =>
        {
            try
            {
                if (e.BytesRecorded <= 0) return;
                buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
                var peak = ComputePeak(e.Buffer, e.BytesRecorded, fmt);
                if (isRender) SystemLevel?.Invoke(this, peak);
                else          MicLevel?.Invoke(this, peak);
            }
            catch (Exception ex) { SafeFireError(ex); }
        };
        cap.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) SafeFireError(e.Exception);
            src.Stopped.Set();
        };

        _sources.Add(src);
    }

    public void Stop() => StopInternal(fromWriter: false);

    private void StopInternal(bool fromWriter)
    {
        Thread?                  writer;
        CancellationTokenSource? cts;
        List<Source>             sources;
        LameMP3FileWriter?       mp3;
        IWaveProvider?           mixOutput;

        lock (_gate)
        {
            if (!IsRecording || _isStopping) return;
            _isStopping = true;
            IsRecording = false;

            writer    = _writerThread;
            cts       = _cts;
            sources   = _sources.ToList();
            mp3       = _mp3;
            mixOutput = _mixOutput;
        }

        _stopwatch.Stop();

        try { cts?.Cancel(); } catch { }
        if (!fromWriter)
        {
            try { writer?.Join(TimeSpan.FromSeconds(5)); } catch { }
        }

        foreach (var s in sources)
        {
            try { s.Capture.StopRecording(); } catch { }
        }
        // Wait for all RecordingStopped callbacks against ONE shared deadline,
        // not 2 s per device (which would serialize to many seconds).
        var deadline = Environment.TickCount64 + 3000;
        foreach (var s in sources)
        {
            int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            try { s.Stopped.Wait(remaining); } catch { }
        }

        // Final tail: write whatever the mix still has buffered (real audio that
        // arrived after the last writer tick), capped so we don't spin on silence.
        try
        {
            if (mixOutput != null && mp3 != null)
            {
                int tailMs = sources.Count == 0
                    ? 0
                    : sources.Max(s => (int)s.Buffer.BufferedDuration.TotalMilliseconds);
                tailMs = Math.Min(tailMs, 5000);
                if (tailMs > 0)
                {
                    int bytes = _mixBytesPerSec * tailMs / 1000;
                    bytes -= bytes % _mixBlockAlign;
                    if (bytes > 0)
                    {
                        var drain = new byte[bytes];
                        int n = mixOutput.Read(drain, 0, drain.Length);
                        if (n > 0) mp3.Write(drain, 0, n);
                    }
                }
            }
        }
        catch (Exception ex) { SafeFireError(ex); }

        try { mp3?.Flush();   } catch { }
        try { mp3?.Dispose(); } catch { }

        lock (_gate)
        {
            CleanupInternal();
            _isStopping = false;
        }

        Stopped?.Invoke(this, EventArgs.Empty);
    }

    private void WriterLoop(object? state)
    {
        var ct  = (CancellationToken)state!;
        var src = _mixOutput!;
        long bytesPerSec  = _mixBytesPerSec;
        int  blockAlign   = _mixBlockAlign;
        var  buf          = new byte[bytesPerSec / 5]; // 200 ms scratch
        long totalWritten = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // How many bytes SHOULD exist by now, by the wall clock?
                double elapsedSec = _stopwatch.Elapsed.TotalSeconds;
                long targetBytes  = (long)(elapsedSec * bytesPerSec);
                targetBytes      -= targetBytes % blockAlign;

                long toWrite = targetBytes - totalWritten;
                if (toWrite < blockAlign)
                {
                    Thread.Sleep(10);
                    continue;
                }

                while (toWrite >= blockAlign && !ct.IsCancellationRequested)
                {
                    int chunk = (int)Math.Min(toWrite, buf.Length);
                    chunk -= chunk % blockAlign;
                    if (chunk <= 0) break;

                    int n = src.Read(buf, 0, chunk);
                    if (n <= 0) break;
                    _mp3?.Write(buf, 0, n);
                    totalWritten += n;
                    toWrite      -= n;
                }
            }
        }
        catch (Exception ex)
        {
            _fatalError = true;
            SafeFireError(ex);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { StopInternal(fromWriter: true); } catch { }
            });
        }
    }

    private void CleanupInternal()
    {
        foreach (var s in _sources)
        {
            try { s.Capture.Dispose(); } catch { }
            try { s.Stopped.Dispose(); } catch { }
        }
        _sources.Clear();

        try { _mp3?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }

        _mp3          = null;
        _mixOutput    = null;
        _writerThread = null;
        _cts          = null;
    }

    private void SafeFireError(Exception ex)
    {
        try { Error?.Invoke(this, ex); } catch { }
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch { return "?"; }
    }

    public void Dispose() => Stop();

    /// <summary>Peak-level estimate covering IEEE float32, PCM16/24/32 and Extensible.</summary>
    private static float ComputePeak(byte[] buf, int count, WaveFormat fmt)
    {
        try
        {
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);

            if (isFloat)
            {
                float peak = 0f;
                for (int i = 0; i + 4 <= count; i += 4)
                {
                    float v = Math.Abs(BitConverter.ToSingle(buf, i));
                    if (v > peak) peak = v;
                }
                return Math.Min(peak, 1f);
            }
            switch (fmt.BitsPerSample)
            {
                case 16:
                {
                    int peak = 0;
                    for (int i = 0; i + 2 <= count; i += 2)
                    {
                        int v = Math.Abs(BitConverter.ToInt16(buf, i));
                        if (v > peak) peak = v;
                    }
                    return peak / 32768f;
                }
                case 24:
                {
                    int peak = 0;
                    for (int i = 0; i + 3 <= count; i += 3)
                    {
                        int v = buf[i] | (buf[i + 1] << 8) | (buf[i + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        int abs = Math.Abs(v);
                        if (abs > peak) peak = abs;
                    }
                    return peak / 8388608f;
                }
                case 32:
                {
                    long peak = 0;
                    for (int i = 0; i + 4 <= count; i += 4)
                    {
                        long v = Math.Abs((long)BitConverter.ToInt32(buf, i));
                        if (v > peak) peak = v;
                    }
                    return peak / 2147483648f;
                }
            }
        }
        catch { }
        return 0f;
    }

    /// <summary>
    /// Wave → IEEE float, 48 kHz, stereo, with a WaveFormat built by NAudio's
    /// CreateIeeeFloatWaveFormat factory so every normalised stream has an
    /// identical WaveFormat for MixingSampleProvider's .Equals() check.
    /// </summary>
    private static ISampleProvider NormalizeToStereo48k(IWaveProvider source)
    {
        ISampleProvider sp = source.ToSampleProvider();

        // Down/upmix to stereo BEFORE resampling (cheaper, avoids resampling
        // surround channels we throw away).
        int channels = sp.WaveFormat.Channels;
        if (channels == 1)
        {
            sp = new MonoToStereoSampleProvider(sp);
        }
        else if (channels > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { sp }, 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            sp = mux;
        }

        // Always run through the resampler — even when already 48 kHz — so the
        // output WaveFormat is built by CreateIeeeFloatWaveFormat, which scrubs
        // every metadata field and guarantees a byte-identical format for the
        // mixer's strict .Equals() check.
        sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);
        return sp;
    }
}
