Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class U {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L,T,R,B; }
}
"@
[void][U]::SetProcessDPIAware()

function Click([int]$x, [int]$y) {
  [void][U]::SetCursorPos($x, $y)
  Start-Sleep -Milliseconds 200
  [U]::mouse_event(2,0,0,0,[IntPtr]::Zero)
  Start-Sleep -Milliseconds 90
  [U]::mouse_event(4,0,0,0,[IntPtr]::Zero)
}

$QQ = [IntPtr]132362
[void][U]::SetForegroundWindow($QQ)
Start-Sleep -Milliseconds 500

# 1) 文件图标
Click 10560 1390

# 2) 等对话框
$dlg = [IntPtr]::Zero
for ($i = 0; $i -lt 20 -and $dlg -eq [IntPtr]::Zero; $i++) {
  Start-Sleep -Milliseconds 500
  $script:found = [IntPtr]::Zero
  $cb = [U+EnumWindowsProc]{ param($h,$l)
    $cn = New-Object System.Text.StringBuilder 256; [void][U]::GetClassName($h,$cn,256)
    $sb = New-Object System.Text.StringBuilder 256; [void][U]::GetWindowText($h,$sb,256)
    if ($cn.ToString() -eq '#32770' -and $sb.ToString() -eq '请选择' -and [U]::IsWindowVisible($h)) { $script:found = $h; return $false }
    return $true }
  [void][U]::EnumWindows($cb, [IntPtr]::Zero)
  $dlg = $script:found
}
if ($dlg -eq [IntPtr]::Zero) { Write-Output "FAIL: 对话框没出现"; exit 1 }
$r = New-Object U+RECT; [void][U]::GetWindowRect($dlg, [ref]$r)
Write-Output ("dialog hwnd={0} rect=({1},{2} {3}x{4})" -f $dlg, $r.L, $r.T, ($r.R-$r.L), ($r.B-$r.T))

# 3) 点文件名框 -> 粘贴路径 -> 回车
[void][U]::SetForegroundWindow($dlg)
Start-Sleep -Milliseconds 400
$bx = [int]($r.L + 0.469 * ($r.R - $r.L))
$by = [int]($r.T + 0.895 * ($r.B - $r.T))
Click $bx $by
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("^v")
Start-Sleep -Milliseconds 900
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 4
Write-Output ("dialog open after enter: {0}" -f [U]::IsWindow($dlg))
if ([U]::IsWindow($dlg)) { exit 2 }

# 4) 点「发送(1)」
[void][U]::SetForegroundWindow($QQ)
Start-Sleep -Milliseconds 500
$root = [System.Windows.Automation.AutomationElement]::FromHandle($QQ)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$btn = $null
foreach ($el in $all) {
  try {
    if ($el.Current.Name -like '发送(1)' -and $el.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') { $btn = $el; break }
  } catch {}
}
if ($btn -eq $null) { Write-Output "FAIL: 没找到发送(1)按钮"; exit 3 }
$br = $btn.Current.BoundingRectangle
Click ([int]($br.X + $br.Width/2)) ([int]($br.Y + $br.Height/2))
Start-Sleep -Seconds 3
Write-Output "OK: 已点发送"
