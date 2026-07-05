# F11 주 모니터 복구 패치

이 패치는 F11 전역 단축키 동작을 변경합니다.

## 변경 전

- F11: `DisplaySwitch.exe /internal`
- 결과: PC 화면만 모드로 변경되어 가상 모니터/확장 화면이 꺼질 수 있음

## 변경 후

- F11: VDD가 아닌 물리 디스플레이를 찾아 주 디스플레이로 지정
- 결과: 디스플레이 확장 상태는 유지하고, 주 모니터만 물리 모니터로 되돌림

## 핵심 변경 파일

- `VirtualDisplayManager.App/Services/DisplayRecoveryService.cs`
- `VirtualDisplayManager.App/MainWindow.xaml.cs`
- `VirtualDisplayManager.App/MainWindow.xaml`

## 주의

프로그램이 완전히 종료된 상태에서는 전역 F11이 작동하지 않습니다.
그때만 `Scripts/EmergencyDisplayRecover.cmd`를 실행하면 `DisplaySwitch.exe /internal`로 강제 복구할 수 있습니다.
