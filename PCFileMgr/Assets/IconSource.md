# PCFileMgr 앱 아이콘

내장 `image_gen`으로 생성한 PNG를 프로젝트에 복사하고 `Build-Icon.ps1`로 크기 변환과 ICO 포맷 변환만 적용했습니다. 원본의 알파 투명도를 보존했습니다.

- `AppIcon-source.png`: 생성 원본
- `AppIcon.png`: 앱 헤더용 256px
- `AppIcon.ico`: EXE·창·작업 표시줄용 16, 20, 24, 32, 40, 48, 64, 96, 128, 256px

최종 생성 프롬프트:

> Use case: logo-brand. Asset type: Windows desktop application icon for PCFileMgr, a local disk space analyzer with a dark navy and mint interface. Create one polished, distinctive, very simple square app icon. Subject: a mint-green file folder whose front face is a clear treemap of three or four large rectangular storage blocks of different sizes, integrated into one compact emblem. Subtle crafted depth with clean crisp edges, nearly frontal, not a scene or mockup. Dark navy rounded-square backing, luminous mint and teal blocks, one restrained blue accent. Strong silhouette and generous solid shapes readable at 16 and 32 pixels. Centered emblem fills roughly 88 percent of the square canvas, small consistent outside margin. Transparent background outside the rounded-square icon, actual alpha. No text, no letters, no numbers, no watermark, no tiny circuitry, no magnifying glass, no extra symbols, no device mockup. Square 1024x1024 production icon.
