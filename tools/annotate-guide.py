"""Turns the raw screenshots (docs/guide/raw, from tests/Shots) into the guide's pictures: a mint box around each
part, a numbered marker with an arrow pointing at it, and a strip under the picture that says what each number is.
Writes docs/guide/<name>.jpg (for GitHub and the Omebly Tools page).

    python tools/annotate-guide.py
"""
import math, os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, '..', 'docs', 'guide', 'raw')
OUT = os.path.join(HERE, '..', 'docs', 'guide')
MINT, INK, PAPER = (46, 242, 179), (8, 17, 16), (232, 245, 240)
F = lambda size, bold=True: ImageFont.truetype(r'C:\Windows\Fonts\seguisb.ttf' if bold else r'C:\Windows\Fonts\segoeui.ttf', size)
WIDTH = 1800

# name: [((x0, y0, x1, y1) box in raw pixels, (bx, by) marker centre, "what it is"), ...]
SPEC = {
 'home': [((15, 8, 450, 88), (540, 48), 'Tabs: many folders in one window'),
          ((300, 92, 1222, 152), (1150, 215), 'Path bar: click a folder or type a path'),
          ((1235, 92, 1812, 152), (1700, 215), 'Search the whole PC'),
          ((18, 268, 445, 495), (490, 380), 'Your pinned folders'),
          ((520, 392, 1562, 540), (1640, 465), 'Drives and free space'),
          ((520, 628, 2312, 702), (1000, 594), 'Quick actions'),
          ((520, 1085, 2296, 1352), (1000, 1052), 'Files you opened lately')],
 'details': [((300, 92, 1222, 152), (800, 205), 'Where you are'),
             ((1895, 92, 2120, 152), (1850, 40), 'Change the view'),
             ((520, 180, 1960, 222), (2050, 202), 'Click a column to sort; right-click for more columns'),
             ((18, 560, 445, 950), (490, 755), 'Your Windows folders'),
             ((1840, 1442, 2380, 1488), (1780, 1462), 'What is selected, and its size')],
 'tabs': [((15, 8, 1140, 88), (1250, 48), 'Each tab is a folder; drag a tab out for its own window'),
          ((1150, 18, 1200, 72), (1205, 120), 'New tab (Ctrl+T)')],
 'menu': [((1420, 646, 1812, 678), (1880, 662), 'Make a ZIP'),
          ((1420, 702, 1812, 734), (1880, 718), 'All the PDF tools'),
          ((1420, 758, 1812, 790), (1880, 774), 'Colour tags'),
          ((1420, 988, 1812, 1020), (1880, 1004), 'Lock, shred, permissions and more'),
          ((1420, 1340, 1812, 1372), (1880, 1356), "Windows' own full menu")],
 'pdf-menu': [((1420, 700, 1810, 736), (1360, 718), 'PDF tools, on any PDF'),
              ((1815, 705, 2125, 925), (1970, 975), 'Grouped by what you want to do'),
              ((2118, 752, 2342, 1312), (2230, 1350), 'Pick one: it opens in its own window')],
 'pdf-tool': [((445, 581, 887, 983), (380, 640), 'Your files are already loaded'),
              ((892, 430, 1355, 488), (1120, 380), 'Add more or start again'),
              ((1433, 537, 1935, 619), (1990, 680), 'One click: the result is saved next to the original')],
 'viewer-picture': [((1530, 148, 2262, 202), (1440, 175), 'Zoom, rotate, copy, delete, open, full screen'),
                    ((2170, 765, 2252, 842), (2210, 700), 'Next and previous (arrow keys)'),
                    ((110, 1228, 2292, 1372), (2335, 1300), 'Every picture in the folder'),
                    ((810, 1385, 1580, 1428), (740, 1406), 'Keyboard keys, always shown')],
 'viewer-pdf': [((125, 255, 345, 1380), (400, 280), 'Pages'),
                ((1010, 1265, 1665, 1335), (1720, 1300), 'Go to a page, zoom'),
                ((1680, 150, 2262, 200), (1600, 175), 'Print, copy, delete, open in its app')],
 'viewer-video': [((965, 1045, 1410, 1120), (1460, 1083), 'Subtitles (an .srt next to the video)'),
                  ((175, 1205, 2205, 1240), (120, 1222), 'Drag to jump'),
                  ((175, 1260, 520, 1325), (580, 1292), 'Play, back, forward'),
                  ((1780, 1265, 2200, 1325), (1720, 1295), 'Volume, speed, subtitles on/off')],
 'archive': [((950, 95, 1200, 150), (1080, 40), 'A ZIP opens like a folder'),
             ((485, 190, 1062, 250), (1120, 220), 'Extract all, or only what you picked'),
             ((480, 285, 2350, 630), (1300, 680), 'Look inside without unzipping')],
 'folder-colour': [((1420, 1118, 1810, 1150), (1360, 1134), 'Right-click a folder, Folder colour'),
                   ((1815, 1110, 2025, 1490), (2090, 1200), 'Windows shows the colour too')],
 'tags': [((1420, 1058, 1810, 1090), (1360, 1074), 'Right-click, Tag'),
          ((1815, 1050, 2045, 1440), (2110, 1150), 'Tagged files are one click away in the sidebar')],
 'command-bar': [((788, 393, 1592, 446), (1660, 420), 'Ctrl+K, then type what you want'),
                 ((795, 465, 1590, 1010), (1660, 700), 'Pick it and press Enter')],
 'search': [((540, 95, 740, 150), (800, 215), 'Results from every folder inside'),
            ((1235, 92, 1812, 152), (1700, 215), 'Narrow the results'),
            ((1950, 250, 2300, 612), (2110, 672), 'Where each file is')],
 'preview-pane': [((1663, 172, 2311, 1377), (1600, 260), 'Preview pane: see a file without opening it')],
 'copy-card': [((1680, 1075, 2340, 1385), (1610, 1100), 'Progress, speed, pause; Done when finished'),
               ((1950, 1310, 2055, 1372), (2115, 1340), 'Undo puts everything back')],
 'bulk-rename': [((752, 262, 1625, 320), (1690, 290), 'New name: {n} is a number'),
                 ((752, 340, 1625, 800), (1690, 560), 'Find and replace, add text, numbers'),
                 ((760, 880, 1625, 1200), (1690, 1030), 'See every new name before you rename'),
                 ((752, 1282, 1185, 1342), (680, 1312), 'Rename (Undo works too)')],
 'duplicates': [((495, 312, 955, 372), (1020, 342), 'Move the extra copies to the Recycle Bin'),
                ((515, 460, 1000, 495), (1060, 478), 'The oldest copy is kept'),
                ((495, 392, 2338, 1018), (1300, 1060), 'Same content, found by a fingerprint')],
 'space-map': [((482, 298, 790, 352), (850, 325), 'Go up, or open the folder'),
               ((482, 367, 2266, 1391), (2320, 420), 'Bigger block = bigger file; click to look inside')],
 'tools-hub': [((505, 440, 1960, 790), (2030, 600), 'Clean up'),
               ((505, 912, 980, 1080), (1050, 1000), 'Compare and sync two folders'),
               ((505, 1200, 1470, 1370), (1540, 1290), 'Rules and backups that run by themselves')],
 'more-tools': [((1420, 1162, 1810, 1194), (1360, 1178), 'Right-click, More tools'),
                ((1815, 1160, 2350, 1490), (2200, 1110), 'Links, permissions, lock with a password, hide, scan, shred')],
 'folder-options': [((780, 195, 1620, 360), (1690, 260), 'Single or double click'),
                    ((780, 715, 1620, 1015), (1690, 860), 'Hidden files, extensions, recent files'),
                    ((780, 1100, 1620, 1160), (1690, 1130), 'Make Omebly open your folders and Win+E')],
 'gallery': [], 'light-theme': [], 'split-view': [],
}

def arrow(d, start, end, color, width=6):
    d.line([start, end], fill=color, width=width)
    ang = math.atan2(end[1] - start[1], end[0] - start[0]); L = 22
    for s in (-0.5, 0.5):
        d.line([end, (end[0] - L * math.cos(ang + s), end[1] - L * math.sin(ang + s))], fill=color, width=width)

def nearest(box, p):
    x0, y0, x1, y1 = box
    return (min(max(p[0], x0), x1), min(max(p[1], y0), y1))

def make(name, marks, key=True, out_dir=OUT):
    im = Image.open(os.path.join(RAW, name + '.png')).convert('RGB')
    w, h = im.size
    marks = [((max(0, x0 - 8), max(0, y0 - 6), min(w - 1, x1 + 8), min(h - 1, y1 + 6)), c, t) for (x0, y0, x1, y1), c, t in marks]
    clean = im.copy()
    glow = Image.new('RGBA', im.size, (0, 0, 0, 0)); g = ImageDraw.Draw(glow)
    for box, _, _ in marks: g.rounded_rectangle(box, 14, outline=MINT + (170,), width=16)
    blur = glow.filter(ImageFilter.GaussianBlur(10)); im.paste(blur, (0, 0), blur)
    for box, _, _ in marks:  # the glow stays outside: what's inside the box is shown as it is
        x0, y0, x1, y1 = box; im.paste(clean.crop((x0 + 3, y0 + 3, x1 - 3, y1 - 3)), (x0 + 3, y0 + 3))
    d = ImageDraw.Draw(im)
    for i, (box, (bx, by), _) in enumerate(marks, 1):
        d.rounded_rectangle(box, 14, outline=MINT, width=5)
        tip = nearest(box, (bx, by))
        dist = math.dist(tip, (bx, by))
        if dist > 44:  # an arrow from the marker's edge to the box
            k = 34 / dist
            arrow(d, (bx + (tip[0] - bx) * k, by + (tip[1] - by) * k), tip, MINT)
        d.ellipse((bx - 30, by - 30, bx + 30, by + 30), fill=MINT, outline=INK, width=4)
        t = str(i); f = F(36); tw = d.textlength(t, font=f)
        d.text((bx - tw / 2, by - 25), t, font=f, fill=INK)
    # The key, under the picture.
    if marks and key:
        f = F(34, False); fb = F(34)
        rows, x, row = [[]], 40, 0
        for i, (_, _, text) in enumerate(marks, 1):
            wtxt = 70 + d.textlength(text, font=f) + 60
            if x + wtxt > w - 40 and rows[-1]: rows.append([]); x = 40
            rows[-1].append((i, text, x)); x += wtxt
        band = 40 + 70 * len(rows)
        out = Image.new('RGB', (w, h + band), INK); out.paste(im, (0, 0)); d = ImageDraw.Draw(out)
        for r, items in enumerate(rows):
            y = h + 30 + r * 70
            for i, text, x in items:
                d.ellipse((x, y, x + 50, y + 50), fill=MINT)
                tw = d.textlength(str(i), font=fb); d.text((x + 25 - tw / 2, y + 2), str(i), font=fb, fill=INK)
                d.text((x + 66, y + 3), text, font=f, fill=PAPER)
        im = out
    im = im.resize((WIDTH, round(im.height * WIDTH / im.width)), Image.LANCZOS)
    im.save(os.path.join(out_dir, name + '.jpg'), quality=86, optimize=True, progressive=True)
    return im.size

for name, marks in SPEC.items():
    if os.path.isfile(os.path.join(RAW, name + '.png')):
        print(name, make(name, marks))
        # The website's copy: numbers only, its page lists what they are (as text people can search and read aloud).
        os.makedirs(os.path.join(OUT, 'web'), exist_ok=True); make(name, marks, key=False, out_dir=os.path.join(OUT, 'web'))
