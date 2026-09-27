# ADR-0005: Native 검사기를 C ABI와 LibraryImport 어댑터로 연결한다

- Date: 2026-09-27
- Status: Accepted

## Context

M2는 실제 C++ DLL로 검사기를 교체하면서 기존 IInspector와 결과 계약을 유지하는 단계다. 숫자 배열·오류·소유권 경계를 직접 확인해야 한다. 이번 M2 선택을 기록하며 비동기 작업·콜백·협조적 중단은 M3b에서 결정·구현한다.

## Decision

NativeInspection은 C ABI v1으로 Create/Run/Destroy를 제공한다. Inspection.Interop만 LibraryImport 선언을 가지며 IInspector를 구현한다. 동기 호출 동안만 샘플 메모리를 고정하고 double 포인터와 int32 원소 수를 전달한다. Native는 입력 포인터를 보관하지 않는다. 결과는 명시한 고정 크기 필드로 반환하고 ABI 버전·레이아웃을 확인한다.

NativeInspector가 자신이 생성한 opaque 핸들을 SafeHandle로 소유하고, Host가 NativeInspector를 Dispose한다. 호출 인자도 SafeHandle을 사용해 호출 동안 핸들의 수명을 유지한다. C++ 예외는 export 경계 안에서 상태 코드로 변환하고 Interop에서 Operation·Status를 가진 관리 예외로 바꾼다. DLL이 없으면 설정 실패이며 C# 검사기로 자동 대체하지 않는다.

## Alternatives

- C++/CLI부터 도입: 비교 학습은 M6에서 같은 IInspector의 별도 구현으로 진행한다. M2는 C ABI 경계를 먼저 확인한다.
- 원시 IntPtr만 소유: 예외 경로와 중복 해제 방지를 직접 관리해야 하므로 SafeHandle을 사용한다.
- Task.Run과 대기 취소만으로 중단 제공: Native 작업 종료를 보장하지 않으므로 M2의 취소 기능으로 채택하지 않는다.

## Consequences

Core 변경 없이 검사기를 교체하고 실제 DLL과 C# 결과를 비교할 수 있다. ABI 선언과 양쪽 버퍼·수명 계약을 맞추는 비용이 생긴다. M2 Native 호출은 동기식이며 취소를 호출 전·반환 후에 확인한다. 실행 중 중단·콜백 종료·해제 경합은 보장하지 않는다. Native 비동기화 시 이 범위를 새 ADR에서 변경한다.

## Validation

실제 DLL 통합 테스트 20개가 x64·ABI·배열 범위·결과 일치·오류 변환·해제를 검사한다. verify.ps1에서 빌드·배포 DLL 해시와 두 검사기의 실제 JSON, DLL 누락 시 실패를 확인한다. Core 테스트와 실제 Native 검증을 구분하며 누락·skip을 통과로 처리하지 않는다.

## Links

- [Core 포트와 조립 결정](0001-core-ports-and-composition.md)
- [Native ABI와 수명 상세](../native-interop.md)
- [M2 작업](../../tasks/M02-native-inspector.md)
- [M2 실제 검증 증거](../practice-M02.md)
