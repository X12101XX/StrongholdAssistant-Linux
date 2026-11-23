"""给官网截图生成小尺寸：python src/site/scripts/shot-sizes.py。

src/assets/shots/ 里的原图（shoot.mjs 拍的 2 倍图）不动，另存 名字-宽度.webp 几档，
宽度表写进 widths.json；页面按图片实际显示宽度和屏幕像素比挑一档（lib.ts 的 shot）。
重拍截图后重跑。另把 static/icon-192.png 缩成顶栏 logo 用的 64 像素 webp。需要 Pillow。
"""

import json
from pathlib import Path

from PIL import Image

SITE = Path(__file__).resolve().parents[1]
SHOTS = SITE / "src/assets/shots"
QUALITY = 80


def main():
    for old in SHOTS.glob("*-*[0-9].webp"):
        if old.stem.rsplit("-", 1)[1].isdigit():
            old.unlink()
    widths = {}
    for path in sorted(SHOTS.glob("*.webp")):
        with Image.open(path) as image:
            full = image.width
            # 原图的约 3/8、1/2、3/4：手机截图 760 宽对应 280、400、560
            sizes = sorted({round(full * k / 10) * 10 for k in (0.375, 0.53, 0.74)})
            for width in sizes:
                height = round(image.height * width / full)
                image.resize((width, height), Image.LANCZOS).save(
                    SHOTS / f"{path.stem}-{width}.webp", quality=QUALITY, method=6)
        widths[path.stem] = sizes + [full]
        print(path.stem, widths[path.stem])
    (SHOTS / "widths.json").write_text(json.dumps(widths, indent=1) + "\n", encoding="utf-8")
    with Image.open(SITE / "static/icon-192.png") as icon:
        icon.resize((64, 64), Image.LANCZOS).save(SITE / "static/icon-64.webp", quality=90, method=6)


if __name__ == "__main__":
    main()
