# 문서 유지 규칙

문서는 현재 구현과 후속 계획을 구분한다. 새 모듈·책임 변경은 architecture, 포트·소유권 변경은 core-class, 호출 순서 변경은 run-sequence에 반영한다. 상태 머신·Native 종료 그림은 M3에서 실제 구현과 함께 추가한다.

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
