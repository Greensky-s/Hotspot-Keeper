# Hotspot Keeper 2.6 — 오류 진단 기능

2.5 버전의 자동 복구·로그 삭제·기간 보관·QR·SSID/비밀번호 변경 기능을 유지합니다.

## 추가 기능
- 활동 기록 > **문제 진단**: 최근에 확인한 오류 코드, 가능한 원인, 권장 확인 절차 표시
- **Windows 설정 열기**: 모바일 핫스팟 설정 바로가기
- **진단 보고서 저장**: 프로그램 버전, OS 버전, 진단 코드와 안내 사항만 저장. 원본 로그, SSID, 비밀번호, 기기 식별자는 포함하지 않음
- 오류 코드 분류: 인터넷 연결 프로필 없음, 권한 부족, Windows API 오류, 핫스팟 복구 실패, 기타 오류
- 오류가 발생한 후 정상 연결이 확인되면 현재 진단은 정상 상태로 초기화. 이전 발생 내역은 활동 기록에 남음

## 주의
- 진단은 원인 추정 및 확인 방법 안내이며 자동 수리나 확정 진단이 아닙니다.
- 로그 보관 기간 및 삭제 기능은 기존 2.5와 동일합니다.
- Store 배포 전환은 포함하지 않았습니다.
- 이 환경에서 실제 Windows 컴파일 및 실행 검증을 완료하지 못했습니다. 기존 EXE를 백업하고 테스트하세요.

## 빌드
PowerShell에서 프로젝트 폴더를 열고:

```powershell
dotnet publish .\HotspotKeeper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

기본 경로: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\HotspotKeeper.exe`
