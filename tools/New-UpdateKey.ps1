<#
.SYNOPSIS
  יוצר זוג מפתחות ECDSA P-256 לחתימת עדכוני הגשר. דורש PowerShell 7+ (pwsh).
.DESCRIPTION
  הפרטי (PKCS#8, base64) נכתב לקובץ שמחוץ לריפו ומודפס פעם אחת בלבד: מדביקים אותו כסוד GitHub בשם
  UPDATE_SIGNING_KEY (Settings > Secrets and variables > Actions > New repository secret) ומוחקים את הקובץ.
  הציבורי (SPKI, base64) מודבק בקבוע UpdateApi.PublicKeySpkiBase64 ב-bridge\UpdateApi.cs.
  אסור לשמור את הפרטי בריפו. מפתח זה משמש לעדכונים בלבד (לא לשום חתימה אחרת).
#>
param([string]$OutDir = (Join-Path $env:USERPROFILE 'otzaria-update-key'))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'נדרש PowerShell 7 (pwsh).' }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ((Resolve-Path -LiteralPath (New-Item -ItemType Directory -Force $OutDir)).Path.StartsWith($repoRoot)) { throw 'OutDir חייב להיות מחוץ לריפו.' }
$ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::NamedCurves.nistP256)
$priv = [Convert]::ToBase64String($ec.ExportPkcs8PrivateKey())
$pub = [Convert]::ToBase64String($ec.ExportSubjectPublicKeyInfo())
$privFile = Join-Path $OutDir 'update-signing-private.b64'
Set-Content -LiteralPath $privFile -Value $priv -Encoding ascii -NoNewline
icacls $privFile /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
Write-Host "מפתח ציבורי (להדביק ב-UpdateApi.PublicKeySpkiBase64):`n$pub`n"
Write-Host "מפתח פרטי נשמר ב: $privFile`nמדביקים את תוכנו כסוד GitHub בשם UPDATE_SIGNING_KEY ואז מוחקים את הקובץ (ושומרים גיבוי לא מקוון)."
