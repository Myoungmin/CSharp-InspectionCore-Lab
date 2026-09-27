# M3b Native ABI와 소유권

NativeInspector는 Core IInspector를 구현한다. 가상 검사 규칙은 RangeInspector와 같으며 P/Invoke 선언과 Native 오류·수명 관리는 Inspection.Interop에 둔다. [ADR-0008](adr/0008-native-async-lifetime.md)이 M2 동기 수명을 대체하고 [ADR-0009](adr/0009-termination-failure-quarantine.md)가 종료 실패를 정의한다.

![Native ownership](diagrams/generated/native-interop.svg)

## ABI v2

Windows x64, C++17, C ABI export, Cdecl을 사용한다. C++ class, bool, size_t, STL 객체를 경계에 노출하지 않는다. ABI 버전과 결과 크기를 생성 전에 확인하며 DLL은 Interop 어셈블리 디렉터리에서 로드한다. Host·테스트에 배포된 DLL 해시도 검증한다.

| 호출 | 계약 |
| --- | --- |
| Create(mode, out handle) | 객체 하나를 생성하고 소유권을 호출자에게 전달. 실패 출력은 null |
| Start(handle, samples, count, bounds, callback, context) | count는 double 원소 수. 호출 안에서 검증·복사한 뒤 스레드를 시작. 반환 후 입력 pin 해제 가능 |
| RequestStop(handle) | 원자적 정지 요청. 완료 보장 없음. Wait·작업·콜백과 동시 호출 가능 |
| Wait(handle, out operationStatus, out result) | 반환 Ok는 작업 스레드 join과 모든 콜백 반환을 보장. 검사 성공·취소·오류는 operationStatus로 구분 |
| Destroy(handle) | Wait 전 joinable 핸들은 Busy로 거절. 종료 확인한 객체만 삭제 |
| Run(handle, samples, count, bounds, result) | 기존 동기 경계 회귀용 export. 입력은 호출 중만 빌림. 현재 어댑터는 사용하지 않음 |

호출자는 유효한 핸들에 Start/Wait/Destroy를 직렬로 호출한다. 동시 Wait나 임의 포인터를 지원하지 않는다. Start가 실패하면 작업과 콜백은 시작되지 않는다. 성공한 Start마다 Wait를 한 번 수행한 뒤 재사용하거나 해제한다. 콜백 안에서 자신의 Wait/Destroy를 호출하면 안 된다.

결과 구조체는 int32 sampleCount, int32 defectCount, double score 순서이며 크기 16바이트, score 오프셋 8이다. 기본 정렬을 사용하고 Native static_assert와 실제 DLL 테스트로 검증한다. score는 반올림 전 정상 비율이며 어댑터는 count와 score의 일관성을 확인한 뒤 InspectionAssessment를 만든다. 외부 점수는 소수 둘째 자리까지 반올림한다.

상태는 Ok=0, InvalidArgument=1, OutOfMemory=2, InternalError=3, Canceled=4, Busy=5다. 잘못된 길이·null·비정상 범위·NaN/Infinity는 거절한다. 임의 주소나 버퍼보다 큰 길이의 안전성은 호출자 책임이다. C++ 예외는 export와 작업 스레드 안에서 상태로 변환한다. 프로세스 충돌·메모리 손상을 복구하는 계약은 아니다.

## 관리 어댑터와 콜백

어댑터는 접수 시 관리 입력을 복사하고 하나의 작업만 허용한다. Start 호출 동안만 배열을 pin하며 Native는 자체 복사본을 사용한다. 전용 관리 대기 스레드가 Wait를 수행한다. 토큰은 Start 전에 등록하므로 접수·시작 사이 취소도 놓치지 않는다.

정적 UnmanagedCallersOnly 콜백에 GCHandle로 작업 문맥을 전달한다. SafeHandle의 추가 참조와 문맥은 전체 작업 동안 유지한다. 정상 경로는 Wait 성공, 토큰 콜백 등록 해제, GCHandle·추가 참조 반환 순서다. SafeHandle 최종 정리는 작업 없는 누락 객체의 보조 수단이다.

진행 값은 완료 원소 수와 전체 원소 수이며 0부터 증가한다. `Action<NativeInspectionProgress>` 관찰자는 Native 작업 스레드에서 동기 호출된다. 다른 UI context로 자동 전환하지 않으며 빠르게 반환해야 한다. 자기 작업의 완료를 기다리지 않는다. 자신의 Dispose 재진입은 즉시 거절한다. 관찰자 예외는 Native 경계를 넘지 않고 정지 요청과 join 후 관리 오류로 전달된다.

RequestStop 전달 뒤 도착한 통지는 외부 관찰자에 전달하지 않는다. 이미 진입한 관찰자는 실행을 마쳐야 한다. Native 시뮬레이터는 정지 요청 뒤에도 마지막 콜백을 한 번 호출할 수 있으며 이를 실제 DLL로 검증한다. Wait 이후에는 Native 콜백이 없다. 다음 실행은 새 문맥을 가지며 이전 실행의 통지가 섞이지 않는다.

Host는 Engine 종료를 기다린 뒤 NativeInspector를 DisposeAsync한다. 어댑터 자체 DisposeAsync도 접수를 닫고 정지·Wait 후 해제하며 중복 호출은 같은 종료를 기다린다. 동기 Dispose는 같은 Task를 기다린다. 진행 중 관찰자가 반환하지 않으면 종료 대기도 계속된다.

## 종료 실패

RequestStop 오류 후에는 Wait를 계속하여 종료를 확인하고 InspectionTerminationException(true)를 반환한다. 안전한 해제는 가능하지만 엔진·어댑터 재사용은 거절한다. Wait 오류는 InspectionTerminationException(false)와 함께 **GCHandle과 SafeHandle 추가 참조를 프로세스 종료까지 보존**한다. Dispose와 GC가 미확인 작업의 메모리를 해제하지 못한다.

Engine은 두 오류 모두 Faulted로 판정하며 기존 StopReason을 진단으로 유지한다. false의 Completion은 관리 측 장애 판정 완료이며 Native 종료를 뜻하지 않는다. [Engine 계약](engine-contract.md)과 [장애 대응](troubleshooting.md)을 따른다. 자동 복구·재시도는 없다.

NativeFaultMode의 Create/Inspect/Stop/Wait 실패는 가상 DLL의 실제 예외 경로 검증 전용이다. 프로세스의 live/destroyed와 핸들별 progress 카운터는 테스트 진단이며 소유권 제어에 사용하지 않는다. Wait 실패 사례는 핸들 하나를 의도적으로 테스트 프로세스 종료까지 보존한다.

## 빌드와 검증

Native는 VS MSBuild, v143/14.44.35207, Windows SDK 10.0.26100.0과 정적 CRT를 사용한다. 관리 프로젝트는 .NET SDK 9.0.305/net9.0/x64로 빌드한다. 전체 검증은 Native 빌드 후 dotnet build/test 순서다.

실제 DLL 테스트 34개는 기존 20개, 비동기 수명 12개와 M3c 자동 예약 중지·현재 실행 취소 2개다. Core 종료 실패 8개는 실제 Native 없이 엔진의 원인·상태 우선순위를 확인한다. Native/파일 통합 테스트에는 프로세스 종료 제한을 두고 필수 누락·skip은 실패한다. [검증 대응표](verification-map.md)에 사례를 고정한다.

## 공식 참고

- [LibraryImport 소스 생성](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation)
- [Native interop 지침](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices)
- [UnmanagedCallersOnly](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute)
- [SafeHandle 참조 해제](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle.dangerousrelease)
