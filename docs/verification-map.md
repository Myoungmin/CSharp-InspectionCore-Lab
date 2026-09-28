# 핵심 계약과 검증의 연결

필수 시나리오의 기계 판정 기준은 [verification-map.json](verification-map.json)이다. 각 항목은 고유 ID, 관련 ADR, 소스 파일, 테스트 클래스·메서드, 데이터 사례 이름을 연결한다. M5까지의 181개 기준을 유지하고 M6의 C++/CLI 34개·추가 저장 프로세스 2개를 더해 전체 217개다. 139개 항목으로 정확한 클래스·메서드·데이터 사례를 지정한다. 후보 실행 결과에서 매번 자동 생성하지 않는다.

| 계약 | 결정 | 검증 위치 |
| --- | --- | --- |
| 입력 복사·검증, 제품 판정과 점수 | [ADR-0001](adr/0001-core-ports-and-composition.md) | [InspectionJobTests](../tests/Inspection.Tests/InspectionJobTests.cs) |
| 단계 순서, 실패 후 호출 차단, 저장 완료와 취소 경계, RunId | [ADR-0003](adr/0003-persistence-completion-boundary.md) | [InspectionRunnerTests](../tests/Inspection.Tests/InspectionRunnerTests.cs) |
| 실제 DLL의 ABI·배열·오류·해제·C# 결과 일치 | [ADR-0005](adr/0005-native-c-abi-adapter.md) | [NativeInspectorTests](../tests/Inspection.IntegrationTests/NativeInspectorTests.cs) |
| 실제 JSON 게시, 덮어쓰기 차단, 저장소 사전 취소 | [ADR-0004](adr/0004-json-result-storage.md) | [JsonResultStoreTests](../tests/Inspection.IntegrationTests/JsonResultStoreTests.cs) |
| Native 입력 복사·작업·콜백 종료·정지/Wait 실패 | [ADR-0008](adr/0008-native-async-lifetime.md), [ADR-0009](adr/0009-termination-failure-quarantine.md) | [NativeLifetimeTests](../tests/Inspection.IntegrationTests/NativeLifetimeTests.cs), M3B-001~009: 12개 사례 |
| 종료 실패 우선순위·원인 보존·해제 후에도 엔진 격리 | [ADR-0009](adr/0009-termination-failure-quarantine.md) | [TerminationFailureTests](../tests/Inspection.Tests/TerminationFailureTests.cs), M3B-010: 8개 사례 |
| 단일 접수·Busy·조회·최초 원인·저장 경계·실제 종료 대기·콜백 실패 | [ADR-0006](adr/0006-single-run-engine.md), [ADR-0007](adr/0007-stop-and-completion-arbitration.md) | [InspectionEngineTests](../tests/Inspection.Tests/InspectionEngineTests.cs), M3A 필수 사례 |
| 두 검사기의 실제 콘솔·JSON, 배포 DLL, 잘못된 인수·저장 오류 | [M2 인수 조건](../tasks/M02-native-inspector.md) | [verify.ps1](../scripts/verify.ps1)에서 독립 확인 |
| 참조 방향, ADR, 다이어그램, 상대 링크 | [문서 규칙](documentation-rules.md) | check-architecture.ps1, build-docs.ps1 |
| 자동 접수·순차 반복·예약 중지·현재 취소·간격·실행별 시간 제한·종료 | [ADR-0010](adr/0010-sequential-auto-admission.md) | [InspectionAutoTests](../tests/Inspection.Tests/InspectionAutoTests.cs), M3C Core 31개 |
| 실제 Native 콜백 중 자동 예약 중지와 현재 실행 취소 | [ADR-0010](adr/0010-sequential-auto-admission.md) | [NativeAutoTests](../tests/Inspection.IntegrationTests/NativeAutoTests.cs), M3C Native 2개 |
| 실제 두 프로세스·접수·조회·취소·자동 중지·프레임·입력·종료 | [ADR-0011](adr/0011-named-pipe-protocol.md), [ADR-0012](adr/0012-start-request-replay.md) | [IpcTests](../tests/Inspection.IntegrationTests/IpcTests.cs), M4-001~015, 020: 21개 |
| 동시 재전송·유실 응답·Busy 재전송·용량 제한·연결 독립 수명 | [ADR-0012](adr/0012-start-request-replay.md) | IpcTests의 M4-002~004, 009~011, 014 |
| Client의 버전·RequestId 검증과 실패한 연결 폐기 | [ADR-0011](adr/0011-named-pipe-protocol.md) | [ClientProtocolTests](../tests/Inspection.IntegrationTests/ClientProtocolTests.cs), M4-016: 2개 |
| 과거 JSON 읽기·파일 부재·저장 식별자 검증 | [ADR-0011](adr/0011-named-pipe-protocol.md) | [JsonResultStoreTests](../tests/Inspection.IntegrationTests/JsonResultStoreTests.cs), M4-017~019: 3개 |
| 조회 조건 검증·UTC 정규화·정확한 JobId | [ADR-0013](adr/0013-sqlite-results-and-query.md) | [ResultQueryTests](../tests/Inspection.Tests/ResultQueryTests.cs), M5-001~002: 7개 |
| 진단 예외 격리·잠금 밖 호출·완료/해제 순서·자동 RunId | [ADR-0014](adr/0014-structured-diagnostics.md) | [DiagnosticTests](../tests/Inspection.Tests/DiagnosticTests.cs), M5-003~005: 4개 |
| 실제 SQLite commit·롤백·중복·잠금·검색·커서·스키마 | [ADR-0013](adr/0013-sqlite-results-and-query.md) | [SqliteResultStoreTests](../tests/Inspection.IntegrationTests/SqliteResultStoreTests.cs), M5-006~014: 11개 |
| 두 검사기의 자동 저장·Host 재시작·오류 로그·유실 응답·조회 오류 | [ADR-0013](adr/0013-sqlite-results-and-query.md), [ADR-0014](adr/0014-structured-diagnostics.md) | [StorageDiagnosticsTests](../tests/Inspection.IntegrationTests/StorageDiagnosticsTests.cs), M5-015~023: 13개 |
| 혼합 DLL·세 검사기 결과·입력·Create/Inspect·해제/GC | [ADR-0015](adr/0015-cpp-cli-transport-and-shared-lifetime.md) | [CliInspectorTests](../tests/Inspection.IntegrationTests/CliInspectorTests.cs), M6-001~009: 16개 |
| C++/CLI 콜백·취소·Timeout/Shutdown·Stop/Wait 오류·입력 복사 | [ADR-0015](adr/0015-cpp-cli-transport-and-shared-lifetime.md) | [CliLifetimeTests](../tests/Inspection.IntegrationTests/CliLifetimeTests.cs), M6-010~017: 11개 |
| C++/CLI 자동 중지와 현재 취소의 배타·수명 | [ADR-0010](adr/0010-sequential-auto-admission.md), [ADR-0015](adr/0015-cpp-cli-transport-and-shared-lifetime.md) | [CliAutoTests](../tests/Inspection.IntegrationTests/CliAutoTests.cs), M6-018: 2개 |
| 별도 Client·JSON/SQLite 자동 실행·C++/CLI 오류 진단 | [ADR-0015](adr/0015-cpp-cli-transport-and-shared-lifetime.md) | [CliProcessTests](../tests/Inspection.IntegrationTests/CliProcessTests.cs), M6-019~020: 5개 |
| C++/CLI SQLite CLI·재시작 조회 | [ADR-0015](adr/0015-cpp-cli-transport-and-shared-lifetime.md) | StorageDiagnosticsTests의 cli 데이터 사례, M6-021~022: 2개 |
| 혼합 DLL·ijwhost·Native DLL 해시/누락·publish 실행 | [ADR-0016](adr/0016-cpp-cli-build-and-deployment.md) | verify.ps1에서 독립 검사; MSTest 개수와 구분 |

verify.ps1은 전체 실행 수·실패·skip 검사에 더해 `check-required-tests.ps1`로 각 필수 클래스·메서드·데이터 사례가 정확히 한 번 실행되어 Passed인지 확인한다. 다른 테스트가 늘어나 전체 수를 채워도 필수 사례 누락은 실패한다. 추가 테스트는 허용한다. TRX의 매 실행 UUID는 기준으로 사용하지 않는다.

MSTest의 언어별 표시 차이인 `Method(args)`와 `Method (args)`는 메서드명과 여는 괄호 사이 공백만 정규화해 비교한다. 클래스·메서드·실제 데이터 인수와 사용자 정의 사례 이름은 유지하며 대소문자도 구분한다. 초기 수동 실행과 영어 환경으로 고정한 verify.ps1에서 이 차이를 확인해 M3a에 반영했다.

테스트 이름이나 데이터 사례가 정당하게 바뀌면 대응표도 같은 변경에서 수정하고 작업 기록에 이유를 남긴다. 필수 사례 삭제·대체는 계약이 계속 검증되는지 리뷰한다. 같은 이름의 테스트 안에서 assertion을 약화하는 것까지 이 검사가 탐지하지는 않는다. 테스트 내용과 계약 일치는 코드 리뷰 대상이다.

Native·IPC·상태 계약이 확장되면 새 ID와 실제 사례를 추가한다. 미구현 단계의 테스트를 통과 목록에 넣지 않는다. 자동 반복 제어기 도입 시 이 기준과 검증 명령을 후보 변경에서 보호하는 방법은 [보류 항목](backlog.md)에서 별도로 다룬다.
