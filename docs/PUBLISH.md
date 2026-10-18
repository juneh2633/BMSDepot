# GitHub에 올리기

## 빈 저장소 만들기

GitHub에서 `BMSDepot` 등의 이름으로 빈 저장소를 만듭니다. 이 폴더에 이미 준비된 README, LICENSE, .gitignore가 있으므로 GitHub 생성 화면에서는 추가하지 않습니다.

현재 폴더에는 로컬 Git 저장소만 준비합니다. 원격 주소 등록·push·공개는 별도 단계입니다. 저장소 URL과 게시 의사를 확인한 뒤 진행하세요.

GitHub Desktop을 사용한다면 이 로컬 저장소를 추가하고 원하는 GitHub 저장소로 게시할 수 있습니다. 터미널에서는 로컬 커밋을 만든 뒤 원격 `origin`을 등록하고 `main`을 push합니다. 인증 정보는 파일에 기록하지 마세요.

## 자동 빌드와 릴리스 초안

- `main` push와 PR: Windows 패키지를 빌드하고 Actions artifact로 보관합니다.
- `v0.1.0-alpha.1` 같은 버전 태그 push: 빌드 후 **Draft + Prerelease** 릴리스를 만듭니다.
- 릴리스 초안의 안내·첨부 파일을 확인하고 GitHub에서 직접 공개합니다.
- 태그 작업을 재실행할 때 이미 같은 초안이 있으면 생성 단계가 실패할 수 있습니다. 기존 초안을 확인하고 중복 게시하지 마세요.

`artifacts`, 실행 파일, 설치 파일은 Git에 올리지 않습니다. 패키지는 GitHub Releases에 첨부합니다. 소스와 함께 커밋된 `next/catalog.json`은 외부 메타데이터이며, 원곡 데이터는 포함하지 않습니다.

워크플로는 일반 빌드에 읽기 권한, 릴리스 초안 작업에만 저장소 쓰기 권한을 부여합니다. 자동 기능은 GitHub에서 아직 실행하지 않은 상태입니다.

참고: [GitHub .NET 빌드 안내](https://docs.github.com/en/actions/tutorials/build-and-test-code/net), [워크플로 권한](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax), [gh release create](https://cli.github.com/manual/gh_release_create).

## 공개 전 확인할 실제 동작

일반 OpenLR2에서 새 난이도표 경로 등록 → 곡 다운로드 → F8 반영 → 플레이를 확인하세요. 취소·실패·캐시 정리도 실제 확인 후 릴리스 노트의 검증 범위를 갱신하세요. 코드 서명 인증서는 현재 포함하지 않습니다.
