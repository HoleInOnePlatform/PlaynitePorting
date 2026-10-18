# 홀인원(HoleInOne) 개발 Task

Playnite를 기반으로 인스턴트 플레이와 Steam·Epic 통합 관리를 구현하는 포팅 프로젝트임. 이번 범위는 데스크톱만 포함함.

## 확정한 범위

- 설치·미설치 보유 게임을 모두 표시함. Steam·Epic 계정 연동을 내장함.
- 설치된 게임은 로컬 실행함. 미설치 게임은 클라우드 플레이와 로컬 다운로드를 함께 시작함.
- Steam 설치 확인 1회는 허용함. Epic은 Legendary로 확인 창 없이 다운로드함.
- 다른 게임도 클라우드 플레이와 다운로드를 지원함. 세이브 이전·자동 로컬 전환은 Steam판 슬더스2만 지원함.
- 사용자가 웹뷰를 닫으면 전환을 중단하고 다운로드는 계속함. 같은 게임을 다시 누르면 기존 창을 활성화함.
- 별도 범용 프레임워크나 자동 재시도·복구 기능은 추가하지 않음.

## 구현 Task

- [x] 상세 화면 외 설치 버튼을 플레이 버튼으로 대체하고 상세 화면에만 별도 설치 버튼 표시.
- [x] 플레이 요청에 게임 ID를 포함해 Backend 세션·Stream URL 확보, CefSharp 웹뷰 표시.
- [x] Steam 설치 호출·완료 감지, Epic 자동 다운로드·완료 확인 연결.
- [x] 슬더스2 모닥불 알림과 설치 완료를 확인한 뒤 세이브 요청, ready 상태 대기.
- [x] 준비된 GameLoader DLL·manifest 복사, 현재 Steam 계정의 모드 설정 활성화, pending 전환 파일 작성, 웹뷰 종료 후 로컬 실행.
- [x] 중복 클릭과 웹뷰 수동 종료 처리. 다른 게임은 자동 전환 제외.
- [x] Playnite 온라인 서비스·애드온 및 외부 확장 로딩 비활성화.
- [x] 원본 Playnite Steam·Epic 애드온 소스와 설정 화면을 내장하고 인증·라이브러리 가져오기·로컬 실행 연결.
- [x] Steam의 Playnite 서버 조회를 Steam 직접 조회로 교체. Epic 인증은 원본 애드온으로 처리하고 자동 다운로드 보조 도구에 연결.
- [x] 설정 화면, Legendary·GameLoader 준비 스크립트와 실행 문서 작성.
- [x] 사용자 설정 화면에서 Backend 주소·토큰·Epic 다운로드 폴더 입력 제거. 기존 내부 설정값을 유지하고 Steam·Epic 연동 설정만 제공.

체크는 코드 연결 완료를 뜻함. 실제 계정·AWS·게임 환경의 동작 확인 결과는 [HOLEINONE.md](HOLEINONE.md)에 별도로 기록함. 슬더스2 v0.107.1은 계정별 settings.save의 mod_settings.mods_enabled를 켜서 최초 동의 화면 없이 GameLoader를 로드함. 다른 설정은 보존하며, nomods 실행 옵션은 제거해야 함.

## 구현 Plan과 구조

1. 기존 게임 편집기의 플레이 진입점을 데스크톱에서 재정의하고, 설치된 게임은 기존 로컬 실행 경로를 사용함.
2. 원본 Steam·Epic 애드온 소스를 내장 모듈로 빌드·등록함. 인증·보유 게임 조회·설정 화면·런처 연동을 재사용하며 Steam의 Playnite 서버 의존성만 직접 조회로 제거함. Epic 자동 다운로드와 해당 설치본 관리에만 Legendary를 사용하고 원본 Epic 인증을 전달함.
3. `BackendConnection`으로 게임별 세션을 만들고 `InstantPlayCoordinator`에서 웹뷰와 다운로드를 시작함. 설치 감시는 기존 설치 컨트롤러에 맡겨 웹뷰 종료와 수명을 분리함.
4. 슬더스2만 `HandoffSession`으로 모닥불·설치·세이브 준비를 확인함. `GameLoader.pending.cfg`를 완성한 뒤 웹뷰를 닫고 로컬 게임을 실행함.
5. 설정·배포 도구를 연결하고 빌드·HTTP 전환·스토어 manifest 검증을 수행함.

Backend의 세션 생성 본문은 `{"gameId":"steam-2868840"}` 형식임. Epic은 `epic-<AppName>` 사용. [Backend Task](../Backend/TASKS.md)의 게임별 AWS 매핑과 HTTP 규격을 따름.
