# 문서 유지 규칙

작업은 [템플릿과 공통 완료 기준](../tasks/README.md)에 따라 기록한다. 테스트 계약 변경은 [검증 대응표](verification-map.md), 미해결·보류 사항은 [재검토 목록](backlog.md)을 갱신한다. 필수 테스트 사례의 메타데이터는 문서 검사, 실제 실행 여부는 전체 검증에서 확인한다.

문서는 현재 구현과 후속 계획을 구분한다. 새 모듈·책임 변경은 architecture, Core 포트·소유권 변경은 core-class, Native 핸들 소유권은 native-interop, 호출 순서 변경은 run-sequence에 반영한다. 현재 다이어그램은 참조 관계·실행 상태·관리 취소와 종료를 포함한 12개다. B04의 ci-verification은 도구 준비·전체 검증·성공/실패 증거 보관을 설명한다. M6의 cpp-cli-comparison은 두 ABI 호출 방식과 공유 수명 코드를 구분한다. M5는 SQLite 트랜잭션·독립 조회·잠금 밖 진단과 완료 게시를 storage-diagnostics에 설명한다. M4는 별도 Client와 프레이밍·재접속·접수 재전송을 ipc-sequence에 설명한다. M3c는 자동 예약·중지와 현재 실행 취소를 auto-sequence에 설명한다. M3b에서 Native 비동기 작업·콜백 join과 종료 실패 시 자원 보존을 반영했다. 이후 상태·수명 계약이 바뀌면 같은 그림과 ADR을 함께 갱신한다.

## 아키텍처 결정

프로젝트 책임·참조, 공개 계약, 완료·취소, 소유권·수명, ABI·IPC, 저장, 플랫폼·도구 체계를 결정하거나 변경하면 [ADR 작성 절차](adr/README.md)에 따라 결정 하나당 파일 하나를 추가한다. [템플릿](adr/template.md)의 배경·결정·대안·영향·검증을 작성하고 목록을 갱신한다. 기존 결정을 바꾸면 새 ADR에서 이전 기록을 연결하고 이전 기록은 Superseded로 표시한다. 기존 이유와 대안은 보존한다.

작업 기록에 적용한 ADR 또는 새 ADR이 불필요한 이유를 남긴다. 코드 리뷰에서 결정 누락과 구현의 일치를 확인하며 관련 아키텍처·다이어그램과 같은 변경 묶음에 포함한다. verify.ps1의 문서 단계는 ADR 번호·상태·필수 항목·목록·상대 링크를 검사한다. 이 형식 검사는 새로운 아키텍처 결정이 필요한지 자동 판단하지 않는다.

## 다이어그램과 링크

프로젝트 참조가 바뀌면 허용 규칙을 검토하고 다음 명령으로 평가된 참조 그림을 생성한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-architecture.ps1 -Update
```

PlantUML 소스를 수정한 뒤 다음 명령으로 SVG를 갱신한다. 소스와 SVG를 함께 커밋한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-docs.ps1 -Update
```

`-Update` 없이 실행하면 임시 빈 출력 폴더에서 다시 생성해 저장소 SVG와 비교한다. 구문 오류, 누락·낡은 SVG, 깨진 상대 파일 링크, 클래스 그림에 적힌 Core 타입의 누락을 실패로 처리한다. 이름 존재 검사는 단순 선언 확인이며 전체 의미 분석은 아니다. 호출 순서와 소유권의 정확성은 코드·테스트 리뷰로 확인한다.

PlantUML JAR 버전·해시와 Smetana 엔진은 toolchain.json으로 고정한다. 도구 업그레이드 시 해당 설정과 SVG를 함께 변경한다. 삭제된 소스에 대응하는 SVG는 변경 목록에서 명시적으로 제거한다.

실습 기록은 코드의 구현·실제 검증·사용자의 학습 판단을 분리한다. 이번 구현만으로 외부 학습 기록의 회차를 완료 처리하지 않는다.
