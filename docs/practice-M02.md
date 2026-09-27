# M02 실습 구현 기록

날짜: 2026-09-27. M1 기준 커밋: `9f0a24c`.

## 구현

C ABI v1 숫자 배열 검사 DLL, LibraryImport 선언, SafeHandle 소유권, Native 오류 변환, managed/native 선택을 추가했다. Core의 인터페이스와 검사·저장 순서는 유지했다. Host가 Native 어댑터를 해제한다.

사용자의 확인 요청에 따라 [ADR 작성 절차](adr/README.md)도 개발 규칙·문서 검증에 연결했다. 기존 결정 네 건은 사후 기록임을 명시하고 이번 Native 연동은 ADR-0005로 남겼다. 향후 구조 변경은 새 ADR·목록·현재 아키텍처·관련 다이어그램을 함께 갱신한다.

## 실제 확인

MSVC 14.44.35207 / Windows SDK 10.0.26100.0의 x64 DLL 빌드가 경고·오류 없이 통과했다. 초기 실제 DLL·파일 MSTest는 23개 통과, 실패 0, skip 0이다. `artifacts/m02-initial-tests/integration.trx`에 초기 결과가 있다.

Visual Studio MSBuild의 혼합 솔루션 Release/x64 빌드도 잠금 파일 restore와 함께 통과했다. Native 프로젝트를 dotnet으로 빌드한 결과와 혼동하지 않는다.

ADR 5건의 형식·목록·링크를 포함한 문서 검증도 통과했다. 저장소 원본을 건드리지 않은 artifacts 복사본에서 목록 상태 불일치, 빈 Decision, 중복 번호, 잘못된 날짜를 각각 주입해 모두 해당 이유로 거절되는 것을 확인했다. 증거는 `artifacts/adr-validation/e54c19df6c7948aeb7fff3e6a9854823`에 있다. 이 문서 검증 사례는 아래 MSTest 42개에 포함하지 않는다.

전체 verify.ps1도 통과했다. Core 19개 + 실제 Native 20개 + 파일 3개, 총 42개 테스트가 실행·통과했고 실패·skip은 0이다. managed/native 각각 정상·불량·정상으로 실행해 결과 JSON 6개와 고유 RunId를 확인했다. DLL 복사본 해시, DLL 누락 시 Setup 실패, 저장 실패, 잘못된 인수, 의존성 규칙과 다이어그램 5개도 통과했다.

최초 전체 통과 기록은 `artifacts/verification/20260927T002138Z-4e8d8877/summary.json`이다. 두 TRX·콘솔 로그·JSON·배포 DLL 해시·소스 해시 목록을 함께 기록했다. 문서 정리 이후 최종 내용의 검증은 별도 실행 폴더에 남긴다. ABI·배열 범위·SafeHandle 소유권·예외 변환·후속 저장 차단을 코드와 테스트로 리뷰했으며 별도 Codex 리뷰 세션을 실행한 것은 아니다.

## 학습 상태와 다음 단계

구현과 테스트 실행은 Codex가 수행했다. 사용자가 직접 ABI 선언을 작성하거나 소유권 질문에 답한 것으로 기록하지 않는다. 외부 기초 학습 기록은 변경하지 않는다.

다음은 M3a의 InspectionEngine, 실행 접수·Busy·상태·취소·타임아웃이다. M2의 Native 호출은 동기식이며 콜백·백그라운드 작업·협조적 Native 중단·종료 경합 검증은 아직 구현하지 않았다. 자동 Codex 반복 제어기도 별도 후속 작업이다.
