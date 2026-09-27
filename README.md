# CSharp InspectionCore Lab

가상 검사 장비로 C# Non-UI Core를 실습하는 프로젝트다. 현재 구현 범위는 **M2: C# 또는 실제 C++ DLL로 검사 한 건 실행 → 판정 → JSON 저장**이다.

## 실행

Windows x64, .NET SDK **9.0.305**, VS 2022 17.14를 기준으로 한다. C++ x64 빌드 도구 **MSVC 14.44.35207 / v143**과 Windows SDK **10.0.26100.0**이 필요하다. 솔루션은 `InspectionLab.sln`이며 Visual Studio에서 x64로 빌드할 수 있다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-native.ps1
dotnet restore src/Inspection.Host/Inspection.Host.csproj --locked-mode
dotnet run --project src/Inspection.Host --configuration Release --no-restore -- --scenario pass --inspector managed
dotnet run --project src/Inspection.Host --configuration Release --no-restore -- --scenario fail --inspector native
```

각 실행은 새 RunId를 발급하고 `artifacts/results/<RunId>.json`에 결과를 저장한다. `--output`으로 저장 폴더를 지정할 수 있다. 경로에 공백이 있으면 따옴표로 감싼다. 검사기의 기본값은 기존과 같은 `managed`다. `native`를 선택했을 때 DLL이 없으면 실패하며 자동 대체하지 않는다.

혼합 솔루션의 `.vcxproj`는 Visual Studio MSBuild가 담당한다. CLI에서는 위 순서나 verify.ps1을 사용한다. `dotnet build InspectionLab.sln`은 사용하지 않는다. Debug 실행은 먼저 `build-native.ps1 -Configuration Debug`로 해당 DLL을 빌드한다. 빌드된 Native DLL은 Host·통합 테스트·publish 출력으로 복사된다.

| 시나리오 | 입력 | 허용 범위 | 결과 |
|---|---|---|---|
| pass | 10, 20, 30, 40 | 0 이상 100 이하 | Pass, 점수 100, 불량 0 |
| fail | 10, 20, 30, 140 | 0 이상 100 이하 | Fail, 점수 75, 불량 1 |

점수는 정상 샘플 비율을 백분율로 환산하고 소수 둘째 자리까지 반올림한다. 제품 판정 Fail도 저장에 성공하면 `Status=Succeeded`, 프로세스 종료 코드 0이다. 실행·저장 실패는 1, 잘못된 인수는 2, 저장 진입 전 취소는 130이다.

## 전체 검증

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
```

검증에는 Java와 `PLANTUML_JAR`가 추가로 필요하다. 현재 검증 도구는 Java 11, PlantUML **1.2026.6**, 내장 **Smetana** 레이아웃이다. [도구 고정값](docs/toolchain.json)에 기록된 JAR SHA-256도 검사한다. Graphviz는 사용하지 않는다.

```powershell
$env:PLANTUML_JAR = 'C:\tools\plantuml.jar'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
```

검증 스크립트는 Native 빌드 → 관리 프로젝트의 잠금 파일 restore·Release 빌드 → Core 단위 테스트 → Native·파일 통합 테스트 → 두 검사기의 실제 콘솔·JSON → 의존성·문서를 검사한다. 현재 필수 테스트는 Core 19개와 통합 23개(실제 DLL 20개 + 파일 3개)다. DLL 배포 해시, DLL 누락 시 실패, 저장 실패와 잘못된 인수도 확인한다. ADR 번호·상태·필수 항목·목록과 상대 링크도 검사한다. 테스트 누락·skip, 결과 파일 누락, 검증 중 소스 변경은 실패다. 상세 결과는 `artifacts/verification/<id>/summary.json`, TRX와 로그에 남는다.

## 구조와 다음 단계

- [작업 목록·템플릿·완료 기준](tasks/README.md)
- [핵심 계약과 필수 검증](docs/verification-map.md)
- [보류 사항과 재검토 기준](docs/backlog.md)
- [현재 아키텍처와 계약](docs/architecture.md)
- [아키텍처 결정 기록과 작성 절차](docs/adr/README.md)
- [M1 작업·인수 조건](tasks/M01-run-one-job.md)
- [실습 구현·검증 기록](docs/practice-M01.md)
- [M2 Native 연동·ABI 계약](docs/native-interop.md)
- [M2 작업·인수 조건](tasks/M02-native-inspector.md)
- [M2 실습 기록](docs/practice-M02.md)
- [문서 유지 규칙](docs/documentation-rules.md)
- [전체 단계와 Codex 루프 설계](InspectionLab-Architecture-and-Codex-Loop.md)

다음 단계는 M3a의 실행 접수·Busy·상태·취소·타임아웃이다. M3b에서 Native 작업과 콜백의 종료를, M3c에서 자동 반복을 연결한다. 현재 Native 호출은 동기식이므로 호출 중 강제 중단을 지원하지 않는다. IPC, SQLite, C++/CLI, Codex 자동 반복 제어기, .NET 10 전환은 후속 작업이다.

구조나 계약을 결정할 때는 ADR을 한 건씩 추가하고 구현·아키텍처·관련 다이어그램과 함께 갱신한다. 전체 설계 문서는 단계별 계획으로 계속 커밋하며, 현재 구조와 결정 이력은 각각 architecture.md와 ADR에서 관리한다.
