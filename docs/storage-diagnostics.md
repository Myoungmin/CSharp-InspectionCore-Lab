# M5 저장·검색과 진단

[ADR-0013](adr/0013-sqlite-results-and-query.md)은 SQLite와 조회, [ADR-0014](adr/0014-structured-diagnostics.md)는 로그 수명과 장애 주입을 설명한다. 기존 [IPC 계약](ipc-contract.md)의 접수·취소·재전송 규칙을 유지한다.

## 저장 선택과 재시작

```powershell
dotnet run --project src/Inspection.Host -c Release -- --store sqlite --scenario fail --repeat 3 --interval-ms 0 --output artifacts/sqlite-demo --log artifacts/logs/demo.jsonl
dotnet run --project src/Inspection.Host -c Release -- --serve --store sqlite --pipe InspectionLab --output artifacts/sqlite-demo --log artifacts/logs/server.jsonl
```

기본값은 json이며 M1~M4 명령이 유지된다. SQLite 결과는 `<output>/inspection.db`, JSON은 `<output>/<RunId>.json`이다. 하나의 실행은 선택한 저장소 한 곳에 저장한다. 같은 output의 SQLite 서버를 다시 시작하면 GetResult와 SearchResults가 과거 성공 결과를 읽는다. 실행 실패/취소 상태·자동 예약·RequestId 캐시는 DB에 저장하지 않는다. 이전 RunId의 상태 조회는 RunNotFound여도 GetResult는 성공할 수 있다. 재시작 뒤 같은 시작 RequestId를 보내면 새 실행이 생길 수 있다.

DB는 application_id=0x494E5350, user_version=1을 사용한다. 검사 완료 UTC tick과 RunId를 인덱싱한다. 점수·판정은 저장된 샘플/불량 수로 복원한다. 중복 RunId INSERT는 오류이고 기존 행은 보존한다. 저장 트랜잭션 commit이 성공해야 실행이 Succeeded다. 제품 Fail은 저장에 성공하면 Succeeded다. 미커밋 행은 다른 연결의 조회에 보이지 않으며 rollback된 행은 결과가 아니다.

WAL·synchronous=FULL을 사용하지만 전원 장애나 파일 시스템의 보장을 별도로 인증한 것은 아니다. 실행 중 DB 파일만 임의 복사하지 않는다. 열려 있는 DB는 WAL/SHM과 함께 동작하므로 백업은 별도 작업으로 다룬다. 알 수 없는 스키마는 자동 삭제·다운그레이드하지 않는다. 스키마 변경은 별도 마이그레이션이 필요하다.

공급자의 동기 SQLite 호출은 작업 스레드에서 실행한다. 연결마다 busy 제한은 2초이며 대기 후에도 잠기면 SqliteException이다. 여러 호출 전체의 총 2초 deadline을 뜻하지 않는다. 직접 호출의 취소는 작업 전·행 읽기·commit 직전에서 확인하고 진행 중 호출의 즉시 중단은 보장하지 않는다. Engine 저장 진입 후에는 취소 토큰을 전달하지 않는다.

## 검색 요청

Client의 요청 파일에 다음 envelope를 작성한다. 새 요청마다 새 RequestId를 사용한다.

```json
{
  "ProtocolVersion": 1,
  "RequestId": "28dc4804-b0ad-4140-9d80-2e16973b0b72",
  "MessageType": "SearchResults",
  "Payload": {
    "FromUtc": "2026-09-28T00:00:00Z",
    "ToUtc": "2026-09-29T00:00:00Z",
    "Verdict": "Fail",
    "JobId": "demo-fail",
    "Limit": 10
  }
}
```

FromUtc/ToUtc/Verdict/JobId/Cursor는 생략할 수 있다. 시간은 **InspectedAtUtc** 기준으로 시작 이상·끝 미만이다. 시간대 오프셋은 UTC로 정규화한다. JobId는 대소문자를 구분하는 정확한 값, Verdict는 Pass/Fail이다. Limit은 1~25, 기본 25다. 유효하지 않은 범위·판정·커서는 InvalidRequest다. SQL 문장에 값을 이어 붙이지 않는다.

응답 Payload는 `Items: ResultDto[]`와 `NextCursor`다. 검사 완료 시각 내림차순, 동률이면 RunId의 N 형식 문자열 내림차순이다. NextCursor가 있으면 같은 필터와 함께 다음 요청의 Cursor로 넣는다. 없으면 현재 검색의 마지막 페이지다. 커서는 InspectedAtUtc와 RunId를 가진다. 페이지 사이에는 DB의 고정 스냅샷을 유지하지 않으므로 새 결과가 추가되면 첫 페이지를 다시 읽어 최신 결과를 확인한다.

SearchResults는 버전 1의 추가 명령이다. 이전 서버는 InvalidRequest, 현재 서버의 JSON 모드는 SearchNotSupported를 반환한다. GetResult는 두 저장소 모두 지원한다. 결과 부재는 ResultNotFound, 파일/DB 읽기·변환 오류는 StoreReadFailed이며 상세 원인은 서버 로그에만 남긴다.

## 로그 읽기

기본 경로는 `artifacts/logs/<guid>.jsonl`, `--log`는 지정 파일 append다. 같은 파일은 동시 Host가 공유하지 않는다. SessionId와 ProcessId는 프로세스 세션을 구분하고 Sequence는 그 세션의 기록 순서다. UTC 시간과 Event, RequestId/RunId/AutoId/ConnectionId/JobId가 최상위 필드다. Data에는 단계·상태·종료 원인·계산 결과 등이, Errors에는 원인 사슬과 SQLite/Native 코드가 있다.

```powershell
$events = Get-Content -Encoding UTF8 artifacts/logs/server.jsonl | ForEach-Object { $_ | ConvertFrom-Json }
$events | Where-Object { $_.Event -eq 'RunCompleted' } | Select-Object Utc,RunId,AutoId,Data,Errors
$events | Where-Object { $_.RequestId -eq '조회할 RequestId' }
```

수동 시작은 RequestHandled의 RunId, 자동 시작은 AutoId로 이후 RunStarted/StageEntered/RunCompleted를 연결한다. 모든 자동 실행은 같은 AutoId와 서로 다른 RunId를 기록한다. 단계 진입은 단계 성공을 뜻하지 않는다. 최종 RunCompleted의 State/StopReason/TerminationConfirmed로 결과를 판단한다. RequestHandled 역시 Client 수신 확인은 아니다. Native Wait 실패의 TerminationConfirmed=false는 프로세스 종료 전 실제 작업 종료를 확인하지 못했다는 뜻이다.

로그 열기 실패는 Setup 실패이고 실행을 시작하지 않는다. 실행 중 쓰기 실패는 stderr의 DiagnosticFailure로 표시하고 검사 결과는 유지한다. Core의 진단 관찰자는 잠금 밖에서 호출하며 완료 공개 전에 반환해야 한다. 관찰자가 자기 Completion을 기다리면 안 된다. Host가 Engine 종료를 기다린 뒤 로그를 닫는다. 현재는 회전·무손실 큐·집중 보관이 없으므로 장기 실행 보존 정책은 후속이다.

## 장애 재현

| 옵션/입력 | 관찰할 결과 |
| --- | --- |
| `--fault store` | Persist의 Faulted, ComputedResult 보존, 결과 미저장, IOException 원인 |
| `--inspector native --fault native-inspect` | Inspect의 Faulted, NativeOperation=Inspect, InternalError, 종료 확인 true |
| `--inspector native --fault native-wait` | Faulted 엔진·신규 접수 거절, NativeOperation=Wait, 종료 확인 false |
| 서버의 `--fault ipc-response` | 첫 Accepted 시작 응답만 유실, IpcResponseDropped와 RequestId 기록; 같은 ID로 재전송하면 같은 실행 |
| 잘못된 프레임 | 해당 연결 종료, IpcTransportFailed 또는 IpcEnvelopeRejected와 ConnectionId |
| 실제 SQLite write lock | busy 제한 후 Faulted/Persist, SqliteErrorCode=5, 계산 결과 보존 |

fault 기본값은 none이다. Native fault에는 native 검사기가 필요하며 ipc-response는 서버 모드에서만 허용한다. 옵션은 실습용 Host 설정이고 IPC 요청이 장애 모드를 바꾸지는 못한다. 실제 트랜잭션/잠금 장애는 통합 테스트가 연결·트리거로 재현하며 임의 지연을 성공 판정 신호로 사용하지 않는다.

![Storage and diagnostics](diagrams/generated/storage-diagnostics.svg)
