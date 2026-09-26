# M0·M1 아키텍처

현재 프로그램은 Host 콘솔 프로세스 하나에서 가상 검사 한 건을 실행한다. Native DLL, 실행 엔진의 Busy·상태 조회, 자동 반복, IPC, SQLite는 아직 없다.

## 프로젝트와 소유권

![M1 runtime](diagrams/generated/architecture.svg)

Host가 SimulatedDevice, RangeInspector, JsonResultStore, InspectionRunner를 생성한다. 현재 어댑터들은 지속적으로 보유하는 해제 대상 자원이 없다. JsonResultStore는 각 저장의 FileStream을 자신의 메서드 안에서 해제한다. Runner는 주입받은 객체를 해제하지 않는다.

![Project references](diagrams/generated/dependencies.generated.svg)

이 그림은 실제 MSBuild 평가 결과에서 생성한다. 실행 중 Core가 인터페이스를 호출하는 방향과 프로젝트 참조 방향을 구분한다. Core에는 외부 패키지 참조가 없다. Tests도 Core만 참조하며 실제 어댑터·콘솔·파일은 검증 스크립트가 확인한다.

## 계약과 실행 순서

![Core contracts](diagrams/generated/core-class.svg)

- `InspectionJob`은 JobId, 허용 범위, 가상 취득용 샘플을 생성 시 검증·복사한다. 배열 변경이 이미 만들어진 Job에 영향을 주지 않는다. 샘플은 가상 장비의 재현 가능한 입력이며 실제 장비 연동을 가정하지 않는다.
- `IDevice`는 준비·취득, `IInspector`는 측정값 판정, `IResultStore`는 저장을 담당한다.
- `InspectionRunner.RunAsync`는 매번 새 RunId를 발급한다. 고정된 네 단계를 모두 마쳐야 InspectionResult를 반환한다.
- `InspectionAssessment`는 샘플 수·불량 수·점수·제품 판정이다. `InspectionResult`에는 RunId, JobId, 시작 시각과 **검사 완료 시각**이 들어간다. InspectedAtUtc는 저장 완료 시각이 아니다.
- 단계 실패는 RunId·단계·원래 예외를 가진 InspectionRunException으로 전파한다. 저장 실패 시 ComputedResult도 보존한다. 외부 토큰 취소는 OperationCanceledException을 상속하는 InspectionCanceledException으로 전달한다.

![Run sequence](diagrams/generated/run-sequence.svg)

취소 접수와 저장 진입은 실행별 원자적 경계에서 결정한다. 취소가 먼저 접수되면 저장하지 않는다. 저장 진입 이후에는 호출자 취소 토큰을 저장소에 전달하지 않고 저장 결과를 기다린다. 저장 실패는 늦은 취소로 대체되지 않는다. 이 경계는 **실행 한 건 내부의 계약**이며, 동시 Start 제어나 Native 중단 완료를 보장하는 엔진은 M3에서 구현한다.

## 파일 저장

저장소는 같은 폴더의 고유 `.tmp` 파일에 JSON을 기록하고 스트림을 닫은 뒤 `<RunId>.json`으로 이동한다. 기존 동일 RunId 파일을 덮어쓰지 않는다. 실패 시 임시 파일 정리를 시도하며 정리 실패가 원래 예외를 가리지 않게 한다. 비정상 프로세스 종료로 남은 `.tmp`는 완료 결과가 아니다. 전원 장애에 대한 디스크 영속성이나 재시작 후 실행 복구는 보장 범위가 아니다.

## 검증 범위

MSTest는 정상·불량, 단계별 실패와 후속 호출 차단, Job 스냅샷, RunId 분리, 취소 시점과 저장 결과의 관계를 검사한다. 시간은 TimeProvider로 주입하고 비동기 순서는 TaskCompletionSource로 제어한다. 실제 콘솔·JSON·저장 오류는 verify.ps1에서 별도로 확인한다.

M3의 Persisting/CancelRequested/TimedOut 등 공개 실행 상태는 전체 설계 문서에 정의한 후속 계약이다. 현재 Host의 종료 로그를 상태 조회 API로 취급하지 않는다.
