# FlashBar 实验功能自测：以 Windows PowerShell 的身份发一条系统通知 -> 检查 FlashBar 接管并显示胶囊 -> 从通知中心删掉 -> 检查胶囊同步消失
# 前提：FlashBar.exe 已运行，且托盘「实验功能 → 接管系统通知」已勾选；用 Windows PowerShell 5.1 运行（pwsh 7 不支持 WinRT 投影）
param([int]$Seconds = 5)

if ($PSVersionTable.PSEdition -eq 'Core') { Write-Output 'FAIL: 请用 Windows PowerShell 5.1（powershell.exe）运行，pwsh 不支持 WinRT'; exit 2 }
if (-not (Get-Process FlashBar -ErrorAction SilentlyContinue)) { Write-Output 'FAIL: FlashBar.exe 未运行'; exit 2 }
$dataDir = Join-Path $env:LOCALAPPDATA 'FlashBar'
$ini = Join-Path $dataDir 'settings.ini'
if (-not ((Test-Path -LiteralPath $ini) -and (Select-String -LiteralPath $ini -Pattern '^ToastTakeover=1' -Quiet))) {
    Write-Output 'FAIL: 「接管系统通知」未开启（托盘 → 实验功能 → 接管系统通知）'; exit 2
}

Add-Type -AssemblyName System.Windows.Forms
if (-not ('FbToast.W' -as [type])) {
Add-Type -Namespace FbToast -Name W -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
'@
}
$W = [FbToast.W]
[void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
[void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime]

$aumid = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
$tag = 'fb-selftest'
$group = 'flashbar-selftest'
$logPath = Join-Path $dataDir 'flashbar.log'

function Pump([int]$ms) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
}
function PillVisible {
    $h = $W::FindWindow([NullString]::Value, 'FlashBarPill')
    return ($h -ne [IntPtr]::Zero) -and $W::IsWindowVisible($h)
}
function WaitFor([scriptblock]$cond, [int]$sec) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $sec) {
        if (& $cond) { return $sw.Elapsed.TotalSeconds }
        Pump 100
    }
    return -1
}
# FlashBar 每接管一条通知记一行 "toast: 应用名 | AUMID | id=N"（不记内容）
function ToastLogCount {
    try {
        return @(Get-Content -LiteralPath $logPath -Tail 400 -Encoding UTF8 -ErrorAction Stop |
            Where-Object { $_.Contains('] toast: ') -and $_.ToLowerInvariant().Contains('powershell') }).Count
    }
    catch { return 0 }
}

$fail = 0
$history = [Windows.UI.Notifications.ToastNotificationManager]::History
$history.Remove($tag, $group, $aumid)   # 清掉上次的残留
Pump 1500                                # 等 FlashBar 轮询把残留同步掉
$pre = PillVisible
if ($pre) { Write-Output 'WARN: 测试前胶囊已可见（已有其他待处理提醒），改用日志确认，跳过胶囊出现/消失检查' }

$before = ToastLogCount
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>FlashBar 自测通知</text><text>test_toast.ps1 $(Get-Date -Format HH:mm:ss)</text></binding></visual></toast>")
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
$toast.Tag = $tag
$toast.Group = $group
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($aumid).Show($toast)
Write-Output '已以 Windows PowerShell 的身份发出测试通知'

$t = WaitFor { (ToastLogCount) -gt $before } $Seconds
if ($t -ge 0) { Write-Output ("PASS: FlashBar 在 {0:N2}s 内接管了这条通知" -f $t) } else { Write-Output "FAIL: $Seconds 秒内日志里没有这条通知（查看日志中的 ERROR / toast source 行）"; $fail++ }
if (-not $pre) {
    $t2 = WaitFor { PillVisible } 2
    if ($t2 -ge 0) { Write-Output 'PASS: 胶囊已出现' } else { Write-Output 'FAIL: 胶囊没有出现'; $fail++ }
}

$history.Remove($tag, $group, $aumid)
Write-Output '已从通知中心删除这条测试通知'
if (-not $pre) {
    $t3 = WaitFor { -not (PillVisible) } 3
    if ($t3 -ge 0) { Write-Output ("PASS: 通知中心删除后胶囊在 {0:N2}s 内同步消失" -f $t3) } else { Write-Output 'FAIL: 通知中心删除后胶囊 3 秒内未消失'; $fail++ }
} else { Write-Output 'SKIP: 测试前已有其他提醒，跳过消失检查' }

Write-Output '提示：Windows PowerShell 第一次被接管时系统横幅可能还会弹一次；不想接管它可在「已接管的应用」里取消勾选'
if ($fail -eq 0) { Write-Output 'RESULT: PASS'; exit 0 } else { Write-Output "RESULT: FAIL ($fail)"; exit 1 }
