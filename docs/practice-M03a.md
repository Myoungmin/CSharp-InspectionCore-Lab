# M3a 실습 구현 기록

날짜: 2026-09-27. 기준 커밋: `7e4f269` — M2 Native와 지속 개발 구조를 검증·커밋한 뒤 시작했다.

## 구현

InspectionEngine이 단일 접수·Busy·상태 스냅샷·RunId 대상 취소·TimeProvider 타임아웃을 제공한다. Runner 내부 진입점이 접수 RunId와 공통 저장 경계를 사용한다. 기존 Runner 공개 호출과 M1 계약은 유지한다. Host는 Engine 완료를 기다리고 Native 어댑터보다 먼저 Engine을 종료한다.

아키텍처 판단은 [ADR-0006](adr/0006-single-run-engine.md), [ADR-0007](adr/0007-stop-and-completion-arbitration.md)에 남겼다. 동작 상세는 [Engine 계약](engine-contract.md), 검증 사례는 [대응표](verification-map.md)에 있다.

## 실제 확인

초기 Core 테스트는 44개(기존 19 + Engine 25)가 통과했고 실패·skip은 0이다. `artifacts/m03a-initial-tests/core.trx`에 기록했다. 동시 시작 24개 중 한 건만 접수, 제어 시간의 경계, 취소·저장 완료 양쪽 순서, 실제 작업·토큰 콜백 종료 대기, 늦은 타이머, 콜백 실패 시 재접수 거절을 확인했다.

Native 불량 콘솔 실행도 Engine을 거쳐 Succeeded/Fail/75와 JSON을 만들었다. `--timeout-ms 0`은 TimedOut/Prepare/Timeout으로 종료했다.

첫 전체 검증은 67개 테스트 통과 후 MSTest 언어별 사례 표시 공백 차이를 검출했다. 표시 구분 공백만 정규화해 두 환경의 TRX 모두 67개 필수 사례 대조에 통과했다. 사례 이름 교체, 데이터 인수 변경, 메서드 대소문자 변경을 넣은 복사본은 모두 거절했다. 증거는 `artifacts/m03a-required-cases/308024766bb444089cb9f5a29a178f29`에 있다. 이 검증 스크립트 사례는 MSTest 실행 수에 포함하지 않는다.

상태·취소 그림은 SVG 렌더링에 더해 PNG로 읽어 흐름과 글자 배치를 확인했다. 전체 verify.ps1도 `artifacts/verification/20260927T040034Z-7dcc7155/summary.json`에서 통과했다. Core 44개와 통합 23개 총 67개, 실패·skip 0이며 필수 사례 대조·두 검사기의 실제 결과 6건·즉시 Timeout 2건·오류·배포 DLL·문서도 통과했다. 기록 정리 후 최종 소스에 대한 동일 명령의 결과를 별도 verification 폴더에 보관한다.

접수·취소·저장·완료의 잠금과 Host의 해제 순서를 자체 리뷰했다. 별도 읽기 전용 리뷰 세션을 호출한 것은 아니다. 이번 커밋은 M2/DEV01 기준과 분리해 M3a 구현·테스트·문서를 포함한다.

## 학습 확인과 다음 단계

코드·테스트는 Codex가 작성·실행했다. 사용자의 직접 구현이나 이해 확인을 완료로 처리하지 않는다. 다음 학습 질문은 ‘취소가 접수돼도 왜 Busy가 유지되는가’, ‘Persisting 이후 왜 취소를 거절하는가’, ‘타임아웃과 Native 종료가 왜 다른가’다.

다음 단계는 M3b Native Start/RequestStop/Wait/Destroy와 Native 진행 콜백이다. 현재 토큰 콜백 검증은 Native 콜백 수명 검증을 대신하지 않는다. 자동 반복·IPC·자동 Codex 제어기는 후속 범위다.
