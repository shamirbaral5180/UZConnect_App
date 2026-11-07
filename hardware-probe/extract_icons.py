import fitz, os

pdf = fitz.open(os.path.expandvars(r'%TEMP%\DWM-222_manual.pdf'))
out = os.path.expandvars(r'%TEMP%\dwm222\icons')
os.makedirs(out, exist_ok=True)
seen = set()
for p in [2, 3]:
    for img in pdf[p].get_images(full=True):
        xref = img[0]
        if xref in seen:
            continue
        seen.add(xref)
        try:
            pix = fitz.Pixmap(pdf, xref)
            if pix.n - pix.alpha >= 4:
                pix = fitz.Pixmap(fitz.csRGB, pix)
            fn = os.path.join(out, f'p{p+1}_x{xref}_{pix.width}x{pix.height}.png')
            pix.save(fn)
            print(fn)
        except Exception as e:
            print('ERR', xref, e)
print('count', len(seen))
