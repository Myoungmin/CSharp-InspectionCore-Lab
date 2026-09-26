function Invoke-RecordedProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$LogPrefix,
        [int]$TimeoutSeconds = 120
    )
    # Quote arguments using Windows command-line escaping, including trailing backslashes.
    $quoted = foreach ($argument in $Arguments) {
        '"' + (($argument -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
    }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.Arguments = $quoted -join ' '
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $info.EnvironmentVariables['DOTNET_CLI_UI_LANGUAGE'] = 'en'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Failed to start $FilePath" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            & taskkill.exe /PID $process.Id /T /F | Out-Null
            $process.WaitForExit()
        }
        $outText = $stdout.GetAwaiter().GetResult()
        $errText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText("$LogPrefix.stdout.log", $outText, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText("$LogPrefix.stderr.log", $errText, [Text.UTF8Encoding]::new($false))
        if ($timedOut) { throw "Process exceeded ${TimeoutSeconds}s: $FilePath. See $LogPrefix.*.log" }
        [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $outText; Stderr = $errText }
    }
    finally { $process.Dispose() }
}
