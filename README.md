# CSharp InspectionCore Lab

가상 검사 장비로 C# Non-UI Core를 실습하는 프로젝트다. 현재 구현 범위는 **M0·M1: 콘솔에서 검사 한 건 실행 → 판정 → JSON 저장**이다.

## 실행

Windows x64, .NET SDK **9.0.305**, VS 2022 17.14를 기준으로 한다. 솔루션은 `InspectionLab.sln`이다.

```powershell
dotnet restore InspectionLab.sln --locked-mode
dotnet run --project src/Inspection.Host -- --scenario pass
dotnet run --project src/Inspection.Host -- --scenario fail
```

각 실행은 새 RunId를 발급하고 `artifacts/results/<RunId>.json`에 결과를 저장한다. `--output`으로 저장 폴더를 지정할 수 있다. 경로에 공백이 있으면 따옴표로 감싼다.

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

검증 스크립트는 잠금 파일 기반 restore, Release 빌드, MSTest, 실제 콘솔 실행·JSON 내용·저장 실패·잘못된 인수, MSBuild 참조 평가, UML/SVG·문서 링크를 검사한다. 테스트 누락·skip, 결과 파일 누락, 검증 중 소스 변경은 실패다. 상세 결과는 `artifacts/verification/<id>/summary.json`, TRX와 로그에 남는다. Native·IPC 테스트는 아직 구현 대상이 아니다.

## 구조와 다음 단계

- [현재 아키텍처와 계약](docs/architecture.md)
- [M1 작업·인수 조건](tasks/M01-run-one-job.md)
- [실습 구현·검증 기록](docs/practice-M01.md)
- [문서 유지 규칙](docs/documentation-rules.md)
- [전체 단계와 Codex 루프 설계](InspectionLab-Architecture-and-Codex-Loop.md)

다음 단계는 M2의 C ABI DLL·LibraryImport 교체다. M3에서 실행 엔진과 Native 종료를 추가한 뒤 자동 반복을 연결한다. IPC, SQLite, C++/CLI는 이후 단계다. .NET 10 전환도 별도 작업으로 진행한다.
