param([int]$ProcessId, [string]$Like)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$els = $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ProcessId)))
foreach ($e in $els) { if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.ListItem" -and $e.Current.Name -like $Like) { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); "selected"; break } }
