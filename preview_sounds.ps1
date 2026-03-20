Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$sounds = @(
    @{ Name = "--- URGENT suggestions ---"; Path = "" }
    @{ Name = "Windows Notify Messaging"; Path = "C:\Windows\Media\Windows Notify Messaging.wav" }
    @{ Name = "Windows Exclamation"; Path = "C:\Windows\Media\Windows Exclamation.wav" }
    @{ Name = "Windows Critical Stop"; Path = "C:\Windows\Media\Windows Critical Stop.wav" }
    @{ Name = "Windows Foreground"; Path = "C:\Windows\Media\Windows Foreground.wav" }
    @{ Name = "tada"; Path = "C:\Windows\Media\tada.wav" }
    @{ Name = "chord"; Path = "C:\Windows\Media\chord.wav" }
    @{ Name = "--- NORMAL suggestions ---"; Path = "" }
    @{ Name = "Windows Notify"; Path = "C:\Windows\Media\Windows Notify.wav" }
    @{ Name = "Windows Notify Email"; Path = "C:\Windows\Media\Windows Notify Email.wav" }
    @{ Name = "Windows Balloon"; Path = "C:\Windows\Media\Windows Balloon.wav" }
    @{ Name = "notify"; Path = "C:\Windows\Media\notify.wav" }
    @{ Name = "Windows Proximity Notification"; Path = "C:\Windows\Media\Windows Proximity Notification.wav" }
    @{ Name = "Windows Message Nudge"; Path = "C:\Windows\Media\Windows Message Nudge.wav" }
    @{ Name = "--- LOW suggestions ---"; Path = "" }
    @{ Name = "ding"; Path = "C:\Windows\Media\ding.wav" }
    @{ Name = "Windows Ding"; Path = "C:\Windows\Media\Windows Ding.wav" }
    @{ Name = "Windows Information Bar"; Path = "C:\Windows\Media\Windows Information Bar.wav" }
    @{ Name = "Windows Navigation Start"; Path = "C:\Windows\Media\Windows Navigation Start.wav" }
    @{ Name = "Windows Background"; Path = "C:\Windows\Media\Windows Background.wav" }
)

$script:player = New-Object System.Media.SoundPlayer

$window = New-Object System.Windows.Window
$window.Title = "MeetNow Sound Preview (default audio device)"
$window.Width = 450
$window.SizeToContent = "Height"
$window.WindowStartupLocation = "CenterScreen"
$window.Background = [System.Windows.Media.BrushConverter]::new().ConvertFrom("#1F1F1F")
$window.ResizeMode = "NoResize"

$header = New-Object System.Windows.Controls.TextBlock
$header.Text = "Click to preview (plays on default audio device)"
$header.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFrom("#AAA")
$header.FontSize = 12
$header.Margin = [System.Windows.Thickness]::new(0, 0, 0, 10)

$scrollViewer = New-Object System.Windows.Controls.ScrollViewer
$scrollViewer.VerticalScrollBarVisibility = "Auto"
$scrollViewer.MaxHeight = 550

$stackPanel = New-Object System.Windows.Controls.StackPanel

foreach ($s in $sounds) {
    if (-not $s.Path) {
        $label = New-Object System.Windows.Controls.TextBlock
        $label.Text = $s.Name
        $label.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFrom("#888")
        $label.FontSize = 12
        $label.FontWeight = "SemiBold"
        $label.Margin = [System.Windows.Thickness]::new(0, 12, 0, 4)
        $stackPanel.Children.Add($label) | Out-Null
    } else {
        $btn = New-Object System.Windows.Controls.Button
        $btn.Content = $s.Name
        $btn.Tag = $s.Path
        $btn.Background = [System.Windows.Media.BrushConverter]::new().ConvertFrom("#2A2A2A")
        $btn.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFrom("#F0F0F0")
        $btn.BorderThickness = [System.Windows.Thickness]::new(0)
        $btn.Padding = [System.Windows.Thickness]::new(14, 9, 14, 9)
        $btn.Margin = [System.Windows.Thickness]::new(0, 1, 0, 1)
        $btn.HorizontalContentAlignment = "Left"
        $btn.Cursor = [System.Windows.Input.Cursors]::Hand
        $btn.FontSize = 13

        $btn.Add_Click({
            param($sender, $e)
            $path = $sender.Tag
            if (Test-Path $path) {
                $script:player.Stop()
                $script:player.SoundLocation = $path
                $script:player.Play()
            }
        })

        $btn.Add_MouseEnter({
            $this.Background = [System.Windows.Media.BrushConverter]::new().ConvertFrom("#3A3A3A")
        })
        $btn.Add_MouseLeave({
            $this.Background = [System.Windows.Media.BrushConverter]::new().ConvertFrom("#2A2A2A")
        })

        $stackPanel.Children.Add($btn) | Out-Null
    }
}

$scrollViewer.Content = $stackPanel

$closeBtn = New-Object System.Windows.Controls.Button
$closeBtn.Content = "Close"
$closeBtn.Background = [System.Windows.Media.BrushConverter]::new().ConvertFrom("#3D3D3D")
$closeBtn.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFrom("#CCC")
$closeBtn.BorderThickness = [System.Windows.Thickness]::new(0)
$closeBtn.Padding = [System.Windows.Thickness]::new(20, 8, 20, 8)
$closeBtn.Margin = [System.Windows.Thickness]::new(0, 12, 0, 0)
$closeBtn.HorizontalAlignment = "Right"
$closeBtn.Cursor = [System.Windows.Input.Cursors]::Hand
$closeBtn.FontSize = 12
$closeBtn.Add_Click({ $script:player.Stop(); $window.Close() })

$outerStack = New-Object System.Windows.Controls.StackPanel
$outerStack.Children.Add($header) | Out-Null
$outerStack.Children.Add($scrollViewer) | Out-Null
$outerStack.Children.Add($closeBtn) | Out-Null
$outerStack.Margin = [System.Windows.Thickness]::new(16)

$window.Content = $outerStack
$window.Add_Closed({ $script:player.Stop(); $script:player.Dispose() })

$window.ShowDialog() | Out-Null
