# DEV03 — Windows CI와 검증 증거 보관

상태: InProgress. 기준 커밋: d39d077. B04 구현 작업이며 제품 마일스톤은 M6를 유지한다.

## 목표와 범위

GitHub Actions Windows x64에서 고정 도구를 확인하고 기존 verify.ps1을 실행한다. 정상·실패 모두 검증 증거를 보관한다. .NET 10 이전, Codex 반복 제어기, 자동 병합·배포는 포함하지 않는다.

## 인수 조건

- main 변경·PR·수동 실행에서 CI를 시작하고 기존 Native→관리 수명→C++/CLI→Host/테스트 빌드 순서를 유지한다.
- SDK 9.0.305, MSVC 14.44.35207, Windows SDK 10.0.26100.0, C++/CLI, Java 11과 PlantUML 해시를 검사한다. 누락 도구는 명시적으로 실패한다.
- 기존 217개 사례·Native/IPC/publish·문서 검증을 유지한다.
- 성공·실패에서 도구 정보·summary·TRX·로그·소스 manifest를 보관한다. 검증 전 환경 실패도 증거를 남긴다.
- 새 checkout의 실제 원격 성공과 의도한 실패를 확인하고 실행 URL·commit·artifact 근거를 기록한다. 원격 미확인 상태를 완료로 기록하지 않는다.

## 아키텍처 결정

[ADR-0017](../docs/adr/0017-windows-ci-and-verification-evidence.md)에 Windows CI 실행·증거 보관 결정을 기록했다. 기존 제품 계약과 도구 고정 결정은 유지한다.

## 검증 계획과 정책 변경

사전 검사·실패 전달·증거 경로의 개발 검증과 기존 scripts/verify.ps1 전체 검증을 수행한다. CI는 GitHub hosted Windows에서 실제 확인한다. 인수 조건·테스트 수·Native/IPC 제한 시간은 약화하지 않는다. 코드·도구 체계를 바꾸지 않고 검증 호출을 자동화한다.

CI 그림 1개를 추가해 문서 inventory를 11→12로 확장하고 ADR은 17건이 된다. 기존 필수 139개 항목·217개 MSTest 사례는 동일하며 TRX 기준 JSON을 변경하지 않는다. CI 동작은 workflow 정적 검사·실제 호출·원격 실패 artifact로 검증하므로 MSTest 수에 포함하지 않는다. GitHub release와 기존 pin의 PlantUML JAR가 달라 Maven Central의 동일 해시 파일을 사용한다.

## 검증 증거와 리뷰

작업 중이다. 실제 실행 결과와 자체 리뷰를 완료 후 기록한다.

## 남은 일과 학습 확인

B01 전환 보류, B03 제어기, B08 장기 운영은 별도 작업이다. 사용자 학습 완료를 추정하지 않는다.

## 완료 확인

구현·로컬 검증·원격 성공/실패·artifact 보관·ADR·문서·후속 범위를 확인한다.
