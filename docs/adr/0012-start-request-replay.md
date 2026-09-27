# ADR-0012: 시작 요청 재전송과 실행 수명

- Date: 2026-09-27
- Status: Accepted

## Context

시작을 접수한 뒤 응답 전송이 실패하면 Client는 접수 여부를 알 수 없다. 새 RequestId로 무조건 다시 시작하면 같은 Job이 중복 실행될 수 있다. 연결 취소를 검사 토큰으로 전달하면 조회 창을 닫는 행동이 제품 검사를 취소하게 된다.

## Decision

Host의 단일 dispatcher 잠금에서 StartJob/StartAuto의 RequestId 조회·Engine 접수·응답 기록을 마친 뒤 연결에 쓴다. 같은 ID와 같은 typed payload는 Accepted/Busy/Unavailable을 포함해 **처음 접수 응답**을 반환한다. 같은 ID의 다른 내용/명령은 RequestIdConflict다. JSON 속성 순서·공백·선택 필드 생략은 DTO 재직렬화로 정규화하며 명령명과 SHA-256 지문을 기록한다. 검증되지 않은 요청은 접수 기록을 만들지 않는다.

기록은 Host 프로세스 수명 동안 보존하며 4,096개를 퇴출 없이 제한한다. 가득 차면 새 시작만 ReplayCapacityExceeded로 거절한다. 기존 ID 재전송과 조회·CancelRun·StopAuto는 계속 가능하다. 재시작 후 같은 ID의 실행 방지는 보장하지 않는다. GetStatus/GetResult는 최신 상태를 읽고 CancelRun/StopAuto는 현재 Engine의 멱등 제어 응답을 반환하므로 이 네 명령의 응답은 캐시하지 않는다. 시작에 사용한 ID는 다른 명령으로 재사용할 수 없다. Client는 논리적으로 새 요청이면 새 ID, 시작 재시도이면 원래 ID를 사용한다.

읽기/쓰기 취소 토큰은 연결에만 적용하고 접수한 실행에는 전달하지 않는다. 검사 취소는 CancelRun 또는 Host 종료, 검사 자체 timeout만 결정한다. 시작 응답은 Completion을 기다리지 않는다. 자동 접수도 기존 Engine 경로를 사용한다.

Host는 접수한 수동 실행·자동 세션의 Completion handle을 보존해 이전 상태 조회를 제공한다. 자동 세션 내부의 과거 개별 실행 상태는 현재/마지막 Core 조회 범위까지다. GetResult는 실제 JSON 게시 파일을 읽어 과거 성공 결과를 반환한다. 파일이 없으면 ResultNotFound이며 진행 중·취소·실패 여부는 상태 조회로 구분한다. 저장 실패의 ComputedResult는 상태 진단에만 들어간다.

## Alternatives

연결별 기록은 재접속을 처리하지 못한다. 오래된 ID를 자동 퇴출하면 프로세스 수명 동안 재전송 보장과 충돌한다. 모든 조회 응답 캐싱은 상태를 낡게 만들고 폴링만으로 용량을 소모하므로 배제했다. 영속 idempotency 저장은 M5의 SQLite 결정과 별도로 필요성을 판단한다.

## Consequences

Client는 접수와 성공을 구분하고 RunId/AutoId를 보관해야 한다. Busy 응답의 재전송은 계속 Busy이므로 새 시도에는 새 RequestId가 필요하다. 제한에 도달하면 실행·저장 종료를 확인하고 운영자가 Host를 재시작해야 한다. 시작 ID 및 수동/세션 완료 기록의 보존량은 유한하다. 자동 개별 실행의 완전한 이력·검색은 M5의 후속 범위다.

## Validation

동시 동일 ID, 응답 유실·재접속, 완료 후 재전송, 의미가 같은 JSON, 내용 충돌, Busy 재전송, 용량 소진 후 원래 ID 재전송·조회·취소를 실제 Host에서 검증한다. 실제 전체 검증은 [작업 기록](../../tasks/M04-named-pipe-ipc.md)에 남긴다.

## Links

- [IPC 계약](../ipc-contract.md)
- [전송 결정](0011-named-pipe-protocol.md)
- [자동 실행 결정](0010-sequential-auto-admission.md)
- [현재 구조](../architecture.md)
