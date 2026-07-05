# VDD 생성 후 앱 목록에 안 뜨는 문제 수정

이 패치는 가상 모니터가 Windows에는 생겼는데 Virtual Display Manager의 `감지된 모니터` 목록에 안 뜨는 문제를 줄이기 위한 수정입니다.

## 원인

기존 `MonitorService.RefreshMonitors()`는 `EnumDisplayMonitors` 결과만 중심으로 목록을 만들었습니다.
일부 VDD 환경에서는 가상 디스플레이가 `\\.\DISPLAY2` 같은 디스플레이 장치로는 잡히지만, 앱의 기존 HMONITOR 열거 결과 또는 하위 모니터 장치명 기준으로는 VDD 표시명이 잘 안 잡힐 수 있습니다.

## 수정 내용

- `EnumDisplayMonitors` 기존 방식 유지
- `EnumDisplayDevices(null, index, ...)`로 Windows 디스플레이 장치도 추가 확인
- `EnumDisplaySettingsEx`로 `\\.\DISPLAYx`의 좌표/해상도 보강
- 기존 목록에 없는 활성 디스플레이 또는 VDD 추정 디스플레이를 fallback으로 추가
- VDD 판정 키워드 확대: `virtual display`, `mttvdd`, `IddSampleDriver`, `virtualdrivers` 등

## 적용 방법

1. 이 패치 압축을 풉니다.
2. Visual Studio 2022 또는 .NET 8 SDK가 있는 Windows에서 `VirtualDisplayManager.sln`을 엽니다.
3. `Release | x64`로 빌드합니다.
4. 관리자 권한으로 실행합니다.
5. `디스플레이 새로고침`을 누릅니다.

## 확인할 점

가상 모니터가 Windows 디스플레이 설정에는 있지만 목록에 계속 안 뜨면, Windows에서 아직 "활성 데스크톱 출력"으로 붙지 않은 상태일 수 있습니다.
이 경우 `Win + P`에서 `확장`을 선택하거나, 설정 → 시스템 → 디스플레이에서 가상 모니터를 확장 모드로 활성화한 뒤 앱에서 새로고침하십시오.
