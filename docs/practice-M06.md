# M6 구현과 검증 기록

기준 커밋: c8fe08a. [작업](../tasks/M06-cpp-cli-comparison.md), [비교 사용법](cpp-cli-comparison.md), [ADR-0015](adr/0015-cpp-cli-transport-and-shared-lifetime.md), [ADR-0016](adr/0016-cpp-cli-build-and-deployment.md)를 함께 읽는다.

## 구현

CliInspector가 IInspector를 구현하고 C++ 헤더/import library로 기존 Native DLL을 호출한다. C# NativeInspector의 비동기 수명 코드는 두 경로가 공유한다. 배열 pin은 fixed 또는 pin_ptr, Destroy는 각 호출 방식으로 수행하고 Wait·콜백 종료와 미확인 종료의 자원 보존 계약은 동일하다.

Host는 managed/native/cli를 선택한다. C++/CLI와 ijwhost를 빌드·테스트·publish 출력에 포함하고 누락 시 Setup 실패를 보고한다. Core·IPC DTO·SQLite 스키마는 변경하지 않는다.

## 실제 검증

검증 ID `20260928T132935Z-09b2553b`에서 `scripts/verify.ps1` 전체 검증과 Core 94 + 통합 123 = 217개 필수 사례가 통과했다. 실패·skip은 0이며 C++/CLI 전용 34개도 포함한다. 세 검사기의 실제 실행·자동 반복·저장·타임아웃, cli의 프로세스 IPC·SQLite 재시작·장애 원인 기록을 확인했다. 세 의존 DLL의 개별 누락은 Setup 실패로 처리했고, publish 출력에서 실제 cli 제품 Fail을 JSON으로 저장했다.

추가로 Release/Debug 혼합 DLL과 Visual Studio MSBuild Release x64 솔루션 빌드를 통과했다. 실제 cli 실행으로 제품 Fail의 SQLite 저장과 Debug Pass의 JSON 저장을 확인했다. 해시·상세 증거와 자체 리뷰는 [작업 기록](../tasks/M06-cpp-cli-comparison.md)에 남겼다. 문서 마무리 후 최종 전체 검증 ID·소스 해시는 커밋 본문에 기록한다. 별도 리뷰 호출은 하지 않았다.

## 직접 확인할 학습 항목

- 세 검사기가 같은 IInspector를 구현하면서 Host에서만 교체되는 이유를 설명한다.
- LibraryImport와 C++/CLI 직접 호출, fixed와 pin_ptr의 차이를 설명한다.
- 배열 pin이 끝나는 시점과 콜백 GCHandle/SafeHandle 추가 참조의 종료 시점이 다른 이유를 설명한다.
- C++/CLI를 사용해도 Native Wait와 실제 종료 확인이 필요한 이유를 설명한다.
- 공유 관리 수명 코드가 보장하는 비교 범위와 독립 C++/CLI 수명 구현·gcroot를 배우려면 추가할 내용을 구분한다.
- vcxproj 선행 빌드, Host의 파일 참조와 ijwhost 배포 누락을 재현하고 진단한다.

Codex의 구현·검증과 사용자의 직접 설명·수정·재현은 별개이며 학습 완료는 아직 확인하지 않았다. M0~M6 이후에는 [보류 목록](backlog.md)의 .NET 10 이전·자동 제어기·장기 운영 작업을 별도로 진행한다.
