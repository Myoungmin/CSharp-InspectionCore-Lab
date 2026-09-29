# DEV03 — Windows CI와 검증 증거 보관

상태: Verified. 기준 커밋: d39d077. B04 구현 작업이며 제품 마일스톤은 M6를 유지한다. 원격 실패 확인 완료일: 2026-09-30 (KST).

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

- 로컬 `scripts/ci.ps1` 전체 검증: `20260929T131845Z-88be30bc`, Passed. Core 94 + Integration 123 = 217개, skip 0. 기준 `d39d077`, 소스 SHA256 `D8B5623E1B73AFA6AF0BF0B4A8B593379954855CC55B9AB503A67D576B3EB6F2`.
- 사전 검사 실패: 로컬의 무시된 JAR만 임시 손상시키고 복원했다. SHA256 불일치에서 exit 1, `result.json`의 Preflight 실패와 `environment.json` 원인을 확인했고 전체 검증이 시작되지 않았다. 증거는 `artifacts/dev03-preflight-failure*`에 남겼다.
- workflow 정적 검사: actionlint 1.7.12 통과. 자체 리뷰에서 고정 action SHA, 읽기 전용 권한, 인증 정보 잔류 방지, 실패 종료 코드 전파와 업로드 범위를 확인했다. 별도 Codex 읽기 전용 리뷰 세션은 실행하지 않았다.
- [원격 성공 실행 36574501830](https://github.com/Myoungmin/CSharp-InspectionCore-Lab/actions/runs/36574501830): 새 Windows runner, commit `551c9feb158f340ce77ed919471c84562f018575`. 검증 ID `20260929T132316Z-4352d960`, 소스 SHA256 `F231B2702F30C1C5FCF52CC405BEB25735DDACEA4E9A4CC59EE98981E65E7299`. summary와 다운로드한 두 TRX에서 94 + 123개 통과, 실패/미실행 0을 확인했다. 필수 Native 34, C++/CLI 34, IPC 23과 publish·문서 검사도 통과했다.
- 성공 artifact `verification-36574501830-1` (ID `11036314833`)를 내려받아 manifest·summary·TRX·도구 정보·로그를 확인했다. ZIP SHA256 `EB224EF13CB3DFC2524C28E1302E05695DDFA857E51D265797F89EADF1859BEC`는 API digest와 일치한다. 원격 만료 예정일은 2026-10-13이며 영구 증거 저장소는 아니다.
- 원격 이미지 `win22 / 20260927.320.1`, SDK 9.0.305, MSVC toolset 폴더 14.44.35207의 compiler 19.44.35229.0, Windows SDK 10.0.26100.0, Java 11.0.32를 확인했다. PlantUML은 기존 pin과 일치한다. 컴파일러 파일의 servicing 버전은 로컬 19.44.35216.0과 다르므로 모든 도구 바이트가 동일하다고 주장하지 않는다.

- [의도한 원격 실패 36586596506](https://github.com/Myoungmin/CSharp-InspectionCore-Lab/actions/runs/36586596506): 별도 후보 `b8f5feef4895c72a36d2493e3a449d1ab6dcd3d1`의 README에 존재하지 않는 상대 링크만 추가했다. 검증 ID `20260929T145936Z-ae07976f`, 소스 SHA256 `0B3D40BBB0056B229094C17476ADC8F41B6146F9EA4DA382BF3077E739D6516A`. 테스트 217개는 통과하고 documentation 단계가 exit 1로 실패했다. job 실패, CI result의 Verification/exit 1, 문서 stderr의 정확한 깨진 링크를 대조했다. 의도한 오류는 구현 브랜치에 포함하지 않았다.
- 실패 artifact `verification-36586596506-1` (ID `11041718360`) 업로드 단계는 성공했다. 내려받은 ZIP SHA256 `137CA01A6E4AB24FCE52E0554E2907ECFA457CB040CD479DA98F0874FB192E08`가 API digest와 일치하며 실패 summary·manifest·TRX·stdout/stderr·환경 정보가 보관됐다. 실패 runner 이미지 `20260920.314.1`은 첫 성공 이미지와 다르지만 동일 pin 검사와 빌드를 통과했다.

문서 증거 갱신 후의 전체 검증 ID·소스 SHA256은 최종 기록 커밋 본문에 남긴다. 로컬 증거와 원격 artifact는 artifacts 아래에 보관하며 Git에 추가하지 않는다.

## 남은 일과 학습 확인

[B01 전환 보류, B03 제어기, B08 장기 운영](../docs/backlog.md)은 별도 작업이다. CI 변경은 `ci/windows-verification` 브랜치에서 검증했고 main 자동 병합이나 필수 상태 검사 설정은 수행하지 않는다. main에 통합하면 그 이후 main 변경에도 workflow가 적용된다. 필수 상태 검사는 `Windows x64 verification`을 대상으로 별도 저장소 운영 작업에서 적용할 수 있다. 사용자 학습 완료를 추정하지 않는다.

## 완료 확인

구현·로컬 전체 검증·원격 성공/실패·artifact 보관을 확인했다. ADR-0017·현재 아키텍처·CI 그림·작업 목록·후속 재검토 조건을 갱신했고 제품 계약과 217개 필수 사례는 유지했다. 자동 제어기·장기 운영과 사용자 학습 완료는 이번 결과에 포함하지 않는다.
