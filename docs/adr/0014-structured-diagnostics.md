# ADR-0014: 실행과 독립된 구조화 진단

- Date: 2026-09-28
- Status: Accepted

## Context

서로 다른 연결의 RequestId와 자동 실행의 여러 RunId를 연결하고 저장·Native·IPC 실패를 구분해야 한다. 로그 실패가 제품 판정이나 이미 끝난 저장을 실패로 바꾸면 진단이 실행 의미를 변경한다. Core가 로깅 패키지나 Native 오류 타입을 참조해서도 안 된다.

## Decision

Core는 IInspectionDiagnostics와 InspectionDiagnostic을 선언한다. Engine은 모든 수동/자동 실행에서 RunStarted·StageEntered·RunCompleted를 관찰자에게 전달한다. RunId·JobId·AutoId와 최종 스냅샷을 제공하고 IPC RequestId는 Core에 넣지 않는다. 관찰자는 Engine 잠금 밖에서 호출한다. 종료 원인·콜백/타이머 정리를 마친 후 최종 진단을 호출하며 그동안 접수 자리를 유지한다. 진단이 반환한 뒤 완료 스냅샷·Completion을 공개하므로 Host가 먼저 로그 자원을 해제하지 못한다. 관찰자 예외는 실행 결과를 바꾸지 않는다. 관찰자는 신속히 반환해야 하며 자기 실행의 Completion을 기다리지 않는다.

Host가 JSONL 로그를 생성·소유하고 Engine·Native·IPC 종료 뒤 닫는다. 시간, 프로세스/세션 ID, 세션 내 순서, 이벤트, RequestId/RunId/AutoId/ConnectionId/JobId, 단계/상태/종료 원인과 계산 결과를 구조화한다. 예외 사슬의 타입·메시지·HResult와 SQLite 기본/확장 코드, Native 작업·상태 이름/숫자를 기록한다. 입력 샘플은 기록하지 않는다. 예외 사슬은 각 원인당 16개, 메시지는 2,048자로 제한한다. 원격 DTO에는 내부 예외·파일 경로를 추가하지 않는다.

기본 로그는 artifacts/logs의 고유 JSONL 파일이고 `--log`로 지정하면 append한다. 한 프로세스의 쓰기를 직렬화하고 각 이벤트를 flush한다. 같은 파일을 여러 Host가 동시에 쓰는 것은 허용하지 않는다. 로그 열기 실패는 실행 전에 Setup 실패로 드러낸다. 실행 중 기록 실패는 stderr에 DiagnosticFailure를 한 번 알리고 해당 sink를 중지하되 실행 결과는 유지한다. 이는 감사 로그의 무손실 보장이 아니며 DB와 원자적으로 commit하지 않는다.

장애 주입은 Host 시작 옵션으로만 제공한다. `store`는 Save 포트에서 IOException, `native-inspect`/`native-wait`는 기존 실제 Native 오류 모드, `ipc-response`는 처음 Accepted 시작 응답만 기록 후 유실시킨다. 마지막 경우 실행은 계속되고 원래 ID의 재전송이 같은 접수를 반환한다. DB busy와 트랜잭션 실패는 통합 테스트에서 실제 연결 잠금·트리거로 재현한다.

## Alternatives

Console 문자열만으로는 상관관계·코드별 검색이 어렵다. 외부 로그 패키지와 비동기 큐는 이 단계에 필수적이지 않다. 큐를 추가하면 용량·유실·종료 flush 계약을 먼저 결정해야 한다. 진단 실패를 검사 실패로 처리하는 선택은 저장 성공과 모순되어 제외했다.

## Consequences

간단한 동기 로컬 파일 쓰기 비용이 실행과 요청에 포함된다. 파일 시스템이 멈추거나 관찰자가 반환하지 않는 상황의 강제 중단은 제공하지 않는다. 장기 실행에 필요한 회전·보존·집계는 [보류 목록](../backlog.md)에 남긴다. RunStarted가 IPC 접수 응답 로그보다 먼저 기록될 수 있으므로 표시 순서를 고정 가정하지 않고 ID로 연결한다. RequestHandled는 서버 처리 완료이며 클라이언트 수신 확인이 아니다.

## Validation

Core 대역으로 진단 예외 격리, 단계 순서, 자동 실행마다 새 RunId/같은 AutoId, 잠금 밖 호출, 완료·Dispose 대기를 검증한다. 실제 Host 로그로 SQLite·Native 오류 코드, 계산 결과, 유실 응답·재전송, 손상 IPC의 연결 ID를 확인한다. 실제 전체 증거는 [M5 작업](../../tasks/M05-storage-diagnostics.md)에 남긴다.

## Links

- [저장·진단 사용법](../storage-diagnostics.md)
- [SQLite 결정](0013-sqlite-results-and-query.md)
- [종료 실패의 기존 계약](0009-termination-failure-quarantine.md)
- [현재 구조](../architecture.md)
