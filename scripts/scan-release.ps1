[CmdletBinding()]
param(
    [string[]]$Path = @()
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$textExtensions = @(
    ".axaml", ".cs", ".csproj", ".config", ".json", ".md", ".props", ".ps1", ".sh",
    ".sln", ".slnx", ".targets", ".txt", ".xml"
)
$binaryExtensions = @("", ".dll", ".dylib", ".exe", ".so")
$excludedDirectories = @(".git", ".worktrees", "artifacts", "bin", "obj", ".playwright")

if ($Path.Count -eq 0) {
    $Path = @(
        (Join-Path $repoRoot "src"),
        (Join-Path $repoRoot "tests"),
        (Join-Path $repoRoot "docs"),
        (Join-Path $repoRoot "scripts"),
        (Join-Path $repoRoot "README.md"),
        (Join-Path $repoRoot "AIHubRouter.sln"),
        (Join-Path $repoRoot "NuGet.Config")
    )
}

function Get-RelativePath([string]$baseDirectory, [string]$targetPath) {
    $baseFullPath = [IO.Path]::GetFullPath($baseDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $targetFullPath = [IO.Path]::GetFullPath($targetPath)
    $baseUri = [Uri]::new($baseFullPath)
    $targetUri = [Uri]::new($targetFullPath)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Test-IsBuildRoot([string]$fullPath) {
    $relative = Get-RelativePath $repoRoot $fullPath
    $segments = $relative -split '[\\/]'
    return $segments | Where-Object { $_ -in @("artifacts", "bin", "obj") } | Select-Object -First 1
}

function Get-ScanFiles([string[]]$roots) {
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($root in $roots) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        $resolved = Resolve-Path -LiteralPath $root -ErrorAction Stop
        foreach ($item in $resolved) {
            $entry = Get-Item -LiteralPath $item.Path
            if (-not $entry.PSIsContainer) {
                $files.Add($entry)
                continue
            }

            $isBuildRoot = Test-IsBuildRoot $entry.FullName
            foreach ($candidate in Get-ChildItem -LiteralPath $entry.FullName -Recurse -File) {
                $extension = $candidate.Extension.ToLowerInvariant()
                if ($extension -notin $textExtensions -and $extension -notin $binaryExtensions) {
                    continue
                }

                $relative = Get-RelativePath $entry.FullName $candidate.FullName
                $segments = $relative -split '[\\/]'
                if ($segments | Where-Object { $_ -eq '.playwright' } | Select-Object -First 1) {
                    continue
                }
                if (-not $isBuildRoot) {
                    if ($segments | Where-Object { $_ -in $excludedDirectories } | Select-Object -First 1) {
                        continue
                    }
                }

                $files.Add($candidate)
            }
        }
    }

    return $files | Sort-Object FullName -Unique
}

function Get-ScanSegments([IO.FileInfo]$file) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($file.Extension.ToLowerInvariant() -in $binaryExtensions) {
        $chunkSize = 4MB
        $overlap = 512
        for ($offset = 0; $offset -lt $bytes.Length; $offset += ($chunkSize - $overlap)) {
            $length = [Math]::Min($chunkSize, $bytes.Length - $offset)
            $latin = [Text.Encoding]::GetEncoding(28591).GetString($bytes, $offset, $length)
            Write-Output ([Text.RegularExpressions.Regex]::Replace($latin, '[^\x20-\x7E]+', "`n"))

            $evenLength = $length - ($length % 2)
            if ($evenLength -gt 0) {
                $utf16 = [Text.Encoding]::Unicode.GetString($bytes, $offset, $evenLength)
                Write-Output ([Text.RegularExpressions.Regex]::Replace($utf16, '[^\x20-\x7E]+', "`n"))
            }

            $offsetLength = $length - 1
            $offsetLength -= $offsetLength % 2
            if ($offsetLength -gt 0) {
                $utf16Offset = [Text.Encoding]::Unicode.GetString($bytes, $offset + 1, $offsetLength)
                Write-Output ([Text.RegularExpressions.Regex]::Replace($utf16Offset, '[^\x20-\x7E]+', "`n"))
            }

            if ($offset + $length -ge $bytes.Length) {
                break
            }
        }
        return
    }

    Write-Output ([Text.Encoding]::UTF8.GetString($bytes))
}

function Get-DisplayPath([string]$fullPath) {
    $relative = Get-RelativePath $repoRoot $fullPath
    if (-not $relative.StartsWith("..", [StringComparison]::Ordinal)) {
        return $relative
    }

    return [IO.Path]::GetFileName($fullPath)
}

$rules = @(
    @{ Name = "JWT"; Pattern = '\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{8,}\b' },
    @{ Name = "BearerValue"; Pattern = '(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{20,}' },
    @{ Name = "ApiKeyValue"; Pattern = '(?i)(?<![A-Za-z0-9_-])(?:sk|ak)-[A-Za-z0-9_-]{16,}(?![A-Za-z0-9_-])' },
    @{ Name = "CookieCredential"; Pattern = '(?i)\b(?:auth_token|access_token|refresh_token|session|sessionid)\s*=\s*[A-Za-z0-9%._~+/=-]{20,}' },
    @{ Name = "SecretAssignment"; Pattern = '(?i)["'']?\b(?:password|passwd|access[_-]?token|refresh[_-]?token|api[_-]?key|cookie)\b["'']?\s*[:=]\s*["''][^"''\r\n]{8,}["'']' },
    @{ Name = "PrivateKey"; Pattern = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' },
    @{ Name = "LocalUserPath"; Pattern = '(?i)(?:\b[A-Z]:[\\/]Users[\\/][^\\/:*?"<>|\r\n]+|/(?:root|home/[A-Za-z0-9._-]+)/)'; TextOnly = $true }
)
$emailPattern = '(?i)\b[A-Z0-9._%+-]+@(?:[A-Z0-9-]+\.)+[A-Z]{2,}\b'
$regexOptions = [Text.RegularExpressions.RegexOptions]::Compiled -bor
    [Text.RegularExpressions.RegexOptions]::CultureInvariant
foreach ($rule in $rules) {
    $rule.Regex = [Text.RegularExpressions.Regex]::new($rule.Pattern, $regexOptions)
}
$emailRegex = [Text.RegularExpressions.Regex]::new($emailPattern, $regexOptions)
$findings = 0
$files = @(Get-ScanFiles $Path)

foreach ($file in $files) {
    $displayPath = Get-DisplayPath $file.FullName
    $isBinary = $file.Extension.ToLowerInvariant() -in $binaryExtensions
    $foundRules = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $unsafeEmail = $false
    Get-ScanSegments $file | ForEach-Object {
        $content = $_
        foreach ($rule in $rules) {
            if ($isBinary -and $rule.ContainsKey("TextOnly") -and $rule.TextOnly) {
                continue
            }
            if (-not $foundRules.Contains($rule.Name) -and
                $rule.Regex.IsMatch($content)) {
                [void]$foundRules.Add($rule.Name)
            }
        }

        if (-not $isBinary -and -not $unsafeEmail) {
            foreach ($match in $emailRegex.Matches($content)) {
                $domain = $match.Value.Split('@')[-1]
                if ($domain.EndsWith(".test", [StringComparison]::OrdinalIgnoreCase) -or
                    $domain.EndsWith(".invalid", [StringComparison]::OrdinalIgnoreCase) -or
                    $domain.Equals("example.com", [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $unsafeEmail = $true
                break
            }
        }
    }

    foreach ($rule in $rules) {
        if ($foundRules.Contains($rule.Name)) {
            Write-Output "[$($rule.Name)] $displayPath"
            $findings++
        }
    }

    if ($unsafeEmail) {
        Write-Output "[EmailAddress] $displayPath"
        $findings++
    }
}

if ($findings -gt 0) {
    exit 1
}

Write-Output "Release scan clean ($($files.Count) files)."
exit 0
