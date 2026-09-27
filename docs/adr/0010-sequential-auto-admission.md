# ADR-0010: 자동 세션이 반복 사이 대기까지 예약하고 같은 실행 경로를 사용한다

- Date: 2026-09-27
- Status: Accepted

## Context

M3c에서는 수동 검사와 자동 반복이 같은 장비·검사기를 공유한다. 각 실행만 잠그면 반복 간격 중 수동 작업이 끼어들 수 있고 StopAuto를 현재 실행의 취소와 혼동하기 쉽다. [ADR-0006](0006-single-run-engine.md)의 접수 결정을 대체하며 한 건 Runner·저장 경계·Native 수명·종료 실패 규칙은 유지한다.

## Decision

InspectionEngine이 자동 세션 하나와 실행 하나를 같은 잠금으로 관리한다. Start와 StartAuto 중 하나만 접수하고 세션은 Running·Waiting·Stopping 동안 예약을 유지한다. 실행 큐는 없다. IsBusy는 실제 실행 존재, Mode=Automatic은 자동 예약 존재를 뜻한다. 대기 중 Busy에는 BusyRunId가 없을 수 있으므로 BusyAutoId를 함께 반환한다.

StartAuto는 검증·복사된 동일 Job을 반복한다. 첫 실행은 즉시 접수하고 각 실행은 새 RunId를 발급받아 기존 StartRunLocked/Runner 경로를 사용한다. 저장·취소 콜백·타이머 정리가 끝난 Completion 뒤 간격을 기다린다. 고정 시각을 따라잡는 예약이나 병렬 실행은 없다. 간격과 실행별 제한 시간은 TimeProvider로 제어한다. maxRuns는 양의 정수 또는 null(중지까지 반복)이며 모든 종료 상태가 완료 횟수에 포함된다.

StopAuto(AutoId)는 예약 전용 토큰만 취소한다. 현재 실행 토큰에는 영향을 주지 않으며 그 실행이 끝날 때까지 세션 예약을 유지한다. 다음 접수와 StopAuto는 같은 잠금에서 순서를 정한다. 이미 다음 실행이 접수됐으면 그것이 현재 실행이며 완료를 기다린다. 실제 현재 실행 취소는 CancelRun(RunId)다. Persisting의 늦은 취소는 기존처럼 TooLate이고 반복을 중지하지 않는다.

제품 Fail을 포함한 Succeeded만 다음 예약으로 간다. Canceled·TimedOut은 세션 Stopped, 실행 Faulted는 세션 Faulted다. 실행 실패가 수동 예약 중지와 겹치면 실행 결과 원인이 우선하며 LastRun에 원래 StopReason과 진단을 보존한다. 실행 성공 시 StopAuto 요청이 반복 횟수 완료보다 우선한다. 다음 예약/실행 설정 실패는 SchedulingFailed로 중지하고 실제 활성 작업이 없으면 엔진은 명시적인 새 접수를 허용한다. 종료 프로토콜 오류는 기존 ADR-0009대로 엔진 Faulted를 유지한다.

Dispose는 새 접수를 닫고 자동 예약 중지와 현재 실행 Shutdown을 요청한 뒤 실행·세션 정리를 모두 기다린다. 현재와 직전 세션 스냅샷만 조회하며 세션은 마지막 실행 하나만 보관한다. 전체 결과는 저장소에 남기므로 무제한 반복에서도 메모리 이력을 쌓지 않는다. 세션 완료 후 옛 AutoId/타이머는 새 실행에 영향을 주지 않는다.

## Alternatives

- Host가 Start를 반복 호출: 대기 구간의 예약과 StopAuto 경합을 중앙에서 보장하기 어려워 배제한다.
- 세션 토큰을 각 검사에 연결: StopAuto가 현재 실행을 취소하므로 계약과 다르다.
- 고정 주기 타이머로 실행 요청 적재: 느린 저장·Native 종료에서 대기열이 쌓이므로 완료 후 간격을 사용한다.
- 모든 완료 실행을 채널·목록에 보관: 소비자가 없는 무제한 실행의 메모리 증가를 피하기 위해 현재 단계는 마지막 결과와 누계만 제공한다.

## Consequences

기존 검사·저장·Native 종료를 재사용한다. 자동 대기는 IsBusy=false여도 수동 접수를 거절하므로 Mode와 BusyAutoId를 같이 읽어야 한다. Host CLI는 --repeat/--interval-ms로 유한 반복을 실행하고 세션 요약·마지막 실행을 출력한다. Ctrl+C는 StopAuto 후 CancelRun을 요청한다. 원격 제어와 DTO는 M4에서 추가한다.

## Validation

수동 시간과 명시적 신호로 접수 경합, 저장 후 간격, 예약 중지와 현재 취소, 이전 세션·타이머, 매 실행 새 타임아웃, 오류·종료를 확인한다. 실제 Native 콜백 중 두 중지 동작을 비교하고 Host의 두 검사기로 반복 Pass/Fail JSON 파일을 검사한다. 실제 증거는 작업 기록에 분리한다.

## Links

- Supersedes: [ADR-0006](0006-single-run-engine.md)
- [종료 실패 규칙](0009-termination-failure-quarantine.md)
- [자동 실행 계약](../auto-contract.md)
- [M3c 작업](../../tasks/M03c-auto-sequence.md)
- [TimeProvider를 이용한 Task.Delay](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-9.0)
