# M5 구현과 검증 기록

기준 커밋: c911ff6. [작업](../tasks/M05-storage-diagnostics.md), [저장·진단 사용법](storage-diagnostics.md), [ADR-0013](adr/0013-sqlite-results-and-query.md), [ADR-0014](adr/0014-structured-diagnostics.md)를 함께 읽는다.

## 구현

Host가 JSON/SQLite 저장소를 선택하고 Core의 별도 조회 포트를 사용한다. SQLite는 RunId를 기본 키로 사용하고 commit 이후 저장 성공을 반환한다. 기간·판정·JobId 검색과 시간/RunId 커서 정렬을 제공한다. 프로세스 재시작 이후 DB 결과는 조회할 수 있지만 Engine 상태와 RequestId 재전송 기록은 복구하지 않는다.

Engine은 진단 sink를 빌려 시작·단계·최종 스냅샷을 잠금 밖에서 관찰시킨다. 최종 진단 호출이 끝난 뒤 완료를 게시한다. Host의 JSONL에는 SessionId·RequestId·AutoId·RunId·ConnectionId와 예외 원인/코드가 남는다. 저장·Native·IPC 장애 주입으로 계산 결과 보존, 종료 미확인, 시작 응답 유실을 구분한다.

## 실제 검증

전체 verify `20260927T224034Z-ea3813fe`에서 Core 94 + 통합 87 = 181개가 통과했고 실패·skip은 0이다. 통합은 기존 Native·JSON·IPC 63개를 유지하면서 SQLite 11개와 저장/진단 프로세스 13개를 추가했다. 실제 Host 재시작·검색, 중복 키·rollback·잠금, 저장 실패의 계산 결과, Native Inspect/Wait 오류, 시작 응답 유실과 손상 연결 로그를 확인했다. 필수 사례의 정확한 클래스/메서드/데이터 이름, ADR 14건·그림 10개·링크와 기존 CLI 검증도 통과했다. 소스 해시·상세 증거와 최종 문서 재검증 절차는 작업 기록에 남겼다.

자체 리뷰에서 진단 완료 전 최종 상태 노출, 저장 행 손상과 요청 오류의 혼동, 자동 실행 로그의 AutoId 연결을 보완했다. 명시적 신호와 실제 DB·프로세스로 확인했으며 별도 리뷰 호출은 하지 않았다.

## 직접 확인할 학습 항목

- SQLite commit과 Engine Succeeded 사이의 순서, 저장 실패의 ComputedResult를 설명한다.
- 검사 완료 시각과 저장 완료 시각의 차이를 설명하고 기간 검색 경계를 판단한다.
- 동일 시각의 결과를 RunId로 정렬할 때 커서 페이지가 중복되지 않는 이유를 설명한다.
- DB 결과는 보존되지만 같은 RequestId의 재시작 후 중복 실행은 막지 못하는 이유를 설명한다.
- JSONL에서 한 RequestId의 접수와 RunId의 단계/종료, AutoId의 반복 실행을 추적한다.
- 진단 예외가 결과를 바꾸지 않아도 관찰자의 무한 대기가 종료를 막을 수 있음을 설명한다.

구현·검증은 Codex가 수행했으며 사용자의 직접 설명·수정·재현은 아직 확인하지 않았다. 다음 구현 단계는 M6다. 장기 운영의 로그 회전·DB 백업·스키마 이전은 [B08](backlog.md)에서 재검토한다.
