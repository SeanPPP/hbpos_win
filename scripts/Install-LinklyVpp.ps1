[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$InstallerPath,

    [string]$VcRedistX86Path,

    [string]$InstallRoot,

    [string]$SilentArgs = "/quiet /norestart",

    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Write-Info {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host $Message -ForegroundColor Gray
}

function Write-Warn {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host "警告：$Message" -ForegroundColor Yellow
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-ExistingPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Purpose
    )

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $resolved) {
        throw "$Purpose 不存在：$Path"
    }

    return $resolved.ProviderPath
}

function Test-VcRedistX86Installed {
    $registryRoots = @(
        "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86"
    )

    foreach ($registryRoot in $registryRoots) {
        $runtime = Get-ItemProperty -LiteralPath $registryRoot -ErrorAction SilentlyContinue
        if ($null -ne $runtime -and [int]$runtime.Installed -eq 1) {
            return $true
        }
    }

    return $false
}

function Invoke-Installer {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Arguments
    )

    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($extension -eq ".msi") {
        $process = Start-Process `
            -FilePath "msiexec.exe" `
            -ArgumentList @("/i", "`"$Path`"", "/qn", "/norestart") `
            -Wait `
            -PassThru `
            -WindowStyle Hidden
    }
    else {
        $argumentList = @()
        if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
            $argumentList = $Arguments
        }

        $process = Start-Process `
            -FilePath $Path `
            -ArgumentList $argumentList `
            -Wait `
            -PassThru `
            -WindowStyle Hidden
    }

    if ($process.ExitCode -notin @(0, 3010)) {
        throw "安装程序退出码异常：$($process.ExitCode)。安装包：$Path"
    }

    if ($process.ExitCode -eq 3010) {
        Write-Warn "安装完成但系统提示需要重启。完成当前配置后建议重启 Windows。"
    }
}

function Get-VirtualPinpadSearchRoots {
    param([string]$PreferredRoot)

    $roots = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($PreferredRoot)) {
        $roots.Add($PreferredRoot)
    }

    $roots.Add("C:\PC_EFT\DevTools")
    $roots.Add("C:\PC_EFT")
    if ($env:ProgramFiles) {
        $roots.Add($env:ProgramFiles)
    }

    if (${env:ProgramFiles(x86)}) {
        $roots.Add(${env:ProgramFiles(x86)})
    }

    return $roots |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
}

function Find-VirtualPinpad {
    param([string]$PreferredRoot)

    foreach ($root in (Get-VirtualPinpadSearchRoots -PreferredRoot $PreferredRoot)) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        # VPP 通常安装在 C:\PC_EFT\DevTools，搜索范围保持收敛，避免扫全盘。
        $match = Get-ChildItem `
            -LiteralPath $root `
            -Filter "VirtualPinpad.exe" `
            -File `
            -Recurse `
            -ErrorAction SilentlyContinue |
            Select-Object -First 1

        if ($null -ne $match) {
            return $match.FullName
        }
    }

    return $null
}

function Show-NextSteps {
    param([string]$VirtualPinpadPath)

    Write-Step "Sandbox VPP 后续操作"
    if (-not [string]::IsNullOrWhiteSpace($VirtualPinpadPath)) {
        Write-Info "VPP 路径：$VirtualPinpadPath"
    }

    Write-Host "1. 在 VPP 上切到 Cloud Mode：FUNC -> 7410 -> OK -> 0 -> 1 -> OK -> CANCEL"
    Write-Host "2. 生成或重置 Pair Code：FUNC -> 8880 -> OK -> OK"
    Write-Host "3. 测试认证场景时建议关闭 Auto Approve，避免金额尾数触发的模拟结果被覆盖。"
    Write-Host "4. 在 HBPOS 设置页选择：Payment Terminal -> ANZ Linkly -> Use Linkly Cloud -> Sandbox。"
    Write-Host "5. 输入 VPP 显示的 6 位 Pair Code，点击“配对 Cloud”。"
    Write-Host "6. 配对成功后点击测试连接，再保存 Linkly Cloud 配置。"
    Write-Host ""
    Write-Host "注意：Cloud username/password 由后端 Linkly Cloud credential 流程提供，本脚本不会保存或显示凭据。" -ForegroundColor Yellow
}

$resolvedInstallerPath = Resolve-ExistingPath -Path $InstallerPath -Purpose "VPP/Linkly Offline Development 安装包"

if (-not (Test-IsAdministrator)) {
    throw "请用管理员身份运行 PowerShell 后再执行本脚本。VPP 与 VC++ 运行库安装需要管理员权限。"
}

Write-Step "检查 Microsoft Visual C++ Redistributable x86"
if (Test-VcRedistX86Installed) {
    Write-Info "已检测到 VC++ x86 运行库。"
}
elseif (-not [string]::IsNullOrWhiteSpace($VcRedistX86Path)) {
    $resolvedVcRedistPath = Resolve-ExistingPath -Path $VcRedistX86Path -Purpose "VC++ x86 运行库安装包"
    Write-Info "未检测到 VC++ x86 运行库，开始安装：$resolvedVcRedistPath"
    Invoke-Installer -Path $resolvedVcRedistPath -Arguments "/quiet /norestart"
}
else {
    throw "未检测到 VC++ x86 运行库。请先安装 Microsoft Visual C++ Redistributable 2015-2022 x86，或通过 -VcRedistX86Path 传入安装包。"
}

Write-Step "安装 Linkly VPP / Offline Development"
Write-Info "安装包：$resolvedInstallerPath"
Invoke-Installer -Path $resolvedInstallerPath -Arguments $SilentArgs

Write-Step "查找 VirtualPinpad.exe"
$virtualPinpadPath = Find-VirtualPinpad -PreferredRoot $InstallRoot
if ([string]::IsNullOrWhiteSpace($virtualPinpadPath)) {
    Write-Warn "没有在默认路径找到 VirtualPinpad.exe。请确认安装时选择了 Offline Development / Virtual Pin Pad 组件。"
    Show-NextSteps -VirtualPinpadPath $null
    exit 2
}

Write-Info "已找到 VPP：$virtualPinpadPath"
if (-not $NoStart) {
    Write-Step "启动 VPP"
    Start-Process -FilePath $virtualPinpadPath -WorkingDirectory (Split-Path -Parent $virtualPinpadPath)
    Write-Info "已启动 Virtual Pin Pad。"
}
else {
    Write-Info "已跳过启动 VPP，因为传入了 -NoStart。"
}

Show-NextSteps -VirtualPinpadPath $virtualPinpadPath
