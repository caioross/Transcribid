# Relatório ultradetalhado — Transcribid

> Análise feita lendo todo o código-fonte de `AudioRecorder/` linha a linha,
> testando build (`dotnet build -c Release` passa com 0 warnings/0 errors),
> e cruzando o que o nome do projeto promete contra o que está implementado.
>
> Data: 2026-05-15

---

## 1. Visão geral do que existe hoje

O projeto se chama **Transcribid**, mas o que está implementado é um
**gravador de áudio** (`AudioRecorder/`), sem qualquer funcionalidade de
transcrição. Arquitetura atual:

```
AudioRecorder/
├── AudioRecorder.csproj         net8.0-windows, WPF, NAudio 2.2.1, NAudio.Lame 2.1.0
├── app.manifest                 DPI per-monitor V2, long path aware, asInvoker
├── App.xaml + App.xaml.cs       tema escuro + handler global de exception
├── MainWindow.xaml + .cs        UI (timer, botão record, meters MIC/SYS, lista)
├── RecorderEngine.cs            captura WASAPI mic + loopback, mixa, escreve MP3
├── RecordingsStore.cs           lista arquivos em %USERPROFILE%\Documents\Recorder
├── build.bat                    dotnet publish self-contained single-file win-x64
└── run-dev.bat                  dotnet run
```

**Compila limpo.** O recorder em si é funcional num happy-path, mas tem
vários bugs concretos e a feature principal sugerida pelo nome do produto
(**transcrição**) é 100% ausente.

---

## 2. Bugs e falhas — recorder

Numerados por severidade e com referência ao arquivo:linha.

### B1. `TryReadMp3Duration` assume CBR, mas LAME `STANDARD` é VBR

**Arquivo:** [RecordingsStore.cs:104-153](AudioRecorder/RecordingsStore.cs)

A função pega o bitrate do **primeiro frame** e divide o tamanho total do
arquivo por ele. Mas o preset usado em
`RecorderEngine.cs:145` (`LAMEPreset.STANDARD`) é **VBR** — cada frame pode
ter bitrate diferente. O número que ela retorna pode estar errado em
±30%, especialmente em gravações com silêncios longos.

**Como resolver corretamente:** ler o header **XING/INFO** que LAME injeta
no primeiro frame e usar o `frame_count` (campo de 4 bytes que vem após o
ID `Xing`/`Info`). Duração = `frame_count * samples_per_frame /
sample_rate`. Para MPEG-1 Layer III → 1152 samples por frame.

### B2. Race condition Start/Stop com sequência de locks separados

**Arquivo:** [RecorderEngine.cs:190-252](AudioRecorder/RecorderEngine.cs)

`Stop()` segura o lock duas vezes — primeiro pra copiar referências e
zerar `IsRecording`, depois (depois de Cancel + Join + StopRecording +
drain) pra rodar `CleanupInternal()`. Entre os dois locks, se `Start()`
rodasse, ele veria `IsRecording=false`, criaria novos objetos, e
`CleanupInternal()` nullzaria-os no segundo lock.

**Em prática:** Start e Stop são chamados da UI thread → não acontece.
Mas se algum dia esses métodos forem expostos a uma API/teclado/CLI,
explode. Fix: adicionar flag `IsStopping` ou nuclear: rodar a cleanup
inteira dentro de um único lock (mais simples e correto).

### B3. Lambdas de `DataAvailable` referenciam `_loopback!` / `_mic!`

**Arquivo:** [RecorderEngine.cs:82, 105](AudioRecorder/RecorderEngine.cs)

```csharp
var peak = ComputePeak(e.Buffer, e.BytesRecorded, _loopback!.WaveFormat);
```

Depois de `CleanupInternal()` zerar `_loopback = null`, qualquer
callback ainda em voo (NAudio às vezes dispara um último `DataAvailable`
em paralelo com `StopRecording()`) joga `NullReferenceException`. O
`try/catch (Exception)` engole, mas suja o `Error` event.

**Fix:** capturar o `WaveFormat` em variável local na hora do `Start()` e
fechar a lambda sobre ela.

### B4. `ComputePeak` é cego para PCM 24-bit e PCM 32-bit

**Arquivo:** [RecorderEngine.cs:315-342](AudioRecorder/RecorderEngine.cs)

Só trata `IeeeFloat` e `BitsPerSample == 16`. Em shared mode WASAPI quase
sempre entrega Float32, mas em alguns drivers exclusivos / placas
profissionais o formato nativo é 24-bit ou 32-bit PCM — nesses casos o
VU meter fica congelado em zero.

**Fix:** adicionar branches para 24-bit (3 bytes LE, sign-extend) e
32-bit PCM (4 bytes LE).

### B5. Engine nunca dispara `Stopped` em erro fatal

**Arquivo:** [RecorderEngine.cs:288-291, MainWindow.xaml.cs:35](AudioRecorder/RecorderEngine.cs)

Se o `WriterLoop` morrer (ex.: disco cheio, erro de LAME), só dispara
`Error`. A UI continua mostrando "gravando…", timer correndo,
`IsRecording=true`. Usuário tem que clicar Stop pra recuperar.

**Fix:** quando o writer pega uma exceção fatal, chamar uma rotina
`StopFromError()` que faz o mesmo que `Stop()` mas seta uma flag
`HadFatalError` e dispara `Stopped`. A UI lê essa flag e mostra
"Gravação interrompida por erro" em vez de "Gravação salva."

### B6. Mensagem da UI dá falso positivo

**Arquivo:** [MainWindow.xaml.cs:111](AudioRecorder/MainWindow.xaml.cs)

`OnEngineStopped()` sempre seta `FooterText.Text = "Gravação salva."` —
mesmo se o engine parou por erro, mesmo se o MP3 ficou com 0 bytes.

**Fix:** ligado ao B5. Mostrar mensagem baseada em
`engine.HadFatalError` e/ou `fileInfo.Length`.

### B7. Botão Record sem debouncing

**Arquivo:** [MainWindow.xaml.cs:78-101](AudioRecorder/MainWindow.xaml.cs)

Double-click rápido durante a transição Start→Recording: o segundo
clique pode entrar no `Stop()` antes de `UpdateRecordButtonState()`
acontecer, ou ainda pior, Start lança exceção (dispositivo travado),
estado fica inconsistente.

**Fix:** desabilitar o botão (`RecordButton.IsEnabled = false`) por
~250 ms ao redor de cada transição, ou usar uma flag local
`_isTransitioning`.

### B8. Sem fallback se um dos dois inputs morrer

**Arquivo:** [RecorderEngine.cs:268-276](AudioRecorder/RecorderEngine.cs)

O `WriterLoop` faz `Math.Min(loopMs, micMs)`. Se um dos dois para de
entregar dados (mic desconectado, driver crash), o `min` fica 0 e a
gravação **trava silenciosamente** — MP3 não cresce mais, mas a UI não
sabe.

**Fix:** se um dos buffers fica sem dados por > 2 s, marcar aquele
input como "morto" e seguir gravando só com o outro, inserindo silêncio
no canal morto. Disparar `Error` informativo.

### B9. Drain final pode ficar curto

**Arquivo:** [RecorderEngine.cs:222-244](AudioRecorder/RecorderEngine.cs)

Depois de parar capturas, o drain lê `Math.Min(loopBuf, micBuf)`. Mas
ao parar `StopRecording()`, NAudio costuma entregar um último chunk
async via `DataAvailable` que pode chegar **depois** da medida do
`BufferedDuration`. Esse último chunk fica preso nos buffers e nunca é
escrito → últimos ~100 ms da gravação perdidos.

**Fix:** após `StopRecording()`, aguardar até o `RecordingStopped`
event de ambos (com timeout) antes de medir o drain. NAudio garante
que após esse evento não vem mais nada.

### B10. Lock de UI vs lock do engine

**Arquivo:** [MainWindow.xaml.cs:33](AudioRecorder/MainWindow.xaml.cs)

```csharp
_engine.Error += (_, ex) => Dispatcher.Invoke(() => ...);
```

`Error` pode ser disparado de **dentro do lock `_gate` do engine**
(via `Error?.Invoke` na linha 162). `Dispatcher.Invoke` é síncrono. Se
a UI thread estiver bloqueada esperando o lock do engine, deadlock.

**Fix:** usar `Dispatcher.BeginInvoke` (assíncrono) para os handlers
da UI, ou nunca disparar `Error` dentro do lock no engine.

### B11. `_engine` nunca é disposed

**Arquivo:** [MainWindow.xaml.cs:41](AudioRecorder/MainWindow.xaml.cs)

No `Closing` só chama `Stop()`. `Dispose()` chama Stop também, mas o
ponto não é o vazamento (process morre), e sim que `IDisposable` foi
implementado e não é honrado — sinal de descuido.

### B12. Reentrância em `OnEngineStopped` se chamado duas vezes

**Arquivo:** [MainWindow.xaml.cs:103](AudioRecorder/MainWindow.xaml.cs)

Se a engine algum dia disparar `Stopped` duas vezes (não dispara hoje,
mas ligado ao B5 quando dispararmos `Stopped` em erro), `ReloadRecordings()`
roda duas vezes, sem dano real, mas pisca a UI.

### B13. `RecordingsCountText` duplicado em dois lugares

**Arquivo:** [MainWindow.xaml.cs:153, 221](AudioRecorder/MainWindow.xaml.cs)

A mesma lógica de string switch existe em `ReloadRecordings()` e em
`Delete_Click()`. Extrair pra helper.

### B14. `MainWindow` reage estranho a animação parada

**Arquivo:** [MainWindow.xaml.cs:128-142](AudioRecorder/MainWindow.xaml.cs)

`AnimateStatusDot(false)` faz `BeginAnimation(null)` e seta
`Opacity=1`. Funciona, mas se vier um `Stopped` no meio da animação,
às vezes o dot fica num frame intermediário. Set também o `Fill` é OK.
Detalhe cosmético.

### B15. Build single-file não inclui `IncludeAllContentForSelfExtract`

**Arquivo:** [build.bat:29](AudioRecorder/build.bat)

Inclui native libs mas não todo o conteúdo. Para LAME nativo
(`libmp3lame.32.dll` / `.64.dll`) o `IncludeNativeLibrariesForSelfExtract`
basta, então OK. Mas vale documentar.

### B16. `WasapiOut` com SilenceProvider pode confundir o mixer do Windows

**Arquivo:** [RecorderEngine.cs:151-158](AudioRecorder/RecorderEngine.cs)

`WasapiOut(AudioClientShareMode.Shared, 100)` em modo padrão renderiza
no **default render device**. Se o usuário trocar de saída padrão
durante a gravação (plugar fone), o keep-alive continua no antigo e o
loopback novo não recebe keep-alive. Reset não acontece.

**Fix:** assinar `MMDeviceEnumerator`/`NotificationClient` para reagir
a mudança de default device — re-iniciar keep-alive e loopback no novo.

### B17. Sem ícone de aplicação

`AudioRecorder.csproj` não define `<ApplicationIcon>` e não há `.ico`.
O .exe sai com o ícone genérico do .NET.

### B18. Sem versão de arquivo / company / copyright

Apenas `<Version>1.0.0</Version>`. Falta `<FileVersion>`,
`<AssemblyVersion>`, `<Company>`, `<Copyright>`.

### B19. `Stopped` event invoca handlers fora do `lock`

Não é bug — é prática boa — mas vale documentar.

---

## 3. Lacunas — funcionalidade que falta para o produto fazer jus ao nome "Transcribid"

### L1. Não há nenhuma transcrição

Maior gap. O produto se chama Transcribid e não transcreve nada. Para
ficar **state of the art** (local, offline, sem API key, sem custo
por minuto, alta qualidade):

- **Engine:** `Whisper.net` (NuGet `Whisper.net` + `Whisper.net.Runtime`)
- **Modelo padrão:** `ggml-small.bin` (~466 MB, balanceia qualidade e
  velocidade em CPU comum). Permitir trocar para `medium` (~1.5 GB,
  qualidade superior) via setting.
- **Hardware accel:** `Whisper.net.Runtime.Cuda` para usuário com NVIDIA
  (opcional, peso grande no instalador).
- **Idioma:** detecção automática + override manual (pt, en, es, etc.).
- **Saídas:** transcript em texto puro + `.srt` legendado com
  timestamps por segmento.
- **Persistência:** salvar `rec-YYYY-...mp3.txt` e `rec-YYYY-...mp3.srt`
  ao lado do MP3. Status (pendente / transcrevendo / pronto / erro)
  visível na UI.

### L2. Não baixa o modelo automaticamente

Whisper.net não traz modelos embutidos (modelos têm centenas de MB).
Precisamos baixar de `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin`
**na primeira vez** que o usuário pedir uma transcrição, com barra de
progresso. Cache em `%LOCALAPPDATA%\Transcribid\models\`.

### L3. Sem UI de transcrição

A lista de gravações precisa de:
- Indicador de status (📝 transcrita, ⏳ na fila, ⚠ erro).
- Botão "Transcrever" por linha + "Transcrever todas".
- Modal/painel para ver o texto, copiar, exportar `.txt`/`.srt`.

### L4. Sem fila / processamento assíncrono

Transcrição em CPU pode levar 0.3×–1.5× o tempo do áudio. Tem que rodar
**em background** sem travar a UI, com cancelamento, com 1 job de cada
vez (Whisper já é multi-thread internamente — paralelizar duas
transcrições simultâneas só piora).

### L5. Sem settings / preferências

Onde o usuário escolhe: tamanho do modelo, idioma padrão, pasta de
saída, qualidade do MP3, se deve auto-transcrever ao final da gravação.
Hoje tudo é hardcoded. Mínimo: arquivo JSON em
`%APPDATA%\Transcribid\settings.json`.

### L6. Sem auto-transcrever após Stop

Fluxo natural: usuário grava → para → quer o texto. Devia rolar
automaticamente (com opt-in nas configurações).

### L7. Sem busca dentro de transcrições

Não crítico pra v1 mas é o killer feature de produto de transcrição:
"achar aquela palavra dita semana passada". Para v1 deixar
`grep`-friendly (salvar `.txt`).

### L8. Sem atalhos de teclado

`Space` pra iniciar/parar, `Ctrl+R` pra refresh, `Delete` pra apagar,
etc. Hoje só mouse.

### L9. Sem indicador de uso de disco / quanto cabe

192 kbps × 1 hora = ~84 MB. Em sessões longas, vale mostrar espaço
livre do disco no footer.

### L10. Sem tray icon / minimize to tray

Para sessões longas (reuniões de 2 h), fechar/minimizar e o ícone na
bandeja indicando "gravando" é o padrão de produto.

### L11. Sem teste de microfone antes da gravação

Botão "Testar mic" que liga capture por 3 s mostrando o meter — útil
pra confirmar que o dispositivo certo está selecionado.

### L12. Sem seleção de dispositivo

Hoje pega o **default** do Windows. Em laptops com webcam, mic-array,
fone Bluetooth e USB simultâneos, o default nem sempre é o que o
usuário quer. UI de dropdown selecionando dispositivos seria padrão.

---

## 4. Mau implementado / parcial

### M1. README documenta atalhos que não existem

> "## Atalhos & detalhes técnicos"

A seção começa com "Atalhos" mas só lista detalhes técnicos. Nenhum
atalho real. Confunde o leitor — ou remover "Atalhos &" do título ou
adicionar atalhos reais (ver L8).

### M2. Comentário sobre "ExtraSize/AverageBytesPerSecond" em
NormalizeToStereo48k está parcialmente correto mas verbose demais

O comentário em `RecorderEngine.cs:115-122` ocupa 8 linhas explicando
algo que poderia ser uma frase. Trocar por algo conciso.

### M3. Pasta hardcoded "Recorder" em Documents

Se vamos chamar o produto de Transcribid, a pasta devia ser
`Documents/Transcribid/` e não `Documents/Recorder/`. Manter
compatibilidade migrando o conteúdo se a pasta antiga existir.

### M4. Logging inexistente

Erros só vão pro footer e pro MessageBox. Para diagnóstico de campo,
gravar log circular em `%LOCALAPPDATA%\Transcribid\log.txt` (mantendo
últimas N linhas / N MB) ajuda muito quando o usuário reporta bug.

### M5. `AudioRecorder.csproj` `<AssemblyName>Recorder</AssemblyName>`

Output é `Recorder.exe`. Para virar Transcribid, mudar pra
`Transcribid` (impacta `build.bat`, README, atalhos do usuário).

### M6. Sem instalação / sem MSI / sem auto-update

`build.bat` produz um EXE. Não tem instalador nem mecanismo de
atualização. Para v1 OK, mas falta. Sugestão: WiX ou Velopack.

### M7. ScrollBar style tem propriedades default mas não estiliza
o thumb

`App.xaml:99-102` define Width=8 mas não define o template — o thumb
sai com cor padrão do Windows e contrasta com o tema escuro.

---

## 5. State of the art — visão de produto completo

Para Transcribid v1.0 ficar "como deve ser":

```
┌─────────────────────────────────────────────────────────┐
│ Transcribid                                  📁  ↻   ⚙  │
├─────────────────────────────────────────────────────────┤
│                                                         │
│                     00:00:00                            │
│                                                         │
│                  [● Iniciar gravação]                   │
│                                                         │
│   MIC ████████░░░░░░░░░░░░                              │
│   SYS ██░░░░░░░░░░░░░░░░░░                              │
│                                                         │
├─────────────────────────────────────────────────────────┤
│ GRAVAÇÕES   3 arquivos                                  │
│                                                         │
│ ┌─────────────────────────────────────────────────────┐ │
│ │ 15/05/2026 · 14:32        ▶  📝 ✓  📁  🗑          │ │
│ │ 23m 14s · 31.2 MB                                  │ │
│ │ "...então o plano é começar pelo onboarding..."    │ │
│ └─────────────────────────────────────────────────────┘ │
│ ┌─────────────────────────────────────────────────────┐ │
│ │ 15/05/2026 · 13:05        ▶  📝 ⏳ 12% 📁  🗑      │ │
│ │ 1h 4m · 86 MB                                      │ │
│ └─────────────────────────────────────────────────────┘ │
│ ┌─────────────────────────────────────────────────────┐ │
│ │ 14/05/2026 · 09:18        ▶  📝 📋  📁  🗑         │ │
│ │ 8m 22s · 12.1 MB                                   │ │
│ └─────────────────────────────────────────────────────┘ │
│                                                         │
├─────────────────────────────────────────────────────────┤
│ pronto · 47 GB livres no disco · modelo: small (PT)     │
└─────────────────────────────────────────────────────────┘
```

Onde `📝 ✓` = transcrita, `📝 ⏳` = na fila com %, `📝 📋` = clicar
pra transcrever.

Componentes técnicos novos:

```
RecorderEngine  ─────────►  rec-...mp3
                              │
                              ▼
                      TranscriptionService
                       (fila, Whisper.net)
                              │
                              ▼
                    rec-...mp3.txt   rec-...mp3.srt
                              │
                              ▼
                       MainWindow / UI
                       (status por linha)
```

---

## 6. Plano de execução

1. **Corrigir bugs do recorder** (B1–B16, exceto cosmética).
2. **Renomear assembly** para `Transcribid` (M5), pasta de gravações
   para `Documents/Transcribid/` (M3).
3. **Adicionar transcrição local** com Whisper.net (L1).
4. **Download de modelo on-demand** com progresso (L2).
5. **UI de status / ações de transcrição** por gravação (L3).
6. **Fila assíncrona** com cancelamento (L4).
7. **Settings persistido** em JSON (L5).
8. **Auto-transcribe opcional** após Stop (L6).
9. **Logging em arquivo circular** (M4).
10. **Re-análise: ler tudo de novo, achar o que escapou.**
11. **Build limpo** e verificação.

Itens cosméticos (B17/B18, ícone, atalhos L8, tray L10, seleção de
dispositivo L12) ficam pra v1.1 — não bloqueiam "funcionar como deve".
