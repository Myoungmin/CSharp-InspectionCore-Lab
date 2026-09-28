# ADR-0013: SQLite 결과 저장과 독립 조회 포트

- Date: 2026-09-28
- Status: Accepted

## Context

M4의 JSON 파일은 한 건 조회에 충분하지만 기간·판정 검색과 재시작 후 결과 탐색에 인덱스가 필요하다. [ADR-0012](0012-start-request-replay.md)의 결과 조회를 선택한 저장소로 확장하므로 이 기록이 0012를 대체한다. 최초 JSON 저장의 이유와 동작은 [ADR-0004](0004-json-result-storage.md)에 유지한다.

## Decision

Host의 `--store json|sqlite`로 선택하고 기존 기본값 json을 유지한다. SQLite는 `--output` 폴더의 inspection.db를 사용한다. Infrastructure만 Microsoft.Data.Sqlite 9.0.20을 직접 참조하며 전이 패키지는 잠금 파일로 고정한다. Core에는 IResultReader/LoadAsync, IResultSearch/SearchAsync와 불변 조회 조건·커서를 선언하고 Host가 사용한다. Runner의 IResultStore/SaveAsync 포트는 변경하지 않는다. JSON은 한 건 조회만, SQLite는 한 건 조회와 검색을 구현한다.

SQLite 스키마 1과 application_id를 검증하고 빈 DB만 초기화한다. 알 수 없는 스키마·다른 앱 DB는 변경하지 않고 실패한다. RunId는 기본 키이며 INSERT만 수행해 덮어쓰기를 거절한다. UTC tick, JobId, 샘플/불량 수를 저장하고 점수·판정은 Core 규칙으로 복원한다. 스키마 생성과 한 건 저장은 각각 트랜잭션으로 처리한다. 성공은 commit 이후이며 이후 취소 검사로 성공을 뒤집지 않는다. WAL과 synchronous=FULL, 연결별 2초 busy 제한, 작업별 연결과 Pooling=false를 사용한다.

SQLite 공급자는 비동기 I/O를 제공하지 않으므로 동기 작업을 Task.Run 안에서 수행한다. 취소는 작업 전·행 처리·commit 직전에 확인하며 이미 진행 중인 SQLite 호출을 즉시 중단한다고 주장하지 않는다. Engine이 Persisting에 들어가면 기존처럼 CancellationToken.None으로 저장한다.

검색은 검사 완료 시각(InspectedAtUtc)의 FromUtc 이상, ToUtc 미만, 정확한 JobId와 Pass/Fail을 조합한다. SQL 값은 매개변수로 바인딩한다. 검사 시각 내림차순·RunId N 형식의 binary 내림차순을 사용하고 마지막 키를 다음 커서로 반환한다. 최대 25건과 한 건 lookahead로 다음 페이지를 판단한다. 페이지 사이 고정 스냅샷은 제공하지 않는다.

IPC 버전 1에 SearchResults 명령과 SearchNotSupported 오류를 추가한다. 기존 envelope·명령·결과 DTO 필드는 유지하므로 기존 클라이언트는 계속 동작한다. 이전 서버는 새 명령을 InvalidRequest로 거절한다. JSON 모드의 SearchResults는 SearchNotSupported다. GetResult는 선택한 저장소를 읽는다. DB에 없는 결과는 ResultNotFound, 읽기·변환 실패는 StoreReadFailed이며 잘못된 검색 입력은 InvalidRequest로 구분한다.

0012의 접수 정책을 그대로 다시 채택한다: StartJob/StartAuto만 typed payload 지문과 응답을 잠금 안에서 기록하고 쓰기 전에 확정한다. 같은 ID는 처음 Accepted/Busy/Unavailable을 반환하며 다른 내용은 충돌이다. 4,096개를 퇴출 없이 프로세스 수명 동안 보존하고 새 시작만 용량 제한으로 거절한다. 조회·제어 응답은 캐시하지 않는다. 연결 단절은 실행을 취소하지 않고 재시작 후 재전송 방지는 보장하지 않는다. 수동 실행/자동 세션의 메모리 상태 보존도 유지한다. DB는 저장된 검사 결과만 보존하며 실패 실행·엔진 상태·RequestId는 저장하지 않는다.

## Alternatives

JSON 전체 파일 검색은 인덱스·정렬·페이지 확장에 불리하다. EF Core는 이 작은 고정 스키마에 추가 모델/마이그레이션 체계를 요구해 제외했다. OFFSET 페이지는 이전 행 삽입에 민감하므로 키 기반 커서를 선택했다. JSON을 동시에 이중 저장하면 한쪽 저장 실패의 성공 계약이 복잡해지므로 저장소 하나만 선택한다.

## Consequences

SQLite 런타임 DLL을 패키지로 배포해야 한다. DB/WAL/SHM 파일은 실행 산출물이며 버전 관리하지 않는다. 스키마 변경은 별도 ADR·마이그레이션 작업이며 자동 재생성하지 않는다. DB commit과 진단 로그는 하나의 트랜잭션이 아니다. 재시작 시 완료 결과만 복구하고 자동 예약·실행 상태·RequestId 캐시는 복구하지 않는다.

## Validation

실제 DB에서 재연결·Host 재시작, 중복 키, SQL 입력, 동률 커서, 트리거 롤백, write lock, 미커밋 행, 스키마 거절을 확인한다. 프로세스 시험으로 두 검사기의 SQLite 자동 실행·검색·오류 응답을 검사한다. 기존 JSON 기준도 유지하며 전체 실행 증거는 [작업 기록](../../tasks/M05-storage-diagnostics.md)에 남긴다.

## Links

- [저장·진단 계약](../storage-diagnostics.md)
- [IPC 계약](../ipc-contract.md)
- [현재 구조](../architecture.md)
- [공식 SQLite 비동기 제한](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)
- [공식 SQLite 트랜잭션](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)
- [고정 공급자 패키지](https://www.nuget.org/packages/Microsoft.Data.Sqlite/9.0.20)
