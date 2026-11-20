# Sends commands to the running dev build (debug builds only) and prints what came back.
# Each argument is one command line, as in a headless script:
#   ./tools/drive.ps1 'window "Agents"' 'click "Model and effort"' 'shot picker'
#   ./tools/drive.ps1 usage     (each login's limits, without opening the Usage page)
# Pictures are saved in %TEMP%\AgentOrange-Drive and the reply gives each one's path.
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Commands)

# Windows PowerShell 5.1 runs on .NET Framework, which has no CurrentUserOnly. The app's end of the
# pipe is CurrentUserOnly either way, so only this Windows account can connect.
$options = [System.IO.Pipes.PipeOptions]::None
if ([enum]::GetNames([System.IO.Pipes.PipeOptions]) -contains 'CurrentUserOnly') { $options = [System.IO.Pipes.PipeOptions]'CurrentUserOnly' }
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'AgentOrange-Drive', [System.IO.Pipes.PipeDirection]::InOut, $options)
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
# The console's own code page mangles anything past ASCII, such as Japanese or curly quotes.
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Write-Output $reader.ReadToEnd()
$pipe.Dispose()
