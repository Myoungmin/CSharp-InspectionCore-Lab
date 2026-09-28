# 보류 사항과 재검토 기준

단계의 전체 순서는 [로드맵](../InspectionLab-Architecture-and-Codex-Loop.md)에 있다. 여기에는 아직 구체화하지 않은 결정과 개발 운영의 후속 일만 기록한다. 항목은 ID·현재 영향·보류 이유·재검토 조건·완료 증거를 가진다. 착수하면 작업 기록에 연결하고, 결정되면 ADR을 연결한다. 완료 항목도 근거를 남긴다.

| ID | 상태·내용 | 영향과 보류 이유 | 재검토 조건 | 완료 증거 |
| --- | --- | --- | --- | --- |
| B01 | Planned — .NET 10 이전 | 현재 도구를 유지하기로 한 [ADR-0002](adr/0002-windows-x64-toolchain.md) 적용 | 로드맵의 지원 종료 전 이전 일정에 맞춰 별도 착수 | 새 ADR, IDE·SDK·C++/CLI 호환성 및 C4679 제외 재검토, 전체 검증 |
| B02 | Verified — Native 비동기 종료 | M3b에서 Start/Stop/Wait/Destroy와 종료 장애 격리 구현 | M3b 전체 검증 통과; 후속 Native 수명 변경 시 재검토 | [M3b 작업](../tasks/M03b-native-lifetime.md), [ADR-0008](adr/0008-native-async-lifetime.md), [ADR-0009](adr/0009-termination-failure-quarantine.md) |
| B03 | Deferred — Codex 반복 제어기 | 현재는 검증 스크립트와 수동 개발 흐름이며 제어기는 없음 | Native·M3 검증 흐름 안정 후 별도 요청 | 예산·동일 실패 중단, 별도 리뷰, 보호된 기준 검증, JSON 보고서 |
| B04 | Deferred — CI와 증거 보관 | 검증 로그는 현재 로컬 artifacts에만 있음 | 원격 CI 구성 작업 시 | 고정 MSVC·SDK·Java·PlantUML 준비, 새 checkout 검증, TRX·요약·로그 보관 |
| B05 | Deferred — 편집·스타일 규칙 | 개행은 gitattributes로 고정했으나 editorconfig는 없음 | 기능 변경과 분리한 정리 작업 시 | 기존 스타일을 반영한 최소 설정과 필요한 검사 |
| B06 | Verified — 장애 대응 기록 | Native Stop/Wait·콜백 경합과 M5 저장·IPC 진단 재현 | 저장·Native·IPC 장애 분류 변경 또는 운영 중 새 장애 발생 시 | [장애 대응](troubleshooting.md), [M5 작업](../tasks/M05-storage-diagnostics.md), [ADR-0014](adr/0014-structured-diagnostics.md) |
| B07 | Verified — IPC 호환성과 재전송 범위 | M4 버전 1·시작 재전송·연결 독립 수명 구현·검증 | 버전/이벤트 push/영속 중복 방지 변경 시 재검토 | [M4 작업](../tasks/M04-named-pipe-ipc.md), [ADR-0011](adr/0011-named-pipe-protocol.md), [ADR-0013](adr/0013-sqlite-results-and-query.md) |
| B08 | Deferred — 데이터·로그 장기 운영 | M5는 결과 DB와 로컬 JSONL만 제공하며 자동 보존 기간·회전·백업·스키마 이전은 없음 | 장시간 자동 운영 배포, DB 스키마 변경 또는 디스크 한계 도달 전 | 새 ADR, 백업/복원·보존 정책·이전 실패 복구 검증 |

매 작업 시작·종료 시 관련 항목만 재검토한다. 일정 도래, 계약 변경, 실제 장애 등 재검토 조건을 구체적으로 적고 단순히 '나중에'로 두지 않는다. 사용자 학습 미확인은 작업·실습 기록에서 관리한다.
