"""生成网站 MiSans 切片：python scripts/split-site-font.py。

需要 fonttools[woff]==4.64.0 和 brotli；不安装依赖、不下载字体。
只保留网站实际会显示的字：网站源码里的文字、图鉴数据里的名字、广场上阵容名和昵称的常用字
（scripts/site-font-user-chars.txt）。用户写的简介、评论、短评用系统字体（site.css 的 .ugc），不进子集。
按页面切片，每个字只进第一个用到它的片，页面只下载自己用到的几片：
基础符号 → 官网和广场共用 → 官网 → 广场 → 阵容详情 → 其余游戏名字 → 产品界面演示（/demo）。
网站只用 400～700 字重，字重轴截到这一段。网站文字或图鉴改了就重跑，生成结果提交后无需构建时运行。
"""

import json
import re
from io import BytesIO
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src/ui/src/assets/fonts/MiSans-VF.woff2"
OUTPUT = ROOT / "src/plaza/site/src/assets/fonts/misans"
USER_CHARS = ROOT / "scripts/site-font-user-chars.txt"
WEIGHT = (400, 700)
TEXT_SUFFIXES = {".svelte", ".ts", ".html", ".json", ".css"}
# 代码注释不显示，不算进子集
COMMENTS = re.compile(r"/\*.*?\*/|<!--.*?-->|(?:^|(?<=\s))//[^\n]*", re.S | re.M)


def text_of(*paths):
    text = []
    for path in map(ROOT.joinpath, paths):
        files = [path] if path.is_file() else [
            p for p in path.rglob("*")
            if p.suffix in TEXT_SUFFIXES and not {"node_modules", "dist"} & set(p.parts)]
        text += [COMMENTS.sub("", f.read_text(encoding="utf-8")) for f in files]
    return "".join(text)


def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from strings(item)


def encode(points):
    font = TTFont(SOURCE, recalcTimestamp=False)
    options = subset.Options()
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.name_languages = ["*"]
    cutter = subset.Subsetter(options=options)
    cutter.populate(unicodes=points)
    cutter.subset(font)
    font = instancer.instantiateVariableFont(font, {"wght": WEIGHT})
    font.flavor = "woff2"
    stream = BytesIO()
    font.save(stream)
    return stream.getvalue()


def unicode_ranges(points):
    runs = []
    for point in sorted(points):
        if runs and point == runs[-1][1] + 1:
            runs[-1][1] = point
        else:
            runs.append([point, point])
    return ",".join(f"U+{a:X}" if a == b else f"U+{a:X}-{b:X}" for a, b in runs)


def main():
    with TTFont(SOURCE) as original:
        points = set(original.getBestCmap())
    catalog = json.loads((ROOT / "src/ui/src/lib/mock/catalog.json").read_text(encoding="utf-8"))

    def names(*kinds, field="name"):
        return "".join(item.get(field) or "" for kind in kinds for item in catalog[kind])

    site = text_of("src/site/src", "src/site/index.html", "src/site/about", "src/site/changelog",
                   "src/site/faq", "src/ui/release-notes")
    plaza = text_of("src/plaza/site/src", "src/plaza/site/index.html", "src/plaza/shared",
                    "src/plaza/server", "src/plaza/functions", "src/ui/src/lib/stages.ts")
    user = "".join(line for line in USER_CHARS.read_text(encoding="utf-8").splitlines()
                   if not line.startswith("#"))
    operators = catalog["operators"]
    detail = (names("operators", "equipment") + names("strategies", field="description")
              + "".join(x["name"] for op in operators for x in (op.get("skills") or []) + (op.get("modules") or [])))
    # 按顺序切片，每个字归第一个用到它的片
    groups = [
        # ASCII、拉丁补充和常用中西文标点，整段保留
        ("basic", {p for p in points if p <= 0xFF or 0x2000 <= p <= 0x206F
                   or 0x3000 <= p <= 0x303F or 0xFF00 <= p <= 0xFFEF}),
        # 官网和广场都有的字，加上两边都显示的盟约、策略名；阵容名里官网也有的字放这里，广场就不用再下官网那片
        ("common", set(site) & set(plaza + user) | set(names("covenants", "strategies"))),
        ("site", set(site)),
        ("plaza", set(plaza + user)),
        # 阵容详情页：干员、装备、技能、模组名和策略说明
        ("detail", set(detail)),
        ("game", set(names("bosses", "enemyTypes", "enemies", "bounties") + names("strategies", field="initiator"))),
        # /demo 是助手界面的构建产物，显示助手界面文字和整份图鉴
        ("demo", set(text_of("src/ui/src") + "".join(strings(catalog)))),
    ]

    OUTPUT.mkdir(parents=True, exist_ok=True)
    css = ["/* 由 scripts/split-site-font.py 生成；按页面切片，只含网站会显示的字。 */"]
    written, seen = set(), set()
    for name, chars in groups:
        members = sorted({ord(c) if isinstance(c, str) else c for c in chars} & points - seen)
        seen.update(members)
        data = encode(members)
        filename = f"misans-{name}.woff2"
        (OUTPUT / filename).write_bytes(data)
        written.add(filename)
        css.append("@font-face {\n"
                   "  font-family: 'MiSans';\n"
                   f"  src: url('./{filename}') format('woff2');\n"
                   f"  font-weight: {WEIGHT[0]} {WEIGHT[1]};\n"
                   "  font-display: swap;\n"
                   f"  unicode-range: {unicode_ranges(members)};\n"
                   "}")
        print(f"{filename}: {len(members)} chars, {len(data) / 1024:.1f} KiB")
    for old in OUTPUT.glob("misans-*.woff2"):
        if old.name not in written:
            old.unlink()
    (OUTPUT / "index.css").write_text("\n\n".join(css) + "\n", encoding="utf-8")
    print(f"{len(written)} subsets; {len(seen)} Unicode characters; wght {WEIGHT[0]} {WEIGHT[1]}")


if __name__ == "__main__":
    main()
