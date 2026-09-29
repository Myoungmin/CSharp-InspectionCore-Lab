# 보류 사항과 재검토 기준

단계의 전체 순서는 [로드맵](../InspectionLab-Architecture-and-Codex-Loop.md)에 있다. 여기에는 아직 구체화하지 않은 결정과 개발 운영의 후속 일만 기록한다. 항목은 ID·현재 영향·보류 이유·재검토 조건·완료 증거를 가진다. 착수하면 작업 기록에 연결하고, 결정되면 ADR을 연결한다. 완료 항목도 근거를 남긴다.

| ID | 상태·내용 | 영향과 보류 이유 | 재검토 조건 | 완료 증거 |
| --- | --- | --- | --- | --- |
| B01 | Deferred — .NET 10 이전 | 2026-09-29 사용자 요청으로 필요 시까지 보류; [ADR-0002](adr/0002-windows-x64-toolchain.md)의 현재 도구 유지 | 새 SDK 기능·패키지·IDE 호환성 요구, 실제 배포의 지원 정책 검토 또는 사용자 전환 요청 시 별도 착수 | 새 ADR, IDE·SDK·C++/CLI 호환성 및 C4679 제외 재검토, 전체 검증 |
| B02 | Verified — Native 비동기 종료 | M3b에서 Start/Stop/Wait/Destroy와 종료 장애 격리 구현 | M3b 전체 검증 통과; 후속 Native 수명 변경 시 재검토 | [M3b 작업](../tasks/M03b-native-lifetime.md), [ADR-0008](adr/0008-native-async-lifetime.md), [ADR-0009](adr/0009-termination-failure-quarantine.md) |
| B03 | Deferred — Codex 반복 제어기 | [구성 검토](development-operations-review.md) 완료; 후보 밖 검증 기준·권한 분리가 선행돼야 함 | 자동 구현 작업을 별도 요청할 때; M6까지 검증 기반은 확보됨 | 보호 기준 변경 차단, 3회/30분/동일 실패 2회 중지, 새 읽기 전용 리뷰, fixture와 실제 한 작업 검증 |
| B04 | InProgress — CI와 증거 보관 | [DEV03](../tasks/DEV03-windows-ci.md)에서 Windows workflow·고정 도구 검사·artifact 구현 | 로컬 전체 검증 후 원격 성공·의도한 실패·artifact 확인 | [ADR-0017](adr/0017-windows-ci-and-verification-evidence.md), [CI 실행](continuous-integration.md); 실제 증거는 작업 기록 |
| B05 | Deferred — 편집·스타일 규칙 | 개행은 gitattributes로 고정했으나 editorconfig는 없음 | 기능 변경과 분리한 정리 작업 시 | 기존 스타일을 반영한 최소 설정과 필요한 검사 |
| B06 | Verified — 장애 대응 기록 | Native Stop/Wait·콜백 경합과 M5 저장·IPC 진단 재현 | 저장·Native·IPC 장애 분류 변경 또는 운영 중 새 장애 발생 시 | [장애 대응](troubleshooting.md), [M5 작업](../tasks/M05-storage-diagnostics.md), [ADR-0014](adr/0014-structured-diagnostics.md) |
| B07 | Verified — IPC 호환성과 재전송 범위 | M4 버전 1·시작 재전송·연결 독립 수명 구현·검증 | 버전/이벤트 push/영속 중복 방지 변경 시 재검토 | [M4 작업](../tasks/M04-named-pipe-ipc.md), [ADR-0011](adr/0011-named-pipe-protocol.md), [ADR-0013](adr/0013-sqlite-results-and-query.md) |
| B08 | Deferred — 데이터·로그 장기 운영 | [B08a~d 검토](development-operations-review.md) 완료; 회전·백업/복원·보존/이전·건강도 미구현 | 장시간 자동 운영 배포, DB 스키마 변경, 디스크/로그 지연 또는 시작 재전송 용량 검토 시 | 새 ADR, 회전·느린/실패 I/O·백업/복원·보존/이전 실패·재시작·4,096개 시작 ID 한도 검증 |

매 작업 시작·종료 시 관련 항목만 재검토한다. 일정 도래, 계약 변경, 실제 장애 등 재검토 조건을 구체적으로 적고 단순히 '나중에'로 두지 않는다. 사용자 학습 미확인은 작업·실습 기록에서 관리한다.
