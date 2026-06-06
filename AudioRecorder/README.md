# Transcribid

Gravador de áudio para Windows que captura **microfone + áudio do sistema simultaneamente** em um único `.mp3`, e **transcreve localmente** com Whisper (sem nuvem, sem API key, sem custo por minuto). Funciona com Meet, Discord, Zoom, navegador — qualquer coisa — porque captura direto do dispositivo de saída do Windows (loopback WASAPI).

## Recursos

- Captura **mic** (sua voz) + **sistema** (tudo que você ouve) em UM mp3
- **Transcrição local com Whisper.net** — multilíngue, offline
- Auto-transcrição opcional ao parar a gravação
- Streaming pra disco em tempo real — aguenta horas sem encher memória
- VU meters separados pra mic e sistema
- Watchdog: se mic ou sistema parar de entregar áudio no meio da gravação, segue gravando só com o outro (sem travar)
- "Silence keep-alive" pra garantir que o loopback continue rodando mesmo em silêncio total
- Lista de gravações com data, duração, tamanho, status de transcrição
- Tema escuro minimalista
- Compilado pra um único `.exe` self-contained (não precisa instalar .NET na máquina alvo)

## Como compilar

### Pré-requisitos

Apenas **.NET 8 SDK**: <https://dotnet.microsoft.com/download/dotnet/8.0>

### Build

Clique duas vezes em **`build.bat`**.

Gera o executável em:

```
bin\publish\Transcribid.exe
```

Esse `.exe` é self-contained: pode levar pra qualquer Windows 10/11 x64 sem instalar nada.

### Modo desenvolvimento

`run-dev.bat` — roda direto sem fazer publish.

## Como funciona

```
┌──────────┐   WasapiCapture    ┌────────────────┐
│   Mic    │───────────────────▶│ BufferedWave   │─┐
└──────────┘                    └────────────────┘ │
                                                   ▼
                                          ┌──────────────┐    ┌─────────┐
                                          │ Mixer 48k    │───▶│  LAME   │──▶ rec.mp3
                                          │ stereo PCM   │    │ CBR 192 │
                                          └──────────────┘    └────┬────┘
                                                   ▲               │
┌──────────┐  WasapiLoopback    ┌────────────────┐ │               ▼
│ Speakers │───────────────────▶│ BufferedWave   │─┘     TranscriptionService
└──────────┘                    └────────────────┘            (Whisper.net)
                                                                   │
                                                                   ▼
                                                          rec.mp3.txt + .srt
```

A thread de escrita só consome do mixer o que está bufferizado em **ambos** os streams — o MP3 acompanha o tempo real. CBR 192 kbps stereo 48 kHz garante que a duração do arquivo é exatamente `bytes × 8 / 192000`, sem precisar varrer headers VBR.

## Transcrição

A primeira transcrição **baixa automaticamente o modelo Whisper** (~466 MB pro modelo `small`) de Hugging Face para `%LOCALAPPDATA%\Transcribid\models\`. Próximas transcrições reusam.

O resultado é gravado **ao lado do .mp3**:

- `rec-...mp3.txt` — texto puro
- `rec-...mp3.srt` — legendado com timestamps por segmento

Configurações (modelo, idioma, auto-transcrever) ficam em `%APPDATA%\Transcribid\settings.json`. Clique no ⚙ na barra superior pra abrir.

```json
{
  "Language": "auto",
  "ModelSize": "Small",
  "AutoTranscribe": true,
  "WriteSrt": true
}
```

Modelos suportados: `Tiny`, `Base`, `Small`, `Medium`, `LargeV3`.

## Onde ficam os arquivos

- Gravações: `%USERPROFILE%\Documents\Transcribid\rec-YYYY-MM-DD_HH-mm-ss.mp3`
- Settings: `%APPDATA%\Transcribid\settings.json`
- Modelos Whisper: `%LOCALAPPDATA%\Transcribid\models\`

Se você usou uma versão antiga em `Documents\Recorder\`, os arquivos são migrados automaticamente na primeira execução.

## Detalhes técnicos

- Format final: MP3 CBR 192 kbps stereo 48 kHz
- Resampling automático se mic e sistema rodarem em sample rates diferentes
- Mono → stereo automático; 5.1/7.1 → stereo (canais 0 e 1)
- Headroom: mic 0.95 / sistema 0.85 pra evitar clipping no mix
- Whisper decodifica via NAudio → 16 kHz mono 16-bit PCM (formato esperado pelo `whisper.cpp`)

## Problemas comuns

- **"Não está gravando o sistema"** → verifique se o dispositivo de saída padrão é o mesmo que está tocando o áudio (Meet/Discord). Configure em *Configurações → Sistema → Som*.
- **"Mic não aparece"** → o app pega o dispositivo de captura padrão do Windows. Defina seu mic como padrão em *Configurações → Sistema → Som → Entrada*.
- **"Travou em 30% no modelo"** → o download do modelo pode demorar em conexões lentas. O download é retomado em arquivo `.part` — não trava o app.
- **Trocar o dispositivo de saída padrão durante a gravação** não é suportado nesta versão; o keep-alive continua no dispositivo antigo. Recomendação: defina o destino antes de iniciar.

## Estrutura do projeto

```
AudioRecorder/
├── AudioRecorder.csproj         NAudio + NAudio.Lame + Whisper.net
├── app.manifest                 DPI per-monitor V2, long-path aware
├── App.xaml / .cs               tema + handler global de exceção
├── MainWindow.xaml / .cs        UI + wiring
├── RecorderEngine.cs            captura + mix + MP3 (com watchdog + recovery)
├── RecordingsStore.cs           lista gravações + duração CBR exata
├── AppSettings.cs               settings persistido em JSON
├── WhisperModelManager.cs       resolve / baixa modelo GGML
├── TranscriptionService.cs      fila + worker Whisper.net
├── build.bat                    compila pra single-file .exe
└── run-dev.bat                  roda direto
```
