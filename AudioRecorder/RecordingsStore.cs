using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AudioRecorder;

public enum TranscriptionStatus
{
    None,
    Queued,
    Running,
    Done,
    Failed,
}

public sealed class RecordingItem : INotifyPropertyChanged
{
    public required string FilePath  { get; init; }
    public required DateTime CreatedAt { get; init; }

    private TimeSpan _duration;
    public TimeSpan Duration
    {
        get => _duration;
        set { _duration = value; OnChanged(nameof(Duration)); OnChanged(nameof(DurationText)); }
    }

    private long _sizeBytes;
    public long SizeBytes
    {
        get => _sizeBytes;
        set { _sizeBytes = value; OnChanged(nameof(SizeBytes)); OnChanged(nameof(SizeText)); }
    }

    private TranscriptionStatus _transcription = TranscriptionStatus.None;
    public TranscriptionStatus Transcription
    {
        get => _transcription;
        set
        {
            if (_transcription == value) return;
            _transcription = value;
            OnChanged(nameof(Transcription));
            OnChanged(nameof(TranscriptionIcon));
            OnChanged(nameof(TranscriptionTooltip));
            OnChanged(nameof(HasTranscript));
            OnChanged(nameof(CanOpenTranscript));
            OnChanged(nameof(TranscriptionProgressText));
        }
    }

    private double _transcriptionProgress;
    public double TranscriptionProgress
    {
        get => _transcriptionProgress;
        set
        {
            if (Math.Abs(_transcriptionProgress - value) < 0.001) return;
            _transcriptionProgress = value;
            OnChanged(nameof(TranscriptionProgress));
            OnChanged(nameof(TranscriptionProgressText));
        }
    }

    public string FileName       => Path.GetFileName(FilePath);
    public string TranscriptPath => FilePath + ".txt";
    public string SrtPath        => FilePath + ".srt";
    public bool   HasTranscript  => File.Exists(TranscriptPath);
    public bool   CanOpenTranscript => HasTranscript || Transcription == TranscriptionStatus.Done;

    public string DisplayDate => CreatedAt.ToString("dd/MM/yyyy · HH:mm");
    public string DurationText
    {
        get
        {
            var d = _duration;
            if (d.TotalHours >= 1)
                return $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s";
            if (d.TotalMinutes >= 1)
                return $"{d.Minutes}m {d.Seconds}s";
            return $"{d.Seconds}s";
        }
    }
    public string SizeText
    {
        get
        {
            double b = _sizeBytes;
            if (b < 1024)                return $"{b:0} B";
            if (b < 1024 * 1024)         return $"{b / 1024.0:0.#} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):0.#} MB";
            return $"{b / (1024.0 * 1024 * 1024):0.##} GB";
        }
    }

    public string TranscriptionIcon => _transcription switch
    {
        TranscriptionStatus.Done    => "✓",
        TranscriptionStatus.Running => "⋯",
        TranscriptionStatus.Queued  => "…",
        TranscriptionStatus.Failed  => "⚠",
        _                            => "📝",
    };

    public string TranscriptionTooltip => _transcription switch
    {
        TranscriptionStatus.Done    => "Transcrição pronta — clicar para abrir",
        TranscriptionStatus.Running => $"Transcrevendo… {_transcriptionProgress:P0}",
        TranscriptionStatus.Queued  => "Na fila de transcrição",
        TranscriptionStatus.Failed  => "Falha na transcrição — clicar para tentar de novo",
        _                            => "Transcrever este áudio",
    };

    public string TranscriptionProgressText =>
        _transcription == TranscriptionStatus.Running
            ? $"{_transcriptionProgress:P0}"
            : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public static class RecordingsStore
{
    private const int Mp3BitRateBps = 192_000;

    public static string Folder
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folder = Path.Combine(docs, "Transcribid");
            Directory.CreateDirectory(folder);
            MigrateLegacyFolder(docs, folder);
            return folder;
        }
    }

    /// <summary>
    /// One-time best-effort migration: if Documents/Recorder exists (from earlier
    /// versions of the app), move its .mp3 files into Documents/Transcribid.
    /// </summary>
    private static bool _migrationDone;
    private static void MigrateLegacyFolder(string docs, string newFolder)
    {
        if (_migrationDone) return;
        _migrationDone = true;
        try
        {
            var legacy = Path.Combine(docs, "Recorder");
            if (!Directory.Exists(legacy) || legacy.Equals(newFolder, StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var f in Directory.EnumerateFiles(legacy, "*.mp3"))
            {
                var dest = Path.Combine(newFolder, Path.GetFileName(f));
                try { if (!File.Exists(dest)) File.Move(f, dest); } catch { }
            }
        }
        catch { /* best-effort, ignore */ }
    }

    public static string NewRecordingPath()
        => Path.Combine(Folder, $"rec-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp3");

    public static List<RecordingItem> LoadAll()
    {
        var items = new List<RecordingItem>();
        if (!Directory.Exists(Folder)) return items;

        foreach (var f in Directory.EnumerateFiles(Folder, "*.mp3"))
        {
            try
            {
                var fi = new FileInfo(f);
                var item = new RecordingItem
                {
                    FilePath  = fi.FullName,
                    CreatedAt = ParseDateFromFileName(fi.Name) ?? fi.CreationTime,
                    SizeBytes = fi.Length,
                    Duration  = EstimateMp3Duration(fi),
                };
                if (item.HasTranscript) item.Transcription = TranscriptionStatus.Done;
                items.Add(item);
            }
            catch { /* skip broken files */ }
        }

        return items.OrderByDescending(r => r.CreatedAt).ToList();
    }

    /// <summary>Parse the timestamp embedded in our filename: rec-YYYY-MM-DD_HH-mm-ss.mp3.</summary>
    private static DateTime? ParseDateFromFileName(string fileName)
    {
        const string prefix = "rec-";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var core = Path.GetFileNameWithoutExtension(fileName).Substring(prefix.Length);
        if (DateTime.TryParseExact(core, "yyyy-MM-dd_HH-mm-ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt;
        return null;
    }

    /// <summary>
    /// CBR 192 kbps stereo is what we write — so duration is just
    /// (file bytes − ID3 header) × 8 / 192000.  Exact, no frame scan needed.
    /// </summary>
    private static TimeSpan EstimateMp3Duration(FileInfo fi)
    {
        try
        {
            long audioBytes = fi.Length;
            // Strip ID3v2 if present (typically a few hundred bytes; doesn't matter
            // much at 192 kbps but we do it for correctness).
            using var s = fi.OpenRead();
            Span<byte> head = stackalloc byte[10];
            if (s.Read(head) == 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            {
                int tagSize = ((head[6] & 0x7F) << 21)
                            | ((head[7] & 0x7F) << 14)
                            | ((head[8] & 0x7F) << 7)
                            |  (head[9] & 0x7F);
                audioBytes -= 10 + tagSize;
            }
            double secs = audioBytes * 8.0 / Mp3BitRateBps;
            return TimeSpan.FromSeconds(Math.Max(0, secs));
        }
        catch
        {
            double secs = fi.Length * 8.0 / Mp3BitRateBps;
            return TimeSpan.FromSeconds(Math.Max(0, secs));
        }
    }
}
