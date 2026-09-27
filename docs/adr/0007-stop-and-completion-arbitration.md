# ADR-0007: 종료 원인 접수와 실제 완료를 분리하고 같은 잠금으로 판정한다

- Date: 2026-09-27
- Status: Accepted

## Context

취소·시간 초과·검사 완료·저장 진입이 경합한다. 취소 요청만으로 실행 자리를 비우면 아직 동작 중인 장비와 다음 실행이 겹친다. 기존 Runner의 저장 완료 계약을 Engine에서도 보존해야 한다.

## Decision

실행별로 사용자 취소·Timeout·Shutdown 중 먼저 접수된 원인을 보존한다. 원인 접수와 Persisting 진입은 Engine의 같은 잠금 안에서 판단한다. 먼저 접수된 원인은 저장을 막고 상태를 CancelRequested로 둔다. 이후 원인 요청은 기존 원인을 바꾸지 않는다. 저장 진입 뒤의 요청은 TooLate이며 저장의 성공·실패를 기다린다.

실제 Runner 반환 시 완료 판정 구간을 닫아 새로운 종료 원인 접수를 막는다. 그 전에 접수된 원인이 있으면 Canceled 또는 TimedOut으로 확정하며 경합한 실행 예외도 진단 정보로 보존한다. 원인이 없으면 실행 예외는 Faulted다. 제품 Fail과 저장 성공은 Succeeded다.

CancellationTokenSource.CancelAsync로 토큰 상태를 바꾸고 콜백을 비동기로 전달한다. 잠금 안에서 외부 취소 콜백을 직접 실행하지 않는다. Runner 종료와 취소 콜백 완료, 타이머 정리까지 기다린 후 완료 Task를 확정하고 실행 자리를 비운다. 콜백·정리 실패는 Faulted와 진단을 남기고 엔진도 Faulted로 유지해 재접수를 거절한다.

시간 제한은 접수 시 시작하는 일회성 TimeProvider 타이머다. 0은 즉시 시간 초과 요청이며 장비 호출을 하지 않는다. 무제한은 null 또는 InfiniteTimeSpan이다. 이전 실행 타이머는 실행 객체를 확인해 다음 실행을 취소할 수 없다. DisposeAsync는 신규 접수를 닫고 현재 실행에 Shutdown을 요청한 뒤 실제 완료를 기다린다.

## Alternatives

- 토큰 연결만으로 취소와 시간 초과 처리: 어떤 원인이 먼저 접수됐는지 보존하기 어렵다.
- 대기 시간 초과 즉시 슬롯 반환: 실제 Native 작업과 다음 실행이 겹칠 수 있다.
- 저장에도 취소 전달: 이미 채택한 저장 결과 확정 계약과 충돌한다.

## Consequences

종료 원인과 실제 종료 시점을 명확히 구분한다. 비협조적 의존성이 끝나지 않으면 종료 대기도 끝나지 않는다. M3b의 Native 정지 실패·작업 및 Native 콜백 종료 확인은 별도 확장이며 이번 토큰 콜백 검증으로 대체하지 않는다.

## Validation

명시적 신호로 취소·저장·반환 순서를 만들고 수동 TimeProvider로 시간 초과를 발생시킨다. 최초 원인 유지, 늦은 취소 무시, 종료 전 Busy, 콜백 실패 시 재접수 차단을 확인한다.

## Links

- [M3a 작업](../../tasks/M03a-single-run-engine.md)
- [저장 완료 경계](0003-persistence-completion-boundary.md)
- [단일 접수 Engine](0006-single-run-engine.md)
- [CancelAsync 계약](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancelasync?view=net-9.0)
- [TimeProvider 타이머](https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.createtimer?view=net-9.0)
