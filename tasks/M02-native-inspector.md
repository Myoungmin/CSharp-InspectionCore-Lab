# M02 — C++ DLL과 LibraryImport 검사기

## 범위

NativeInspection, Inspection.Interop, Inspection.IntegrationTests를 추가한다. 기존 Core 포트는 유지하고 Host에서 managed/native를 선택한다. 동기 호출과 안전한 소유권까지 구현하며 M3의 작업 스레드·중단·콜백·자동 반복은 포함하지 않는다. Codex 자동 반복 제어기는 Native 검증 안정화 이후의 별도 작업이다.

## 인수 조건

- v143, Windows SDK 10.0.26100.0, x64로 실제 C++ DLL을 빌드한다.
- C ABI v1의 배열 원소 수·result 레이아웃·오류 코드를 문서화하고 실제 호출로 검증한다.
- LibraryImport로 같은 IInspector 계약을 구현하고 기존 C# 검사기와 결과가 일치한다.
- SafeHandle이 객체를 한 번만 해제한다. 해제 후 호출을 거절하며 Native 생성·검사 실패가 누수나 저장으로 이어지지 않는다.
- C++ 예외가 ABI 밖으로 전파되지 않고 관리 예외에 Operation·Status가 남는다.
- Core 19개에 추가하여 실제 Native 20개와 파일 3개 통합 테스트가 통과한다. 누락·skip은 실패다.
- managed/native 각각 정상·불량·반복 콘솔 실행과 JSON 6개를 확인한다. Native DLL 누락 시 명확하게 실패하고 managed로 대체하지 않는다.
- Native 빌드·관리 빌드·DLL 복사 해시·TRX·문서 검증을 verify.ps1에 연결한다.
- 아키텍처 결정을 지속 기록할 ADR 목록·템플릿·개발 규칙을 연결하고, 기존 채택 결정과 이번 Native 결정을 구분해 남긴다. 문서 단계에서 ADR 형식·목록·링크를 검사한다.

## 검증 정책 변경 이유

M1의 19개 Core 테스트·저장 실패·인수 검증은 유지한다. Native 필수 검증을 추가하여 통합 테스트 최소 23개와 Native 클래스 결과 최소 20개를 따로 검사한다. 콘솔 실행은 같은 세 시나리오를 두 구현에 적용하여 3회에서 6회로 늘린다. 새 Interop 수명을 설명하기 위해 다이어그램은 4개에서 5개로 늘리고, 관리 프로젝트 6개와 독립 Native 프로젝트의 참조 규칙을 검사한다. vcxproj를 dotnet이 빌드하지 않도록 빌드 경로를 명시적으로 나눈다.

사용자의 ADR 지속 기록 절차 확인 요청에 따라 이번 작업에 ADR 운영을 추가했다. 기존 검증 기준은 유지하며 문서 검사에 ADR 파일명·고유 번호·제목·날짜·상태·필수 항목·목록의 상태 일치 검사를 추가한다. 결정의 누락과 타당성은 코드 리뷰로 확인한다. 자동 Codex 제어기를 추가한 것은 아니다.

## 관련 아키텍처 결정

- 새 기록: [ADR-0005 — C ABI와 LibraryImport 어댑터](../docs/adr/0005-native-c-abi-adapter.md).
- 기존 결정 유지: [ADR-0001 — Core 포트와 조립](../docs/adr/0001-core-ports-and-composition.md), [ADR-0002 — 고정 도구 체계](../docs/adr/0002-windows-x64-toolchain.md), [ADR-0003 — 저장 완료 경계](../docs/adr/0003-persistence-completion-boundary.md), [ADR-0004 — JSON 저장](../docs/adr/0004-json-result-storage.md). Core 계약·완료 정책·저장 방식은 유지하고 허용된 Native 어댑터와 해당 빌드 도구를 구체화한다. 0001~0004는 기존 결정을 이번에 처음 ADR로 정리한 것이다.

## 학습할 판단

1. 배열을 고정한 범위를 벗어나 Native가 포인터를 보관하면 안 되는 이유.
2. SafeHandle을 인자로 전달하는 것과 원시 IntPtr만 전달하는 것의 수명 차이.
3. 동기 Native 호출의 대기 취소가 실제 Native 중단을 의미하지 않는 이유.

사용자가 직접 답변·작성한 것으로 기록하지 않는다. [연동 설명](../docs/native-interop.md)과 [실습 기록](../docs/practice-M02.md)을 함께 남긴다.
