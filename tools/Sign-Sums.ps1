<#
.SYNOPSIS
  חותם SHA256SUMS.txt (ECDSA P-256, SHA-256, חתימת P1363 ב-base64) לקובץ SHA256SUMS.txt.sig. דורש pwsh 7+.
.DESCRIPTION
  המפתח הפרטי נלקח ממשתנה הסביבה UPDATE_SIGNING_KEY (PKCS#8 base64), או מ-KeyFile. מאמת את החתימה מול
  המפתח הציבורי הנגזר לפני סיום. ללא מפתח: נכשל (אין שחרור בלי חתימה).
#>
param(
  [string]$Sums = 'SHA256SUMS.txt',
  [string]$KeyFile,
  [string]$Out,
  # המפתח/ות הציבוריים המוטמעים בגשר (UpdateApi.PublicKeySpkiBase64, ';' בין כמה). חובה ב-CI: חתימה שאינה מתאימה להם = נכשל (U3)
  [string]$PublicKey = $env:UPDATE_PUBLIC_KEY
)
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = "$Sums.sig" }
$keyB64 = if ($KeyFile) { (Get-Content -LiteralPath $KeyFile -Raw).Trim() } else { $env:UPDATE_SIGNING_KEY }
if ([string]::IsNullOrWhiteSpace($keyB64)) { throw 'חסר מפתח חתימה (UPDATE_SIGNING_KEY או -KeyFile).' }
$ec = [System.Security.Cryptography.ECDsa]::Create()
$read = 0
$ec.ImportPkcs8PrivateKey([Convert]::FromBase64String($keyB64.Trim()), [ref]$read)
if ($ec.KeySize -ne 256) { throw 'המפתח חייב להיות P-256.' }
$data = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Sums))
$sig = $ec.SignData($data, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
if (-not $ec.VerifyData($data, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) { throw 'אימות עצמי נכשל.' }
# WHY (U3): אימות מול המפתח הציבורי הנגזר מהפרטי לא תופס סוד שגוי/ישן (נחתם, אך הגשר המותקן דוחה). לכן מאמתים מול המפתח המוטמע בגשר.
if ([string]::IsNullOrWhiteSpace($PublicKey)) {
  if ($env:CI -eq 'true') { throw 'חסר -PublicKey (או UPDATE_PUBLIC_KEY): ב-CI חובה לאמת מול המפתח המוטמע בגשר.' }
  Write-Warning 'לא ניתן מפתח ציבורי מוטמע: מאומת רק מול המפתח הנגזר (מקומי בלבד).'
} else {
  $ok = $false
  foreach ($k in ($PublicKey -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    try {
      $pub = [System.Security.Cryptography.ECDsa]::Create()
      $r2 = 0
      $pub.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($k), [ref]$r2)
      if ($pub.VerifyData($data, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) { $ok = $true; break }
    } catch { }
  }
  if (-not $ok) { throw 'החתימה אינה מאומתת מול המפתח הציבורי המוטמע בגשר (UpdateApi.PublicKeySpkiBase64). הסוד UPDATE_SIGNING_KEY שגוי או שהמפתח הוחלף.' }
}
Set-Content -LiteralPath $Out -Value ([Convert]::ToBase64String($sig)) -Encoding ascii -NoNewline
Write-Host "נחתם: $Out"
