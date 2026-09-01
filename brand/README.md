# Brand

The source art. Nothing builds from this directory.

| File | What it is |
|---|---|
| `Parley Big Transparent Black.png` | the full lockup — mark and wordmark — black on transparency |
| `Parley Icon Big.png` | the mark alone, in its ring, black on transparency |
| `Parley Big.png` | the full lockup on a white ground |

The console ships the first two, reduced, in `src/Parley.Web/Brand/`. It does not ship the
third, and the reason is worth writing down so nobody adds it back: it is opaque white, so it
carries its own background into a page that has one — a white card on an off-white page in the
light theme, and a white card on a near-black one in the dark. The transparent pair has no such
opinion, and the console inverts it for the dark theme rather than shipping a second copy that
could fall out of step with the first.

## Regenerating what the console ships

The art is uniformly black, so the alpha channel is the whole picture. Storing it as
grayscale-plus-alpha at sixteen levels of coverage takes the lockup from 121 KB to 15 KB with
no visible difference in a line drawing — which is worth doing, because these are served on
every page of a console somebody keeps open.

```python
from PIL import Image

def prep(src, dst, box):
    im = Image.open(src).convert('RGBA')
    im = im.crop(im.split()[3].getbbox())          # trim to the ink
    w, h = im.size
    s = min(box[0] / w, box[1] / h)
    im = im.resize((round(w * s), round(h * s)), Image.LANCZOS)
    a = im.split()[3].point(lambda v: (v // 16) * 17)
    Image.merge('LA', (Image.new('L', im.size, 0), a)).save(dst, optimize=True)

prep('brand/Parley Icon Big.png', 'src/Parley.Web/Brand/icon.png', (128, 128))
prep('brand/Parley Big Transparent Black.png', 'src/Parley.Web/Brand/wordmark.png', (480, 480))
```

Keep them black on transparency. The console inverts them under `prefers-color-scheme: dark`,
which works because the ink is one colour; art with more than one would need a second file and
a rule about which is authoritative.
