using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AudioRecorder;

public partial class MainWindow : Window
{
    private readonly RecorderEngine _engine = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TranscriptionService _transcription;

    public ObservableCollection<RecordingItem> Recordings { get; } = new();

    private float _micLevelTarget;
    private float _sysLevelTarget;
    private float _micLevel;
    private float _sysLevel;

    public MainWindow()
    {
        InitializeComponent();

        _transcription = new TranscriptionService(_settings);
        _transcription.StatusChanged += OnTranscriptionStatusChanged;

        RecordingsList.ItemsSource = Recordings;

        _engine.MicLevel    += (_, v) => _micLevelTarget = v;
        _engine.SystemLevel += (_, v) => _sysLevelTarget = v;
        // BeginInvoke (async) avoids deadlocks if Error fires from inside the engine lock.
        _engine.Error       += (_, ex) => Dispatcher.BeginInvoke(new Action(() =>
            FooterText.Text = "Aviso: " + ex.Message));
        _engine.Stopped     += (_, _) => Dispatcher.BeginInvoke(new Action(OnEngineStopped));

        _uiTimer.Tick += UiTick;
        _uiTimer.Start();

        Loaded   += (_, _) => ReloadRecordings();
        Closing  += (_, _) =>
        {
            try { _engine.Dispose(); } catch { }
            try { _transcription.Dispose(); } catch { }
            try { _uiTimer.Stop(); } catch { }
        };

        UpdateRecordButtonState();
        StatusDot.Fill = (Brush)FindResource("TextMuted");
        FooterText.Text = $"modelo: {_settings.ModelSize.ToString().ToLowerInvariant()} · idioma: {_settings.Language}";
    }

    // ----------------------------------------------------------------- timer

    private void UiTick(object? sender, EventArgs e)
    {
        if (_engine.IsRecording)
        {
            var t = _engine.Elapsed;
            TimerText.Text = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        _micLevel = Math.Max(_micLevelTarget, _micLevel * 0.78f);
        _sysLevel = Math.Max(_sysLevelTarget, _sysLevel * 0.78f);
        _micLevelTarget *= 0.5f;
        _sysLevelTarget *= 0.5f;

        UpdateMeter(MicLevelBar, _micLevel);
        UpdateMeter(SysLevelBar, _sysLevel);
    }

    private void UpdateMeter(FrameworkElement bar, float level)
    {
        var parent = bar.Parent as FrameworkElement;
        var fullWidth = (parent?.ActualWidth ?? 0);
        if (fullWidth <= 0) return;
        double w = Math.Clamp(level, 0, 1) * fullWidth;
        bar.Width = w;
    }

    // ----------------------------------------------------------------- buttons

    private bool _transitioning;
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_transitioning) return;
        _transitioning = true;
        try { RecordButton.IsEnabled = false; } catch { }

        try
        {
            if (_engine.IsRecording)
            {
                try { _engine.Stop(); }
                catch (Exception ex) { ShowError("Erro ao parar: " + ex.Message); }
                return;
            }

            try
            {
                var path = RecordingsStore.NewRecordingPath();
                _engine.Start(path);
                UpdateRecordButtonState();
                HeaderHint.Text = "gravando…";
                StatusDot.Fill = (Brush)FindResource("AccentRed");
                FooterText.Text = "Salvando em: " + path;
                AnimateStatusDot(true);
            }
            catch (Exception ex)
            {
                ShowError("Erro ao iniciar: " + ex.Message);
            }
        }
        finally
        {
            // Re-enable shortly so a quick toggle won't run twice.
            var dt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            dt.Tick += (s, _) =>
            {
                dt.Stop();
                _transitioning = false;
                RecordButton.IsEnabled = true;
            };
            dt.Start();
        }
    }

    private void OnEngineStopped()
    {
        UpdateRecordButtonState();
        HeaderHint.Text = "pronto";
        StatusDot.Fill = (Brush)FindResource("TextMuted");
        AnimateStatusDot(false);
        TimerText.Text = "00:00:00";
        var lastPath = _engine.CurrentFilePath;
        ReloadRecordings();

        if (_engine.HadFatalError)
        {
            FooterText.Text = "Gravação interrompida por erro — o arquivo pode estar incompleto.";
        }
        else
        {
            FooterText.Text = "Gravação salva.";
            if (_settings.AutoTranscribe && !string.IsNullOrEmpty(lastPath) && File.Exists(lastPath))
            {
                if (_transcription.Enqueue(lastPath))
                {
                    var item = Recordings.FirstOrDefault(r =>
                        r.FilePath.Equals(lastPath, StringComparison.OrdinalIgnoreCase));
                    if (item != null) item.Transcription = TranscriptionStatus.Queued;
                    FooterText.Text = "Gravação salva. Transcrição enfileirada.";
                }
            }
        }
    }

    private void UpdateRecordButtonState()
    {
        if (_engine.IsRecording)
        {
            RecordButton.Content = "■  Parar";
            RecordButton.Style = (Style)FindResource("StopButtonStyle");
        }
        else
        {
            RecordButton.Content = "●  Iniciar gravação";
            RecordButton.Style = (Style)FindResource("RecordButtonStyle");
        }
    }

    private void AnimateStatusDot(bool on)
    {
        StatusDot.BeginAnimation(UIElement.OpacityProperty, null);
        if (!on)
        {
            StatusDot.Opacity = 1;
            return;
        }
        var anim = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(800))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        StatusDot.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // ----------------------------------------------------------------- list

    private void ReloadRecordings()
    {
        Recordings.Clear();
        foreach (var item in RecordingsStore.LoadAll())
        {
            if (_transcription.IsQueued(item.FilePath) &&
                item.Transcription == TranscriptionStatus.None)
                item.Transcription = TranscriptionStatus.Queued;
            Recordings.Add(item);
        }
        UpdateListCounters();
    }

    private void UpdateListCounters()
    {
        EmptyHint.Visibility = Recordings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordingsCountText.Text = Recordings.Count switch
        {
            0 => "",
            1 => "1 arquivo",
            _ => $"{Recordings.Count} arquivos",
        };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => ReloadRecordings();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = RecordingsStore.Folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Make sure the file exists on disk.
            _settings.Save();
            Process.Start(new ProcessStartInfo
            {
                FileName        = AppSettings.SettingsPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecordingItem r }) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = r.FilePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecordingItem r }) return;

        // If the transcript is already on disk → open it.
        if (r.HasTranscript)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = r.TranscriptPath,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { ShowError(ex.Message); }
            return;
        }

        // Otherwise queue (or re-queue on failure).
        if (r.Transcription == TranscriptionStatus.Running ||
            r.Transcription == TranscriptionStatus.Queued)
        {
            return;
        }

        if (_transcription.Enqueue(r.FilePath))
        {
            r.Transcription = TranscriptionStatus.Queued;
            r.TranscriptionProgress = 0;
            FooterText.Text = "Transcrição enfileirada.";
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecordingItem r }) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName  = "explorer.exe",
                Arguments = $"/select,\"{r.FilePath}\"",
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecordingItem r }) return;

        var ok = MessageBox.Show(
            $"Excluir \"{r.FileName}\"? Essa ação não pode ser desfeita.",
            "Transcribid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        try
        {
            // Delete the MP3 and any transcript/SRT siblings.
            if (File.Exists(r.FilePath))    File.Delete(r.FilePath);
            if (File.Exists(r.TranscriptPath)) File.Delete(r.TranscriptPath);
            if (File.Exists(r.SrtPath))     File.Delete(r.SrtPath);
            Recordings.Remove(r);
            UpdateListCounters();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ----------------------------------------------------------------- transcription events

    private void OnTranscriptionStatusChanged(object? sender, TranscriptionProgressEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var item = Recordings.FirstOrDefault(r =>
                r.FilePath.Equals(e.FilePath, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;

            item.Transcription = e.Status;
            item.TranscriptionProgress = e.Progress;

            switch (e.Status)
            {
                case TranscriptionStatus.Running:
                    FooterText.Text = e.Message ?? "Transcrevendo…";
                    break;
                case TranscriptionStatus.Done:
                    FooterText.Text = $"Transcrição salva: {Path.GetFileName(item.TranscriptPath)}";
                    break;
                case TranscriptionStatus.Failed:
                    FooterText.Text = "Falha na transcrição: " + (e.Message ?? "erro desconhecido");
                    break;
            }
        }));
    }

    // ----------------------------------------------------------------- helpers

    private void ShowError(string message)
        => MessageBox.Show(message, "Transcribid", MessageBoxButton.OK, MessageBoxImage.Error);
}
