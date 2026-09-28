# ADR-0015: C++/CLI 호출 경계와 공유 Native 수명

- Date: 2026-09-28
- Status: Accepted

## Context

M6는 IInspector 계약을 C++/CLI로 교체하고 LibraryImport 경로와 결과·오류·종료를 비교한다. 비동기 수명 코드를 두 언어로 복제하면 콜백 join과 미확인 Wait 보존 정책이 달라질 수 있다. 같은 Native DLL을 사용해야 호출 방식의 차이를 관찰하기 쉽다.

## Decision

Inspection.CppCli는 별도 혼합 모드 DLL이다. CliInspector가 Core의 IInspector와 IAsyncDisposable을 구현하고 C++/CLI 소멸자로 IDisposable을 제공한다. Host가 managed/native/cli 중 하나를 생성·소유하며 Engine 종료 후 어댑터를 해제한다. cli 로드는 별도 NoInlining 생성 메서드 안에서 수행해 선택 시의 로드 오류를 Host Setup 경계에서 보고한다.

Inspection.Interop에 어댑터 간 호출 계약 INativeInspectionApi를 둔다. 이는 Core 포트가 아니며 Core는 이 타입을 참조하지 않는다. 기존 NativeInspector는 관리 실행·취소·콜백·SafeHandle 보유·오류 변환을 담당하고 기본 PInvokeInspectionApi 또는 주입된 CliNativeApi로 호출한다. CliInspector는 이 수명 객체를 소유한다. C++/CLI는 Core와 Interop을 참조하며 Interop은 C++/CLI를 참조하지 않는다.

두 경로 모두 동일한 NativeInspection ABI 2를 사용한다. PInvokeInspectionApi는 LibraryImport, CliNativeApi는 C++ 헤더와 import library를 사용한다. CliNativeApi의 private unmanaged thunk는 void*만 받아 불투명 inspection_handle을 CLR 메타데이터에 노출하지 않는다. Native DLL의 구현 레이아웃을 가짜 구조체로 정의하지 않는다. 네이티브 예외는 기존 noexcept C ABI에서 상태 코드로 변환한다.

공유 수명 객체가 입력을 복사하고 각 호출 구현은 Start가 반환할 때까지만 배열을 고정한다. C++/CLI는 pin_ptr를 사용한다. Wait는 dedicated waiter에서 호출하고 작업·콜백 종료를 확인한다. C++/CLI 개별 호출도 DangerousAddRef/Release로 SafeHandle을 보호한다. 작업 전체의 추가 참조와 GCHandle 문맥은 공유 코드가 관리한다. SafeHandle의 release delegate가 해당 호출 방식으로 Destroy를 수행한다. 미확인 Wait는 추가 참조·콜백 문맥을 프로세스 종료까지 보존한다.

NativeInspectionException·InspectionTerminationException과 진행 데이터 형식을 공유한다. Core의 IInspector·Engine·Runner 계약은 바꾸지 않는다. 기존 [ADR-0008](0008-native-async-lifetime.md), [ADR-0009](0009-termination-failure-quarantine.md), [ADR-0010](0010-sequential-auto-admission.md)은 두 경로에 계속 적용된다. ProgressCallbackCount는 비교 진단이며 소유권 판단에 사용하지 않는다.

## Alternatives

모든 비동기/종료 로직을 C++/CLI로 다시 쓰면 언어 학습 범위는 넓어지지만 두 구현의 수명 정책을 별도로 유지해야 한다. 단순히 기존 LibraryImport 검사기를 감싸기만 하면 다른 호출 경계를 검증하지 못한다. 이번 결정은 수명은 공유하되 실제 ABI 호출과 pinning을 다른 언어로 구현한다. C++ 전용 클래스 SDK를 직접 래핑하는 작업은 해당 SDK의 소유권 계약이 생길 때 별도로 다룬다.

## Consequences

Interop의 호출 계약·결과 구조체·SafeHandle이 혼합 어셈블리에서 접근할 수 있도록 public이지만 제품 Core API는 아니다. 이 실습은 import·배포·pinning 차이를 비교하며 전체 수명 알고리즘의 독립 구현이나 성능 우열을 증명하지 않는다. 관리 callback trampoline을 두 경로가 공유하므로 C++ gcroot 콜백 구현을 추가한 것은 아니다.

## Validation

혼합 DLL의 x64/CLR 플래그, 세 검사기의 결과 일치·배열 슬라이스·오류를 검사한다. C++/CLI에서도 명시적 신호로 콜백 중 취소/Dispose, 자동 예약 중지, 타임아웃·Shutdown, Stop/Wait 오류, GC 해제·보존과 늦은 콜백 억제를 확인한다. 실제 Host/Client·SQLite 재시작을 포함한 증거는 [M6 작업](../../tasks/M06-cpp-cli-comparison.md)에 기록한다.

## Links

- [C++/CLI 비교 사용법](../cpp-cli-comparison.md)
- [혼합 빌드 결정](0016-cpp-cli-build-and-deployment.md)
- [Microsoft C++/CLI와 .NET](https://learn.microsoft.com/en-us/dotnet/core/porting/cpp-cli)
