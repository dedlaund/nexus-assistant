param([ValidateSet('listen', 'speak')][string]$Mode)

$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

try {
    if ($Mode -eq 'speak') {
        $synth = New-Object -ComObject SAPI.SpVoice
        $voices = $synth.GetVoices('Language=419', '')
        if ($voices.Count -eq 0) { [Console]::WriteLine('NO_VOICE'); exit 0 }
        $synth.Voice = $voices.Item(0)
        $audio = New-Object -ComObject SAPI.SpMemoryStream
        $format = New-Object -ComObject SAPI.SpAudioFormat
        $format.Type = 18 # SAFT16kHz16BitMono
        $audio.Format = $format
        $synth.AudioOutputStream = $audio
        $null = $synth.Speak([Console]::In.ReadToEnd(), 16) # SVSFIsNotXML
        [byte[]]$pcm = $audio.GetData()
        $wave = [System.IO.MemoryStream]::new()
        $writer = [System.IO.BinaryWriter]::new($wave)
        try {
            $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
            $writer.Write([int]($pcm.Length + 36))
            $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
            $writer.Write([int]16)
            $writer.Write([uint16]1)
            $writer.Write([uint16]1)
            $writer.Write([int]16000)
            $writer.Write([int]32000)
            $writer.Write([uint16]2)
            $writer.Write([uint16]16)
            $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
            $writer.Write([int]$pcm.Length)
            $writer.Write($pcm)
            [Console]::WriteLine([Convert]::ToBase64String($wave.ToArray()))
        } finally {
            $writer.Dispose()
        }
        exit 0
    }

    Add-Type -AssemblyName System.Speech
    $info = [System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers() |
        Where-Object { $_.Culture.TwoLetterISOLanguageName -eq 'ru' } | Select-Object -First 1
    if (!$info) { [Console]::WriteLine('NO_RECOGNIZER'); exit 0 }
    $recognizer = [System.Speech.Recognition.SpeechRecognitionEngine]::new($info)
    try {
        $recognizer.SetInputToDefaultAudioDevice()
        $recognizer.InitialSilenceTimeout = [TimeSpan]::FromSeconds(7)
        $recognizer.EndSilenceTimeout = [TimeSpan]::FromMilliseconds(600)
        $recognizer.LoadGrammar([System.Speech.Recognition.DictationGrammar]::new())
        $null = Register-ObjectEvent -InputObject $recognizer -EventName AudioLevelUpdated -SourceIdentifier level
        $null = Register-ObjectEvent -InputObject $recognizer -EventName RecognizeCompleted -SourceIdentifier done
        $recognizer.RecognizeAsync([System.Speech.Recognition.RecognizeMode]::Single)
        [Console]::WriteLine('READY')
        $deadline = [DateTime]::UtcNow.AddSeconds(18)
        while ([DateTime]::UtcNow -lt $deadline) {
            $result = Get-Event -SourceIdentifier done -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($result) {
                $recognition = $result.SourceEventArgs
                if ($recognition.Error) { throw $recognition.Error }
                if ($recognition.Result -and $recognition.Result.Confidence -ge 0.35) {
                    [Console]::WriteLine('TEXT ' + $recognition.Result.Text)
                }
                break
            }
            $levels = @(Get-Event -SourceIdentifier level -ErrorAction SilentlyContinue)
            if ($levels.Count) {
                [Console]::WriteLine('LEVEL ' + $levels[-1].SourceEventArgs.AudioLevel)
                $levels | Remove-Event
            }
            Start-Sleep -Milliseconds 45
        }
    } finally {
        Unregister-Event -SourceIdentifier level -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier done -ErrorAction SilentlyContinue
        $recognizer.RecognizeAsyncCancel()
        $recognizer.Dispose()
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
