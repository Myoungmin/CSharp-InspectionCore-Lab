# M3b 아키텍처

현재 프로그램은 Host 콘솔 프로세스 하나에서 Engine을 통해 가상 검사 한 건을 실행한다. C# RangeInspector와 C++ DLL 기반 NativeInspector를 선택할 수 있다. Core Engine의 단일 접수·Busy·상태·취소·타임아웃을 제공하며 Native 협조적 정지·진행 콜백·실제 종료 후 해제를 제공한다. 자동 반복, IPC, SQLite는 후속이다.

책임 분리·도구 체계·완료 경계·JSON 저장·Native 연동을 선택한 이유는 [ADR 목록](adr/README.md)에 기록한다. 이 문서는 현재 계약을 설명하며 결정이 바뀌면 새 ADR과 함께 갱신한다.

## 프로젝트와 소유권

![M3b runtime](diagrams/generated/architecture.svg)

Host가 SimulatedDevice, 선택한 IInspector 구현, JsonResultStore, InspectionRunner, InspectionEngine을 생성한다. NativeInspector는 SafeHandle로 C++ 객체 하나를 소유하며 Host는 Engine의 DisposeAsync를 기다린 후 NativeInspector를 해제한다. JsonResultStore는 각 저장의 FileStream을 자신의 메서드 안에서 해제한다. Engine은 실행별 토큰·타이머를 소유하고, Engine과 Runner 모두 주입받은 의존성은 해제하지 않는다.

![Project references](diagrams/generated/dependencies.generated.svg)

실선은 실제 MSBuild 평가 결과의 관리 프로젝트 참조이며 점선은 Interop의 Native DLL 런타임 호출이다. Core와 Interop에는 외부 패키지 참조가 없다. Tests는 Core만 참조한다. IntegrationTests가 실제 어댑터·DLL·파일을 검사하며 콘솔 프로세스와 배포는 검증 스크립트가 확인한다. NativeInspection은 관리 프로젝트를 참조하지 않는 독립 C++ DLL이다.

Native DLL은 Visual Studio MSBuild로 빌드하고 관리 프로젝트는 dotnet으로 빌드한다. Visual Studio 솔루션에는 Interop 이전에 Native를 빌드하도록 솔루션 의존성을 둔다. [Native ABI와 수명 계약](native-interop.md)을 함께 읽는다.

## 계약과 실행 순서

![Core contracts](diagrams/generated/core-class.svg)

- `InspectionJob`은 JobId, 허용 범위, 가상 취득용 샘플을 생성 시 검증·복사한다. 배열 변경이 이미 만들어진 Job에 영향을 주지 않는다. 샘플은 가상 장비의 재현 가능한 입력이며 실제 장비 연동을 가정하지 않는다.
- `IDevice`는 준비·취득, `IInspector`는 측정값 판정, `IResultStore`는 저장을 담당한다.
- `InspectionEngine.Start`가 실행 자리를 확보하고 RunId를 발급한다. 내부 Runner 호출은 이 RunId를 그대로 사용한다. 기존 공개 Runner.RunAsync를 직접 호출하면 Runner가 새 RunId를 발급하며, 네 단계를 마쳐야 InspectionResult를 반환한다.
- `InspectionAssessment`는 샘플 수·불량 수·점수·제품 판정이다. `InspectionResult`에는 RunId, JobId, 시작 시각과 **검사 완료 시각**이 들어간다. InspectedAtUtc는 저장 완료 시각이 아니다.
- 단계 실패는 RunId·단계·원래 예외를 가진 InspectionRunException으로 전파한다. 저장 실패 시 ComputedResult도 보존한다. 외부 토큰 취소는 OperationCanceledException을 상속하는 InspectionCanceledException으로 전달한다.

![Run sequence](diagrams/generated/run-sequence.svg)

취소 접수와 저장 진입은 Engine의 같은 잠금으로 결정한다. 취소가 먼저 접수되면 저장하지 않는다. 저장 진입 이후에는 호출자 취소 토큰을 저장소에 전달하지 않고 저장 결과를 기다린다. 저장 실패는 늦은 취소로 대체되지 않는다. Runner 직접 호출도 기존 원자적 경계를 유지한다. [Engine 상태·종료 계약](engine-contract.md)에 접수 결과, 최초 종료 원인, 실제 종료 대기와 조회 범위를 설명한다.

## 파일 저장

저장소는 같은 폴더의 고유 `.tmp` 파일에 JSON을 기록하고 스트림을 닫은 뒤 `<RunId>.json`으로 이동한다. 기존 동일 RunId 파일을 덮어쓰지 않는다. 실패 시 임시 파일 정리를 시도하며 정리 실패가 원래 예외를 가리지 않게 한다. 비정상 프로세스 종료로 남은 `.tmp`는 완료 결과가 아니다. 전원 장애에 대한 디스크 영속성이나 재시작 후 실행 복구는 보장 범위가 아니다.

## 검증 범위

Core MSTest 52개는 기존 Job·Runner 19개, Engine 25개, 종료 실패 판정 8개다. 동시 접수, 현재 단계, 취소·시간 초과·완료 경합, 콜백 종료, Dispose를 확인한다. 시간은 TimeProvider로 주입하고 비동기 순서는 TaskCompletionSource로 제어한다. 통합 테스트 35개는 기존 실제 DLL 20개·파일 3개와 Native 비동기 수명 12개다. 명시적 콜백 신호로 취소·Dispose·늦은 콜백과 정지·Wait 실패를 확인한다. 두 검사기의 실제 콘솔·JSON·저장 오류·DLL 누락·즉시 타임아웃은 verify.ps1에서 별도로 확인한다. [필수 사례 대응표](verification-map.md)로 기존 사례 누락을 검사한다.

상태 조회는 Core Engine API이며 Host는 아직 CLI 한 건 실행이다. 별도 프로세스의 조회·취소는 M4의 IPC 작업이다. NativeInspector는 Start에서 입력을 복사하고 Wait에서 작업·콜백을 join한다. Wait 실패 시 자원을 보존하고 Engine을 Faulted로 고정한다. 이때 TerminationConfirmed=false이며 관리 완료를 실제 Native 종료로 해석하지 않는다. [장애 대응](troubleshooting.md)을 따른다.
