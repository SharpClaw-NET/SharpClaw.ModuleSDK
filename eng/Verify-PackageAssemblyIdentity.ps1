 [CmdletBinding()]
 param(
     [Parameter(Mandatory = $true)]
     [string]$ModuleSdkPackage,

     [Parameter(Mandatory = $true)]
     [string]$InProcessPackage,

     [Parameter(Mandatory = $true)]
     [string]$OutOfProcessPackage
 )

 $ErrorActionPreference = 'Stop'
 Add-Type -AssemblyName System.IO.Compression.FileSystem

 function Get-ZipEntry {
     param(
         [System.IO.Compression.ZipArchive]$Archive,
         [string]$Path
     )

     $matches = @($Archive.Entries | Where-Object { $_.FullName -eq $Path })
     if ($matches.Count -ne 1) {
         throw "Expected one archive entry '$Path', found $($matches.Count)."
     }

     return $matches[0]
 }

 function Get-ZipEntryHash {
     param(
         [System.IO.Compression.ZipArchive]$Archive,
         [string]$Path
     )

     $entry = Get-ZipEntry -Archive $Archive -Path $Path
     $sha = [System.Security.Cryptography.SHA256]::Create()
     $stream = $entry.Open()
     try {
         return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '')
     }
     finally {
         $stream.Dispose()
         $sha.Dispose()
     }
 }

 foreach ($package in @($ModuleSdkPackage, $InProcessPackage, $OutOfProcessPackage)) {
     if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
         throw "Package does not exist: $package"
     }
 }

 $sdkArchive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ModuleSdkPackage).Path)
 $inProcessArchive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $InProcessPackage).Path)
 $outOfProcessArchive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $OutOfProcessPackage).Path)
 try {
     $sdkPackageHash = Get-ZipEntryHash -Archive $sdkArchive -Path 'lib/net10.0/SharpClaw.ModuleSDK.dll'
     $sdkPayloadHash = Get-ZipEntryHash -Archive $outOfProcessArchive -Path 'tools/net10.0/any/SharpClaw.ModuleSDK.dll'
     $inProcessPackageHash = Get-ZipEntryHash -Archive $inProcessArchive -Path 'lib/net10.0/SharpClaw.SidecarHost.InProcess.dll'
     $inProcessPayloadHash = Get-ZipEntryHash -Archive $outOfProcessArchive -Path 'tools/net10.0/any/SharpClaw.SidecarHost.InProcess.dll'

     if ($sdkPackageHash -ne $sdkPayloadHash) {
         throw "ModuleSDK package and OutOfProcess payload hashes differ: $sdkPackageHash != $sdkPayloadHash."
     }

     if ($inProcessPackageHash -ne $inProcessPayloadHash) {
         throw "InProcess package and OutOfProcess payload hashes differ: $inProcessPackageHash != $inProcessPayloadHash."
     }

     Write-Output "ModuleSDK identity gate passed: $sdkPackageHash"
     Write-Output "InProcess identity gate passed: $inProcessPackageHash"
 }
 finally {
     $sdkArchive.Dispose()
     $inProcessArchive.Dispose()
     $outOfProcessArchive.Dispose()
 }
