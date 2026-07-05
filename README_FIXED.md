# Virtual Display Manager - VDD 생성 보강판

이 수정본은 기존 프로그램에 **가상 모니터 만들기** 흐름을 추가합니다.

## 바뀐 점

- `C:\VirtualDisplayDriver\vdd_settings.xml` 자동 생성/백업
- `<monitors><count>...</count></monitors>` 값 자동 설정
- 기본 해상도/주사율 XML 자동 작성
- VDD가 미설치 상태면 선택된 INF로 설치
- 설치 후 `pnputil /scan-devices` 및 VDD 장치 재시작 시도
- UI에 `가상 모니터 만들기`, 개수, 해상도, Hz 입력 추가

## 사용 방법

1. Visual Studio 2022 또는 .NET 8 SDK가 설치된 Windows에서 솔루션을 엽니다.
2. `VirtualDisplayManager.App`를 `Release | x64`로 빌드합니다.
3. 빌드된 EXE를 관리자 권한으로 실행합니다.
4. 개수 `1`, 해상도 `1920 x 1080`, Hz `60` 상태에서 `가상 모니터 만들기`를 누릅니다.
5. Windows 설정 > 시스템 > 디스플레이에서 새 모니터가 나타나는지 확인합니다.

## 바로 적용용 PowerShell

빌드하지 않고 바로 테스트하려면 관리자 PowerShell에서 다음을 실행할 수 있습니다.

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Scripts\Create-VirtualDisplay.ps1 -Count 1 -Width 1920 -Height 1080 -RefreshRate 60
```

특정 INF를 직접 쓰려면 다음처럼 실행합니다.

```powershell
.\Scripts\Create-VirtualDisplay.ps1 -InfPath "C:\path\to\MttVDD.inf" -Count 1
```

## 참고

제가 수정본 소스와 스크립트는 만들었지만, 현재 작업 환경에 Windows/.NET SDK가 없어 EXE 재빌드는 수행하지 못했습니다.
