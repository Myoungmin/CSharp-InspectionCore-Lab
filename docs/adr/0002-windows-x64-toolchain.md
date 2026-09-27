# ADR-0002: 초기 도구 체계를 Windows x64와 .NET 9로 고정한다

- Date: 2026-09-27
- Status: Accepted

## Context

사용자는 현재 VS 2022 17.14와 .NET SDK 9.0.305 환경으로 학습을 시작하기로 선택했다. 초기 구현과 도구 이전을 동시에 진행하지 않도록 이미 수용한 선택을 이번에 기록한다. M2에서는 실제 C++ 빌드 경로를 추가로 구체화했다.

## Decision

SDK 9.0.305, net9.0, Windows x64를 사용하고 global.json과 패키지 잠금 파일을 커밋한다. .NET 10 이전은 별도 작업으로 수행한다. 일반 .sln 안에 관리 프로젝트와 MSVC .vcxproj를 둔다. M2 Native 도구는 v143 / MSVC 14.44.35207 / Windows SDK 10.0.26100.0이다.

CLI 검증은 Visual Studio MSBuild로 Native를 먼저 빌드한 후 관리 진입 프로젝트를 dotnet build/test로 검증한다. 혼합 솔루션 전체의 dotnet build를 표준 명령으로 사용하지 않는다. Visual Studio에서는 x64 구성과 Native → Interop 의존 순서를 사용한다.

## Alternatives

- 즉시 .NET 10으로 이전: 초기 선택 범위를 바꾸고 IDE·SDK 이전 검증이 섞인다. 별도 이전 작업에서 검토한다.
- 초기부터 여러 플랫폼·아키텍처 지원: ABI와 배포 검증 범위를 크게 늘리므로 Windows x64부터 확인한다.

## Consequences

현재 환경과 검증 명령을 재현하기 쉬워지지만 정확한 SDK·C++ 도구 설치가 필요하다. 오래된 도구를 계속 사용한다는 결정은 아니다. 도구 체계 이전 시 새 ADR에서 호환성과 빌드·테스트 결과를 기록한다.

## Validation

verify.ps1은 잠금 모드 restore, Native·관리 빌드, 실제 DLL x64와 복사본 해시를 확인한다. Visual Studio MSBuild 혼합 솔루션 빌드도 M2에서 실제 확인했다. 도구 버전이나 대상 프레임워크 변경은 별도 검증 대상이다.

## Links

- [실행과 필요한 도구](../../README.md)
- [전체 단계와 이전 계획](../../InspectionLab-Architecture-and-Codex-Loop.md)
- [Native 빌드 계약](../native-interop.md)
- [M2 실습 증거](../practice-M02.md)
