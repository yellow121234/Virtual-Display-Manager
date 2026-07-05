<#
Creates/updates VirtualDrivers Virtual Display Driver settings and restarts the driver.
Run PowerShell as Administrator.

Examples:
  .\Create-VirtualDisplay.ps1
  .\Create-VirtualDisplay.ps1 -Count 2 -Width 2560 -Height 1440 -RefreshRate 60
  .\Create-VirtualDisplay.ps1 -InfPath "C:\Path\To\MttVDD.inf"
#>
[CmdletBinding()]
param(
    [ValidateRange(1,16)] [int]$Count = 1,
    [ValidateRange(640,7680)] [int]$Width = 1920,
    [ValidateRange(480,4320)] [int]$Height = 1080,
    [ValidateRange(24,240)] [int]$RefreshRate = 60,
    [string]$InfPath
)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '관리자 PowerShell로 실행해야 합니다.'
    }
}

function Write-VddSettings {
    param([int]$Count, [int]$Width, [int]$Height, [int]$RefreshRate)

    $dir = 'C:\VirtualDisplayDriver'
    $path = Join-Path $dir 'vdd_settings.xml'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    if (Test-Path $path) {
        $backup = "$path.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
        Copy-Item $path $backup -Force
        Write-Host "기존 설정 백업: $backup"
    }

    $xml = @"
<?xml version='1.0' encoding='utf-8'?>
<vdd_settings>
  <monitors>
    <count>$Count</count>
  </monitors>
  <gpu>
    <friendlyname>default</friendlyname>
  </gpu>
  <global>
    <g_refresh_rate>$RefreshRate</g_refresh_rate>
  </global>
  <resolutions>
    <resolution>
      <width>$Width</width>
      <height>$Height</height>
      <refresh_rate>$RefreshRate</refresh_rate>
    </resolution>
    <resolution>
      <width>1920</width>
      <height>1080</height>
      <refresh_rate>60</refresh_rate>
    </resolution>
    <resolution>
      <width>2560</width>
      <height>1440</height>
      <refresh_rate>60</refresh_rate>
    </resolution>
    <resolution>
      <width>3840</width>
      <height>2160</height>
      <refresh_rate>60</refresh_rate>
    </resolution>
  </resolutions>
  <auto_resolutions>
    <enabled>false</enabled>
    <source_priority>manual</source_priority>
    <preferred_mode>
      <use_edid_preferred>false</use_edid_preferred>
      <fallback_width>$Width</fallback_width>
      <fallback_height>$Height</fallback_height>
      <fallback_refresh>$RefreshRate</fallback_refresh>
    </preferred_mode>
  </auto_resolutions>
  <logging>
    <SendLogsThroughPipe>true</SendLogsThroughPipe>
    <logging>false</logging>
    <debuglogging>false</debuglogging>
  </logging>
  <colour>
    <SDR10bit>false</SDR10bit>
    <HDRPlus>false</HDRPlus>
    <ColourFormat>RGB</ColourFormat>
  </colour>
</vdd_settings>
"@

    [System.IO.File]::WriteAllText($path, $xml, [System.Text.UTF8Encoding]::new($false))
    Write-Host "VDD 설정 저장: $path"
}

function Install-VddIfNeeded {
    param([string]$InfPath)

    $device = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match 'Virtual Display Driver|IddSampleDriver|MttVDD|VirtualDrivers' }

    if ($device) {
        Write-Host 'VDD 장치가 이미 감지되었습니다.'
        return
    }

    if ($InfPath) {
        if (-not (Test-Path $InfPath)) { throw "INF 파일을 찾지 못했습니다: $InfPath" }
        Write-Host "INF로 드라이버 설치: $InfPath"
        pnputil /add-driver $InfPath /install | Write-Host
        pnputil /scan-devices | Write-Host
        return
    }

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host 'winget으로 공식 Virtual Display Driver 설치를 시도합니다.'
        winget install -e --id VirtualDrivers.Virtual-Display-Driver --accept-package-agreements --accept-source-agreements
        pnputil /scan-devices | Write-Host
        return
    }

    throw 'VDD 장치가 없고 INF 경로도 없으며 winget도 없습니다. -InfPath로 MttVDD.inf 경로를 지정해 주세요.'
}

function Restart-Vdd {
    $devices = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match 'Virtual Display Driver|IddSampleDriver|MttVDD|VirtualDrivers' }

    if (-not $devices) {
        Write-Warning 'VDD 장치가 아직 보이지 않습니다. 재부팅 후 다시 확인해 주세요.'
        return
    }

    foreach ($device in $devices) {
        Write-Host "VDD 재시작: $($device.FriendlyName)"
        try {
            Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false -ErrorAction Stop
            Start-Sleep -Seconds 2
            Enable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false -ErrorAction Stop
        } catch {
            Write-Warning "Disable/Enable 실패, pnputil 재시작 시도: $($_.Exception.Message)"
            pnputil /restart-device $device.InstanceId | Write-Host
        }
    }

    pnputil /scan-devices | Write-Host
}

Assert-Admin
Write-VddSettings -Count $Count -Width $Width -Height $Height -RefreshRate $RefreshRate
Install-VddIfNeeded -InfPath $InfPath
Restart-Vdd
Write-Host ''
Write-Host "완료: 가상 모니터 $Count개, ${Width}x${Height}@${RefreshRate}Hz 설정을 적용했습니다."
Write-Host 'Windows 설정 > 시스템 > 디스플레이에서 새 모니터가 보이는지 확인해 주세요. 안 보이면 한 번 재부팅하세요.'
