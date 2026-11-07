from PIL import Image
import os

src = os.path.expandvars(r'%TEMP%\dwm222\icons')
dst = r'C:\Users\SRB\Desktop\UZConnect\src\UZConnect.App\Assets\icons'
os.makedirs(dst, exist_ok=True)

mapping = {
    'p3_x8_66x66.png': 'connection',
    'p3_x116_66x66.png': 'sms',
    'p3_x114_66x66.png': 'contacts',
    'p3_x113_66x65.png': 'ussd',
    'p4_x141_66x66.png': 'statistics',
    'p4_x139_66x66.png': 'settings',
    'p4_x14_66x66.png': 'help',
}

for filename, name in mapping.items():
    image = Image.open(os.path.join(src, filename)).convert('RGBA')
    pixels = image.load()
    white = Image.new('RGBA', image.size, (255, 255, 255, 0))
    out = white.load()
    for y in range(image.height):
        for x in range(image.width):
            r, g, b, a = pixels[x, y]
            brightness = (r + g + b) / 3
            if brightness > 165 and b >= r - 20:
                alpha = int(min(255, max(0, (brightness - 165) / 90 * 255)))
                out[x, y] = (255, 255, 255, alpha)
    white.save(os.path.join(dst, name + '-white.png'))

    # Light watermark tint of the symbol for the connection screen.
    tinted = Image.new('RGBA', image.size, (0, 0, 0, 0))
    tp = tinted.load()
    for y in range(image.height):
        for x in range(image.width):
            r, g, b, a = out[x, y]
            if a:
                tp[x, y] = (205, 224, 244, int(a * 0.55))
    tinted.save(os.path.join(dst, name + '-watermark.png'))

    print(name, 'done')
