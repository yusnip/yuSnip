# -*- coding: utf-8 -*-
"""
yuSnip 官网更新日志同步脚本
将「更新记录.txt」转换为与 docs.html 视觉一致的 changelog.html 及 data/changelog.json。
"""

import argparse
import json
import os
import re


def parse_changelog_txt(txt_path: str) -> list[dict]:
    if not os.path.exists(txt_path):
        raise FileNotFoundError(f"更新记录不存在: {txt_path}")

    with open(txt_path, "r", encoding="utf-8", errors="replace") as f:
        lines = f.read().splitlines()

    releases: list[dict] = []
    cur_rel = None
    cur_cat = None
    cur_item = None

    date_re = re.compile(
        r"^(?:###\s*更新日志\s*[（\(]([\d\-]+)[）\)](?:\s*[·•\-]\s*(v?[\d.]+))?|(\d{4}-\d{2}-\d{2}))\s*$"
    )
    cat_re = re.compile(r"^(?:####\s*|[【\[])(.*?)(?:[】\]])?$")

    for raw_line in lines:
        line = raw_line.strip()
        if not line or line.startswith("="):
            continue

        dm = date_re.match(line)
        if dm:
            ds = dm.group(1) or dm.group(3)
            ver = dm.group(2)
            title = f"更新日志（{ds}）" + (f" · {ver}" if ver else "")
            cur_rel = {
                "date": ds,
                "id": "v-" + ds.replace("-", ""),
                "title": title,
                "categories": [],
            }
            if ver:
                cur_rel["version"] = ver.lstrip("v")
            releases.append(cur_rel)
            cur_cat = cur_item = None
            continue

        if not cur_rel:
            continue

        is_h4 = line.startswith("#### ")
        cm = cat_re.match(line)
        if cm and (is_h4 or any(k in line for k in ("新增", "优化", "修复", "改进"))):
            cn = cm.group(1).strip()
            if "新增" in cn:
                btype = "feat"
            elif "修复" in cn:
                btype = "fix"
            else:
                btype = "opt"
            cur_cat = {"name": cn, "type": btype, "items": []}
            cur_rel["categories"].append(cur_cat)
            cur_item = None
            continue

        if not cur_cat:
            continue

        im = re.match(r"^(?:[-*•]|\d+[.、])\s*(.*)$", line)
        if im:
            t_raw = im.group(1).strip()
            m_lead = re.match(r"^\*\*([^\*]+)\*\*[:：]?\s*(.*)$", t_raw)
            if m_lead:
                t_part, b_part = m_lead.group(1).strip(), m_lead.group(2).strip()
                cur_item = {
                    "title": t_part,
                    "body": b_part if b_part else t_part,
                    "details": [],
                }
            else:
                col_idx = -1
                for ch in ("：", ":"):
                    pos = t_raw.find(ch)
                    if pos != -1 and (col_idx == -1 or pos < col_idx):
                        col_idx = pos
                if col_idx != -1 and col_idx < 30:
                    cur_item = {
                        "title": t_raw[:col_idx].strip(),
                        "body": t_raw[col_idx + 1 :].strip(),
                        "details": [],
                    }
                else:
                    cur_item = {"title": "", "body": t_raw, "details": []}
            cur_cat["items"].append(cur_item)
            continue

        if cur_item:
            sm = re.match(r"^[-*•]\s*(.*)$", line)
            cur_item["details"].append(sm.group(1).strip() if sm else line)

    if not releases:
        raise ValueError("未能从更新记录中解析出任何版本条目。")
    return releases


def fmt(s: str) -> str:
    if not s:
        return ""
    s = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", s)
    q = chr(96)
    return re.sub(q + r"([^" + q + r"]+)" + q, r"<code>\1</code>", s)


def build_changelog(releases: list[dict], docs_html_path: str) -> str:
    with open(docs_html_path, "r", encoding="utf-8") as f:
        doc = f.read()

    tocs = []
    for idx, r in enumerate(releases):
        num = f"{idx + 1:02d}"
        subs = [
            f'<a href="#{r["id"]}-h{cidx + 1}" class="docs-toc__sub">{idx + 1}.{cidx + 1} {c["name"]} ({len(c["items"])})</a>'
            for cidx, c in enumerate(r["categories"])
        ]
        sub_div = f'<div class="docs-toc__subs">{"".join(subs)}</div>' if subs else ""
        badge = ' <span class="doc-toc__badge-latest">LATEST</span>' if idx == 0 else ""
        tocs.append(
            f'<a href="#{r["id"]}" class="docs-toc__item" data-section="{r["id"]}">'
            f'<span class="docs-toc__num">{num}</span>'
            f'<span class="docs-toc__text">{r["date"]}</span>{badge}</a>{sub_div}'
        )
    toc_html = "\n".join(tocs)

    secs = []
    for idx, r in enumerate(releases):
        num = f"{idx + 1:02d}"
        badge = ' <span class="doc-section__badge doc-section__badge--latest">LATEST</span>' if idx == 0 else ""
        ver_tag = f' · v{r["version"]}' if r.get("version") else ""
        c_counts = [f"{len(c['items'])} 项{c['name']}" for c in r["categories"]]
        intro = f"本版本于 {r['date']} 正式发布，累计包含 {'、'.join(c_counts)}。"
        cat_blocks = []
        for cidx, c in enumerate(r["categories"]):
            cid = f"{r['id']}-h{cidx + 1}"
            lis = []
            for it in c["items"]:
                t, b, d = fmt(it.get("title", "")), fmt(it.get("body", "")), it.get("details", [])
                lead = f"<strong>{t}</strong>：{b}" if t and b else (f"<strong>{t}</strong>" if t else b)
                d_p = "".join([f"<p>{fmt(x)}</p>" for x in d]) if d else ""
                lis.append(f"          <li>{lead}{d_p}</li>")
            cat_blocks.append(
                f'        <h3 class="doc-h3" id="{cid}">{idx + 1}.{cidx + 1} {c["name"]}</h3>\n'
                f'        <ul class="doc-list">\n' + "\n".join(lis) + "\n        </ul>"
            )
        secs.append(
            f'      <section class="doc-section" id="{r["id"]}" data-section="{r["id"]}">\n'
            f'        <div class="doc-section__head">\n'
            f'          <span class="doc-section__num">{num}</span>\n'
            f'          <h2 class="doc-section__title">{r["date"]}{ver_tag}{badge}</h2>\n'
            f"        </div>\n"
            f'        <div class="doc-section__body">\n'
            f'          <p class="doc-intro">{intro}</p>\n'
            + "\n\n".join(cat_blocks)
            + "\n        </div>\n      </section>"
        )
    secs_html = "\n\n".join(secs)

    dot = chr(183)
    hero_html = f"""      <header class="docs-hero">
        <p class="docs-hero__eyebrow">yuSnip {dot} Release Notes &amp; Changelog</p>
        <h1 class="docs-hero__title">更新日志</h1>
        <p class="docs-hero__lead">
          <strong>记录 yuSnip 的每一个版本迭代与演进细节。</strong>坚持轻量、无感与极致性能，每一次更新都凝聚着对细节的雕琢。快捷键与行为以最新发布版本为准；旧版本和已有个性化配置可能不同，请结合实际软件版本查看。
        </p>
        <div class="docs-hero__meta">
          <span><em>最新版本</em>{releases[0]['date']}</span>
          <span><em>累计发布</em>{len(releases)} 个版本</span>
          <span><em>记录区间</em>{releases[-1]['date']} 至 {releases[0]['date']}</span>
        </div>
        <div class="docs-hero__notice">
          <span class="docs-hero__notice-icon" aria-hidden="true">
            <svg viewBox="0 0 16 16" width="13" height="13" fill="currentColor">
              <path d="M8 1.5a6.5 6.5 0 100 13 6.5 6.5 0 000-13zM0 8a8 8 0 1116 0A8 8 0 010 8z"/>
              <path d="M7.25 7.25a.75.75 0 011.5 0v3.5a.75.75 0 01-1.5 0v-3.5zm.75-3a.875.875 0 100 1.75.875.875 0 000-1.75z"/>
            </svg>
          </span>
          <p class="docs-hero__notice-text">
            <strong>更新提醒</strong> yuSnip 支持检测并获取最新版本。如果在更新后遇到热键冲突或显示异常，可先在「设置 → 恢复默认」中核对配置。
          </p>
        </div>
      </header>"""

    out = doc
    out = re.sub(r"<title>.*?</title>", "<title>更新日志 | yuSnip</title>", out, count=1, flags=re.DOTALL)
    out = re.sub(
        r'<meta name="description" content=".*?" />',
        f'<meta name="description" content="yuSnip 更新日志：完整收录各版本新增功能、体验优化与问题修复细节。{releases[0]["date"]} 最新更新。" />',
        out,
        count=1,
    )
    out = re.sub(r'<meta property="og:title" content=".*?" />', '<meta property="og:title" content="yuSnip 更新日志" />', out, count=1)
    out = re.sub(
        r'<meta property="og:description" content=".*?" />',
        '<meta property="og:description" content="记录 yuSnip 的每一个版本迭代与演进细节。" />',
        out,
        count=1,
    )
    out = out.replace("<em>docs</em>", "<em>changelog</em>")
    out = out.replace(
        'placeholder="搜索文档（功能 / 快捷键 / 设置项 / 命令）…"',
        'placeholder="搜索更新日志（功能 / 优化 / 修复 / 版本）…"',
    )
    out = out.replace(
        '<a href="docs.html" class="docs-nav__action is-active" aria-current="page">',
        '<a href="docs.html" class="docs-nav__action">',
    )
    out = out.replace(
        '<a href="changelog.html" class="docs-nav__action">',
        '<a href="changelog.html" class="docs-nav__action is-active" aria-current="page">',
    )
    out = out.replace('<p class="docs-sidebar__label">文档导航</p>', '<p class="docs-sidebar__label">版本目录</p>')
    out = re.sub(
        r'<nav class="docs-toc" id="docsToc">.*?</nav>',
        '<nav class="docs-toc" id="docsToc">\n' + toc_html + "\n        </nav>",
        out,
        count=1,
        flags=re.DOTALL,
    )

    stag, etag = '<main class="docs-content">', '<footer class="docs-content__footer">'
    s_idx, e_idx = out.find(stag), out.find(etag)
    if s_idx != -1 and e_idx != -1:
        out = out[: s_idx + len(stag)] + "\n\n" + hero_html + "\n\n" + secs_html + "\n\n      " + out[e_idx:]

    if "css/changelog.css" not in out:
        out = out.replace("</head>", '  <link rel="stylesheet" href="css/changelog.css" />\n</head>', 1)
    return out


def main() -> None:
    parser = argparse.ArgumentParser(description="生成 yuSnip 官网 changelog.html 和 changelog.json")
    parser.add_argument("--txt", required=True, help="更新记录.txt 路径")
    parser.add_argument("--docs-html", required=True, help="docs.html 模板路径")
    parser.add_argument("--out-html", required=True, help="输出的 changelog.html 路径")
    parser.add_argument("--out-json", required=True, help="输出的 changelog.json 路径")
    args = parser.parse_args()

    rels = parse_changelog_txt(args.txt)
    os.makedirs(os.path.dirname(os.path.abspath(args.out_json)), exist_ok=True)
    with open(args.out_json, "w", encoding="utf-8") as f:
        json.dump(rels, f, ensure_ascii=False, indent=2)

    html = build_changelog(rels, args.docs_html)
    with open(args.out_html, "w", encoding="utf-8") as f:
        f.write(html)
    print(f"已生成更新日志：{len(rels)} 个版本（最新 {rels[0]['date']}）")


if __name__ == "__main__":
    main()
