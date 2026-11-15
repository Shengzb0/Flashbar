# FlashBar 自测：建最小化窗口并闪烁 -> 检查胶囊出现 -> 激活窗口 -> 检查胶囊消失
param([int]$Seconds = 3)

Add-Type -AssemblyName System.Windows.Forms
if (-not ('FbTest.W' -as [type])) {
Add-Type -Namespace FbTest -Name W -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
[DllImport("user32.dll")] public static extern bool FlashWindowEx(ref FLASHWINFO fi);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
'@
}
$W = [FbTest.W]

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
function Activate([IntPtr]$h) {
    $W::ShowWindow($h, 9) | Out-Null   # SW_RESTORE
    $fg = $W::GetForegroundWindow()
    $ft = $W::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
    $me = $W::GetCurrentThreadId()
    $att = $false
    if ($ft -ne 0 -and $ft -ne $me) { $att = $W::AttachThreadInput($me, $ft, $true) }
    $W::BringWindowToTop($h) | Out-Null
    $W::SetForegroundWindow($h) | Out-Null
    if ($att) { $W::AttachThreadInput($me, $ft, $false) | Out-Null }
    Pump 200
    if ($W::GetForegroundWindow() -ne $h) {
        # 兜底：模拟 Alt 获取前台权限
        $W::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        $W::SetForegroundWindow($h) | Out-Null
        $W::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        Pump 200
    }
    return ($W::GetForegroundWindow() -eq $h)
}

$fail = 0
if (-not (Get-Process FlashBar -ErrorAction SilentlyContinue)) { Write-Output 'FAIL: FlashBar.exe 未运行'; exit 2 }
$pre = PillVisible
if ($pre) { Write-Output 'WARN: 测试前胶囊已可见（已有其他待处理窗口），改用日志确认出现，跳过消失检查' }
$logPath = Join-Path $env:LOCALAPPDATA 'FlashBar\flashbar.log'
function LogCount([string]$needle) {
    try { return @(Get-Content -LiteralPath $logPath -Tail 400 -Encoding UTF8 -ErrorAction Stop | Where-Object { $_.EndsWith($needle) }).Count }
    catch { return 0 }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'FlashBar测试'
$form.ShowInTaskbar = $true
$form.StartPosition = 'CenterScreen'
$h = $form.Handle
$W::ShowWindow($h, 7) | Out-Null   # SW_SHOWMINNOACTIVE
Pump 300

$needle = ' | 0x{0:X}' -f $h.ToInt64()
$before = LogCount $needle
$fi = New-Object FbTest.W+FLASHWINFO
$fi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($fi)
$fi.hwnd = $h
$fi.dwFlags = 2 -bor 12            # FLASHW_TRAY | FLASHW_TIMERNOFG
$W::FlashWindowEx([ref]$fi) | Out-Null
Write-Output ("测试窗口 0x{0:X} 已最小化并开始闪烁" -f $h.ToInt64())

if ($pre) {
    # 胶囊本来就可见，不能据此判断；要求日志里出现本测试窗口的 flash 记录
    $t = WaitFor { (LogCount $needle) -gt $before } $Seconds
    if ($t -ge 0) { Write-Output ("PASS: 日志在 {0:N2}s 内记录到测试窗口的提醒" -f $t) } else { Write-Output "FAIL: $Seconds 秒内日志未记录测试窗口的提醒"; $fail++ }
} else {
    $t = WaitFor { PillVisible } $Seconds
    if ($t -ge 0) { Write-Output ("PASS: 胶囊在 {0:N2}s 内出现" -f $t) } else { Write-Output "FAIL: 胶囊 $Seconds 秒内未出现"; $fail++ }
}

$ok = Activate $h
Write-Output ("激活测试窗口: " + $(if ($ok) { '成功' } else { '未成为前台' }))
if (-not $pre) {
    $t2 = WaitFor { -not (PillVisible) } 3
    if ($t2 -ge 0) { Write-Output ("PASS: 激活后胶囊在 {0:N2}s 内消失" -f $t2) } else { Write-Output 'FAIL: 激活后胶囊 3 秒内未消失'; $fail++ }
} else { Write-Output 'SKIP: 测试前已有其他提醒，跳过消失检查' }

$form.Close(); $form.Dispose()
Pump 200
if ($fail -eq 0) { Write-Output 'RESULT: PASS'; exit 0 } else { Write-Output "RESULT: FAIL ($fail)"; exit 1 }
