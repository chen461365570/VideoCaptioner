param([int]$ProcessId, [string]$Name, [string]$Action = "invoke")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$cond = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Name)), (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ProcessId)))
$el = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $el) { Write-Error "not found $Name"; exit 1 }
$want = @{select=[System.Windows.Automation.SelectionItemPattern]::Pattern; expand=[System.Windows.Automation.ExpandCollapsePattern]::Pattern; invoke=[System.Windows.Automation.InvokePattern]::Pattern}[$Action]
$w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
while ($el -and -not ($el.GetSupportedPatterns() -contains $want)) { $el = $w.GetParent($el) }
if (-not $el) { Write-Error "no pattern"; exit 1 }
foreach ($pat in $el.GetSupportedPatterns()) { Write-Output "pattern: $($pat.ProgrammaticName)" }
switch ($Action) {
 "select" { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
 "expand" { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
 default { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
}
Write-Output "done $Action"
