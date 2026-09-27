# ADR-0001: Core 포트를 중심으로 책임을 나누고 Host에서 조립한다

- Date: 2026-09-27
- Status: Accepted

## Context

장비·검사·저장 구현을 단계적으로 교체하면서 검사 순서를 외부 환경 없이 테스트해야 한다. 이미 수용된 M0/M1 계획과 구현을 M2에서 처음 ADR로 기록한다.

## Decision

Core가 IDevice, IInspector, IResultStore와 검사 한 건의 순서를 선언한다. Infrastructure와 Interop은 Core 포트를 구현한다. Core는 구현 프로젝트·Native import·IPC DTO를 참조하지 않는다. Host가 의존성을 생성·주입하고 자신이 생성한 자원의 수명을 관리한다. Runner는 주입된 의존성을 빌려 쓰며 해제하지 않는다. 어댑터가 내부에서 만든 자원은 해당 어댑터가 소유한다.

M0/M1은 Core·Infrastructure·Host·Tests 네 프로젝트로 시작했다. M2는 Interop·NativeInspection·IntegrationTests를 추가한다. M3의 실행 접수·Busy·상태를 담당할 Engine은 후속 범위이며 Runner의 한 건 실행 책임과 구분한다.

## Alternatives

- 단일 프로젝트에서 모두 처리: 첫 작성은 짧지만 Core의 외부 의존성과 실제 장비 없는 테스트 경계가 흐려진다.
- 처음부터 범용 workflow와 DI 컨테이너 도입: 현재 고정 네 단계와 작은 조립 지점에는 불필요한 추상화다. 실제 요구가 커질 때 재검토한다.

## Consequences

검사기 교체가 Core 순서 변경으로 이어지지 않고 테스트 대역을 주입할 수 있다. 프로젝트·인터페이스 관리 비용은 늘어난다. 새로운 포트·책임 이동·참조 방향 변경 시 이 결정을 검토한다.

## Validation

check-architecture.ps1에서 평가된 프로젝트 참조를 검사한다. Inspection.Tests는 Core만 참조하고 단계 실패 후 후속 호출 차단을 확인한다. 실제 어댑터·DLL·파일은 별도 통합 검증한다. 실제 통과 기록은 아래 실습 문서에 있다.

## Links

- [현재 아키텍처](../architecture.md)
- [M1 작업](../../tasks/M01-run-one-job.md)
- [M1 실습 증거](../practice-M01.md)
- [M2 실습 증거](../practice-M02.md)
