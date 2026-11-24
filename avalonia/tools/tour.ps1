param([int]$ProcessId, [string]$OutDir)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public class M { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
[M]::SetProcessDPIAware() | Out-Null
[M]::SetForegroundWindow((Get-Process -Id $ProcessId).MainWindowHandle) | Out-Null
$A = [System.Windows.Automation.AutomationElement]
function Find($name) {
  $cond = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ProcessId)))
  return $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Act($name) {
  $el = Find $name
  if (-not $el) { Write-Output "not found: $name"; return }
  $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  while ($el) {
    $p = $el.GetSupportedPatterns()
    if ($p -contains [System.Windows.Automation.SelectionItemPattern]::Pattern) {
      $r = $el.Current.BoundingRectangle
      [M]::SetCursorPos([int]($r.X + 24), [int]($r.Y + $r.Height / 2)) | Out-Null
      Start-Sleep -Milliseconds 100
      [M]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); [M]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
      return }
    if ($p -contains [System.Windows.Automation.InvokePattern]::Pattern) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return }
    if ($p -contains [System.Windows.Automation.TogglePattern]::Pattern) { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return }
    $el = $w.GetParent($el)
  }
  Write-Output "no pattern: $name"
}
$shots = @(
 @('主页','语音转录','home_2_transcribe'), @('主页','字幕优化与翻译','home_3_subtitle'), @('主页','字幕视频合成','home_4_synthesis'), @('主页','任务创建','home_1_task'),
 @('批量处理',$null,'batch'), @('字幕样式',$null,'style'), @('请求日志',$null,'logs'), @('Settings',$null,'settings'))
foreach ($s in $shots) {
  Act $s[0]; Start-Sleep -Milliseconds 800
  if ($s[1]) { Act $s[1]; Start-Sleep -Milliseconds 800 }
  & "$PSScriptRoot\shot.ps1" -ProcessId $ProcessId -Out (Join-Path $OutDir ($s[2] + '.png'))
}
