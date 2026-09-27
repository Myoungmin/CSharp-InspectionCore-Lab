# ADR-0008: Native 작업과 콜백을 Wait로 종료한 뒤 해제한다

- Date: 2026-09-27
- Status: Accepted

## Context

M3a는 관리 실행의 접수와 취소를 제공하지만 M2 Native Run은 동기 호출이다. Native 작업에 협조적 정지와 진행 통지를 추가하면 입력 포인터·콜백 문맥·핸들의 수명이 호출 반환보다 길어진다. [ADR-0005](0005-native-c-abi-adapter.md)의 동기 수명 결정을 대체한다.

## Decision

C ABI v2의 Start는 입력을 Native vector로 복사한 뒤 작업 스레드를 시작한다. RequestStop은 원자적 정지 요청만 기록한다. Wait의 성공은 스레드 join과 모든 진행 콜백 반환을 뜻하며, 검사 자체의 성공·취소·오류는 별도 출력 상태로 받는다. Destroy는 join하지 않은 핸들을 Busy로 거절한다. 관리 어댑터는 한 번에 한 작업만 허용하고 전용 대기 스레드에서 Wait를 호출한다.

LibraryImport·Cdecl·16바이트 결과·Core IInspector 포트는 유지한다. 기존 동기 Run은 ABI 경계 회귀용으로 유지하되 어댑터는 Start/Wait를 사용한다. 어댑터가 관리 입력도 접수 시 복사하므로 작업 스케줄링 전 외부 수정의 영향을 받지 않는다.

UnmanagedCallersOnly 정적 콜백에 GCHandle 문맥을 전달한다. 작업 전체에 SafeHandle 참조를 추가 유지하고 Wait 성공과 토큰 등록 해제 후 GCHandle과 추가 참조를 반환한다. 진행 관찰자는 Native 작업 스레드에서 동기 호출되며 예외는 경계 안에서 잡아 정지를 요청하고 join 후 관리 오류로 전달한다. 관찰자에서 자신의 완료를 기다리면 안 되며, 자신의 Dispose 재진입은 즉시 거절한다. 정지 전달 이후 들어오는 진행 통지는 외부 관찰자에게 전달하지 않지만 이미 실행 중인 콜백은 반환을 기다린다.

Host는 Engine, NativeInspector 순서로 DisposeAsync를 기다린다. 어댑터를 먼저 종료하는 경합에도 새 접수를 닫고 현재 작업을 정지·join한 뒤 해제한다. 동기 Dispose는 같은 종료를 기다리는 호환 진입점이다. 미완료 작업을 가진 핸들을 최종화로 강제 해제하지 않는다.

## Alternatives

- 관리 버퍼를 전체 작업 동안 pin: 가능한 방식이지만 이 가상 장비에서는 작은 입력을 복사해 GC pin 기간과 소유권을 줄인다.
- Task 대기만 취소: Native 작업·콜백이 계속될 수 있어 채택하지 않는다.
- 진행 콜백을 Progress<T>로 비동기 전달: UI context가 없고 전달 큐의 종료가 Native Wait와 분리되므로 현재 동기 관찰자를 선택한다.

## Consequences

작업당 Native 작업 스레드와 관리 대기 스레드가 하나씩 생긴다. 현재 단일 실행 범위에는 적합하며 대량 병렬 처리 최적화는 별도 결정이다. 진행 관찰자가 멈추면 정상 Wait와 Dispose도 기다린다. 비정상 종료 프로토콜은 [ADR-0009](0009-termination-failure-quarantine.md)로 처리한다.

## Validation

실제 DLL에서 입력 복사, 순서 있는 진행, 반복 실행 격리, GC 중 문맥 유지, 취소 후 늦은 콜백 억제, 실행 중 Dispose, 콜백 예외·재진입, 조기 Destroy 거절을 명시적 신호로 검증한다. 기존 ABI·오류·결과·소유권 사례도 유지한다.

## Links

- Supersedes: [ADR-0005](0005-native-c-abi-adapter.md)
- [Native 계약](../native-interop.md)
- [M3b 작업](../../tasks/M03b-native-lifetime.md)
- [UnmanagedCallersOnly](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute)
- [SafeHandle 추가 참조 해제](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle.dangerousrelease)
