# 작업 목록과 완료 기준

새 작업은 [템플릿](template.md)으로 작성한다. 한 번에 요청된 작업 또는 마일스톤 하나를 수행하고 시작·종료 시 이 목록을 갱신한다. 기존 기록은 당시 형식을 유지하며 상태와 증거를 이 목록에서 연결한다.

| 작업 | 구현 상태 | 증거 또는 다음 행동 |
| --- | --- | --- |
| [M01 — 기반과 한 건 실행](M01-run-one-job.md) | Verified | [M1 실습](../docs/practice-M01.md), 기준 커밋 9f0a24c |
| [M02 — Native 검사기](M02-native-inspector.md) | Verified | [M2 실습](../docs/practice-M02.md) |
| [DEV01 — 지속 개발 구조](DEV01-development-workflow.md) | Verified | 필수 42개 사례와 전체 검증 통과, M2와 함께 7e4f269 커밋 |
| [M3a — 단일 실행 제어](M03a-single-run-engine.md) | Verified | [M3a 실습](../docs/practice-M03a.md), 총 67개 필수 사례와 전체 검증 통과 |
| [M3b — Native 비동기 종료](M03b-native-lifetime.md) | Verified | [M3b 실습](../docs/practice-M03b.md), 총 87개 필수 사례와 전체 검증 통과 |
| [M3c — 순차 자동 반복](M03c-auto-sequence.md) | Verified | [M3c 실습](../docs/practice-M03c.md), 총 120개 필수 사례·자동 반복 JSON·전체 검증 통과 |
| [M4 — Named Pipe IPC](M04-named-pipe-ipc.md) | Verified | [M4 실습](../docs/practice-M04.md), 총 146개 필수 사례·프로세스 IPC·전체 검증 통과 |
| [M5 — 저장·진단](M05-storage-diagnostics.md) | Verified | [M5 실습](../docs/practice-M05.md), 필수 181개·실제 SQLite 재시작/검색·장애 진단·전체 검증 통과 |
| [M6 — C++/CLI 비교](M06-cpp-cli-comparison.md) | Verified | [M6 실습](../docs/practice-M06.md), 필수 217개·실제 혼합 DLL/IPC/저장·종료·누락/publish·전체 검증 통과 |

## 공통 완료 기준

각 작업은 인수 조건 확인, `scripts/verify.ps1` 통과, 관련 ADR·계약·다이어그램·검증 대응표 갱신, 최종 소스 식별 정보와 리뷰 결과 기록까지 마쳐야 Verified다. 필수 검증의 skip·누락은 통과가 아니다. 정책 변경은 이유를 작업 기록에 남기고 기존 보장 범위를 검토한다.

검증 결과와 커밋 상태는 별도로 기록한다. 실제 로그는 artifacts에, 읽을 수 있는 요약은 작업·실습 문서에 둔다. 별도 리뷰 호출을 하지 않았다면 자체 리뷰로 기록한다. 구현 Verified는 사용자의 학습 완료를 뜻하지 않는다.
