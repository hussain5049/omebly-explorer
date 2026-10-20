# Makes the sample files the viewer test opens, in D:\oxtest\viewers (needs ffmpeg, Python and Edge;
# Word and Excel samples are made by Office if it is installed).
set -e
D=/d/oxtest/viewers
rm -rf $D; mkdir -p $D
cd $D
cp /c/Windows/Web/Wallpaper/Windows/img0.jpg "photo 1.jpg"
cp /c/Windows/Web/Wallpaper/Windows/img19.jpg "photo 2.jpg"
ffmpeg -loglevel error -f lavfi -i testsrc=size=640x360:rate=25 -frames:v 1 "pattern.png"
cat > logo.svg <<'EOF'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200"><circle cx="100" cy="100" r="80" fill="#2bd99f"/><text x="100" y="112" font-size="36" text-anchor="middle" fill="#04120d">OX</text></svg>
EOF
ffmpeg -loglevel error -f lavfi -i testsrc=size=1280x720:rate=30 -f lavfi -i sine=frequency=440 -t 12 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "clip.mp4"
printf '1\r\n00:00:01,000 --> 00:00:05,000\r\nHello from the subtitles\r\n\r\n' > clip.srt
ffmpeg -loglevel error -f lavfi -i sine=frequency=330 -t 20 -metadata title="Test Tone" -metadata artist="Omebly" -metadata album="Samples" -b:a 128k "song.mp3"
cp /c/Windows/Media/Alarm01.wav "alarm.wav"
cat > notes.txt <<'EOF'
Shopping list
- milk
- bread
नमस्ते, this line has Hindi too.
EOF
cat > hello.cs <<'EOF'
// A tiny program
using System;
public static class Hello
{
    public static void Main() { var n = 42; Console.WriteLine("Hello " + n); /* done */ }
}
EOF
cat > script.py <<'EOF'
# A python file
def add(a, b):
    return a + b
print(add(2, 3), "five")
EOF
cat > readme.md <<'EOF'
# Omebly Explorer
Open **any file** inside the Explorer.

## Lists
- pictures
- `code`
- [a link](https://omebly.com)

> A quote

```
code block
```
EOF
printf 'Name,City,Score\r\nAsha,"Mumbai, IN",91\r\nRavi,Delhi,78\r\n"Sara ""S""",Pune,88\r\n' > table.csv
cat > page.html <<'EOF'
<!doctype html><html><head><title>Test page</title><style>body{font-family:sans-serif;background:#eef;padding:30px}</style></head>
<body><h1>A web page</h1><p>Scripts should not run here.</p><p id="x">script did not run</p>
<script>document.getElementById('x').textContent='SCRIPT RAN';</script>
<img src="https://example.com/track.png" alt="remote image (should be blocked)"></body></html>
EOF
cp /c/Windows/Fonts/segoeui.ttf "segoe.ttf"
python - <<'EOF'
import zipfile
with zipfile.ZipFile('bundle.zip', 'w') as z:
    z.writestr('docs/readme.txt', 'hi')
    z.writestr('docs/more/deep.txt', 'deep' * 100)
    z.writestr('top.csv', 'a,b\n1,2\n')
# a minimal .docx
ct = '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
rels = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
doc = '<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Hello from a Word document</w:t></w:r></w:p></w:body></w:document>'
with zipfile.ZipFile('letter.docx', 'w') as z:
    z.writestr('[Content_Types].xml', ct); z.writestr('_rels/.rels', rels); z.writestr('word/document.xml', doc)
open('mystery.bin', 'wb').write(bytes(range(256)) * 40)
open('config.ini', 'w').write('[app]\nname=ox\n')
EOF
# a PDF with 3 pages via Edge
cat > /d/oxtest/pdfsrc.html <<'EOF'
<html><body style="font-family:sans-serif"><h1>Page one</h1><div style="page-break-after:always"></div><h1>Page two</h1><div style="page-break-after:always"></div><h1>Page three</h1></body></html>
EOF
"/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --headless --disable-gpu --no-pdf-header-footer --print-to-pdf='D:\oxtest\viewers\three pages.pdf' "file:///D:/oxtest/pdfsrc.html"
powershell -NoProfile -Command '$w = New-Object -ComObject Word.Application; $d = $w.Documents.Add(); $d.Content.Text = "Hello from a Word document made for the viewer test.`rSecond paragraph."; $d.SaveAs2("D:\oxtest\viewers\letter.docx", 16); $d.Close(); $w.Quit(); $x = New-Object -ComObject Excel.Application; $x.DisplayAlerts = $false; $b = $x.Workbooks.Add(); $s = $b.Worksheets.Item(1); $s.Cells.Item(1,1).Value2 = "Month"; $s.Cells.Item(1,2).Value2 = "Sales"; $s.Cells.Item(2,1).Value2 = "Jan"; $s.Cells.Item(2,2).Value2 = 120; $b.SaveAs("D:\oxtest\viewers\sheet.xlsx", 51); $b.Close(); $x.Quit()' || echo "No Office: letter.docx stays the small hand-made one, sheet.xlsx is skipped"
ls -la
