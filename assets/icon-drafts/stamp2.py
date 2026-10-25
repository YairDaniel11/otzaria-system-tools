"""חותמת 'אחת שאלתי' — גרסה משודרגת (15.10.2026): זהב מדורג, רקע שמנת, טבעות כפולות, כוכבי הפרדה.
הרצה: python3 stamp2.py <out_dir>   (דורש Pillow)"""
import math, sys, os
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops
OUT = sys.argv[1] if len(sys.argv) > 1 else '.'
S = 1024; C = S / 2
FB = '/usr/share/fonts/truetype/freefont/FreeSerifBold.ttf'
FS = '/usr/share/fonts/truetype/freefont/FreeSansBold.ttf'

# שכבת מסכה (לבן=דיו) — מציירים הכל בלבן ואז צובעים בזהב מדורג
m = Image.new('L', (S, S), 0); d = ImageDraw.Draw(m)
def ring(r, w): d.ellipse([C-r, C-r, C+r, C+r], outline=255, width=w)
ring(500, 26); ring(470, 6); ring(334, 8); ring(320, 3)

def arc(text, r, fsize, top, font=FB, gap=1.0):
    f = ImageFont.truetype(font, fsize)
    ws = [f.getlength(c)*gap for c in text]; span = sum(ws)/r
    a = (-math.pi/2+span/2) if top else (math.pi/2-span/2)
    for c, w in zip(text, ws):
        step = w/r; mid = a-step/2 if top else a+step/2
        x = C+r*math.cos(mid); y = C+r*math.sin(mid)
        t = Image.new('L', (fsize*2, fsize*2), 0)
        ImageDraw.Draw(t).text((fsize, fsize), c, font=f, fill=255, anchor='mm')
        rot = -(math.degrees(mid)+90) if top else -(math.degrees(mid)-90)
        t = t.rotate(rot, resample=Image.BICUBIC)
        m.paste(255, (int(x-fsize), int(y-fsize)), t)
        a = a-step if top else a+step

arc("שבתי בבית ה' כל ימי חיי לחזות בנועם ה' ולבקר בהיכלו", 400, 52, True, gap=1.04)
arc('לע"נ הנערה הצדקת אסתר גיטה דינר ע"ה ב"ר מיכאל שלום שיבדלחט"א', 398, 38, False)

def star(cx, cy, R, r, n=6):
    pts = []
    for i in range(2*n):
        ang = -math.pi/2 + i*math.pi/n; rr = R if i % 2 == 0 else r
        pts.append((cx+rr*math.cos(ang), cy+rr*math.sin(ang)))
    d.polygon(pts, fill=255)
for ang in (0, math.pi):
    star(C+400*math.cos(ang), C+400*math.sin(ang), 20, 9)

# ספר פתוח קווי (בסגנון אייקוני אוצריא) + קורת "בית"
L = 10
d.line([(C, 300), (C, 410)], fill=255, width=L)
for s in (-1, 1):
    d.line([(C, 318), (C+s*48, 302), (C+s*118, 308), (C+s*118, 398), (C+s*48, 392), (C, 410)],
           fill=255, width=L, joint='curve')
d.line([(C-75, 282), (C, 236), (C+75, 282)], fill=255, width=L, joint='curve')

def line(txt, y, size, font=FB):
    d.text((C, y), txt, font=ImageFont.truetype(font, size), fill=255, anchor='mm')
line("אחת שאלתי", 505, 128)
d.line([(C-210, 582), (C-22, 582)], fill=255, width=5); d.line([(C+22, 582), (C+210, 582)], fill=255, width=5)
star(C, 582, 14, 6, 4)
line("תוכנת חסימת מחשב", 632, 46, FS); line("לתוכנות תורניות בלבד", 686, 46, FS)

# זהב מדורג (אלכסוני, בהיר-כהה-בהיר)
grad = Image.new('RGBA', (S, S))
gp = grad.load()
stops = [(0, (236, 205, 120)), (0.45, (176, 128, 36)), (0.7, (214, 172, 78)), (1, (142, 100, 26))]
def col(t):
    for (a, ca), (b, cb) in zip(stops, stops[1:]):
        if t <= b:
            k = (t-a)/(b-a); return tuple(int(ca[i]+(cb[i]-ca[i])*k) for i in range(3))
    return stops[-1][1]
row = [col(x/(2*S)) for x in range(2*S)]
for y in range(S):
    for x in range(S): gp[x, y] = row[x+y] + (255,)

im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
bg = Image.new('L', (S, S), 0); ImageDraw.Draw(bg).ellipse([C-512, C-512, C+511, C+511], fill=255)
im.paste((253, 248, 235, 255), (0, 0), bg)                     # רקע שמנת
sh = m.filter(ImageFilter.GaussianBlur(3)).point(lambda v: v*0.35)
im.paste((90, 60, 10, 255), (3, 4), sh)                          # צל עדין (הטבעה)
im.paste(grad, (0, 0), m)
os.makedirs(OUT, exist_ok=True)
im.save(f'{OUT}/stamp-1024.png')
for s in (256, 64, 32, 16):
    im.resize((s, s), Image.LANCZOS).save(f'{OUT}/stamp-{s}.png')
