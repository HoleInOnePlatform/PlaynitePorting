# SteamLink.Core 빌드 의존성

두 workflow는 앱을 `PlaynitePorting/`, Backend를 형제 `Backend/`에 준비한다.
고정 버전은 `steam-link-core.lock.json`의 `b6f0716ebe482d0999b9311c75463ae308b44397`이다.
관리자는 Backend 저장소 contents:read 전용 fine-grained token을 `BACKEND_READ_TOKEN` secret으로 등록해야 한다.
checkout은 자격 증명을 저장하지 않으며, pull_request에서는 Backend checkout과 통합 빌드를 실행하지 않는다.
외부·내부 PR 모두 secret 없이 계약·설정 검사만 수행한다. 통합 빌드는 검토한 코드의 push에서 실행한다.
PR 검사 성공만으로 통합 빌드 성공을 의미하지 않는다. 머지 전 관리자가 신뢰하는 브랜치에서 통합 빌드 결과를 확인해야 한다.

로컬에서는 Backend 읽기 권한으로 위 리비전을 checkout하고 다음을 실행한다.

```powershell
./build/Build-SteamLink.ps1 -BackendDirectory ../Backend -OutputDirectory ./artifacts/SteamLink
./build/Build-HoleInOne.ps1 -OutputDirectory ./artifacts/Full
```

소스 세 파일의 SHA256 검증은 계속 필수다. Backend 권한이나 고정 리비전이 없으면 빌드를 완료할 수 없다.
2026-10-23 추가 확인: GitHub 연결은 404를 반환하지만 기존 로컬 Git 인증으로 고정 리비전을 fetch할 수 있었다. 기존 Backend 체크아웃은 바꾸지 않고 `git archive`로 `artifacts/review-backend-core`에 소스를 추출했다. 고정 해시 검증과 SteamLink publish가 성공했다.

## 머지 전 수동 CI

기본 브랜치에 수동 workflow를 등록하기 전에는 검토한 커밋을 저장소의 `ci/` 접두사 브랜치에 push하여 두 통합 빌드를 실행한다. 이 경로는 저장소 쓰기 권한을 가진 관리자가 검토한 코드에만 사용한다. 외부 PR 이벤트에는 적용되지 않는다.

두 workflow에 `workflow_dispatch`를 추가했다. GitHub Actions에서 검토한 브랜치를 선택해 각각 실행하면 Backend checkout과 전체 빌드도 수행한다. PR 이벤트는 계속 secret 없이 실행한다.

- 관리자가 `BACKEND_READ_TOKEN`을 등록한다. 값은 문서·로그·채팅에 넣지 않는다.
- 수동 실행 workflow 정의가 기본 브랜치에도 있어야 실행 메뉴를 사용할 수 있다. 아직 없다면 workflow 변경만 먼저 반영하는 준비 PR이 필요하다.
- `App refactoring checks`와 `V5 session prewarm`에서 검토한 앱 브랜치를 선택하고 실행한 커밋 SHA와 성공 링크를 기록한다.
- 신뢰하지 않는 외부 PR 브랜치에는 토큰이 사용되는 수동 빌드를 실행하지 않는다.

토큰 등록 및 push 후 두 통합 CI가 성공했다: [App](https://github.com/HoleInOnePlatform/PlaynitePorting/actions/runs/39903447865), [V5](https://github.com/HoleInOnePlatform/PlaynitePorting/actions/runs/39903447775). 검증 커밋은 f189cfe9072c69b04acc62589a62fe22d6a44e56이며 운영 배포는 수행하지 않았다.
