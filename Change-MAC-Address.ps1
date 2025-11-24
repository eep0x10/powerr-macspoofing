# Check for administrator privileges and relaunch if necessary
if (-not ([System.Security.Principal.WindowsPrincipal][System.Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        $scriptPath = $MyInvocation.MyCommand.Definition
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-STA -NoProfile -File `"$scriptPath`""
        Exit
    } catch {
        Write-Error "Failed to relaunch with administrator privileges: $_ "
        if ($psISE) { Read-Host "Press Enter to continue" } else { Start-Sleep -Seconds 5 }
        Exit
    }
}

# Hide the console window
Add-Type -Name Window -Namespace Console -MemberDefinition '[System.Runtime.InteropServices.DllImport("Kernel32.dll")] public static extern IntPtr GetConsoleWindow(); [System.Runtime.InteropServices.DllImport("User32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);'
try { [Console.Window]::ShowWindow([Console.Window]::GetConsoleWindow(), 0) } catch {}

# Add assemblies for GUI elements
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# --- SCRIPT-LEVEL VARIABLES ---
$script:tempCaptureFile = $null

# --- FORM AND CONTROLS INITIALIZATION ---
$form = New-Object System.Windows.Forms.Form
$form.Text = "MAC Address Changer & Capture"
$form.Size = New-Object System.Drawing.Size(450, 700)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.MinimizeBox = $false

$macLabel = New-Object System.Windows.Forms.Label
$macLabel.Text = "New MAC Address:"
$macLabel.Location = New-Object System.Drawing.Point(10, 20)
$macLabel.AutoSize = $true

$macTextBox = New-Object System.Windows.Forms.TextBox
$macTextBox.Location = New-Object System.Drawing.Point(130, 17)
$macTextBox.Size = New-Object System.Drawing.Size(150, 20)
$macTextBox.CharacterCasing = "Upper"
$macTextBox.MaxLength = 17

$refreshButton = New-Object System.Windows.Forms.Button
$refreshButton.Text = "Refresh"
$refreshButton.Location = New-Object System.Drawing.Point(300, 50)
$refreshButton.Size = New-Object System.Drawing.Size(100, 25)

$resetAllButton = New-Object System.Windows.Forms.Button
$resetAllButton.Text = "Reset All"
$resetAllButton.Location = New-Object System.Drawing.Point(300, 20)
$resetAllButton.Size = New-Object System.Drawing.Size(100, 25)

$interfacesGroupBox = New-Object System.Windows.Forms.GroupBox
$interfacesGroupBox.Text = "Select Network Interface"
$interfacesGroupBox.Location = New-Object System.Drawing.Point(10, 80)
$interfacesGroupBox.Size = New-Object System.Drawing.Size(410, 550)
$interfacesGroupBox.Anchor = "Top, Left, Right, Bottom"
$interfacesGroupBox.AutoScroll = $true

$saveButton = New-Object System.Windows.Forms.Button
$saveButton.Text = "Update MAC"
$saveButton.Location = New-Object System.Drawing.Point(110, 640)
$saveButton.Size = New-Object System.Drawing.Size(100, 30)
$form.AcceptButton = $saveButton

$captureButton = New-Object System.Windows.Forms.Button
$captureButton.Text = "Capture MAC"
$captureButton.Location = New-Object System.Drawing.Point(240, 640)
$captureButton.Size = New-Object System.Drawing.Size(100, 30)

# --- FUNCTIONS ---
function Refresh-Interfaces {
    $interfacesGroupBox.Controls.Clear()
    $tsharkPath = "C:\Program Files\Wireshark\tshark.exe"
    if (-not (Test-Path $tsharkPath)) {
        return
    }

    # Build a lookup table of NetAdapter objects, keyed by their Name for matching.
    $netAdaptersByName = @{}
    try {
        Get-NetAdapter -IncludeHidden | ForEach-Object { $netAdaptersByName[$_.Name] = $_ }
    } catch {
        return
    }

    $tsharkInterfaces = (& "$tsharkPath" -D)
    if (-not $tsharkInterfaces) {
        return
    }

    $location = 20
    foreach ($line in $tsharkInterfaces) {
        # Match the tshark index and the user-friendly name in parentheses.
        if ($line -match '^(\d+)\..*\((.*?)\)') {
            $tsharkIndex = $matches[1]
            $tsharkName = $matches[2]
            
            # Find the corresponding adapter by its name.
            $adapter = $netAdaptersByName[$tsharkName]
            
            if ($adapter) {
                $radioButton = New-Object System.Windows.Forms.RadioButton
                $radioButton.Text = "{0} - {1}" -f $adapter.Name, $adapter.MacAddress
                $radioButton.Tag = @{ TsharkIndex = $tsharkIndex; Adapter = $adapter }
                $radioButton.AutoSize = $true
                $radioButton.Location = New-Object System.Drawing.Point(10, $location)
                # Removed Anchor for radio buttons

                # Color-code if the MAC is modified.
                $permanentMac = ""
                try { $permanentMac = ($adapter | Get-NetAdapter -Physical).PermanentAddress } catch {}
                if ($permanentMac -and ($adapter.MacAddress -ne $permanentMac)) {
                    $radioButton.ForeColor = [System.Drawing.Color]::Red
                }

                $interfacesGroupBox.Controls.Add($radioButton)
                $location += 25
            }
        }
    }
}

# --- EVENT HANDLERS ---
$refreshButton.Add_Click({ Refresh-Interfaces })

$resetAllButton.Add_Click({
    $confirmResult = [System.Windows.Forms.MessageBox]::Show("Are you sure you want to reset ALL modified MAC addresses to their factory defaults?", "Confirm Reset All", "YesNo", "Warning")
    if ($confirmResult -ne 'Yes') { return }

    try {
        $allAdapters = Get-NetAdapter -IncludeHidden
        $resetCount = 0

        foreach ($adapter in $allAdapters) {
            # Try to reset the MAC address to its original (by setting it to empty string)
            # This implicitly relies on Windows behavior to revert to hardware MAC
            Set-NetAdapter -Name $adapter.Name -MacAddress "" -Confirm:$false

            # Disable/Enable to apply changes
            Disable-NetAdapter -InputObject $adapter -Confirm:$false
            Start-Sleep -Seconds 1
            Enable-NetAdapter -InputObject $adapter
            $resetCount++
        }

        [System.Windows.Forms.MessageBox]::Show("Successfully attempted to reset MAC addresses for $resetCount adapter(s). Please check network connectivity.", "Reset Complete", "OK", "Information")
    } catch {
        [System.Windows.Forms.MessageBox]::Show("An error occurred during the reset operation: $($_.Exception.Message)", "Reset Error", "OK", "Error")
    }
    Refresh-Interfaces
})

$saveButton.Add_Click({
    $rawMacAddress = $macTextBox.Text
    $normalizedMac = $rawMacAddress.Replace(":", "").Replace("-", "").ToUpper()

    if (-not ($normalizedMac -match '^[0-9A-F]{12}$')) {
        [System.Windows.Forms.MessageBox]::Show("Invalid MAC address format.", "Invalid MAC", "OK", "Error"); return
    }

    $selectedRadioButton = $interfacesGroupBox.Controls | Where-Object { $_.Checked -eq $true }
    if (-not $selectedRadioButton) {
        [System.Windows.Forms.MessageBox]::Show("Please select a network interface first.", "No Interface Selected", "OK", "Warning"); return
    }
    
    $selectedAdapter = $selectedRadioButton.Tag.Adapter
    if (-not $selectedAdapter) {
        [System.Windows.Forms.MessageBox]::Show("Could not retrieve adapter details from the selected interface.", "Error", "OK", "Error"); return
    }

    try {
        # Use Set-NetAdapter as requested by the user
        Set-NetAdapter -Name $selectedAdapter.Name -MacAddress $normalizedMac -Confirm:$false
        
        # A disable/enable cycle is more reliable for applying changes
        Disable-NetAdapter -InputObject $selectedAdapter -Confirm:$false
        Start-Sleep -Seconds 1
        Enable-NetAdapter -InputObject $selectedAdapter
        
        # --- VERIFICATION STEP ---
        $verificationSuccess = $false
        $verificationAttempts = 5
        [System.Windows.Forms.MessageBox]::Show("Applying and verifying the new MAC address for `"$($selectedAdapter.Name)`". Please wait...", "Verifying...", "OK", "Information")
        Start-Sleep -Seconds 3 # Initial sleep for adapter to come online

        for ($i = 1; $i -le $verificationAttempts; $i++) {
            $newAdapterState = Get-NetAdapter -Name $selectedAdapter.Name
            $currentMac = ($newAdapterState.MacAddress -replace '[-:]').ToUpper()
            if ($currentMac -eq $normalizedMac) {
                $verificationSuccess = $true
                break
            }
            Start-Sleep -Seconds 2 # Wait and try again
        }

        if ($verificationSuccess) {
            [System.Windows.Forms.MessageBox]::Show("MAC address successfully changed and verified for `"$($selectedAdapter.Name)`".", "Success", "OK", "Information")
        } else {
            [System.Windows.Forms.MessageBox]::Show("Failed to verify MAC address change for `"$($selectedAdapter.Name)`". The adapter driver may have rejected the change.", "Verification Failed", "OK", "Error")
        }
        
        Refresh-Interfaces
    } catch {
        [System.Windows.Forms.MessageBox]::Show("An error occurred while trying to set the MAC address for '$($selectedAdapter.Name)'.`n`nError: $($_.Exception.Message)", "Update Error", "OK", "Error")
    }
})

$captureButton.Add_Click({
    $tsharkPath = "C:\Program Files\Wireshark\tshark.exe"
    if (-not (Test-Path $tsharkPath)) { [System.Windows.Forms.MessageBox]::Show("tshark.exe not found.", "Error"); return }
    $selectedRadioButton = $interfacesGroupBox.Controls | Where-Object { $_.Checked -eq $true }
    if (-not $selectedRadioButton) { [System.Windows.Forms.MessageBox]::Show("Select an interface.", "Error"); return }
    
    # Get the pre-stored tshark index from the tag's hashtable
    $interfaceIndex = $selectedRadioButton.Tag.TsharkIndex
    if (-not $interfaceIndex) {
        [System.Windows.Forms.MessageBox]::Show("Could not find the tshark index for the selected interface.", "Error", "OK", "Error"); return
    }
    
    # Define the command as a string, injecting the interface index
    $commandString = @"
        Write-Host 'Starting tshark capture on interface $interfaceIndex...'
        & 'C:\Program Files\Wireshark\tshark.exe' -l -i $interfaceIndex -Y 'arp or lldp' -T 'fields' -e 'eth.src' -a 'duration:20'
        Write-Host 'Capture finished.'
        Read-Host 'Press Enter to close this window.'
"@

    # Encode the command string to Base64
    $encodedCommand = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($commandString))

    try {
        # Start the new PowerShell window using the encoded command for maximum reliability
        Start-Process powershell.exe -ArgumentList "-NoProfile -EncodedCommand $encodedCommand" -Verb RunAs
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Failed to open PowerShell window: $($_.Exception.Message)", "Error")
    }
})

$form.add_FormClosing({
    param($sender, $e)
    # Clean up the temp file when the form closes, just in case
    if ($null -ne $script:tempCaptureFile -and (Test-Path $script:tempCaptureFile)) {
        Remove-Item $script:tempCaptureFile -Force
    }
})

# --- FORM SETUP AND LAUNCH ---
$form.Controls.Add($macLabel); $form.Controls.Add($macTextBox); $form.Controls.Add($refreshButton)
$form.Controls.Add($resetAllButton); $form.Controls.Add($interfacesGroupBox); $form.Controls.Add($saveButton)
$form.Controls.Add($captureButton)
Refresh-Interfaces
$form.ShowDialog() | Out-Null