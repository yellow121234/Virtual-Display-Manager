# Virtual Display Manager for Windows

`VirtualDrivers/Virtual-Display-Driver`의 서명된 Release 패키지를 설치·삭제·진단하고, Windows가 감지한 모니터 한 대를 사용자의 명시적 동의 후 앱 안에서 미리 보는 WPF 관리 프로그램입니다. 자체 드라이버 코드는 포함하지 않습니다.

- 대상: Windows 10/11 x64
- 앱: C# / .NET 8 / WPF
- 드라이버 원본: [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
- 캡처: DXGI Desktop Duplication API(SharpDX 바인딩)
- 드라이버 관리: `pnputil.exe`, WMI, SetupAPI

## 1. 아키텍처

```text
VirtualDisplayManager.App (WPF, requireAdministrator)
 └─ MainWindow: 상태 표시, 사용자 확인, 버튼/테이블/미리보기/로그
          │
          ▼
VirtualDisplayManager.Core
 ├─ PrivilegeService: 관리자 토큰 확인과 UAC 재실행
 ├─ DriverService: 공식 Release 다운로드, 패키지 검증/서명, pnputil 설치·삭제·상태 조회
 ├─ MonitorService: Win32 모니터 열거와 안정적인 복합 ID 생성
 ├─ CaptureService: 선택 출력만 DXGI Desktop Duplication으로 캡처
 ├─ LoggingService: UI 로그 이벤트
 └─ Interop: WinVerifyTrust 서명 확인, 서명 패키지용 Root 장치 생성
```

위험한 시스템 작업은 `DriverService`와 Interop 계층에만 있습니다. UI는 사용자 확인과 결과 표시를 담당합니다. 네트워크는 공식 GitHub Release 조회와 드라이버 ZIP 다운로드에만 사용하며, 화면이나 로그를 전송하지 않습니다. 백그라운드 상주, 자동 시작, 숨김 실행, UAC/서명 우회도 구현하지 않습니다.

## 2. 프로젝트 구조

```text
VirtualDisplayManager.sln
VirtualDisplayManager.App/
  App.xaml(.cs)
  MainWindow.xaml(.cs)
  app.manifest
  VirtualDisplayManager.App.csproj
VirtualDisplayManager.Core/
  Models/
  Services/
    PrivilegeService.cs
    DriverService.cs
    MonitorService.cs
    CaptureService.cs
    LoggingService.cs
  Interop/
    SignatureVerifier.cs
    SetupApiDeviceCreator.cs
```

## 3. 드라이버 Release 자동 다운로드

드라이버 바이너리는 앱에 재배포하지 않습니다. 로컬 패키지가 없는 상태에서 **드라이버 설치**를 누르면 앱이 [공식 GitHub Releases](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases)의 최신 배포판을 조회하고 현재 아키텍처용 Driver Only ZIP을 자동으로 다운로드합니다. `Drivers` 폴더를 만들거나 Release 파일을 직접 배치할 필요가 없습니다.

다운로드한 패키지는 `%LOCALAPPDATA%\VirtualDisplayManager\DriverPackages\<Release 태그>`에 캐시됩니다. 압축 경로 탈출과 비정상적인 크기를 차단하고, INF의 VDD 식별자·아키텍처·참조 파일을 검사하며, WinVerifyTrust가 CAT 서명을 신뢰할 때만 설치 후보로 사용합니다.

## 4. 빌드

필수 항목:

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 또는 Visual Studio 2022의 .NET 데스크톱 개발 워크로드
- 인터넷 연결(첫 NuGet 복원 및 드라이버 패키지 최초 다운로드)

PowerShell에서:

```powershell
dotnet restore .\VirtualDisplayManager.sln
dotnet build .\VirtualDisplayManager.sln -c Release
```

빌드 출력:

```text
VirtualDisplayManager.App\bin\Release\net8.0-windows\win-x64\
```

자체 포함 단일 배포 폴더가 필요하면:

```powershell
dotnet publish .\VirtualDisplayManager.App\VirtualDisplayManager.App.csproj `
  -c Release -r win-x64 --self-contained true
```

## 5. 실행과 관리자 권한

`VirtualDisplayManager.App.exe`를 실행합니다. `app.manifest`는 다음을 선언하고 `.csproj`의 `ApplicationManifest`가 이 파일을 연결합니다.

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

따라서 Windows는 프로세스 시작 전에 UAC를 표시합니다. 승인하면 상승된 앱이 열리고 기존 일반 권한 프로세스는 남지 않습니다. UAC를 거부하면 Windows가 실행을 취소합니다. 매니페스트가 무시되는 특수 호스트를 위해 `PrivilegeService.RestartAsAdministratorIfNeeded()`도 시작 시 검사하며, UI의 재실행 버튼은 제한 모드에서 `TryRestartAsAdministrator()`를 호출합니다. 오류 1223(UAC 취소)은 정상적으로 처리합니다. UAC 우회는 없습니다.

관리자 권한이 없는 경우 설치/삭제 버튼은 비활성화됩니다.

## 6. 드라이버 설치 흐름

1. 로컬 패키지가 없으면 공식 GitHub 최신 Release의 현재 아키텍처용 ZIP을 다운로드하고 캐시합니다.
2. 선택 INF가 원본 VDD 식별자와 Display 클래스를 포함하는지 확인합니다.
3. `CatalogFile`과 `[SourceDisksFiles]`를 읽어 CAT, DLL/SYS 등 실제 참조 파일의 존재 여부를 검사합니다.
4. OS 아키텍처와 INF의 대상 아키텍처를 비교합니다.
5. WinVerifyTrust로 모든 참조 CAT의 Authenticode 신뢰 상태를 확인합니다. 실패하면 즉시 중단합니다.
6. `pnputil /enum-drivers`, `pnputil /enum-devices /class Display`, `Win32_PnPSignedDriver`로 중복 설치를 확인합니다.
7. 사용자 확인 후 `pnputil /add-driver <INF> /install`을 실행합니다.
8. 최초 설치에서 `pnputil`이 패키지만 Driver Store에 등록하고 Root 장치를 만들지 않는 경우, **서명 등록 성공 뒤에만** INF에서 읽은 `Root\MttVDD` 장치를 표준 SetupAPI로 생성합니다.
9. `pnputil /scan-devices`와 `/add-driver ... /install`을 다시 수행합니다.
10. VDD 장치와 디스플레이 목록을 재조회해 실제 장치 감지를 확인합니다.

SetupAPI 보조 단계는 새로운 드라이버를 만들지 않습니다. 원본 INF의 하드웨어 ID를 가진 PnP 장치 노드를 만들 뿐이며, 실제 바인딩과 패키지 관리는 Windows와 `pnputil`이 담당합니다.

## 7. 드라이버 삭제 흐름

1. 삭제 확인 대화상자를 표시합니다.
2. 실행 중인 미리보기를 먼저 중지하고 DXGI 리소스를 해제합니다.
3. `pnputil`/WMI에서 `Root\MttVDD`, `MttVDD`, 원본 이름/공급자에 맞는 장치와 Driver Store 패키지를 찾습니다.
4. 각 장치에 `pnputil /remove-device <InstanceId>`를 실행합니다.
5. 식별된 `oem*.inf`에 `pnputil /delete-driver <oem*.inf> /uninstall`을 실행합니다.
6. 다시 상태를 조회하고, 출력이 재부팅을 요구하면 UI에 표시합니다.

`/force`를 사용하지 않으며 이미 삭제된 경우 성공 성격의 “이미 삭제됨” 결과를 반환합니다.

## 8. 상태 확인 흐름

폴더 존재만으로 설치됨을 판단하지 않습니다.

- `pnputil /enum-drivers`: Driver Store의 Published/Original INF, 공급자, 클래스, 서명자 파싱
- `pnputil /enum-devices /class Display`: 실제 Display 장치의 Instance ID, 상태, 드라이버 파싱
- `Win32_PnPSignedDriver`: 로캘/출력 차이를 보완하는 WMI 교차 확인
- `C:\VirtualDisplayDriver\vdd_settings.xml`: 설정 파일 존재만 별도 표시

패키지만 Driver Store에 있고 실제 장치가 없으면 “오류”로 표시합니다. 실제 VDD 장치가 있어야 “설치됨”입니다. 설정 파일 수정 UI는 최소 구현 범위에서 의도적으로 제외했지만, 향후 수정 전에 사용할 `BackupVddSettingsXml()`은 타임스탬프 `.bak` 파일을 생성합니다.

## 9. 모니터 감지 흐름

`EnumDisplayMonitors`, `GetMonitorInfo`, `EnumDisplayDevices`로 현재 활성 모니터를 읽습니다. 각 행은 이름, `\\.\DISPLAYx` 장치명, 장치 ID, 좌표, 해상도, 주 디스플레이 여부를 가집니다.

내부 ID는 Windows 표시 번호 하나가 아니라 `장치명 + PnP 장치 ID + 좌표/경계` 조합입니다. `MttVDD`, `Virtual Display Driver`, `IddSampleDriver`, 원본 공급자 힌트가 있으면 “VDD 추정”으로 표시합니다. Windows의 디스플레이 번호를 1번으로 강제하는 기능은 없습니다. 주 디스플레이 변경도 이 최소 구현에는 포함하지 않아 운영체제 배치를 임의로 변경하지 않습니다.

## 10. 화면 미리보기 흐름

1. 앱 시작 시 캡처하지 않습니다.
2. 사용자가 모니터 테이블에서 대상 하나를 선택합니다.
3. 사용자가 “미리보기 시작”을 누릅니다.
4. 선택 모니터의 DXGI 출력만 Desktop Duplication으로 캡처합니다.
5. 프레임은 앱 메모리에서 `BitmapSource`로 변환되고 WPF `Image`의 `Uniform` 렌더링으로 비율을 유지합니다.
6. FPS는 5/10/15/30/60, 미리보기 내부 해상도 스케일은 25/50/75/100% 중 선택합니다.
7. “미리보기 중지”, 창 닫기, 드라이버 삭제, 대상 모니터 제거 시 캡처와 D3D/DXGI 리소스를 해제합니다.

프레임은 파일이나 서버로 보내지 않습니다. 네트워크 코드가 없습니다. Desktop Duplication이 지원되지 않는 세션, 잠금 화면, 일부 원격 세션 또는 GPU 드라이버 상태에서는 친화적 오류를 표시하고 안전하게 중지합니다.

## 11. 주요 클래스와 공개 메서드

- `PrivilegeService`: `IsRunAsAdministrator`, `RestartAsAdministratorIfNeeded`, `TryRestartAsAdministrator`
- `DriverService`: `FindVddDriverPackage`, `FindVddInfFiles`, `ValidateVddPackage`, `CheckVddSignature`, `IsVddInstalled`, `InstallVddAsync`, `UninstallVddAsync`, `GetVddInstallDirectory`, `GetVddSettingsFilePath`, `BackupVddSettingsXml`, `RefreshVddDevices`, `ParsePnPUtilEnumDrivers`, `ParsePnPUtilEnumDevices`
- `MonitorService`: `GetMonitors`, `RefreshMonitors`, `GetPrimaryMonitor`, `GetMonitorById`
- `CaptureService`: `StartPreview`, `StopPreview`, `SetFpsLimit`, `SetPreviewScale`, `DisposeCaptureResources`
- `LoggingService`: 시간, 단계, 성공/경고/오류가 있는 UI 로그 이벤트

설치/삭제는 `SemaphoreSlim`으로 동시 실행을 막고 비동기 프로세스로 실행되어 UI를 차단하지 않습니다. 로그에는 실행 명령, 종료 코드, 성공/실패와 설명이 표시됩니다.

## 12. 안전 제한

- 원본 VDD로 식별되지 않는 INF는 거부합니다.
- CAT 누락, 바이너리 누락, 아키텍처 불일치, 신뢰되지 않는 서명은 설치 전에 거부합니다.
- 테스트 서명 모드, Secure Boot, 보안 정책, 드라이버 서명 적용 설정을 변경하지 않습니다.
- 화면은 모니터 선택 + 시작 버튼 전에는 캡처하지 않습니다.
- 네트워크 전송, 자동 시작, 백그라운드 상주, 트레이 은닉, 프로세스 위장 기능이 없습니다.
- 위험한 설치·삭제 전 확인 대화상자를 표시합니다.
- 주요 GPU/칩셋 드라이버 업데이트 전 VDD 제거를 권장하는 원본 프로젝트의 주의사항을 따르십시오.

## 13. 테스트 체크리스트

테스트는 복구 가능한 개발 PC 또는 VM에서 먼저 수행하십시오.

- [ ] `dotnet build -c Release`가 경고/오류 없이 완료됨
- [ ] UAC 승인 시 상단에 “관리자 권한: 예” 표시
- [ ] UAC 취소 시 앱이 실행되지 않거나 제한 모드 유지
- [ ] 로컬 패키지가 없을 때 공식 Release가 자동 다운로드되고 캐시됨
- [ ] 인터넷 연결이 없을 때 명확한 다운로드 오류를 표시함
- [ ] CAT/DLL 누락 패키지가 설치 전에 거부됨
- [ ] 변조/무서명 CAT가 설치 전에 거부됨
- [ ] 다른 아키텍처 INF가 거부됨
- [ ] 여러 INF가 콤보박스에 나타나고 원본 VDD 후보가 우선 선택됨
- [ ] 서명된 Release 설치 후 Device Manager Display adapters에 Virtual Display Driver 표시
- [ ] 앱 상태가 “설치됨”이고 VDD 장치가 로그/상태에 표시됨
- [ ] Windows 디스플레이 설정과 앱 테이블에 가상 모니터 표시
- [ ] 모니터를 선택하지 않으면 미리보기가 시작되지 않음
- [ ] 선택 후 시작 시 해당 모니터만 비율 유지로 표시
- [ ] FPS/스케일 변경이 적용됨
- [ ] 중지 버튼이 즉시 캡처를 끝냄
- [ ] 대상 디스플레이 비활성화/제거 시 예외로 앱이 종료되지 않고 캡처만 중지됨
- [ ] 미리보기 중 삭제 시 캡처가 먼저 중지됨
- [ ] 이미 설치/삭제 상태에서 중복 작업이 수행되지 않음
- [ ] 동시 설치/삭제 클릭이 Busy로 차단됨
- [ ] 제거 뒤 장치와 `oem*.inf`가 사라지고 필요 시 재부팅 표시
- [ ] 캡처 프레임이 파일 또는 네트워크로 전송되지 않음

## 14. 알려진 범위

- 앱은 드라이버 Release 바이너리를 포함하지 않으며, 최초 설치 시 공식 GitHub에서 자동으로 다운로드합니다.
- 이 빌드는 x64 대상입니다. ARM64용으로 확장할 때는 별도 `win-arm64` 빌드와 원본 ARM64 서명 패키지가 필요합니다.
- Windows 로캘별 `pnputil` 라벨은 한국어/영어 중심으로 파싱하고 WMI로 교차 확인합니다.
- 실제 드라이버 설치/삭제 검증은 시스템 상태를 변경하므로 빌드 과정에서 자동 실행하지 않습니다.
