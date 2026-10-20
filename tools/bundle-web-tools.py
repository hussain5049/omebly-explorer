"""Copies the Omebly Tools PDF tools from the website build (website/dist) into the Explorer, so they run inside it
with no internet. Only the PDF tools are taken (the AI ones are left out on purpose: no AI in Omebly Explorer),
with the code they load and the files they need (the PDF engine, qpdf, fonts, the text-reading model).
Writes src/OmeblyExplorer/WebTools/site/... and src/OmeblyExplorer/WebTools/tools.json.

    python tools/bundle-web-tools.py "D:\\my latest project\\Omebly\\website\\dist"
"""
import json, os, re, shutil, sys

DIST = sys.argv[1] if len(sys.argv) > 1 else r'D:\my latest project\Omebly\website\dist'
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src', 'OmeblyExplorer', 'WebTools')
LEAVE_OUT = {'ai-summarizer', 'chat-with-pdf', 'translate-pdf',            # AI: not in Omebly Explorer
             'background-remover', 'image-editor', 'photo-enhancer', 'palette-generator', 'video-editor',
             'video-frame-generator', 'downloader', 'meet', 'credits'}     # not PDF tools
ASSET_DIRS = ['pdfjs', 'qpdf', 'icc', 'ocr', 'ort', 'fonts', 'dict', 'tesseract']
ASSET_FILES = ['brand/omebly-logo-horizontal-dark.svg', 'favicon-32.png', 'favicon.ico', 'models/ppocr-v6-small-det.onnx', 'models/ppocr-v6-small-rec.onnx']
GROUPS = [
    ('Organise', ['merge-pdf', 'split-pdf', 'extract-pages', 'remove-pages', 'organise-pdf', 'rotate-pdf', 'pages-per-sheet', 'resize-pdf', 'crop-pdf', 'batch-pdf']),
    ('Convert from PDF', ['pdf-to-word', 'pdf-to-excel', 'pdf-to-powerpoint', 'pdf-to-jpg', 'pdf-to-png', 'pdf-to-text', 'pdf-to-markdown', 'pdf-to-epub', 'pdf-to-pdfa', 'extract-images']),
    ('Convert to PDF', ['word-to-pdf', 'excel-to-pdf', 'powerpoint-to-pdf', 'jpg-to-pdf', 'image-to-pdf', 'html-to-pdf', 'markdown-to-pdf', 'text-to-pdf', 'epub-to-pdf', 'scan-to-pdf']),
    ('Edit', ['pdf-editor', 'sign-pdf', 'add-watermark', 'add-page-numbers', 'header-footer-pdf', 'bates-numbering', 'bookmarks-pdf', 'edit-pdf-metadata', 'pdf-forms', 'create-pdf-form', 'redact-pdf', 'remove-comments', 'flatten-pdf', 'grayscale-pdf']),
    ('Protect and fix', ['protect-pdf', 'unlock-pdf', 'compress-pdf', 'optimize-pdf-for-web', 'repair-pdf', 'ocr-pdf', 'compare-pdf']),
]

def read(p): return open(p, encoding='utf-8', errors='ignore').read()

tools = [t for t in sorted(os.listdir(os.path.join(DIST, 'tools'))) if os.path.isfile(os.path.join(DIST, 'tools', t, 'index.html')) and t not in LEAVE_OUT]
files = set()
ref = re.compile(r'''(?:import\(|from\s*|import\s*)["'](\.{0,2}/?[^"']+\.(?:js|mjs|css))["']''')
def add(rel):
    if rel in files: return
    p = os.path.join(DIST, rel.lstrip('/').replace('/', os.sep))
    if not os.path.isfile(p): return
    files.add(rel)
    if not rel.endswith(('.js', '.mjs', '.css')): return
    txt = read(p)
    for m in ref.findall(txt):
        add(m if m.startswith('/') else '/' + os.path.normpath(os.path.join(os.path.dirname(rel.lstrip('/')), m)).replace(os.sep, '/'))
    for m in re.findall(r'''["'`](?:\.?/)?(_astro/[A-Za-z0-9_.@-]+\.(?:js|mjs|css|wasm))''', txt): add('/' + m)
    for m in re.findall(r'''["'`]\./([A-Za-z0-9_.@-]+\.(?:js|mjs|css|wasm))["'`]''', txt): add('/_astro/' + m)
    for m in re.findall(r'''url\((/_astro/[^)"']+)\)''', txt): add(m)

info = []
for t in tools:
    html = read(os.path.join(DIST, 'tools', t, 'index.html'))
    files.add(f'/tools/{t}/index.html')
    for m in re.findall(r'''(?:src|href)=["'](/_astro/[^"']+)["']''', html): add(m)
    for m in re.findall(r'''["'](/_astro/[^"']+\.(?:js|css))["']''', html): add(m)
    h1 = re.search(r'<h1[^>]*>(.*?)</h1>', html, re.S)
    title = re.sub(r'<[^>]+>', '', h1.group(1)).strip() if h1 else t
    inp = re.search(r'<input[^>]*type="file"[^>]*accept="([^"]*)"[^>]*>', html)
    accept = [a.strip().lower() for a in (inp.group(1) if inp else '').split(',') if a.strip()]
    multiple = bool(inp and 'multiple' in inp.group(0))
    group = next((g for g, names in GROUPS if t in names), 'More')
    name = t.replace('-', ' ').replace('pdf', 'PDF').replace('jpg', 'JPG').replace('png', 'PNG').replace('epub', 'EPUB').replace('pdfa', 'PDF/A').replace('html', 'HTML')
    name = name[0].upper() + name[1:]
    info.append({'slug': t, 'name': name, 'title': title, 'accept': accept, 'multiple': multiple, 'group': group})

site = os.path.join(OUT, 'site')
if os.path.isdir(site): shutil.rmtree(site)
for f in files:
    dst = os.path.join(site, f.lstrip('/').replace('/', os.sep)); os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(os.path.join(DIST, f.lstrip('/').replace('/', os.sep)), dst)
for d in ASSET_DIRS:
    if os.path.isdir(os.path.join(DIST, d)): shutil.copytree(os.path.join(DIST, d), os.path.join(site, d), dirs_exist_ok=True)
for f in ASSET_FILES:
    if os.path.isfile(os.path.join(DIST, f)): os.makedirs(os.path.join(site, os.path.dirname(f)), exist_ok=True); shutil.copy2(os.path.join(DIST, f), os.path.join(site, f))
json.dump(info, open(os.path.join(OUT, 'tools.json'), 'w', encoding='utf-8'), indent=1)
total = sum(os.path.getsize(os.path.join(r, n)) for r, _, ns in os.walk(site) for n in ns)
print(f'{len(info)} tools, {len(files)} code files, {total / 1e6:.0f} MB in {site}')
