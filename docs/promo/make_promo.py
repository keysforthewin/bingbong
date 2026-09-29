"""
Renders the 15-second bingbong promo (1920x1080, 30 fps, 16:9 for X) with
Pillow-drawn motion graphics and numpy-synthesised slapstick sound effects,
then muxes everything with ffmpeg.

Outputs:
  <repo>/docs/bingbong-promo.mp4   (H.264 + AAC, ready to post)
  <repo>/docs/bingbong-promo.gif   (small looping preview for the README)
"""
import math
import os
import random
import subprocess
import sys
import wave

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFont

W, H, FPS, DUR = 1920, 1080, 30, 15.0
N_FRAMES = int(DUR * FPS)
REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
OUT_DIR = os.path.join(REPO, "docs")
SCRATCH = os.path.dirname(os.path.abspath(__file__))
FFMPEG = "ffmpeg"

BG = (30, 30, 46, 255)
PURPLE = (124, 58, 237, 255)
PURPLE2 = (155, 93, 229, 255)
GREEN = (16, 185, 129, 255)
YELLOW = (250, 204, 21, 255)
ORANGE = (245, 158, 11, 255)
RED = (239, 68, 68, 255)
WHITE = (255, 255, 255, 255)
DIM = (154, 154, 176, 255)
SURFACE = (42, 42, 60, 255)
SURFACE2 = (56, 56, 78, 255)
INK = (18, 18, 28, 255)
CODE_BG = (21, 21, 31, 255)


def font(name, size):
    return ImageFont.truetype(f"C:/Windows/Fonts/{name}", size)


F_HEAD = font("seguibl.ttf", 112)
F_HEAD_S = font("seguibl.ttf", 84)
F_SUB = font("bahnschrift.ttf", 60)
F_BIG = font("impact.ttf", 240)
F_CODE = font("consolab.ttf", 50)
F_BTN = font("seguibl.ttf", 64)
F_URL = font("bahnschrift.ttf", 44)
F_LOGO = font("seguibl.ttf", 150)

# ----------------------------------------------------------------- easing --

def clamp(x, a=0.0, b=1.0):
    return max(a, min(b, x))


def seg(t, t0, d):
    """Progress 0..1 of a segment starting at t0 lasting d seconds."""
    return clamp((t - t0) / d) if d > 0 else (1.0 if t >= t0 else 0.0)


def ease_out_back(x, s=1.9):
    x = clamp(x)
    return 1 + (s + 1) * (x - 1) ** 3 + s * (x - 1) ** 2


def ease_out_elastic(x):
    x = clamp(x)
    if x <= 0 or x >= 1:
        return x
    return 2 ** (-10 * x) * math.sin((x * 10 - 0.75) * (2 * math.pi / 3)) + 1


def ease_out_cubic(x):
    x = clamp(x)
    return 1 - (1 - x) ** 3


def ease_in_cubic(x):
    x = clamp(x)
    return x ** 3


def ease_in_out(x):
    x = clamp(x)
    return x * x * (3 - 2 * x)


def lerp(a, b, x):
    return a + (b - a) * x

# ---------------------------------------------------------------- sprites --

_cache = {}


def text_sprite(text, fnt, fill, stroke=0, stroke_fill=INK):
    key = ("t", text, id(fnt), fill, stroke, stroke_fill)
    if key in _cache:
        return _cache[key]
    l, t, r, b = fnt.getbbox(text, stroke_width=stroke)
    pad = stroke + 10
    img = Image.new("RGBA", (r - l + 2 * pad, b - t + 2 * pad), (0, 0, 0, 0))
    ImageDraw.Draw(img).text((pad - l, pad - t), text, font=fnt, fill=fill,
                             stroke_width=stroke, stroke_fill=stroke_fill)
    _cache[key] = img
    return img


def rrect_sprite(w, h, r, fill, outline=None, ow=0):
    key = ("rr", w, h, r, fill, outline, ow)
    if key in _cache:
        return _cache[key]
    img = Image.new("RGBA", (int(w * 2), int(h * 2)), (0, 0, 0, 0))
    ImageDraw.Draw(img).rounded_rectangle([0, 0, w * 2 - 1, h * 2 - 1], radius=r * 2,
                                          fill=fill, outline=outline, width=ow * 2)
    img = img.resize((int(w), int(h)), Image.LANCZOS)
    _cache[key] = img
    return img


def circle_sprite(d, fill, outline=None, ow=0):
    key = ("c", d, fill, outline, ow)
    if key in _cache:
        return _cache[key]
    img = Image.new("RGBA", (int(d * 2), int(d * 2)), (0, 0, 0, 0))
    ImageDraw.Draw(img).ellipse([0, 0, d * 2 - 1, d * 2 - 1], fill=fill, outline=outline, width=ow * 2)
    img = img.resize((int(d), int(d)), Image.LANCZOS)
    _cache[key] = img
    return img


def poly_sprite(points, fill, outline=None, ow=0, size=None):
    """points in local coords; sprite sized to bounding box (+pad)."""
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    pad = ow + 4
    w = int(max(xs) - min(xs) + 2 * pad)
    h = int(max(ys) - min(ys) + 2 * pad)
    img = Image.new("RGBA", (w * 2, h * 2), (0, 0, 0, 0))
    pts = [((x - min(xs) + pad) * 2, (y - min(ys) + pad) * 2) for x, y in points]
    d = ImageDraw.Draw(img)
    d.polygon(pts, fill=fill)
    if outline and ow:
        d.line(pts + [pts[0]], fill=outline, width=ow * 2, joint="curve")
    return img.resize((w, h), Image.LANCZOS)


def blit(canvas, sprite, cx, cy, scale=1.0, angle=0.0, alpha=1.0, sx=None, sy=None):
    if alpha <= 0.01:
        return
    sx = scale if sx is None else sx
    sy = scale if sy is None else sy
    if sx <= 0.01 or sy <= 0.01:
        return
    s = sprite
    if sx != 1.0 or sy != 1.0:
        nw, nh = max(1, int(s.width * sx)), max(1, int(s.height * sy))
        s = s.resize((nw, nh), Image.LANCZOS if nw < s.width else Image.BICUBIC)
    if angle:
        s = s.rotate(angle, resample=Image.BICUBIC, expand=True)
    if alpha < 1:
        s = s.copy()
        s.putalpha(s.getchannel("A").point(lambda v: int(v * alpha)))
    canvas.alpha_composite(s, (int(cx - s.width / 2), int(cy - s.height / 2)))


# The app icon, rendered the same way as client/bingbong.ico
def app_icon_sprite(S):
    key = ("icon", S)
    if key in _cache:
        return _cache[key]
    ss = 2
    Wd = S * ss
    img = Image.new("RGBA", (Wd, Wd), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, Wd - 1, Wd - 1], radius=int(Wd * 0.24), fill=PURPLE)
    box = Wd * 0.62
    u = box / 24.0
    ox = Wd / 2 - 11.25 * u
    oy = Wd / 2 - 12.0 * u
    P = lambda x, y: (ox + x * u, oy + y * u)
    stroke = max(2, int(round(2.2 * u)))
    cap = lambda pt: d.ellipse([pt[0] - stroke / 2, pt[1] - stroke / 2, pt[0] + stroke / 2, pt[1] + stroke / 2], fill="white")
    body = [P(11, 5), P(6, 9), P(2, 9), P(2, 15), P(6, 15), P(11, 19), P(11, 5), P(6, 9)]
    d.line(body, fill="white", width=stroke, joint="curve")
    for pt in body[:6]:
        cap(pt)
    r = 5.0
    dx = math.sqrt(r * r - 3.5 * 3.5)
    ang = math.degrees(math.atan2(3.5, dx))
    R = r * u
    cx, cy = P(15.5 - dx, 12)
    d.arc([cx - R, cy - R, cx + R, cy + R], start=-ang, end=ang, fill="white", width=stroke)
    cap(P(15.5, 8.5))
    cap(P(15.5, 15.5))
    img = img.resize((S, S), Image.LANCZOS)
    _cache[key] = img
    return img


def speaker_sprite(S, color=WHITE):
    """Just the speaker glyph (no square), for the 'emitting' shots."""
    key = ("spk", S, color)
    if key in _cache:
        return _cache[key]
    ss = 2
    Wd = S * ss
    img = Image.new("RGBA", (Wd, Wd), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = Wd / 24.0
    P = lambda x, y: (x * u, y * u)
    stroke = max(2, int(round(2.0 * u)))
    cap = lambda pt: d.ellipse([pt[0] - stroke / 2, pt[1] - stroke / 2, pt[0] + stroke / 2, pt[1] + stroke / 2], fill=color)
    body = [P(11, 5), P(6, 9), P(2, 9), P(2, 15), P(6, 15), P(11, 19), P(11, 5), P(6, 9)]
    d.line(body, fill=color, width=stroke, joint="curve")
    for pt in body[:6]:
        cap(pt)
    for r, x0 in ((5.0, 15.5), (9.0, 19.0)):
        half = 3.5 if r == 5.0 else 6.5
        dx = math.sqrt(r * r - half * half)
        ang = math.degrees(math.atan2(half, dx))
        R = r * u
        cx, cy = P(x0 - dx, 12)
        d.arc([cx - R, cy - R, cx + R, cy + R], start=-ang, end=ang, fill=color, width=stroke)
        cap(P(x0, 12 - half))
        cap(P(x0, 12 + half))
    img = img.resize((S, S), Image.LANCZOS)
    _cache[key] = img
    return img


def laptop_sprite():
    if "laptop" in _cache:
        return _cache["laptop"]
    img = Image.new("RGBA", (620, 420), (0, 0, 0, 0))
    img.alpha_composite(rrect_sprite(520, 330, 26, SURFACE2, PURPLE2, 8), (50, 0))
    img.alpha_composite(rrect_sprite(470, 280, 14, INK), (75, 25))
    # a tiny "app" on screen: header bar + a button
    img.alpha_composite(rrect_sprite(470, 44, 14, SURFACE), (75, 25))
    img.alpha_composite(rrect_sprite(220, 60, 30, GREEN), (200, 170))
    t = text_sprite("Sign up", font("seguibl.ttf", 34), WHITE)
    img.alpha_composite(t, (310 - t.width // 2, 200 - t.height // 2))
    img.alpha_composite(rrect_sprite(620, 34, 17, PURPLE2), (0, 340))
    _cache["laptop"] = img
    return img


def phone_sprite(pressed=0.0, thanks=0.0):
    key = ("phone", round(pressed, 2), round(thanks, 2))
    if key in _cache:
        return _cache[key]
    img = Image.new("RGBA", (360, 700), (0, 0, 0, 0))
    img.alpha_composite(rrect_sprite(340, 680, 48, SURFACE2, PURPLE2, 8), (10, 10))
    img.alpha_composite(rrect_sprite(300, 610, 30, INK), (30, 45))
    img.alpha_composite(rrect_sprite(120, 14, 7, SURFACE2), (120, 62))
    # big BUY button
    bw, bh = 220, 110
    sc = 1 - 0.18 * pressed
    btn = rrect_sprite(int(bw * sc), int(bh * sc), int(30 * sc), GREEN)
    bx, by = 180, 470
    img.alpha_composite(btn, (bx - btn.width // 2, by - btn.height // 2))
    lab = text_sprite("BUY", font("seguibl.ttf", int(56 * sc)), WHITE)
    img.alpha_composite(lab, (bx - lab.width // 2, by - lab.height // 2))
    # product placeholder
    img.alpha_composite(rrect_sprite(240, 200, 20, SURFACE), (60, 130))
    img.alpha_composite(circle_sprite(90, PURPLE), (135, 185))
    if thanks > 0:
        bub = rrect_sprite(240, 80, 40, YELLOW)
        img.alpha_composite(bub, (60, 360))
        tt = text_sprite("Thanks!", font("seguibl.ttf", 40), INK)
        img.alpha_composite(tt, (180 - tt.width // 2, 400 - tt.height // 2))
    _cache[key] = img
    return img


def cursor_sprite():
    if "cursor" in _cache:
        return _cache["cursor"]
    pts = [(0, 0), (0, 100), (26, 78), (44, 118), (66, 108), (48, 70), (80, 68)]
    _cache["cursor"] = poly_sprite(pts, WHITE, INK, 6)
    return _cache["cursor"]


def house_sprite():
    if "house" in _cache:
        return _cache["house"]
    img = Image.new("RGBA", (380, 360), (0, 0, 0, 0))
    roof = poly_sprite([(0, 150), (190, 0), (380, 150)], RED, INK, 6)
    img.alpha_composite(roof, (0, 0))
    img.alpha_composite(rrect_sprite(280, 200, 10, ORANGE, INK, 6), (50, 150))
    img.alpha_composite(rrect_sprite(70, 120, 8, INK), (155, 230))
    img.alpha_composite(rrect_sprite(60, 60, 8, (140, 220, 255, 255), INK, 5), (80, 180))
    img.alpha_composite(rrect_sprite(60, 60, 8, (140, 220, 255, 255), INK, 5), (240, 180))
    _cache["house"] = img
    return img


def person_sprite():
    if "person" in _cache:
        return _cache["person"]
    img = Image.new("RGBA", (200, 200), (0, 0, 0, 0))
    img.alpha_composite(circle_sprite(80, WHITE), (60, 10))
    img.alpha_composite(rrect_sprite(150, 110, 55, WHITE), (25, 100))
    _cache["person"] = img
    return img


def cart_sprite():
    if "cart" in _cache:
        return _cache["cart"]
    img = Image.new("RGBA", (220, 200), (0, 0, 0, 0))
    body = poly_sprite([(0, 30), (40, 30), (70, 130), (190, 130), (215, 55), (60, 55)], WHITE, WHITE, 8)
    img.alpha_composite(body, (0, 0))
    img.alpha_composite(circle_sprite(44, WHITE), (60, 145))
    img.alpha_composite(circle_sprite(44, WHITE), (150, 145))
    _cache["cart"] = img
    return img


def bubble_sprite():
    if "bubble" in _cache:
        return _cache["bubble"]
    img = Image.new("RGBA", (230, 200), (0, 0, 0, 0))
    img.alpha_composite(rrect_sprite(220, 140, 50, WHITE), (5, 5))
    img.alpha_composite(poly_sprite([(0, 0), (60, 0), (10, 50)], WHITE), (50, 135))
    for i in range(3):
        img.alpha_composite(circle_sprite(30, PURPLE), (50 + i * 50, 60))
    _cache["bubble"] = img
    return img


def ring_sprite(d, color, ow):
    return circle_sprite(d, None, color, ow)


def starburst_sprite(R=520, color=YELLOW):
    key = ("burst", R, color)
    if key in _cache:
        return _cache[key]
    pts = []
    n = 18
    for i in range(n * 2):
        a = math.pi * 2 * i / (n * 2)
        r = R if i % 2 == 0 else R * 0.72
        pts.append((R + r * math.cos(a), R + r * math.sin(a)))
    _cache[key] = poly_sprite(pts, color, INK, 8)
    return _cache[key]

# --------------------------------------------------------------- confetti --

random.seed(7)
CONFETTI = [dict(x=random.uniform(100, 1820), vy=random.uniform(380, 720), vx=random.uniform(-60, 60),
                 w=random.randint(14, 30), h=random.randint(22, 44), rot=random.uniform(0, 360),
                 rs=random.uniform(-400, 400), phase=random.uniform(0, 6.28),
                 color=random.choice([YELLOW, GREEN, PURPLE2, RED, WHITE, ORANGE]))
            for _ in range(110)]


def draw_confetti(canvas, t, t0, t1):
    if t < t0 or t > t1 + 0.5:
        return
    dt = t - t0
    fade = 1.0 if t < t1 else clamp(1 - (t - t1) / 0.5)
    for c in CONFETTI:
        y = -60 + c["vy"] * dt
        if y > H + 60:
            continue
        x = c["x"] + c["vx"] * dt + 40 * math.sin(dt * 3 + c["phase"])
        spr = rrect_sprite(c["w"], c["h"], 4, c["color"])
        blit(canvas, spr, x, y, angle=c["rot"] + c["rs"] * dt, alpha=fade)

# ------------------------------------------------------------------ scenes --

def scene_intro(cv, t):
    # 0.0 - 2.6  "You built your first app."
    if t > 2.7:
        return
    out = seg(t, 2.35, 0.25)
    # laptop
    p = ease_out_back(seg(t, 0.05, 0.6))
    x = lerp(-400, 520, p)
    bob = 10 * math.sin(t * 4)
    lp = laptop_sprite()
    blit(cv, lp, x, 560 + bob - out * 900, scale=1.0, angle=-6 * (1 - p) + 2 * math.sin(t * 2))
    words = [("You", 0.25), ("built", 0.47), ("your", 0.69), ("first", 0.91), ("app.", 1.13)]
    lines = [["You", "built", "your"], ["first", "app."]]
    fnt = F_HEAD
    y0 = 430
    for li, line in enumerate(lines):
        sprites = [text_sprite(w, fnt, WHITE if w != "app." else YELLOW) for w in line]
        total = sum(s.width for s in sprites) + 26 * (len(sprites) - 1)
        xx = 1280 - total / 2
        for w, s in zip(line, sprites):
            t0 = dict(words)[w]
            sc = ease_out_back(seg(t, t0, 0.32)) * (1 - ease_in_cubic(out))
            blit(cv, s, xx + s.width / 2, y0 + li * 150 - out * 700,
                 scale=sc, angle=(1 - clamp(seg(t, t0, 0.32))) * -15)
            xx += s.width + 26


def scene_used(cv, t):
    # 2.6 - 5.2  someone used it, you had no idea
    if t < 2.6 or t > 5.3:
        return
    out = seg(t, 5.0, 0.2)
    p = ease_out_back(seg(t, 2.6, 0.5))
    py = lerp(H + 400, 600, p) + out * 900
    pressed = 0.0
    if 3.6 <= t < 3.8:
        pressed = 1 - seg(t, 3.6, 0.2)
    thanks = seg(t, 3.75, 0.2)
    ph = phone_sprite(pressed, thanks)
    blit(cv, ph, 960, py, angle=3 * math.sin(t * 3))
    # click ring
    if 3.6 <= t < 4.0:
        q = seg(t, 3.6, 0.4)
        blit(cv, ring_sprite(int(80 + 260 * q), WHITE, 10), 960, py + 120, alpha=1 - q)
    # cursor
    q = ease_in_out(seg(t, 3.05, 0.55))
    cx = lerp(1750, 985, q)
    cy = lerp(120, py + 100, q)
    cs = 1.0 - 0.15 * (1 if 3.6 <= t < 3.72 else 0)
    if t < 5.0:
        blit(cv, cursor_sprite(), cx + 30, cy + 45, scale=cs)
    # texts
    top = text_sprite("Someone just used it.", F_HEAD_S, WHITE)
    blit(cv, top, 960, 150, scale=ease_out_back(seg(t, 2.95, 0.3)) * (1 - out), angle=-3)
    bot = text_sprite("…and you had no idea.", F_SUB, DIM)
    a = ease_out_cubic(seg(t, 4.2, 0.4)) * (1 - out)
    blit(cv, bot, 960, 985 + (1 - a) * 40, alpha=a)


def scene_slam(cv, t):
    # 5.2 - 8.4  the icon slams in, BING BONG!
    if t < 5.2 or t > 8.5:
        return
    out = ease_in_cubic(seg(t, 8.2, 0.2))
    icon = app_icon_sprite(440)
    # fall
    fall = ease_in_cubic(seg(t, 5.2, 0.35))
    y = lerp(-400, 470, fall)
    sx = sy = 1.0
    if t >= 5.55:
        k = seg(t, 5.55, 0.45)
        sx = 1 + 0.35 * (1 - ease_out_elastic(k)) if k < 1 else 1
        sy = 1 - 0.3 * (1 - ease_out_elastic(k)) if k < 1 else 1
        sx = 1.35 - 0.35 * ease_out_elastic(k)
        sy = 0.7 + 0.3 * ease_out_elastic(k)
    # rings
    if t >= 5.7:
        for k in range(4):
            ph = ((t - 5.7) * 0.9 + k * 0.25) % 1.0
            d = int(460 + 900 * ph)
            blit(cv, ring_sprite(d, PURPLE2, 16), 960, 470, alpha=(1 - ph) * 0.9 * (1 - out))
    blit(cv, icon, 960, y, sx=sx * (1 - out), sy=sy * (1 - out))
    # starburst + BING BONG!
    if t >= 5.85:
        k = seg(t, 5.85, 0.6)
        sc = ease_out_elastic(k) * (1 - out)
        wob = 6 * math.sin(t * 18)
        blit(cv, starburst_sprite(), 960, 840, scale=sc * 0.62, angle=t * 25)
        blit(cv, text_sprite("BING BONG!", F_BIG, YELLOW, 14, INK), 960, 840, scale=sc, angle=wob)
    # house shaking on the right
    if t >= 5.55:
        shake = 14 * math.sin(t * 60) * (1 if t < 8.2 else 0)
        blit(cv, house_sprite(), 1580 + shake, 380 - out * 600, angle=4 * math.sin(t * 40))
        for k in range(3):
            ph = ((t - 5.55) * 1.2 + k * 0.33) % 1.0
            blit(cv, ring_sprite(int(120 + 320 * ph), YELLOW, 10), 1580, 380, alpha=(1 - ph) * (1 - out))
    draw_confetti(cv, t, 5.85, 8.2)


def scene_every(cv, t):
    # 8.4 - 11.6  Every signup / order / hello
    if t < 8.4 or t > 11.7:
        return
    out = ease_in_cubic(seg(t, 11.4, 0.2))
    rows = [("Every signup.", person_sprite(), 8.5, GREEN),
            ("Every order.", cart_sprite(), 9.4, YELLOW),
            ("Every hello.", bubble_sprite(), 10.3, PURPLE2)]
    for i, (label, ic, t0, col) in enumerate(rows):
        p = ease_out_back(seg(t, t0, 0.38))
        yy = 300 + i * 220
        x = lerp(2600, 960, p) - out * 2400
        bump = 1 + 0.12 * math.exp(-max(0, t - t0 - 0.38) * 6) * (1 if t >= t0 + 0.38 else 0)
        chip = rrect_sprite(1120, 180, 90, SURFACE2, col, 8)
        blit(cv, chip, x, yy, scale=bump)
        blit(cv, ic, x - 430, yy, scale=0.62 * bump)
        blit(cv, text_sprite(label, F_HEAD_S, WHITE), x + 60, yy, scale=bump)
    # pulsing speaker bottom right
    pulse = 1.0
    for t0 in (8.5, 9.4, 10.3):
        if t >= t0 + 0.3:
            pulse += 0.25 * math.exp(-(t - t0 - 0.3) * 7)
    blit(cv, speaker_sprite(200, PURPLE2), 1700, 930 + out * 400, scale=pulse)
    for k in range(2):
        ph = ((t - 8.4) * 1.1 + k * 0.5) % 1.0
        blit(cv, ring_sprite(int(140 + 260 * ph), PURPLE2, 8), 1700, 930 + out * 400, alpha=(1 - ph) * 0.8)


CMD = "curl http://your-server:3260/bingbong/order_bell"


def scene_code(cv, t):
    # 11.6 - 14.0  one line, any sound, your speakers
    if t < 11.6 or t > 14.05:
        return
    out = ease_in_cubic(seg(t, 13.85, 0.15))
    heads = [("One line.", 11.7, WHITE), ("Any sound.", 11.95, YELLOW), ("Your speakers.", 12.2, GREEN)]
    sprites = [text_sprite(w, F_HEAD_S, c) for w, _, c in heads]
    total = sum(s.width for s in sprites) + 60 * 2
    xx = 960 - total / 2
    for (w, t0, c), s in zip(heads, sprites):
        sc = ease_out_back(seg(t, t0, 0.3)) * (1 - out)
        blit(cv, s, xx + s.width / 2, 250, scale=sc, angle=(1 - seg(t, t0, 0.3)) * 12)
        xx += s.width + 60
    # code card
    p = ease_out_back(seg(t, 12.1, 0.4))
    cy = lerp(1400, 600, p) + out * 900
    bump = 1 + 0.08 * math.exp(-max(0, t - 13.45) * 8) * (1 if t >= 13.45 else 0)
    card = rrect_sprite(1500, 230, 28, CODE_BG, PURPLE2, 6)
    blit(cv, card, 960, cy, scale=bump)
    n = int(len(CMD) * seg(t, 12.3, 1.05))
    typed = CMD[:n]
    if typed:
        s = text_sprite(typed, F_CODE, (214, 214, 230, 255))
        blit(cv, s, 260 + s.width / 2, cy, scale=bump)
        caret_x = 260 + s.width + 14
    else:
        caret_x = 262
    if int(t * 4) % 2 == 0 and t < 13.4:
        blit(cv, rrect_sprite(24, 56, 4, PURPLE2), caret_x + 12, cy)
    # speaker reacts
    if t >= 13.45:
        k = t - 13.45
        blit(cv, speaker_sprite(190, WHITE), 1640, 880, scale=1 + 0.3 * math.exp(-k * 6))
        for j in range(3):
            ph = (k * 1.6 + j * 0.33) % 1.0
            blit(cv, ring_sprite(int(140 + 380 * ph), YELLOW, 10), 1640, 880, alpha=(1 - ph) * (1 - out))
    sub = text_sprite("Any webhook. Any script. Your app.", F_SUB, DIM)
    blit(cv, sub, 960, 900, alpha=ease_out_cubic(seg(t, 12.6, 0.4)) * (1 - out) if t < 13.45 else 0)


def scene_outro(cv, t):
    if t < 14.0:
        return
    p = ease_out_back(seg(t, 14.0, 0.35))
    bump = 1 + 0.1 * math.exp(-max(0, t - 14.35) * 6) * (1 if t >= 14.35 else 0)
    blit(cv, app_icon_sprite(300), 960, 380, scale=p * bump)
    blit(cv, text_sprite("bingbong", F_LOGO, WHITE), 960, 660, scale=ease_out_back(seg(t, 14.12, 0.3)))
    a = ease_out_cubic(seg(t, 14.35, 0.3))
    blit(cv, text_sprite("github.com/keysforthewin/bingbong", F_URL, DIM), 960, 800, alpha=a)
    tag = text_sprite("Hear your app get used.", F_SUB, YELLOW)
    blit(cv, tag, 960, 900, alpha=a)


def draw_frame(t):
    cv = Image.new("RGBA", (W, H), BG)
    scene_intro(cv, t)
    scene_used(cv, t)
    scene_slam(cv, t)
    scene_every(cv, t)
    scene_code(cv, t)
    scene_outro(cv, t)
    # camera shake right after the slam
    if 5.55 <= t < 5.9:
        k = 1 - seg(t, 5.55, 0.35)
        cv = ImageChops.offset(cv, int(18 * k * math.sin(t * 90)), int(14 * k * math.cos(t * 70)))
    return cv.convert("RGB")

# ------------------------------------------------------------------- audio --

SR = 44100


def _t(dur):
    return np.arange(int(SR * dur)) / SR


def env(dur, attack=0.005, tau=0.25):
    t = _t(dur)
    e = np.exp(-np.maximum(t - attack, 0) / tau)
    e *= np.minimum(t / attack, 1)
    return e


def sine(freq, dur):
    t = _t(dur)
    if np.isscalar(freq):
        return np.sin(2 * np.pi * freq * t)
    return np.sin(2 * np.pi * np.cumsum(freq) / SR)


def saw(freq, dur, nh=10):
    t = _t(dur)
    out = np.zeros_like(t)
    for k in range(1, nh + 1):
        out += np.sin(2 * np.pi * freq * k * t) / k
    return out / 1.6


def bell(freq, dur=0.7, tau=0.28):
    t = _t(dur)
    out = np.sin(2 * np.pi * freq * t) * np.exp(-t / tau)
    out += 0.5 * np.sin(2 * np.pi * freq * 2.76 * t) * np.exp(-t / (tau * 0.5))
    out += 0.25 * np.sin(2 * np.pi * freq * 5.4 * t) * np.exp(-t / (tau * 0.3))
    return out * env(dur, 0.002, 10)


def noise(dur):
    return np.random.default_rng(3).uniform(-1, 1, int(SR * dur))


def lowpass(x, k=12):
    return np.convolve(x, np.ones(k) / k, mode="same")


def sfx_pop():
    dur = 0.09
    t = _t(dur)
    f = 900 * np.exp(-t * 25) + 200
    return sine(f, dur) * env(dur, 0.002, 0.03) * 0.8


def sfx_whoosh():
    dur = 0.55
    t = _t(dur)
    n = lowpass(noise(dur), 6)
    e = np.exp(-((t - 0.22) ** 2) / 0.02)
    return n * e * 0.6


def sfx_click():
    dur = 0.05
    return (noise(dur) * env(dur, 0.001, 0.008) * 0.9 + sine(2400, dur) * env(dur, 0.001, 0.012) * 0.6)


def sfx_wah():
    dur = 0.9
    t = _t(dur)
    f = 280 * np.exp(-t * 0.9)
    trem = 0.6 + 0.4 * np.sin(2 * np.pi * 7 * t)
    return saw(1, dur)[:0].sum() * 0 + sine(f, dur) * trem * env(dur, 0.02, 0.5) * 0.7 + \
        0.3 * sine(f * 2, dur) * trem * env(dur, 0.02, 0.4)


def sfx_boing():
    dur = 0.75
    t = _t(dur)
    f = 130 + 420 * np.exp(-t * 3.2) + 45 * np.exp(-t * 2) * np.sin(2 * np.pi * 28 * t)
    x = sine(f, dur) + 0.4 * sine(f * 2.01, dur)
    return x * env(dur, 0.004, 0.32) * 0.9


def sfx_slam():
    dur = 0.35
    t = _t(dur)
    thud = sine(70 * np.exp(-t * 6) + 40, dur) * env(dur, 0.002, 0.12)
    crack = lowpass(noise(dur), 3) * env(dur, 0.001, 0.03)
    return thud * 1.0 + crack * 0.5


def sfx_chaching():
    n = lowpass(noise(0.06), 2) * env(0.06, 0.001, 0.02) * 0.6
    b1 = bell(1480, 0.45, 0.16) * 0.7
    b2 = bell(1975, 0.6, 0.25) * 0.8
    out = np.zeros(int(SR * 0.75))
    out[: len(n)] += n
    out[: len(b1)] += b1
    o = int(SR * 0.09)
    out[o: o + len(b2)] += b2
    return out


def sfx_honk():
    dur = 0.4
    t = _t(dur)
    vib = 1 + 0.02 * np.sin(2 * np.pi * 6 * t)
    x = saw(1, dur)[:0].sum() * 0
    x = np.zeros_like(t)
    for k in range(1, 9):
        x += np.sin(2 * np.pi * 185 * k * vib * t) / k
    x += 0.5 * np.sin(2 * np.pi * 277 * t)
    e = np.minimum(t / 0.03, 1) * np.minimum((dur - t) / 0.08, 1)
    return x / 2.2 * e * 0.8


def sfx_tick():
    dur = 0.025
    return (noise(dur) * env(dur, 0.001, 0.004) * 0.35 + sine(3200, dur) * env(dur, 0.001, 0.006) * 0.3)


def sfx_bing():
    return bell(880, 0.9, 0.3) * 0.9


def sfx_bong():
    return bell(587, 1.1, 0.4) * 0.9


def build_audio():
    master = np.zeros(int(SR * (DUR + 1)))

    def add(sfx, at, vol=1.0):
        i = int(at * SR)
        n = min(len(sfx), len(master) - i)
        master[i: i + n] += sfx[:n] * vol

    add(sfx_whoosh(), 0.02)
    for t0 in (0.25, 0.47, 0.69, 0.91):
        add(sfx_pop(), t0, 0.8)
    add(sfx_pop(), 1.13, 1.0)
    add(sfx_whoosh(), 2.3, 0.7)
    add(sfx_pop(), 2.95, 0.7)
    add(sfx_whoosh(), 3.05, 0.5)
    add(sfx_click(), 3.6)
    add(sfx_pop(), 3.78, 0.6)
    add(sfx_wah(), 4.2, 0.9)
    add(sfx_whoosh(), 5.2, 0.8)
    add(sfx_slam(), 5.55, 1.0)
    add(sfx_boing(), 5.56, 1.0)
    add(sfx_bing(), 5.92)
    add(sfx_bong(), 6.28)
    add(sfx_bing(), 7.0, 0.5)
    add(sfx_bong(), 7.36, 0.5)
    add(sfx_whoosh(), 8.25, 0.6)
    add(sfx_pop(), 8.5, 1.0)
    add(sfx_chaching(), 9.4, 1.0)
    add(sfx_honk(), 10.3, 1.0)
    add(sfx_whoosh(), 11.45, 0.6)
    for t0 in (11.7, 11.95, 12.2):
        add(sfx_pop(), t0, 0.8)
    n = len(CMD)
    for i in range(0, n, 2):
        add(sfx_tick(), 12.3 + 1.05 * i / n, 0.8)
    add(sfx_bing(), 13.45)
    add(sfx_whoosh(), 13.85, 0.5)
    add(sfx_bing(), 14.05)
    add(sfx_bong(), 14.4)

    master = master[: int(SR * DUR)]
    master = np.tanh(master * 1.1)
    master *= 0.9 / max(1e-6, np.abs(master).max())
    pcm = (master * 32767).astype("<i2")
    path = os.path.join(SCRATCH, "sfx.wav")
    with wave.open(path, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(SR)
        wf.writeframes(pcm.tobytes())
    return path

# ------------------------------------------------------------------- main --

def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    only_frames = [float(a) for a in sys.argv[1:]]
    if only_frames:
        for t in only_frames:
            draw_frame(t).save(os.path.join(SCRATCH, f"frame_{t:05.2f}.png"))
        print("preview frames written")
        return

    wav = build_audio()
    mp4 = os.path.join(OUT_DIR, "bingbong-promo.mp4")
    cmd = [FFMPEG, "-y", "-loglevel", "error",
           "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-",
           "-i", wav,
           "-c:v", "libx264", "-pix_fmt", "yuv420p", "-profile:v", "high", "-crf", "19", "-preset", "medium",
           "-c:a", "aac", "-b:a", "160k", "-ar", "44100",
           "-movflags", "+faststart", "-shortest", mp4]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    for i in range(N_FRAMES):
        frame = draw_frame(i / FPS)
        proc.stdin.write(frame.tobytes())
        if i % 60 == 0:
            print(f"frame {i}/{N_FRAMES}", flush=True)
    proc.stdin.close()
    proc.wait()
    if proc.returncode != 0:
        raise SystemExit(f"ffmpeg failed: {proc.returncode}")

    gif = os.path.join(OUT_DIR, "bingbong-promo.gif")
    vf = "fps=12,scale=720:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=128[p];[s1][p]paletteuse=dither=bayer:bayer_scale=4"
    subprocess.run([FFMPEG, "-y", "-loglevel", "error", "-i", mp4, "-vf", vf, "-loop", "0", gif], check=True)
    print("done:", mp4, gif)


if __name__ == "__main__":
    main()
