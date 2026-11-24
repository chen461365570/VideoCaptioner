param([int]$ProcessId)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$els = $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ProcessId)))
foreach ($e in $els) { $t=$e.Current.ControlType.ProgrammaticName; if ($t -eq "ControlType.ListItem") { "$t | $($e.Current.Name) | " + (($e.GetSupportedPatterns() | % { $_.ProgrammaticName }) -join ",") } }
