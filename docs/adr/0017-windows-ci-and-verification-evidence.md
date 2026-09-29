# ADR-0017: Windows CI와 성공·실패 증거 보관

- Date: 2026-09-29
- Status: Accepted

## Context

M0~M6의 로컬 전체 검증은 재현했지만 원격의 새 checkout에서 자동 검증·증거 보관을 수행하지 않았다. .NET 10 전환은 보류됐으므로 현재 SDK·Native·C++/CLI·문서 도구를 그대로 준비해야 한다.

## Decision

GitHub Actions의 windows-2022 x64 runner에서 main 변경, main 대상 PR, 수동 실행과 ci/** 검증 브랜치를 처리한다. 같은 branch/PR의 이전 실행은 취소하며 job 제한은 30분이다. checkout/setup-dotnet/upload-artifact는 검토한 릴리스의 전체 commit SHA로 고정한다. 저장소 권한은 contents:read, checkout 인증 정보의 잔류는 비활성화한다. Codex 호출·자동 병합·배포는 포함하지 않는다.

setup-dotnet은 global.json의 9.0.305를 설치한다. prepare-ci.ps1은 Windows x64, MSVC 14.44.35207, Windows SDK 10.0.26100.0, C++/CLI 구성 요소와 Java 11을 확인한다. 정확한 도구가 없으면 환경 실패로 보고하며 프로젝트 pin이나 필수 테스트를 변경하지 않는다. PlantUML은 기존 해시와 일치하는 Maven Central의 GPL JAR를 내려받는다. 같은 버전의 GitHub release JAR는 해시가 달라 대체하지 않는다.

ci.ps1은 사전 검사와 기존 verify.ps1을 별도 프로세스로 실행하고 실패 종료 코드를 job까지 전달한다. 사전 검사는 120초, 전체 검증은 20분이며 기존 각 검증 단계의 제한은 유지한다. 사전 검사도 finally에서 환경 결과를 남긴다. workflow는 SDK 설치 전에 실행 식별 정보를 남기고 always 조건으로 환경·결과·TRX·manifest·검증/IPC 로그를 14일간 보관한다. DLL·DB·인증 정보는 업로드하지 않는다. 초기 버전에는 빌드·NuGet 캐시를 두지 않는다.

기존 [ADR-0002](0002-windows-x64-toolchain.md)·[ADR-0016](0016-cpp-cli-build-and-deployment.md)의 도구·빌드 계약은 유지한다. CI는 개발 검증 인프라이고 제품 Core의 의존성이 아니다.

## Alternatives

windows-latest는 IDE 변경에 영향을 받으므로 사용하지 않는다. 개인 PC를 self-hosted runner로 쓰면 외부 PR 코드와 개인 환경이 섞이므로 채택하지 않는다. hosted 이미지에서 정확한 도구를 확보하지 못할 때 별도 고정 이미지 runner를 검토한다. Core 테스트만 실행하는 빠른 job은 Native·IPC 누락을 감추므로 전체 검증을 대체할 수 없다.

## Consequences

원격 환경 변경이나 다운로드 장애는 명시적인 실패가 된다. runner 이미지를 완전히 고정한 것은 아니므로 실제 ImageVersion과 도구 정보를 기록한다. artifact 보관은 저장소/조직 정책의 제한을 받으며 영구 보관은 아니다. job 강제 중단·runner 손실·checkout 실패 등에서는 artifact 업로드가 완료되지 않을 수 있고 GitHub 실행 로그를 확인해야 한다.

자동 제어기의 후보 밖 보호된 검증은 별도 B03 범위다. CI에서 후보의 검증 스크립트를 실행하는 이번 구성을 그 보호 경계가 구현됐다고 해석하지 않는다. 필수 상태 검사 설정은 실제 원격 성공·실패 확인 뒤 별도로 적용한다.

## Validation

workflow 정적 검사, 로컬 사전 검사 성공/해시 오류 실패, ci.ps1 전체 실행, 새 checkout의 원격 성공과 의도한 검증 실패에서 artifact 보관을 확인한다. 실제 실행 URL·commit과 남은 항목은 [DEV03 작업](../../tasks/DEV03-windows-ci.md)에 기록한다.

## Links

- [CI 실행과 증거](../continuous-integration.md)
- [개발·운영 구성 검토](../development-operations-review.md)
- [GitHub artifact 보관](https://docs.github.com/en/actions/tutorials/store-and-share-data)
- [GitHub Windows 2022 이미지](https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md)
