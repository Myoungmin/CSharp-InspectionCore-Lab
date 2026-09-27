# ADR-0004: 초기 결과는 RunId별 JSON 파일로 저장한다

- Date: 2026-09-27
- Status: Accepted

## Context

첫 실행에서는 DB 설치 없이 결과가 실제로 저장됐는지 확인해야 한다. 조회·검색은 후속 M5 범위다. 기존 M1 저장 방식과 그 보장 범위를 이번에 기록한다.

## Decision

IResultStore의 첫 구현은 JsonResultStore다. 같은 폴더의 고유 임시 파일에 JSON을 쓰고 스트림을 닫은 후 RunId를 이름으로 하는 결과 파일로 이동한다. 기존 같은 RunId 파일을 덮어쓰지 않는다. 저장 실패 시 임시 파일 정리를 시도하고 정리 오류가 원래 실패를 가리지 않게 한다.

## Alternatives

- 처음부터 SQLite 도입: 조회에는 유리하지만 첫 검사 실행에 스키마·DB 수명·마이그레이션까지 필요해진다. M5에서 검토한다.
- 결과 경로에 직접 덮어쓰기: 불완전 파일이나 같은 실행 ID의 덮어쓰기를 완료 결과와 구분하기 어렵다.

## Consequences

결과를 직접 열어 확인하기 쉽고 저장 실패를 분리할 수 있다. 여러 결과의 검색·트랜잭션·재시작 복구는 제공하지 않는다. 비정상 종료로 남은 .tmp는 완료 결과가 아니며 전원 장애까지의 디스크 영속성은 보장하지 않는다. artifacts의 실행 결과는 Git에 넣지 않는다.

## Validation

verify.ps1에서 실제 JSON 내용과 서로 다른 RunId를 검사한다. M2 파일 통합 테스트는 JSON 게시, 중복 RunId 덮어쓰기 차단·임시 파일 정리, 저장소 직접 호출의 사전 취소를 확인한다. Core 단위 테스트는 실제 파일에 의존하지 않는다.

## Links

- [저장 완료 결정](0003-persistence-completion-boundary.md)
- [현재 파일 저장 계약](../architecture.md)
- [M2 작업과 검증 정책](../../tasks/M02-native-inspector.md)
- [실제 파일 검증 증거](../practice-M02.md)
