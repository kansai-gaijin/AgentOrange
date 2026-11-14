# Sends commands to the running dev build (debug builds only) and prints what came back.
# Each argument is one command line, as in a headless script:
#   ./tools/drive.ps1 'window "Agents"' 'click "Model and effort"' 'shot picker'
# Pictures are saved in %TEMP%\AgentOrange-Drive and the reply gives each one's path.
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Commands)

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'AgentOrange-Drive', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::CurrentUserOnly)
try {
    $pipe.Connect(5000)
} catch {
    Write-Output 'The dev build isn''t running, or isn''t taking commands.'
    exit 1
}
$writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 4096, $true)
foreach ($command in $Commands) { $writer.WriteLine($command) }
$writer.WriteLine('<<end>>')
$writer.Flush()
$reader = [System.IO.StreamReader]::new($pipe)
Write-Output $reader.ReadToEnd()
$pipe.Dispose()
