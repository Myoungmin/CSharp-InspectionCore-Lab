# ADR-0009: 종료 프로토콜 실패는 엔진 장애로 고정하고 미확인 자원을 보존한다

- Date: 2026-09-27
- Status: Accepted

## Context

정지 요청 실패와 종료 확인 실패는 제품 불량이나 일반 검사 오류와 다르다. 특히 Wait가 오류를 반환하면 작업·콜백이 살아 있는지 관리 코드가 확정할 수 없다. [ADR-0007](0007-stop-and-completion-arbitration.md)을 대체하며 최초 원인·저장 경계·정상 종료 대기 규칙은 그대로 유지하고 종료 실패의 우선순위를 확장한다.

## Decision

Core에 플랫폼 독립 InspectionTerminationException을 둔다. 어댑터는 RequestStop 실패 후에도 Wait를 계속한다. Wait가 성공하면 TerminationConfirmed=true인 종료 오류를 반환하고 안전하게 해제할 수 있다. Wait가 실패하면 false로 반환하고 GCHandle과 SafeHandle 추가 참조를 프로세스 종료까지 보존한다. 자동 재시도나 같은 어댑터 재사용은 없다.

Engine은 이 예외가 Runner의 단계 오류로 전달되면 실행과 엔진을 Faulted로 확정한다. 먼저 접수된 UserCancellation/Timeout/Shutdown은 진단에 보존하되 종료 실패를 Canceled나 TimedOut으로 덮지 않는다. Error에 단계 오류, Engine.Error에 종료 오류를 남긴다. Dispose 후에도 Faulted를 유지하며 Start는 항상 Unavailable이다.

스냅샷의 TerminationConfirmed는 진행 중 null, 정상 종료·종료 확인 후 장애는 true, 종료 확인 실패는 false다. 후자의 Completion과 CompletedAtUtc는 **관리 측 장애 판정 완료**이며 Native 작업 완료를 뜻하지 않는다. IsBusy=false여도 엔진이 Faulted이므로 실행 자리가 재사용되지 않는다. 이 명시적 예외 경로 외에는 실제 작업·콜백 완료 전에 실행을 확정하지 않는다.

## Alternatives

- Wait 오류에서도 Dispose: 사용 중일 수 있는 메모리를 해제하므로 배제한다.
- 종료 실패를 취소 성공으로 처리: 안전한 종료라는 잘못된 신호를 준다.
- 무조건 영원히 기다리기: 오류가 반환된 Wait에 반복 호출 계약이 없어 복구를 보장하지 못한다. 장애 판정을 알리고 자원을 격리한다.
- 프로세스 강제 종료 자동화: 상위 운영 정책이며 현재 라이브러리에서 결정하지 않는다. Host를 다시 시작하는 복구 절차를 기록한다.

## Consequences

Wait 실패 시 의도적으로 자원과 콜백 문맥이 남는다. 이는 종료 확인 없는 해제보다 안전한 중단 상태이며 정상 누수 없는 종료와 구분해야 한다. 복구에는 Host 프로세스 재시작이 필요하다. 실패를 주입한 통합 테스트도 해당 프로세스 종료까지 핸들 하나를 보존한다. Native가 Wait에서 반환하지 않는 경우에는 기존처럼 Busy와 종료 대기가 유지된다.

## Validation

Core 테스트는 종료 확인 여부와 네 원인(없음·사용자·타임아웃·종료)을 조합해 오류 우선순위, 원인 보존, 저장 차단, Dispose 후 재접수 차단을 확인한다. 실제 DLL에서 Stop 실패 후 join·안전한 해제와 Wait 실패 후 Dispose·GC에도 자원이 살아 있는지 확인한다.

## Links

- Supersedes: [ADR-0007](0007-stop-and-completion-arbitration.md)
- [Native 비동기 수명](0008-native-async-lifetime.md)
- [Engine 계약](../engine-contract.md)
- [장애 대응](../troubleshooting.md)
- [M3b 작업](../../tasks/M03b-native-lifetime.md)
