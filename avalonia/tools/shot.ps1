param([string]$Title = "VideoCaptioner", [string]$Out = "shot.png", [int]$ProcessId = 0)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class W { [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
public struct RECT { public int L, T, R, B; } }
"@
if ($ProcessId -gt 0) { $p = Get-Process -Id $ProcessId } else { $p = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Title*" } | Select-Object -First 1 }
if (-not $p) { Write-Error "window not found"; exit 1 }
$h = $p.MainWindowHandle
[W]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [W]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 800
Write-Output ("foreground=" + ([W]::GetForegroundWindow() -eq $h))
$r = New-Object W+RECT
[W]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($Out)
Write-Output "saved $Out $($bmp.Width)x$($bmp.Height)"
