import math
from PIL import Image, ImageDraw, ImageFont
S=1024; C=S/2; G=(184,140,40,255)
im=Image.new('RGBA',(S,S),(0,0,0,0)); d=ImageDraw.Draw(im)
FB='/usr/share/fonts/truetype/freefont/FreeSerifBold.ttf'; FS='/usr/share/fonts/truetype/freefont/FreeSansBold.ttf'
def ring(r,w): d.ellipse([C-r,C-r,C+r,C+r],outline=G,width=w)
ring(500,22); ring(462,7); ring(330,7); ring(318,3)
def arc(text,r,fsize,top,font=FB,gap=1.0):
    f=ImageFont.truetype(font,fsize)
    chars=list(text)  # logical order; first char is rightmost
    ws=[f.getlength(c)*gap for c in chars]; tot=sum(ws)
    span=tot/r
    # top: start at right (angle -90+span/2 deg in screen) and go counterclockwise toward left
    a=(-math.pi/2+span/2) if top else (math.pi/2-span/2)
    for c,w in zip(chars,ws):
        step=w/r
        mid=a-step/2 if top else a+step/2
        x=C+r*math.cos(mid); y=C+r*math.sin(mid)
        t=Image.new('RGBA',(fsize*2,fsize*2),(0,0,0,0)); td=ImageDraw.Draw(t)
        td.text((fsize,fsize),c,font=f,fill=G,anchor='mm')
        rot=-(math.degrees(mid)+90) if top else -(math.degrees(mid)-90)
        t=t.rotate(rot,resample=Image.BICUBIC)
        im.alpha_composite(t,(int(x-fsize),int(y-fsize)))
        a = a-step if top else a+step
arc("שבתי בבית ה' כל ימי חיי לחזות בנועם ה' ולבקר בהיכלו",400,50,True,gap=1.04)
arc('לע"נ הנערה הצדקת אסתר גיטה דינר ע"ה ב"ר מיכאל שלום שיבדלחט"א',398,38,False)
# small separators (stars) at the sides
for ang in (0,math.pi):
    x=C+400*math.cos(ang); y=C+400*math.sin(ang); d.ellipse([x-9,y-9,x+9,y+9],fill=G)
from bidi.algorithm import get_display
def line(txt,y,size,font=FB):
    f=ImageFont.truetype(font,size); d.text((C,y),txt,font=f,fill=G,anchor='mm')
# book line-glyph (Otzaria style)
L=8
d.line([(C,330),(C,420)],fill=G,width=L)
for s in (-1,1):
    d.line([(C,330),(C+s*45,315),(C+s*110,320),(C+s*110,405),(C+s*45,400),(C,420)],fill=G,width=L,joint='curve')
line("אחת שאלתי",500,118)
d.line([(C-200,575),(C+200,575)],fill=G,width=5)
line("תוכנת חסימת מחשב",620,44,FS); line("לתוכנות תורניות בלבד",672,44,FS)
im.save('/tmp/ic2/stamp-1024.png')
for s in (256,64,32,16): im.resize((s,s),Image.LANCZOS).save(f'/tmp/ic2/stamp-{s}.png')
