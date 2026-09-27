# 핵심 계약과 검증의 연결

필수 시나리오의 기계 판정 기준은 [verification-map.json](verification-map.json)이다. 각 항목은 고유 ID, 관련 ADR, 소스 파일, 테스트 클래스·메서드, 데이터 사례 이름을 연결한다. M1의 19개와 M2의 23개 기준을 유지하고 M3a Engine 25개를 검토해 추가했다. 전체 필수 사례는 67개이며 현재 결과에서 매번 자동 생성하지 않는다.

| 계약 | 결정 | 검증 위치 |
| --- | --- | --- |
| 입력 복사·검증, 제품 판정과 점수 | [ADR-0001](adr/0001-core-ports-and-composition.md) | [InspectionJobTests](../tests/Inspection.Tests/InspectionJobTests.cs) |
| 단계 순서, 실패 후 호출 차단, 저장 완료와 취소 경계, RunId | [ADR-0003](adr/0003-persistence-completion-boundary.md) | [InspectionRunnerTests](../tests/Inspection.Tests/InspectionRunnerTests.cs) |
| 실제 DLL의 ABI·배열·오류·해제·C# 결과 일치 | [ADR-0005](adr/0005-native-c-abi-adapter.md) | [NativeInspectorTests](../tests/Inspection.IntegrationTests/NativeInspectorTests.cs) |
| 실제 JSON 게시, 덮어쓰기 차단, 저장소 사전 취소 | [ADR-0004](adr/0004-json-result-storage.md) | [JsonResultStoreTests](../tests/Inspection.IntegrationTests/JsonResultStoreTests.cs) |
| 단일 접수·Busy·조회·최초 원인·저장 경계·실제 종료 대기·콜백 실패 | [ADR-0006](adr/0006-single-run-engine.md), [ADR-0007](adr/0007-stop-and-completion-arbitration.md) | [InspectionEngineTests](../tests/Inspection.Tests/InspectionEngineTests.cs), M3A 필수 사례 |
| 두 검사기의 실제 콘솔·JSON, 배포 DLL, 잘못된 인수·저장 오류 | [M2 인수 조건](../tasks/M02-native-inspector.md) | [verify.ps1](../scripts/verify.ps1)에서 독립 확인 |
| 참조 방향, ADR, 다이어그램, 상대 링크 | [문서 규칙](documentation-rules.md) | check-architecture.ps1, build-docs.ps1 |

verify.ps1은 전체 실행 수·실패·skip 검사에 더해 `check-required-tests.ps1`로 각 필수 클래스·메서드·데이터 사례가 정확히 한 번 실행되어 Passed인지 확인한다. 다른 테스트가 늘어나 전체 수를 채워도 필수 사례 누락은 실패한다. 추가 테스트는 허용한다. TRX의 매 실행 UUID는 기준으로 사용하지 않는다.

MSTest의 언어별 표시 차이인 `Method(args)`와 `Method (args)`는 메서드명과 여는 괄호 사이 공백만 정규화해 비교한다. 클래스·메서드·실제 데이터 인수와 사용자 정의 사례 이름은 유지하며 대소문자도 구분한다. 초기 수동 실행과 영어 환경으로 고정한 verify.ps1에서 이 차이를 확인해 M3a에 반영했다.

테스트 이름이나 데이터 사례가 정당하게 바뀌면 대응표도 같은 변경에서 수정하고 작업 기록에 이유를 남긴다. 필수 사례 삭제·대체는 계약이 계속 검증되는지 리뷰한다. 같은 이름의 테스트 안에서 assertion을 약화하는 것까지 이 검사가 탐지하지는 않는다. 테스트 내용과 계약 일치는 코드 리뷰 대상이다.

Native·IPC·상태 계약이 확장되면 새 ID와 실제 사례를 추가한다. 미구현 M3b·M3c 테스트를 통과 목록에 넣지 않는다. 자동 반복 제어기 도입 시 이 기준과 검증 명령을 후보 변경에서 보호하는 방법은 [보류 항목](backlog.md)에서 별도로 다룬다.
