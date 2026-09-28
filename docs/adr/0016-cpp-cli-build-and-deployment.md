# ADR-0016: 혼합 DLL 빌드와 배포 검증

- Date: 2026-09-28
- Status: Accepted

## Context

C++/CLI는 SDK 형식 csproj가 아닌 vcxproj와 Visual Studio MSBuild가 필요하다. dotnet Host 빌드가 vcxproj를 따라가면 기존 검증 흐름을 사용할 수 없다. 혼합 DLL 외에 ijwhost와 NativeInspection도 배포해야 한다.

## Decision

SDK 9.0.305/net9.0/Windows x64, MSVC 14.44.35207/v143, Windows SDK 10.0.26100.0을 유지한다. Visual Studio C++/CLI 지원 구성 요소를 필수 확인한다. Inspection.CppCli.vcxproj는 CLRSupport=NetCore, /MD(Release)·/MDd(Debug)를 사용하며 두 구성을 제공한다. 플랫폼 이전 결정 [ADR-0002](0002-windows-x64-toolchain.md)는 유지한다.

scripts/build-cppcli.ps1은 기존 build-native → Interop/Core의 잠금 restore·dotnet build → C++/CLI의 Visual Studio MSBuild 순서를 실행한다. C++/CLI는 Core/Interop ProjectReference를 가지며 스크립트 호출에서는 선행 빌드한 프로젝트를 다시 빌드하지 않는다. Host와 IntegrationTests는 Consumer.targets의 정확한 파일 Reference로 혼합 DLL을 사용한다. 솔루션에는 Native·관리 참조·C++/CLI·Host/통합 테스트 의존 순서를 둔다. dotnet build 혼합 솔루션은 사용하지 않는다.

혼합 DLL과 ijwhost는 artifacts/cppcli/<Configuration>에 생성한다. Host/테스트의 출력 및 publish로 복사하고 누락 시 빌드를 실패시킨다. NativeInspection.dll은 기존 Interop 배포 규칙을 유지한다. 실행은 관리 Host가 시작하므로 Host.runtimeconfig를 사용한다. 이 환경의 ijwhost는 .NET 9.0.9 host pack에서 왔으며 검증마다 실제 DLL 해시를 기록한다. 배포 대상에는 .NET 9 x64와 v143 C++ 런타임이 필요하다. 다른 컴퓨터의 설치·서비스 배포를 검증한 것으로 해석하지 않는다.

고정 컴파일러가 .NET 숫자 타입의 사용하지 않는 generic-math 멤버 메타데이터에서 C4679를 발생시키므로 C++/CLI 프로젝트에만 4679를 제외한다. 나머지는 /W4·경고 오류 처리, 링커도 경고 오류 처리를 사용한다. 불투명 핸들의 LNK4248은 억제하지 않고 unmanaged thunk 경계를 사용해 제거한다. 허용 예외는 작업 기록에 이유를 남기고 도구 이전 시 재검토한다.

## Alternatives

Host에서 vcxproj를 직접 ProjectReference하면 dotnet 빌드가 VC targets를 요구한다. 런타임 리플렉션 플러그인 로딩은 컴파일 시 타입 검증·일반 배포 흐름을 복잡하게 하므로 제외했다. 소스 링크나 Native DLL 중복 컴파일 대신 같은 Native DLL의 import library를 사용한다.

## Consequences

Host를 처음 빌드하기 전에 build-cppcli.ps1이 필요하다. 파일 참조는 자동 프로젝트 빌드가 아니므로 참조 검사에서 경로·Private 복사를 검증하고 verify가 DLL 해시·누락·publish 실행을 확인한다. 혼합 DLL은 Windows x64 및 MSVC 런타임에 종속된다. 파일 참조의 package lock 항목은 없으며 기존 NuGet 잠금 파일은 그대로 적용된다.

## Validation

Release/Debug 스크립트 빌드와 실제 cli 실행, Visual Studio MSBuild의 Release x64 솔루션 빌드로 순서를 확인한다. verify는 Host/테스트/publish의 DLL 해시, 각각의 혼합 DLL·ijwhost·Native DLL 누락 시 Setup 실패, publish 출력 실행을 검사한다. 기존 181개에 M6 36개를 추가해 필수 217개를 유지한다. [작업 기록](../../tasks/M06-cpp-cli-comparison.md)에 실제 결과를 연결한다.

## Links

- [공유 수명 결정](0015-cpp-cli-transport-and-shared-lifetime.md)
- [Microsoft C++/CLI .NET 제한·ijwhost·runtimeconfig](https://learn.microsoft.com/en-us/dotnet/core/porting/cpp-cli)
- [Microsoft generic-math와 C++/CLI 호환성](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/7.0/cpluspluscli-compiler-version)
