"""Makes the demo folder used for the guide's screenshots (never anyone's real files):
D:\\Omebly Demo with Photos, Documents, Music, Videos, Work and an archive, plus a folder of shortcuts that
stands in for 'Recent' (OMEBLY_RECENT_DIR) and a settings folder with pinned folders.

    python tools/make-demo.py
"""
import json, os, shutil, subprocess, zipfile, glob

DEMO = r'D:\Omebly Demo'
SAMPLES = r'D:\oxtest\viewers'
BLOG = r'D:\my latest project\Omebly\website\public\images\blog'
SETTINGS = os.path.join(os.environ['TEMP'], 'omebly-explorer-shots-settings')
RECENT = os.path.join(os.environ['TEMP'], 'omebly-explorer-shots-recent')

for d in (DEMO, SETTINGS, RECENT):
    if os.path.isdir(d):
        subprocess.run(['attrib', '-r', '-s', '-h', d + r'\*', '/s', '/d'], capture_output=True)
        shutil.rmtree(d)
    os.makedirs(d)

def put(src, *rel):
    dst = os.path.join(DEMO, *rel); os.makedirs(os.path.dirname(dst), exist_ok=True); shutil.copy2(src, dst); return dst

photos = ['Beach morning', 'City lights', 'Family dinner', 'Mountain trip', 'Garden', 'Birthday party',
          'Sunset walk', 'Old town', 'Road trip', 'Friends', 'Festival night', 'Lake view']
covers = sorted(glob.glob(os.path.join(BLOG, '*', 'share.jpg')))[::5]
for name, src in zip(photos, covers):
    put(src, 'Photos', name + '.jpg')

put(os.path.join(SAMPLES, 'letter.docx'), 'Documents', 'Project plan.docx')
put(os.path.join(SAMPLES, 'letter.docx'), 'Documents', 'Cover letter.docx')
put(os.path.join(SAMPLES, 'sheet.xlsx'), 'Documents', 'Budget 2026.xlsx')
# A real-looking brochure: Omebly's public home page printed to PDF by Edge.
edge = r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
brochure = os.path.join(os.environ['TEMP'], 'omebly-demo-brochure.pdf')
if not os.path.isfile(brochure):
    subprocess.run([edge, '--headless=new', '--disable-gpu', '--no-pdf-header-footer', '--run-all-compositor-stages-before-draw', '--virtual-time-budget=15000', '--print-to-pdf=' + brochure, 'https://omebly.com/'], capture_output=True, timeout=120)
put(brochure if os.path.isfile(brochure) else os.path.join(SAMPLES, 'three pages.pdf'), 'Documents', 'Brochure.pdf')
put(os.path.join(SAMPLES, 'three pages.pdf'), 'Documents', 'Invoice 1042.pdf')
put(os.path.join(SAMPLES, 'three pages.pdf'), 'Documents', 'Contract.pdf')
put(os.path.join(SAMPLES, 'notes.txt'), 'Documents', 'Shopping list.txt')
put(os.path.join(SAMPLES, 'readme.md'), 'Documents', 'Meeting notes.md')
put(os.path.join(SAMPLES, 'table.csv'), 'Documents', 'Contacts.csv')
put(os.path.join(SAMPLES, 'song.mp3'), 'Music', 'Morning song.mp3')
put(os.path.join(SAMPLES, 'alarm.wav'), 'Music', 'Bell.wav')
# A short, good-looking video: the demo photos as a slow slideshow.
os.makedirs(os.path.join(DEMO, 'Videos'), exist_ok=True)
ff = shutil.which('ffmpeg') or r'D:\my latest project\Omebly Downloader\app\src-tauri\tools\ffmpeg.exe'
inputs = []
for name in photos[:4]: inputs += ['-loop', '1', '-t', '3', '-i', os.path.join(DEMO, 'Photos', name + '.jpg')]
filt = ';'.join(f'[{i}:v]scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720,zoompan=z=\'min(zoom+0.0008,1.08)\':d=75:s=1280x720:fps=25,setsar=1[v{i}]' for i in range(4))
filt += ';' + ''.join(f'[v{i}]' for i in range(4)) + 'concat=n=4:v=1:a=0,format=yuv420p[out]'
subprocess.run([ff, '-y', '-loglevel', 'error'] + inputs + ['-f', 'lavfi', '-t', '12', '-i', 'sine=frequency=330:sample_rate=44100', '-filter_complex', filt, '-map', '[out]', '-map', '4:a', '-af', 'volume=0.05', '-c:v', 'libx264', '-crf', '24', '-c:a', 'aac', '-shortest', os.path.join(DEMO, 'Videos', 'Trip video.mp4')], check=True)
open(os.path.join(DEMO, 'Videos', 'Trip video.srt'), 'w', encoding='utf-8').write('1\n00:00:00,500 --> 00:00:05,500\nA morning by the sea\n\n2\n00:00:06,000 --> 00:00:11,500\nThen off to the city\n')
for client in ('Client A', 'Client B'):
    put(os.path.join(SAMPLES, 'three pages.pdf'), 'Work', client, 'Proposal.pdf')
    put(os.path.join(SAMPLES, 'sheet.xlsx'), 'Work', client, 'Quote.xlsx')
put(os.path.join(SAMPLES, 'hello.cs'), 'Work', 'Website', 'app.cs')
put(os.path.join(SAMPLES, 'page.html'), 'Work', 'Website', 'index.html')
put(os.path.join(SAMPLES, 'script.py'), 'Work', 'Website', 'build.py')
# Two copies of one picture, for the duplicate finder.
put(os.path.join(DEMO, 'Photos', 'Garden.jpg'), 'Downloads', 'Garden.jpg')
put(os.path.join(DEMO, 'Photos', 'Garden.jpg'), 'Downloads', 'Garden (1).jpg')
put(os.path.join(DEMO, 'Photos', 'Sunset walk.jpg'), 'Downloads', 'IMG_2041.jpg')
put(os.path.join(DEMO, 'Photos', 'Sunset walk.jpg'), 'Downloads', 'IMG_2041 (copy).jpg')
put(os.path.join(SAMPLES, 'three pages.pdf'), 'Downloads', 'Ticket.pdf')
put(os.path.join(SAMPLES, 'three pages.pdf'), 'Downloads', 'Ticket (2).pdf')
with zipfile.ZipFile(os.path.join(DEMO, 'Holiday photos.zip'), 'w', zipfile.ZIP_DEFLATED) as z:
    for name in photos[:6]:
        z.write(os.path.join(DEMO, 'Photos', name + '.jpg'), name + '.jpg')

# 'Recent': shortcuts to a few demo files.
ps = []
for i, rel in enumerate([r'Documents\Budget 2026.xlsx', r'Photos\Beach morning.jpg', r'Documents\Brochure.pdf',
                         r'Documents\Project plan.docx', r'Music\Morning song.mp3']):
    lnk = os.path.join(RECENT, f'{i}.lnk')
    ps.append(f"$s=$w.CreateShortcut('{lnk}'); $s.TargetPath='{os.path.join(DEMO, rel)}'; $s.Save(); (Get-Item '{lnk}').LastWriteTime=(Get-Date).AddMinutes(-{i * 7})")
subprocess.run(['powershell', '-NoProfile', '-Command', '$w=New-Object -ComObject WScript.Shell; ' + '; '.join(ps)], check=True)

json.dump({'Pinned': [{'Path': os.path.join(DEMO, 'Work'), 'Color': '#2EF2B3'}, {'Path': os.path.join(DEMO, 'Photos'), 'Color': '#FF8A5B'},
                      {'Path': os.path.join(DEMO, 'Documents'), 'Color': '#7AA2FF'}]},
          open(os.path.join(SETTINGS, 'settings.json'), 'w', encoding='utf-8'), indent=1)
print('demo ready:', DEMO)
