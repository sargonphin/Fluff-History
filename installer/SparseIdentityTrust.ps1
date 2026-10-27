# Shared build/install verification. This contains only public identity data.
function GetSparseIdentityPolicy([string]$directory = $PSScriptRoot) {
    $policy = Get-Content -LiteralPath (Join-Path $directory 'SparseIdentity.json') -Raw | ConvertFrom-Json
    $path = Join-Path $directory 'SparseIdentity.cer'
    $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $path
    if ($policy.schema -ne 1 -or $policy.packageName -cne 'Sargonphin.FluffHistory' -or
        $policy.publisher -cne 'CN=Fluff-History Sparse Identity' -or $policy.trustStore -cne 'LocalMachine\TrustedPeople' -or
        $certificate.Subject -cne $policy.publisher -or $certificate.Issuer -cne $policy.publisher -or
        $certificate.Thumbprint -cne $policy.certificateThumbprint -or $certificate.HasPrivateKey -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $policy.certificateSha256 -or
        $policy.packageFamilyName -cne 'Sargonphin.FluffHistory_53b6jha2b007a') { throw 'Sparse identity public certificate/pin mismatch.' }
    if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) { throw 'Sparse identity certificate is outside its validity period.' }
    [pscustomobject]@{ Policy=$policy; Certificate=$certificate; Path=$path }
}
function SparseIdentityTrust([ValidateSet('Trust','RemoveIfUnused')][string]$mode, [string]$directory = $PSScriptRoot) {
    $identity = GetSparseIdentityPolicy $directory
    $pin = $identity.Policy; $certificate = $identity.Certificate
    $ownership = 'HKLM:\Software\Fluff-History\SparseIdentityTrust\' + $pin.certificateSha256
    $machinePath = 'Cert:\LocalMachine\TrustedPeople\' + $pin.certificateThumbprint
    try {
        if ($mode -eq 'Trust' -and (Test-Path -LiteralPath $machinePath)) {
            $existing = Get-Item -LiteralPath $machinePath
            if ([Convert]::ToBase64String($existing.RawData) -cne [Convert]::ToBase64String($certificate.RawData)) { throw 'Sparse identity trust collision.' }
            return
        }
        if ($mode -eq 'RemoveIfUnused' -and !(Test-Path -LiteralPath $ownership)) { return }
        # Windows rejects this signed sparse package against CurrentUser trust (0x800B0109).
        # Only exact public DER and fixed operations cross elevation. No installer/profile path,
        # user-supplied code, private key, app activation or HKCU change enters the elevated child.
        $body = @'
$ErrorActionPreference='Stop'
$bytes=[Convert]::FromBase64String('__DER__')
$sha=[Security.Cryptography.SHA256]::Create()
try{$hash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','')}finally{$sha.Dispose()}
if($hash -cne '__HASH__'){exit 1}
$cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2 (,$bytes)
if($cert.HasPrivateKey -or $cert.Subject -cne 'CN=Fluff-History Sparse Identity' -or $cert.Thumbprint -cne '__THUMB__'){exit 1}
$store=New-Object Security.Cryptography.X509Certificates.X509Store 'TrustedPeople','LocalMachine'
$registry=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
$name='SOFTWARE\Fluff-History\SparseIdentityTrust\__HASH__'
try {
 $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
 $matching=@($store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,'__THUMB__',$false))
 foreach($item in $matching){if([Convert]::ToBase64String($item.RawData) -cne '__DER__'){exit 1}}
 if('__MODE__' -eq 'Trust') {
  if(!$matching.Count) {
   $key=$registry.CreateSubKey($name)
   try{$key.SetValue('CertificateSha256',$hash);$key.SetValue('Owned',1,[Microsoft.Win32.RegistryValueKind]::DWord)}finally{$key.Dispose()}
   $store.Add($cert)
  }
 } else {
  $key=$registry.OpenSubKey($name)
  if(!$key){exit 0}
  try{$owned=$key.GetValue('Owned') -eq 1 -and $key.GetValue('CertificateSha256') -ceq $hash}finally{$key.Dispose()}
  if(!$owned){exit 1}
  Import-Module (Join-Path $PSHOME 'Modules\Appx\Appx.psd1') -Force
  if(@(Get-AppxPackage -AllUsers -Name 'Sargonphin.FluffHistory' | Where-Object Publisher -ceq 'CN=Fluff-History Sparse Identity').Count){exit 0}
  foreach($item in $matching){$store.Remove($item)}
  $registry.DeleteSubKey($name)
 }
}finally{$store.Close();$registry.Dispose();$cert.Dispose()}
exit 0
'@
        $body = $body.Replace('__DER__',[Convert]::ToBase64String($certificate.RawData)).Replace('__HASH__',$pin.certificateSha256).
            Replace('__THUMB__',$pin.certificateThumbprint).Replace('__MODE__',$mode)
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
        $start.Arguments = '-NoLogo -NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
        $start.UseShellExecute = $true; $start.Verb = 'runas'; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $process = [Diagnostics.Process]::Start($start)
        try { if (!$process.WaitForExit(120000) -or $process.ExitCode -ne 0) { throw 'Exact sparse identity trust could not be completed.' } }
        finally { $process.Dispose() }
        if ($mode -eq 'Trust' -and !(Test-Path -LiteralPath $machinePath)) { throw 'Sparse identity trust readback failed.' }
    } finally { $certificate.Dispose() }
}
