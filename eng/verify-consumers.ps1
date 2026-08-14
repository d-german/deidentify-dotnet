[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$targets = @(
    @{ Project = "eng\consumers\Deidentify.WebConsumer\Deidentify.WebConsumer.csproj"; Runtime = "win-x64"; Output = "artifacts\publish\web-win-x64" },
    @{ Project = "eng\consumers\Deidentify.WebConsumer\Deidentify.WebConsumer.csproj"; Runtime = "linux-x64"; Output = "artifacts\publish\web-linux-x64" },
    @{ Project = "eng\consumers\Deidentify.WpfConsumer\Deidentify.WpfConsumer.csproj"; Runtime = "win-x64"; Output = "artifacts\publish\wpf-win-x64" }
)

foreach ($target in $targets) {
    dotnet publish $target.Project -c Release -r $target.Runtime --self-contained true -o $target.Output
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not (Test-Path (Join-Path $target.Output "Models\bert-base-ner\model.onnx"))) {
        throw "NER model is missing from $($target.Output)."
    }
}

$env:DEIDENTIFY_SMOKE = "1"
try {
    & artifacts\publish\wpf-win-x64\Deidentify.WpfConsumer.exe
    if ($LASTEXITCODE -ne 0) {
        throw "The self-contained WPF consumer smoke test failed."
    }
}
finally {
    Remove-Item Env:\DEIDENTIFY_SMOKE -ErrorAction SilentlyContinue
}

$env:ASPNETCORE_URLS = "http://127.0.0.1:5099"
$web = Start-Process `
    -FilePath artifacts\publish\web-win-x64\Deidentify.WebConsumer.exe `
    -PassThru `
    -WindowStyle Hidden
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            Invoke-RestMethod -Uri "http://127.0.0.1:5099/" -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $ready) {
        throw "The self-contained web consumer did not start."
    }

    $response = Invoke-RestMethod `
        -Uri "http://127.0.0.1:5099/sanitize" `
        -Method Post `
        -ContentType "application/json" `
        -Body '{"text":"Sarah uses test.user@example.test in London."}'
    if ($response.text -notmatch '<PERSON>' -or
        $response.text -notmatch '<EMAIL_ADDRESS>' -or
        $response.text -notmatch '<LOCATION>') {
        throw "The self-contained web consumer returned an unexpected result."
    }
}
finally {
    if (-not $web.HasExited) {
        Stop-Process -Id $web.Id
    }
    Remove-Item Env:\ASPNETCORE_URLS -ErrorAction SilentlyContinue
}
