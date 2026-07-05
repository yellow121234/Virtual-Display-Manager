# F11 긴급 화면 복구 패치

이 패치는 Virtual Display Manager에 전역 F11 복구키를 추가합니다.

## 동작

- 프로그램이 실행 중이면 창이 최소화되어 있어도 F11을 누를 수 있습니다.
- F11이 눌리면 `DisplaySwitch.exe /internal`을 실행합니다.
- Windows 표시 모드를 `PC 화면만`으로 바꿔서 가상 모니터가 메인으로 잡힌 상황을 복구합니다.
- 같은 기능을 실행하는 `물리 화면 복구(F11)` 버튼도 추가했습니다.

## 추가/수정 파일

- `VirtualDisplayManager.App/Services/GlobalHotkeyService.cs`
  - Win32 `RegisterHotKey`로 F11 전역 단축키 등록
- `VirtualDisplayManager.App/Services/DisplayRecoveryService.cs`
  - `DisplaySwitch.exe /internal` 실행
- `VirtualDisplayManager.App/MainWindow.xaml`
  - `물리 화면 복구(F11)` 버튼 및 안내 문구 추가
- `VirtualDisplayManager.App/MainWindow.xaml.cs`
  - 프로그램 시작 시 F11 등록
  - 프로그램 종료 시 F11 등록 해제
  - F11/버튼 클릭 시 화면 복구 실행
- `Scripts/EmergencyDisplayRecover.cmd`
  - 프로그램이 실행되지 않는 상황에서 수동으로 PC 화면만 모드 복구

## 빌드

Windows에서 Visual Studio 2022 또는 .NET 8 SDK로 빌드하십시오.

```powershell
dotnet build .\VirtualDisplayManager.sln -c Release -p:Platform=x64
```

## 주의

- F11은 전역 단축키로 등록되므로 프로그램 실행 중에는 다른 앱의 F11 전체화면 기능보다 이 복구 기능이 우선될 수 있습니다.
- 프로그램이 완전히 종료되어 있으면 F11은 작동하지 않습니다. 이때는 `Win + R` → `DisplaySwitch.exe /internal` 또는 `Scripts\EmergencyDisplayRecover.cmd`를 사용하십시오.
- F11 등록이 실패하면 다른 프로그램이 이미 F11을 전역 단축키로 점유하고 있을 수 있습니다.
