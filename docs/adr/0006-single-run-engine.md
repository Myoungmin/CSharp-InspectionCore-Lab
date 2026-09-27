# ADR-0006: Engine이 실행 자리 하나와 접수·조회 API를 관리한다

- Date: 2026-09-27
- Status: Superseded
- Superseded by: [ADR-0010](0010-sequential-auto-admission.md). 아래는 M3a 당시 접수 결정이며 M3c에서 자동 대기 예약으로 확장한다.

## Context

M3a에서 여러 시작 요청이 같은 장비를 동시에 사용하지 못하게 해야 한다. M2 Runner는 한 건의 순서만 관리하며 실행 접수와 상태 조회는 없다.

## Decision

Core의 InspectionEngine.Start는 잠금 안에서 실행 자리 하나를 확보한 요청에만 Accepted와 RunId·완료 Task를 반환한다. 실행 중 요청은 Busy, 종료 중·해제 후·엔진 장애 상태의 요청은 Unavailable이다. 큐는 없다. Host는 장비·검사기·저장소와 Engine을 조립하고 Engine 종료를 기다린 후 자신이 소유한 어댑터를 해제한다.

Runner는 내부 진입점으로 접수 RunId와 단계·저장 경계 통지를 받으며 기존 공개 RunAsync도 유지한다. Engine은 ThreadPool에서 Runner를 실행해 M2의 동기 Native 호출이 접수 API 반환을 막지 않도록 한다. 이것은 Native의 비동기 API나 강제 취소를 구현한 것이 아니다.

조회는 현재 실행과 직전 완료 실행만 보관한다. 반환 스냅샷은 이후 상태 변경으로 바뀌지 않는다. 오래된 실행의 완료 결과는 접수 핸들의 Task로 보존하고 전체 이력 검색은 저장소·후속 단계에서 다룬다. Ready는 접수 기능이 열린 수명 상태이며 별도의 IsBusy가 실행 자리 사용 여부를 나타낸다.

## Alternatives

- Runner 안에 중복 시작·이력·종료까지 통합: 한 건의 순서와 접수 수명 책임이 섞인다.
- 시작 요청을 큐에 넣기: 취소 대상·대기 상태·예약 수명 계약을 추가해야 하므로 이번 범위에서 제외한다.
- 모든 실행을 메모리에 보관: 초기 조회 요구보다 큰 수명과 메모리 관리가 필요하다.

## Consequences

Core 계약으로 접수·상태·취소를 테스트하고 이후 IPC와 자동 반복에서 같은 Engine을 사용할 수 있다. 동기 Native 작업이 실제로 돌아오지 않으면 Busy·종료 대기도 유지된다. 엔진의 Task 대기를 취소해도 실제 Native 종료로 취급하지 않는다.

## Validation

동시 시작 한 건 접수, Busy 시 장비 미호출, 완료 후 재접수, 이전 RunId·타이머의 다음 실행 간섭 차단, 상태 스냅샷을 신호 기반 테스트로 검증한다. 실제 결과는 M3a 작업·실습 기록에 남긴다.

## Links

- [M3a 작업](../../tasks/M03a-single-run-engine.md)
- [Core 포트와 조립](0001-core-ports-and-composition.md)
- [M2 Native 동기 계약](0005-native-c-abi-adapter.md)
- [종료 원인과 완료](0007-stop-and-completion-arbitration.md)
