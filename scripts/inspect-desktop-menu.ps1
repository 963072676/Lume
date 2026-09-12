param([string]$Invoke = '', [string]$Screenshot = '', [int]$X = 1900, [int]$Y = 650, [string]$Parent = 'Lume 桌面整理', [switch]$NativeMessage, [switch]$RequireLume, [switch]$ExpectAbsent)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class MenuProbe {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr v);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
}
'@
[MenuProbe]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
[MenuProbe]::keybd_event(27,0,0,[UIntPtr]::Zero); [MenuProbe]::keybd_event(27,0,2,[UIntPtr]::Zero)
if (![MenuProbe]::SetCursorPos($X,$Y)) { throw '当前输入桌面不接受鼠标定位，请解锁或恢复本机会话后重试。' }
Start-Sleep -Milliseconds 150
[MenuProbe]::mouse_event(8,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 100
[MenuProbe]::mouse_event(16,0,0,0,[UIntPtr]::Zero)
if($NativeMessage){
 $lease=Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\Lume" -Filter 'desktop-lease-*.json' | Select-Object -First 1
 $state=Get-Content -LiteralPath $lease.FullName -Raw | ConvertFrom-Json
 [MenuProbe]::PostMessage([IntPtr]$state.Icons,0x7B,[IntPtr]$state.Icons,[IntPtr](($Y -shl 16) -bor $X)) | Out-Null
}
Start-Sleep -Milliseconds 500
$condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Menu)
$menus=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$condition)
$found=$false; $invoked=$false
foreach($m in $menus){
 Write-Output ('菜单进程：' + $m.Current.ProcessId + ' / ' + (Get-Process -Id $m.Current.ProcessId).ProcessName)
 $items=$m.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 foreach($item in $items){
  Write-Output $item.Current.Name
  if($item.Current.Name -eq $Parent){
   $found=$true
   $r=$item.Current.BoundingRectangle
   [MenuProbe]::SetCursorPos([int]($r.Left+$r.Width/2),[int]($r.Top+$r.Height/2)) | Out-Null
  }
 }
}
Start-Sleep -Milliseconds 700
$menus=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$condition)
foreach($m in $menus){
 foreach($item in $m.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)){
  Write-Output ('child: '+$item.Current.Name)
  if($Invoke -and $item.Current.Name -eq $Invoke){
   $invoked=$true
   $r=$item.Current.BoundingRectangle
   [MenuProbe]::SetCursorPos([int]($r.Left+$r.Width/2),[int]($r.Top+$r.Height/2)) | Out-Null
   [MenuProbe]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [MenuProbe]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  }
 }
}
if($Screenshot){
 $b=New-Object Drawing.Bitmap(2560,1440);$g=[Drawing.Graphics]::FromImage($b)
 $g.CopyFromScreen(0,0,0,0,$b.Size);$b.Save($Screenshot);$g.Dispose();$b.Dispose()
}
[MenuProbe]::keybd_event(27,0,0,[UIntPtr]::Zero); [MenuProbe]::keybd_event(27,0,2,[UIntPtr]::Zero)
[MenuProbe]::keybd_event(27,0,0,[UIntPtr]::Zero); [MenuProbe]::keybd_event(27,0,2,[UIntPtr]::Zero)
if($RequireLume -and !$found){throw '实际 Explorer 菜单未显示 Lume。'}
if($ExpectAbsent -and $found){throw '关闭入口后实际 Explorer 菜单仍显示 Lume。'}
if($Invoke -and !$invoked){throw '实际菜单未找到要点击的命令。'}
