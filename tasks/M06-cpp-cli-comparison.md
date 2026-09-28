# M06 — C++/CLI 검사 어댑터 비교

상태: Verified. 기준 커밋: c8fe08a. 마일스톤: M6. 구현 검증과 사용자 학습 확인을 구분한다.

## 목표와 범위

별도 C++/CLI vcxproj가 IInspector를 구현하고 Host에서 managed/native/cli를 선택한다. 같은 Native C ABI를 LibraryImport 또는 C++ 헤더·import library로 호출한다. 비동기 실행·취소·콜백 join·미확인 종료 격리는 관리 수명 코드 하나를 공유한다. C++ 전용 SDK 추가·성능 우열 결론·도구 이전은 이번 범위가 아니다.

## 인수 조건

- 실제 x64 혼합 모드 DLL을 빌드·배포하고 C++/CLI 경로에서 Pass/Fail·배열 슬라이스·입력 오류를 확인한다.
- Create/Inspect/Stop/Wait 오류, 사전 취소·콜백 중 취소/Dispose·늦은 콜백·해제/GC와 미확인 Wait 자원 보존이 기존 계약과 일치한다.
- Host CLI·자동 실행·JSON/SQLite·IPC에서 cli를 실제 선택한다. 의존 DLL 누락은 명시적으로 실패하고 다른 검사기로 대체하지 않는다.
- 기존 필수 181개를 유지하고 공유 수명 리팩터링의 회귀를 검증한다. 추가 필수 사례는 내용을 검토해 수동으로 대응표에 기록한다.
- Native → 관리 수명/Core → C++/CLI → Host/테스트 순서를 스크립트와 Visual Studio에서 재현한다.

## 아키텍처 결정

[ADR-0015](../docs/adr/0015-cpp-cli-transport-and-shared-lifetime.md)에 C++/CLI 호출 경계와 공유 수명 정책, [ADR-0016](../docs/adr/0016-cpp-cli-build-and-deployment.md)에 혼합 프로젝트 빌드·배포 규칙을 기록했다. Core의 IInspector와 기존 완료·종료 계약은 유지한다. 구현과 함께 ADR 목록·참조 그림·아키텍처를 갱신한다.

## 검증 계획과 정책 변경

SDK 9.0.305, net9.0, x64, MSVC/Windows SDK 고정값을 유지한다. Visual Studio C++/CLI 구성 요소를 필수로 확인하고 혼합 DLL과 ijwhost 배포를 검증한다. dotnet으로 vcxproj를 빌드하지 않는다. 기존 테스트 이름과 인수 조건을 유지하며 M6-001~022의 36개(혼합 검사 16·수명 11·자동 2·프로세스 5·기존 저장 테스트의 cli 데이터 2)를 추가해 Core 94 + 통합 123 = 217개를 필수로 지정한다. 기존 181개 ID/사례/Assertion은 유지했다. 수명·자동 테스트는 기존 계약의 동일 신호·Assertion을 별도 C++/CLI 클래스에서 실행하며 transport는 실제 CliNativeApi다. 기준은 TRX에서 생성하지 않고 소스·인수 조건을 검토해 작성했다. 세 검사기의 smoke와 혼합 DLL 누락/publish 검증을 추가했다.

고정 C++/CLI 컴파일러가 .NET 숫자 타입의 사용하지 않는 static generic-math 메타데이터에서 C4679를 발생시켰다. 새 혼합 프로젝트에만 4679를 제외하고 /W4·그 외 경고 오류 처리 및 링커 경고 오류 처리를 유지한다. LNK4248은 경고 억제가 아닌 불투명 핸들을 unmanaged thunk 안에 두는 수정으로 제거했다. 생산 파일 참조 허용은 Host/통합 테스트의 정확한 C++/CLI 출력 경로로 한정하며 기존 MSTest 패키지의 확장 참조도 고정 경로로 검사한다.

통합 테스트는 실제 DLL과 프로세스를 사용하고 신호로 콜백 경합을 제어한다. Native·IPC skip은 허용하지 않는다. 최종 명령은 scripts/verify.ps1이다.

## 검증 증거와 리뷰

2026-09-28(KST)에 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1` 전체 검증을 통과했다.

- 검증 ID: `20260928T132935Z-09b2553b` (UTC). 기준 커밋: `c8fe08ae744462bb9faa951041d842276131c5a0`.
- 소스 SHA-256: `28B70B919506A73B156FE47792D71DBB081F14A4ECA461ACC063BEACB57D3B6B`.
- Native DLL SHA-256: `9742FEED4D483412AA11A2971B774048EDDB7D2380C13EDB8816B55C67C42832`.
- C++/CLI DLL SHA-256: `1717794B934556D909E8AC8CA6183A3BAFA5DF114E3B4BD12732EAA5A6D21D40`.
- ijwhost SHA-256: `DBF4AF33A1C3F65F70CB019B374DE6495F78E8DAB629EEB3738BEAAE9DB27B2B`.
- Core 94 + 통합 123 = 217개 Passed, 실패·skip 0. 기존 181개에 C++/CLI 34개와 cli 저장 프로세스 2개를 추가했다. 139개 검증 항목의 정확한 클래스·메서드·데이터 사례도 모두 통과했다.
- 세 검사기의 Pass/Fail·독립 RunId JSON 저장·순차 자동 반복·즉시 타임아웃과 기존 실패 조건을 확인했다. 실제 cli IPC·SQLite 재시작 조회·저장/Native 장애 진단을 통합 TRX에서 확인했다.
- Inspection.CppCli.dll·ijwhost.dll·NativeInspection.dll을 각각 누락시킨 실행은 Setup 실패로 보고하고 결과를 저장하지 않았다. publish 출력의 세 DLL 해시가 일치하며 실제 cli Fail 검사·JSON 저장이 성공했다.
- 잠금 restore, Native·C++/CLI·관리 Release 빌드, 참조 방향, ADR 16건·SVG 11개·상대 링크·Core 타입 검사를 통과했다. 원본 summary.json·TRX·로그·source-manifest.json은 `artifacts/verification/<id>`에 보관하고 버전 관리하지 않는다.
- 추가로 `scripts/build-cppcli.ps1 -Configuration Debug`와 실제 Debug cli Pass JSON 저장, Visual Studio MSBuild의 Release x64 솔루션 빌드를 통과했다. 빌드 로그는 `artifacts/m6-build-debug.log`, `artifacts/m6-solution-release.log`에 있다. 고정 도구 체계에서 허용한 C4679 이외의 경고를 완화하지 않았다.

자체 리뷰에서 두 transport의 Start 동안만 배열을 pin하고 성공한 Wait 이후에만 콜백 context와 SafeHandle lease를 해제하는 것을 확인했다. 종료 미확인 시 기존 격리 정책을 유지한다. C++ 불투명 핸들은 unmanaged thunk 안에 두어 CLR 메타데이터 경고를 제거했고, Host의 혼합 DLL 로딩을 Setup 오류 경계 안으로 제한했다. 원래의 Native 회귀 사례와 별도 C++/CLI 계약 사례를 모두 실행했다. 변경한 그림을 렌더링해 인터페이스 겹침과 구조도의 잘린 표시를 정리했다. 별도 리뷰 호출은 하지 않았다.

이 증거 문단과 그림 표시 수정 이후 최종 문서를 포함해 전체 검증을 다시 실행하고, 마지막 검증 ID·소스 해시는 커밋 본문에 기록한다. 문서에 자기 자신의 해시를 계속 갱신하는 순환을 피한다. 코드·ADR·전체 설계·SVG·작업 기록을 같은 커밋으로 묶고 실행 산출물은 제외한다.

## 남은 일과 학습 확인

도구 이전 B01·Codex 제어기 B03·장기 운영 B08은 [보류 목록](../docs/backlog.md)의 별도 작업으로 유지한다. C++/CLI 도구·경고 호환성도 B01에서 재검토한다. 공유 수명을 사용하는 이번 비교로 독립 C++/CLI 수명 구현이나 성능 우열까지 검증한 것은 아니다. 사용자의 직접 설명·수정·재현은 아직 확인하지 않았다.

## 완료 확인

- 인수 조건과 필수 217개 사례·전체 검증을 통과했다.
- ADR-0015/0016, 아키텍처·그림·검증 대응표·개발 규칙을 갱신했다.
- 실제 증거와 자체 리뷰, 최종 문서 재검증 절차를 기록했다.
- 후속 도구 이전·자동 제어기·장기 운영과 학습 미확인을 구분했다.
- 전체 설계 문서는 계속 유지·커밋하며 DLL/DB/JSONL/검증 artifacts는 제외한다.
