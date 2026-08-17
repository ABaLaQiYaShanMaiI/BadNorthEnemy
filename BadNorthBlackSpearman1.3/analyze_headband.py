from PIL import Image

frame_path = r'C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Texture2D\Swordsman0001.png'
part_path = r'C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite\PartTex_SwordShield.png'

im = Image.open(frame_path).convert('RGBA')
pt = Image.open(part_path).convert('RGBA')
w, h = im.size
pw, ph = pt.size
print('frame size', w, h, 'part size', pw, ph)
px = im.load()
pp = pt.load()

for y in range(h):
    counts = {'head47_88': 0, 'y0_45': 0, 'y45_70': 0, 'y70_90': 0, 'y90_112': 0, 'y112_126': 0, 'trans': 0}
    for x in range(w):
        r, g, b, a = px[x, y]
        if a < 10:
            counts['trans'] += 1
            continue
        cx = int((r / 255.0) * pw)
        cy = int((g / 255.0) * ph)
        if cx >= pw:
            cx = pw - 1
        if cy >= ph:
            cy = ph - 1
        if 47 <= cy < 88:
            key = 'head47_88'
        elif cy < 45:
            key = 'y0_45'
        elif cy < 70:
            key = 'y45_70'
        elif cy < 90:
            key = 'y70_90'
        elif cy < 112:
            key = 'y90_112'
        else:
            key = 'y112_126'
        counts[key] += 1
    nonop = w - counts['trans']
    if nonop > 0:
        top = max(counts, key=lambda k: (counts[k] if k != 'trans' else 0))
        print('y=%3d opaque=%3d top=%s head47_88=%d y0_45=%d y45_70=%d y70_90=%d y90_112=%d y112=%d'
              % (y, nonop, top, counts['head47_88'], counts['y0_45'], counts['y45_70'],
                 counts['y70_90'], counts['y90_112'], counts['y112_126']))
