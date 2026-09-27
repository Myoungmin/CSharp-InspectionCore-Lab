# 아키텍처 결정 기록 (ADR)

ADR(Architecture Decision Record)은 **무엇을, 왜 선택했고 어떤 대가를 받아들였는지**를 결정 하나당 파일 하나로 남긴다. [현재 아키텍처](../architecture.md)는 현재 구조, [전체 설계](../../InspectionLab-Architecture-and-Codex-Loop.md)는 단계별 계획을 설명한다. ADR은 이 문서들을 대체하지 않고 결정의 근거와 변경 이력을 보존한다.

이 절차는 2026-09-27 M2 작업 중 도입했다. 0001~0004는 이미 수용된 M0/M1 계획과 구현을 이번에 정리한 기록이며, 당시 ADR 절차를 운영했다는 뜻은 아니다. 0005는 이번 M2의 연동 결정이다. Accepted는 프로젝트의 채택 상태이며 사용자가 해당 개념을 직접 구현하거나 학습을 완료했다는 뜻이 아니다.

## 작성 기준

다음 선택을 새로 하거나 변경하면 ADR을 작성한다: 프로젝트 책임·참조 방향, 공개 계약, 상태·완료·취소 규칙, 자원 소유권·수명, Native ABI·IPC 프로토콜, 저장 방식, 지원 플랫폼·도구 체계. 기존 결정 안에서의 지역 변수 변경, 내부 리팩터링, 오탈자 수정은 새 ADR이 필요하지 않다.

서로 독립적으로 변경할 수 있는 결정은 별도 파일로 나눈다. 아직 구현하지 않은 로드맵 전체를 구현 완료로 기록하지 않는다. 후속 단계에서 구체화하는 결정은 그 단계의 범위와 검증을 기록한다.

## 작업 흐름

1. 작업 시작 시 관련 ADR을 읽고 작업 기록에 링크한다. 새 결정이 없으면 작업 기록에 `ADR: 기존 결정 유지`와 그 이유를 간단히 남긴다.
2. 새 결정이 필요하면 [템플릿](template.md)을 복사해 가장 큰 번호 다음의 `NNNN-english-topic.md`를 만든다. 결정 전에는 Proposed로 두고, 채택 시 Accepted로 바꾼다. 이미 승인된 작업 범위의 통상적인 판단에 별도 승인 절차를 추가하지 않는다.
3. 배경·선택·대안·영향·검증을 적고 아래 목록에 같은 상태로 추가한다. 날짜는 기록한 날짜이며 기존 결정을 나중에 기록했다면 그 사실을 명시한다.
4. 구현과 함께 현재 아키텍처·관련 계약·다이어그램·작업 기록을 갱신한다. ADR에 적힌 판단이 코드와 맞는지 리뷰하고 `scripts/verify.ps1`을 실행한다.
5. 코드, ADR, 관련 문서를 같은 변경 묶음으로 커밋한다. 기존 Accepted 결정을 바꾸면 새 ADR을 작성하고, 기존 기록은 Superseded로 바꾸어 양쪽에 링크한다. 기존 이유와 대안은 지우거나 새 결론으로 덮어쓰지 않는다.

상태는 Proposed(검토 중), Accepted(채택), Rejected(미채택), Superseded(후속 결정으로 대체)를 사용한다. Rejected와 Superseded도 삭제하거나 번호를 재사용하지 않는다. 사실 오기·링크 수정은 기존 파일에서 가능하지만 결정 변경을 단순 문서 수정으로 숨기지 않는다.

## 목록

| ADR | 상태 | 적용 범위 |
| --- | --- | --- |
| [0001 — Core 포트와 Host 조립](0001-core-ports-and-composition.md) | Accepted | M0/M1, M2에도 유지 |
| [0002 — Windows x64와 고정 도구 체계](0002-windows-x64-toolchain.md) | Accepted | M0/M1, M2 도구 확장 |
| [0003 — 저장 완료와 취소의 경계](0003-persistence-completion-boundary.md) | Accepted | M1 Runner 한 건 |
| [0004 — RunId별 JSON 저장](0004-json-result-storage.md) | Accepted | M1/M2 |
| [0005 — C ABI와 LibraryImport 어댑터](0005-native-c-abi-adapter.md) | Superseded | M2 동기식 Native 검사; 0008로 대체 |
| [0006 — 단일 실행 접수와 조회](0006-single-run-engine.md) | Superseded | M3a Engine; 0010으로 확장 |
| [0007 — 종료 원인과 실제 완료](0007-stop-and-completion-arbitration.md) | Superseded | M3a 취소·타임아웃·종료; 0009로 대체 |
| [0008 — Native 작업·콜백 수명](0008-native-async-lifetime.md) | Accepted | M3b Start/Stop/Wait/Destroy |
| [0009 — 종료 실패와 자원 보존](0009-termination-failure-quarantine.md) | Accepted | M3b 종료 미확인 장애·재접수 차단 |
| [0010 — 순차 자동 실행과 예약](0010-sequential-auto-admission.md) | Accepted | M3c 수동/자동 배타·예약 중지·현재 취소 |

## 검증 범위

`scripts/build-docs.ps1`은 ADR 파일명·번호 중복·제목·날짜·상태·필수 항목·목록의 상태 일치와 상대 링크를 검사한다. 이 검사는 `scripts/verify.ps1`에도 포함된다. 새로운 결정의 기록 누락이나 설계의 타당성은 자동 추론하지 않으므로 코드·작업 기록 리뷰에서 확인한다. Accepted는 테스트 통과 여부를 대신하지 않으며 실제 증거는 실습 기록과 검증 로그에 남긴다.
