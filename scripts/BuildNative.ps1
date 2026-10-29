param([switch]$Checks)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $root '.work/windows-sdk'
$sdkVersion = '10.0.26100.9169'
foreach ($package in 'microsoft.windows.sdk.cpp','microsoft.windows.sdk.cpp.x64') {
    $destination = Join-Path $sdk $package
    if (!(Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Force -Path $sdk | Out-Null
        $archive = Join-Path $sdk "$package.nupkg"
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$package/$sdkVersion/$package.$sdkVersion.nupkg" -OutFile $archive
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $destination)
    }
}
$vswhere = 'C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Install Visual Studio C++ x64 build tools before building the microphone component.' }
$vc = Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$compiler = Join-Path $vc.FullName 'bin/Hostx64/x64/cl.exe'
$includeRoot = Join-Path $sdk 'microsoft.windows.sdk.cpp/c/Include/10.0.26100.0'
$libRoot = Join-Path $sdk 'microsoft.windows.sdk.cpp.x64/c'
$env:INCLUDE = "$($vc.FullName)/include;$includeRoot/ucrt;$includeRoot/shared;$includeRoot/um;$includeRoot/winrt"
$env:LIB = "$($vc.FullName)/lib/x64;$libRoot/ucrt/x64;$libRoot/um/x64"
$out = Join-Path $root 'outputs/native'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Push-Location $out
try {
    $source = Join-Path $root 'src/Soundshark.AudioNative/SoundsharkApo.cpp'
    & $compiler /nologo /std:c++17 /O2 /MT /EHsc /W4 /DUNICODE /D_UNICODE /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /LD $source /Fe:SoundsharkMicrophone.dll /link /MANIFEST:NO "/DEF:$root/src/Soundshark.AudioNative/SoundsharkMicrophone.def" audiobaseprocessingobject.lib ole32.lib uuid.lib advapi32.lib propsys.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native APO compilation failed.' }
    if ($Checks) {
        & $compiler /nologo /std:c++17 /O2 /MT /EHsc /W4 /DUNICODE /D_UNICODE (Join-Path $root 'tests/Soundshark.NativeChecks/Checks.cpp') /Fe:SoundsharkNativeChecks.exe /link audiobaseprocessingobject.lib ole32.lib uuid.lib advapi32.lib propsys.lib
        if ($LASTEXITCODE -ne 0) { throw 'Native check compilation failed.' }
        & ./SoundsharkNativeChecks.exe
        if ($LASTEXITCODE -ne 0) { throw 'Native checks failed.' }
    }
} finally { Pop-Location }
